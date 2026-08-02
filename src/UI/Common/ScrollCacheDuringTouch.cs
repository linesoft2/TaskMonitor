using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace task_monitor
{
    /// <summary>
    /// 触屏拖拽滚动期间，把滚动内容临时切到 <see cref="BitmapCache"/> 的附加行为。
    /// 原生 PanningMode 是布局驱动：手指每动一帧都 ScrollToVerticalOffset → 布局 +
    /// 整页重绘，Mica/亚克力 + 整页卡片在弱 GPU 触屏设备上必掉帧（"有点卡"）。
    /// 缓存后每帧只是位图位移 blit，渲染开销归零（与 SmoothScrollViewer 的
    /// RenderTransform 物理滚动也天然叠加：transform 滑动缓存位图）。
    /// 不能常驻缓存 —— hover/展开/切换动画会每帧整页重栅格化，反而更卡。所以是窗口式：
    ///   TouchDown 记起点 → TouchMove 越过拖拽阈值(8 DIP, ≈PanningMode 自身阈值)才上缓存
    ///   （轻点不付任何栅格化代价）→ TouchUp 后由 ScrollChanged 给惯性续命，
    ///   静默 600ms 判定滚动落定 → 恢复原 CacheMode。
    /// 触摸事件只观察不处理（不 Capture、不 e.Handled），tap→鼠标晋升不受影响。
    /// 注意订阅必须用 handledEventsToo —— TouchDragScrollViewer 的触屏拖拽会把
    /// move/up 标 handled（类处理器先于实例处理器执行），不加这个标志就拿不到事件，
    /// 缓存永不生效。
    /// </summary>
    public static class ScrollCacheDuringTouch
    {
        private const double DragThresholdDip = 8.0;
        private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(600);

        private static readonly EventHandler<TouchEventArgs> s_touchMove = OnTouchMove;
        private static readonly EventHandler<TouchEventArgs> s_touchUp = OnTouchUp;

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollCacheDuringTouch),
                new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);
        public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached("State", typeof(PanCacheState), typeof(ScrollCacheDuringTouch));

        private sealed class PanCacheState
        {
            public ScrollViewer Viewer;
            public UIElement Content;      // 被缓存的滚动内容（ScrollViewer 的单个子元素）
            public CacheMode SavedCache;   // 上缓存前的 CacheMode（一般为 null）
            public Point DownPoint;        // TouchDown 位置（相对 Viewer）
            public bool Tracking;          // TouchDown~TouchUp 之间
            public bool Settling;          // TouchUp 之后、惯性落定之前
            public DispatcherTimer SettleTimer;
        }

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;

            if ((bool)e.NewValue)
            {
                var st = new PanCacheState { Viewer = sv };
                st.SettleTimer = new DispatcherTimer { Interval = SettleQuiet };
                st.SettleTimer.Tick += (_, _) =>
                {
                    st.SettleTimer.Stop();
                    RestoreCache(st);
                };
                sv.SetValue(StateProperty, st);
                sv.TouchDown += OnTouchDown;
                // handledEventsToo: TouchDragScrollViewer 拖拽时把 move/up 标 handled，
                // 类处理器先于实例处理器 —— 不加就拿不到事件，缓存永不生效
                sv.AddHandler(UIElement.TouchMoveEvent, s_touchMove, handledEventsToo: true);
                sv.AddHandler(UIElement.TouchUpEvent, s_touchUp, handledEventsToo: true);
                sv.LostTouchCapture += OnTouchUp; // 捕获结束后松手走这里，等价收尾
                sv.ScrollChanged += OnScrollChanged;
            }
            else
            {
                sv.TouchDown -= OnTouchDown;
                sv.RemoveHandler(UIElement.TouchMoveEvent, s_touchMove);
                sv.RemoveHandler(UIElement.TouchUpEvent, s_touchUp);
                sv.LostTouchCapture -= OnTouchUp;
                sv.ScrollChanged -= OnScrollChanged;
                if (sv.GetValue(StateProperty) is PanCacheState st)
                {
                    st.SettleTimer.Stop();
                    RestoreCache(st);
                    sv.ClearValue(StateProperty);
                }
            }
        }

        private static void OnTouchDown(object sender, TouchEventArgs e)
        {
            if (((ScrollViewer)sender).GetValue(StateProperty) is not PanCacheState st) return;
            st.SettleTimer.Stop();
            st.Settling = false;
            st.Tracking = true;
            st.DownPoint = e.GetTouchPoint(st.Viewer).Position;
        }

        private static void OnTouchMove(object sender, TouchEventArgs e)
        {
            if (((ScrollViewer)sender).GetValue(StateProperty) is not PanCacheState st) return;
            if (!st.Tracking || st.Content != null) return; // 已上缓存或非本手势

            Point pos = e.GetTouchPoint(st.Viewer).Position;
            if (Math.Abs(pos.Y - st.DownPoint.Y) < DragThresholdDip &&
                Math.Abs(pos.X - st.DownPoint.X) < DragThresholdDip) return;

            if (st.Viewer.Content is UIElement content)
            {
                st.Content = content;
                st.SavedCache = content.CacheMode;
                content.CacheMode = new BitmapCache();
                // 一次性诊断：触屏拖拽卡顿时确认缓存真的生效（手势级状态变化，非每帧路径）
                Logger.Debug($"ScrollCacheDuringTouch: cache engaged on {st.Viewer.GetType().Name}");
            }
        }

        private static void OnTouchUp(object sender, RoutedEventArgs e)
        {
            if (((ScrollViewer)sender).GetValue(StateProperty) is not PanCacheState st) return;
            st.Tracking = false;
            if (st.Content == null) return; // 轻点：没上过缓存
            st.Settling = true;
            st.SettleTimer.Stop();
            st.SettleTimer.Start();
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (((ScrollViewer)sender).GetValue(StateProperty) is not PanCacheState st) return;
            if (!st.Settling) return;
            st.SettleTimer.Stop();  // 惯性还在滚 → 续命，等静默
            st.SettleTimer.Start();
        }

        private static void RestoreCache(PanCacheState st)
        {
            st.Settling = false;
            if (st.Content == null) return;
            st.Content.CacheMode = st.SavedCache;
            st.Content = null;
        }
    }
}
