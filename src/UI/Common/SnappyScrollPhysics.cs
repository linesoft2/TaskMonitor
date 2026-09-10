using System;
using FluentWpfCore.ScrollPhysics;

namespace task_monitor
{
    /// <summary>
    /// 库默认手感的滚动物理，替换 FluentWpfCore 1.0.5 的 DefaultScrollPhysics。
    /// 同为"目标距离指数趋近"模型，速率常量与库默认严格等价（推导见下方常量注释）。
    /// 与库的两处分歧全是 bugfix，不改手感：
    /// 1) 稳定态：库的 <see cref="IScrollPhysics.IsStable"/> 在精确模式下永假 —— 渲染
    ///    循环退不出、页面卡死，症状与失效权衡见 <see cref="TouchDragScrollViewer"/>
    ///    的类注释（那边是受害方，单一起源）。这里的修法：精确模式改为
    ///    "剩余 &lt; StopDistance 且输入静默 &gt; PreciseIdleSeconds"才 stable ——
    ///    流式 delta 持续到达时不会在帧间误停（误停 = StopRendering 里
    ///    ScrollToVerticalOffset + transform 复位的顿挫 + hit-test 闪烁），
    ///    手势/惯性结束后静默窗口到期即恢复 hit-test。
    /// 2) 越界累积：见 <see cref="Update"/> 的钳制注释。
    /// </summary>
    public class SnappyScrollPhysics : IScrollPhysics
    {
        // 与库 1.0.5 默认手感等价的趋近速率：k = -144·ln(friction)（两模型同为指数趋近，数学上等价）。
        // 精确模式 = PreciseModeFriction 0.88 → k≈18.4/s；滚轮 = Smoothness 0.78 → friction 0.9358 → k≈9.55/s。
        // （用户反馈过"不跟手"试过加快，最终要求回默认 —— 与库的分歧只剩下方的稳定态修复。）
        private const double PreciseApproachRate = 18.4; // 1/s
        private const double WheelApproachRate = 9.55;   // 1/s
        private const double StopDistance = 0.5;         // px
        private const double PreciseIdleSeconds = 0.08;  // 精确模式静默收尾窗口

        private double _remaining;   // 距累计目标的剩余距离（符号 = 方向）
        private double _idleSeconds; // 精确模式下"剩余已归零"的持续时长
        private bool _isStable = true;

        public bool IsStable => _isStable;

        public bool IsPreciseMode { get; set; }

        /// <summary>
        /// 可滚动的最大 offset（宿主 ScrollViewer 的 ScrollableHeight，由
        /// TouchDragScrollViewer.OnScrollChanged 推送；水平轴的克隆体拿不到，保持默认值）。
        /// 默认不设限（等效旧行为）。
        /// </summary>
        public double MaxOffset { get; set; } = double.MaxValue;

        public void OnScroll(double delta)
        {
            _isStable = false;
            _idleSeconds = 0;
            _remaining -= delta;
        }

        public void Reset()
        {
            _remaining = 0;
            _idleSeconds = 0;
            _isStable = true;
        }

        public double Update(double currentOffset, double dt)
        {
            if (_isStable) return currentOffset;

            // 目标钳进 [0, MaxOffset]：越界累积的剩余距离当场丢弃 —— 否则滚到底/顶后
            // 继续滚，视觉被宿主 clamp 住了但 _remaining 还在界外越堆越多，往回滚要先
            // "还清"整段页面才动（滚轮/触控板同感，2026-08-02 反馈；库默认物理同病）。
            // currentOffset 是宿主已 clamp 的视觉 offset，每帧都会回传，钳制即时生效。
            // MaxOffset 由 TouchDragScrollViewer.OnScrollChanged 推送 ScrollableHeight。
            double target = currentOffset + _remaining;
            if (target < 0)
                _remaining = -currentOffset;
            else if (target > MaxOffset)
                _remaining = MaxOffset - currentOffset;

            double displacement;
            if (Math.Abs(_remaining) < StopDistance)
            {
                // 收尾：一次性走完亚像素残余，精确落到目标
                displacement = _remaining;
                _remaining = 0;
            }
            else
            {
                double rate = IsPreciseMode ? PreciseApproachRate : WheelApproachRate;
                displacement = _remaining * (1.0 - Math.Exp(-rate * dt));
                _remaining -= displacement;
            }

            if (_remaining == 0)
            {
                if (IsPreciseMode)
                {
                    _idleSeconds += dt;
                    if (_idleSeconds >= PreciseIdleSeconds) _isStable = true;
                }
                else
                {
                    _isStable = true;
                }
            }

            return currentOffset + displacement;
        }
    }
}
