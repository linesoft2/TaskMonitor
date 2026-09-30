using System;
using System.Globalization;
using System.Windows.Data;

namespace task_monitor
{
    /// <summary>
    /// Formats a bytes/second rate into a compact human string with an adaptive unit:
    /// B/s below 1 KB, KB/s up to 1 MB, MB/s above. Thresholds are binary (1024), matching
    /// TrafficMonitor. Shared by the taskbar overlay (D2D text) and the detail popup (WPF).
    /// </summary>
    internal static class NetRateFormatter
    {
        private const double KB = 1024.0;
        private const double MB = 1024.0 * 1024.0;

        /// <summary>e.g. 0 → "0 B/s", 51200 → "50.0 KB/s", 12_000_000 → "11.4 MB/s".</summary>
        public static string Format(long bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;
            double bps = bytesPerSec;
            if (bps >= MB) return $"{bps / MB:F1} MB/s";
            if (bps >= KB) return $"{bps / KB:F1} KB/s";
            return $"{bps:F0} B/s";
        }

        /// <summary>
        /// The narrow-strip form of <see cref="Format"/> for a SIDE-docked taskbar: no space
        /// and no "/s" — such a strip is only ~46 DIP wide (a 48-DIP side bar minus its
        /// margin), where "11.4 MB/s" (~56 DIP) cannot fit, while "11.4M" (37 DIP) does.
        /// The unit letter carries the scale (K = KB/s, M = MB/s, B = B/s); the detail popup
        /// keeps the full form. One decimal below 100M, none above (4 glyphs max).
        /// </summary>
        public static string FormatCompact(long bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;
            double bps = bytesPerSec;
            if (bps >= 100 * MB) return $"{bps / MB:F0}M";
            if (bps >= MB) return $"{bps / MB:F1}M";
            if (bps >= KB) return $"{bps / KB:F0}K";
            return $"{bps:F0}B";
        }
    }

    /// <summary>
    /// XAML bridge for <see cref="NetRateFormatter"/>: binds a process's per-second
    /// up/down byte rate to its display string in the Network detail list template.
    /// </summary>
    public sealed class NetRateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => NetRateFormatter.Format(value is long l ? l : System.Convert.ToInt64(value, culture));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
