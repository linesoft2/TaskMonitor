using System;
using System.Globalization;
using System.Windows.Data;

namespace task_monitor
{
    /// <summary>
    /// Formats a byte count into a compact human string with an adaptive unit
    /// (B / KB / MB / GB), binary thresholds (1024), matching how Task Manager's
    /// Memory column reads. Used by the RAM detail's per-process list.
    /// </summary>
    internal static class MemorySizeFormatter
    {
        private const double KB = 1024.0;
        private const double MB = 1024.0 * 1024.0;
        private const double GB = 1024.0 * 1024.0 * 1024.0;

        /// <summary>e.g. 0 → "0 B", 51200 → "50.0 KB", 12_000_000 → "11.4 MB", 3_221_000_000 → "3.0 GB".</summary>
        public static string Format(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= GB) return $"{bytes / GB:F1} GB";
            if (bytes >= MB) return $"{bytes / MB:F1} MB";
            if (bytes >= KB) return $"{bytes / KB:F1} KB";
            return $"{bytes} B";
        }
    }

    /// <summary>
    /// XAML bridge for <see cref="MemorySizeFormatter"/>: binds a process's working-set
    /// byte count to its display string in the RAM detail list template.
    /// </summary>
    public sealed class BytesToSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => MemorySizeFormatter.Format(value is long l ? l : System.Convert.ToInt64(value, culture));

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
