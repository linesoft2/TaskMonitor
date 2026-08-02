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
