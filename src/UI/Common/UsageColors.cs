using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Green/orange/red brush for a 0–100 usage value. Shared by the CPU and RAM
    /// detail panels (and the taskbar could use it too).
    /// </summary>
    internal static class UsageColors
    {
        public static Brush ForPercent(double percent)
        {
            if (percent < 50) return new SolidColorBrush(Color.FromRgb(0x10, 0x89, 0x3E)); // green
            if (percent < 80) return new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C)); // orange
            return new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));                    // red
        }
    }
}
