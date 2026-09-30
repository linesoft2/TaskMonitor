using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace task_monitor
{
    /// <summary>
    /// The hover tooltip every hand-drawn chart shares (gotcha §18 — a real <see cref="Popup"/>,
    /// not a WPF ToolTip). The charts render in-proc and expose <c>HitTest</c>, so the host
    /// hit-tests on every mouse move and places the popup next to the cursor. It also keeps the
    /// hovered index and formatter, so a view's per-second <c>Refresh</c> can update the shown
    /// value without waiting for the mouse to move.
    /// </summary>
    internal sealed class ChartTipHost
    {
        private Popup _popup;
        private TextBlock _text;
        private Func<int, string> _format;
        private int _index = -1;

        /// <summary>
        /// Wires one chart to one popup. <paramref name="hitTest"/> is the chart's own
        /// <c>HitTest</c> (taken as a delegate so the host needs no shared chart interface);
        /// <paramref name="formatByIndex"/> renders the hovered point, "" meaning "no tip here".
        /// A view may attach several charts to the same host — that is what the previous
        /// per-view field set did, and the last hovered chart is the one <see cref="Refresh"/>
        /// keeps updating.
        /// </summary>
        public void Attach(FrameworkElement chart, Func<Point, int> hitTest, Popup popup,
                           TextBlock text, Func<int, string> formatByIndex)
        {
            var lastMouse = new Point();

            chart.MouseMove += (_, e) =>
            {
                lastMouse = e.GetPosition(chart);
                int index = hitTest(lastMouse);

                if (index < 0)
                {
                    popup.IsOpen = false;
                    _popup = null;
                    return;
                }

                _index = index;
                _format = formatByIndex;
                _text = text;
                _popup = popup;
                text.Text = formatByIndex(index);
                if (string.IsNullOrEmpty(text.Text))
                {
                    popup.IsOpen = false;
                    _popup = null;
                    return;
                }
                popup.HorizontalOffset = lastMouse.X + 14;
                popup.VerticalOffset = lastMouse.Y + 14;
                popup.IsOpen = true;
            };

            chart.MouseLeave += (_, _) =>
            {
                popup.IsOpen = false;
                _popup = null;
            };
        }

        /// <summary>Re-render the open tooltip for the currently hovered index — the view
        /// calls this once per tick so the value stays live under a still cursor.</summary>
        public void Refresh()
        {
            if (_popup is not null && _popup.IsOpen && _format is not null && _text is not null && _index >= 0)
                _text.Text = _format(_index);
        }
    }
}
