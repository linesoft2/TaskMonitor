using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// SmoothScrollViewer + 自实现的"延迟捕获"触屏拖拽（替代原生 PanningMode）。
    /// 为什么不要 PanningMode：它是布局驱动 —— 每个触摸 move（触屏 90~120Hz）都
    /// ScrollToVerticalOffset → 整页布局，弱 GPU 触屏机实测掉帧（BitmapCache 消掉
    /// 每帧重绘后仍卡，2026-08-02 日志证实 cache engaged 但卡顿依旧）。
    /// 为什么不要库的 manipulation（IsEnableSmoothManipulating）：IsManipulationEnabled=true
    /// 在 TouchDown 即捕获触摸点，tap 不再晋升鼠标点击，页面内开关/按钮触屏全失灵。
    /// 这里的做法：
    ///   TouchDown 只观察（不处理不捕获 → tap 正常晋升 Click；起点落在 ScrollBar 上
    ///   的手势直接不跟踪，滚动条保持原生晋升拖拽）；位移过阈值才 Capture 触摸点
    ///   开始拖拽 —— 此刻首个 TouchDown 已晋升出的 MouseLeftButtonDown 用
    ///   Mouse.Capture(null) 平衡（ButtonBase 失捕获即解除按下态、不触发 Click），
    ///   拖拽中的 move/up 全部 e.Handled 吞掉，晋升流就此掐断，松手不会补出 Click。
    ///   拖拽位移按增量喂给 physics（AnimatedScrollToVerticalOffset 内部
    ///   delta = VerticalOffset - offset，传 VerticalOffset - dy 即精确投递 dy，
    ///   与逻辑 offset 的陈旧程度无关，无累积误差）→ 渲染走 RenderTransform 路径，
    ///   显示频率推进、每 20px 才同步一次逻辑 offset，输入路径零布局。
    ///   惯性：松手速度 > 阈值时自跑 CompositionTarget.Rendering 衰减循环，继续按
    ///   v·dt 喂增量（v 指数衰减 ≈0.3~0.6s 滑停），撞边界/低速/重新按下即收。
    /// 已知取舍（与原生 PanningMode 同款）：拖拽起点落在 ComboBox 上时下拉会先弹开、
    /// 落在 TextBox 上会先落焦点 —— 拖拽启动即中止，控件不卡死；physics 渲染期间
    /// 内容 IsHitTestVisible=false（库固有），滚动视觉停稳后 ~0.1s 内的 tap 会被吞。
    /// </summary>
    public class TouchDragScrollViewer : FluentWpfCore.Controls.SmoothScrollViewer
    {
        private const double DragThresholdDip = 8.0;
        private const double FlingMinVelocity = 240.0;  // DIP/s，低于此值不甩惯性
        private const double FlingDecayRate = 3.5;      // 1/s，v *= e^(-k·dt)
        private const double FlingStopVelocity = 30.0;  // DIP/s，低于此值惯性收尾
        private const double VelocityWindowMs = 80.0;   // 松手速度采样窗口

        private TouchDevice _device;     // 只跟踪第一根手指
        private Point _downPoint;
        private Point _lastPoint;
        private bool _dragging;

        private bool _flinging;
        private double _flingVelocity;   // DIP/s，符号同 dy（负 = 内容向上滚）
        private long _flingLastTimestamp;

        private readonly List<(long ts, double y)> _samples = new List<(long, double)>();

        public TouchDragScrollViewer()
        {
            Unloaded += (_, _) => StopFling();
        }

        protected override void OnScrollChanged(ScrollChangedEventArgs e)
        {
            base.OnScrollChanged(e);
            // 把可滚范围实时推给物理（目标钳进边界用，见 SnappyScrollPhysics.Update 注释）；
            // ScrollChanged 在 offset 与 extent 变化时都会触发，覆盖布局引起的 ScrollableHeight 变化
            if (Physics is SnappyScrollPhysics p) p.MaxOffset = ScrollableHeight;
        }

        protected override void OnTouchDown(TouchEventArgs e)
        {
            base.OnTouchDown(e);
            if (_device != null) return;
            // 起点在滚动条上的手势不跟踪 —— 滚动条的拖拽走它自己的晋升鼠标路径，
            // 我们抢过来会和 Thumb 拖拽冲突（方向映射还相反）。
            if (IsOnScrollBar(e.OriginalSource as DependencyObject)) return;

            StopFling();                 // 惯性滑行中重新按下 = 抓住内容
            _device = e.TouchDevice;
            _downPoint = _lastPoint = e.GetTouchPoint(this).Position;
            _samples.Clear();
            PushSample(_downPoint.Y);
        }

        protected override void OnTouchMove(TouchEventArgs e)
        {
            base.OnTouchMove(e);
            if (e.TouchDevice != _device) return;

            Point pos = e.GetTouchPoint(this).Position;

            if (!_dragging)
            {
                double dyTotal = pos.Y - _downPoint.Y;
                double dxTotal = pos.X - _downPoint.X;
                if (Math.Abs(dyTotal) < DragThresholdDip || Math.Abs(dyTotal) <= Math.Abs(dxTotal))
                    return;
                if (ScrollableHeight <= 0) return;  // 页面装得下就不抢手势，全部当 tap

                // 平衡 TouchDown 已晋升的 MouseLeftButtonDown（开关已 IsPressed/捕获鼠标；
                // 失捕获即解除按下态、不产生 Click），再捕获触摸点，之后的 move/up 全吞。
                Mouse.Capture(null);
                _device.Capture(this);
                _dragging = true;
                _lastPoint = pos;
                PushSample(pos.Y);
                e.Handled = true;
                Logger.Debug("TouchDragScrollViewer: drag start");
                return;
            }

            double dy = pos.Y - _lastPoint.Y;
            _lastPoint = pos;
            if (dy != 0)
            {
                PushSample(pos.Y);
                // 符号与库 OnManipulationDelta 一致：dy<0（手指上滑）→ 内容向上滚。
                AnimatedScrollToVerticalOffset(VerticalOffset - dy, usePreciseMode: true);
            }
            e.Handled = true;
        }

        protected override void OnTouchUp(TouchEventArgs e)
        {
            base.OnTouchUp(e);
            if (e.TouchDevice != _device) return;

            if (_dragging)
            {
                e.Handled = true;         // 掐断晋升，松手不补 Click
                _dragging = false;
                _device.Capture(null);
                StartFling(ComputeReleaseVelocity());
            }
            _device = null;
        }

        protected override void OnLostTouchCapture(TouchEventArgs e)
        {
            base.OnLostTouchCapture(e);
            // 捕获被外力夺走（窗口失焦等）：按松手收尾，不留半截手势
            if (e.TouchDevice == _device && _dragging)
            {
                _dragging = false;
                StartFling(ComputeReleaseVelocity());
            }
            if (e.TouchDevice == _device) _device = null;
        }

        private static bool IsOnScrollBar(DependencyObject src)
        {
            for (DependencyObject d = src; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is ScrollBar) return true;
                if (d is TouchDragScrollViewer) return false;  // 到自己为止
            }
            return false;
        }

        private void PushSample(double y)
        {
            long now = Stopwatch.GetTimestamp();
            _samples.Add((now, y));
            long minTs = now - (long)(VelocityWindowMs / 1000.0 * Stopwatch.Frequency);
            int cut = 0;
            while (cut < _samples.Count && _samples[cut].ts < minTs) cut++;
            if (cut > 0) _samples.RemoveRange(0, cut);
        }

        private double ComputeReleaseVelocity()
        {
            if (_samples.Count < 2) return 0;
            var first = _samples[0];
            var last = _samples[_samples.Count - 1];
            double dt = (last.ts - first.ts) / (double)Stopwatch.Frequency;
            return dt > 0.001 ? (last.y - first.y) / dt : 0;
        }

        private void StartFling(double velocity)
        {
            if (Math.Abs(velocity) < FlingMinVelocity) return;
            _flingVelocity = velocity;
            _flingLastTimestamp = Stopwatch.GetTimestamp();
            if (_flinging) return;
            _flinging = true;
            CompositionTarget.Rendering += OnFlingFrame;
            Logger.Debug($"TouchDragScrollViewer: fling v={velocity:F0} DIP/s");
        }

        private void OnFlingFrame(object sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _flingLastTimestamp) / (double)Stopwatch.Frequency;
            _flingLastTimestamp = now;
            if (dt > 0.1) dt = 0.1;   // GC/挂起后防跳变

            double dy = _flingVelocity * dt;
            _flingVelocity *= Math.Exp(-FlingDecayRate * dt);

            bool atEnd = _flingVelocity < 0 && (ScrollableHeight <= 0 || VerticalOffset >= ScrollableHeight - 0.5);
            bool atStart = _flingVelocity > 0 && VerticalOffset <= 0.5;
            if (Math.Abs(_flingVelocity) < FlingStopVelocity || atEnd || atStart)
            {
                StopFling();
                return;
            }
            if (dy != 0)
                AnimatedScrollToVerticalOffset(VerticalOffset - dy, usePreciseMode: true);
        }

        private void StopFling()
        {
            if (!_flinging) return;
            CompositionTarget.Rendering -= OnFlingFrame;
            _flinging = false;
        }
    }
}
