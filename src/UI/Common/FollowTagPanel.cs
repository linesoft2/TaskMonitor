using System;
using System.Windows;
using System.Windows.Controls;

namespace task_monitor
{
    /// <summary>
    /// Two-child panel for the process-list rows: child 0 = the (trimming) name TextBlock,
    /// child 1 = the tag chip (服务/服务组/×N — Collapsed for an ordinary row). The chip is
    /// measured FIRST and the name gets only what remains, so a long name ellipsizes while
    /// the chip stays fully visible; the chip is then arranged right after the name's
    /// DESIRED width, so it hugs a short name instead of floating at the cell's right edge.
    /// A Collapsed chip takes no space — untagged rows keep the full cell width (a fixed
    /// MaxWidth on the name would have trimmed them early, a regression in the narrow
    /// Disk/GPU/Net name columns).
    /// </summary>
    internal sealed class FollowTagPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var name = Children[0];
            var tag = Children.Count > 1 ? Children[1] : null;
            bool tagged = tag != null && tag.Visibility == Visibility.Visible;

            var tagSize = new Size();
            if (tagged)
            {
                tag.Measure(availableSize);
                tagSize = tag.DesiredSize;
            }
            double nameWidth = Math.Max(0, availableSize.Width - tagSize.Width);
            name.Measure(new Size(nameWidth, availableSize.Height));

            double w = name.DesiredSize.Width + tagSize.Width;
            if (w > availableSize.Width) w = availableSize.Width;
            return new Size(w, Math.Max(name.DesiredSize.Height, tagSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var name = Children[0];
            var tag = Children.Count > 1 ? Children[1] : null;
            bool tagged = tag != null && tag.Visibility == Visibility.Visible;

            var tagSize = tagged ? tag.DesiredSize : new Size();
            double nameWidth = Math.Max(0, finalSize.Width - tagSize.Width);
            name.Arrange(new Rect(0, CenterY(finalSize.Height, name.DesiredSize.Height),
                nameWidth, name.DesiredSize.Height));
            if (tagged)
            {
                // Hug the name's desired width (short text → right after it; trimmed text →
                // the cap), not the arranged slot.
                double x = Math.Min(name.DesiredSize.Width, nameWidth);
                tag.Arrange(new Rect(x, CenterY(finalSize.Height, tagSize.Height),
                    tagSize.Width, tagSize.Height));
            }
            return finalSize;
        }

        private static double CenterY(double outer, double inner) => inner >= outer ? 0 : (outer - inner) / 2;
    }
}
