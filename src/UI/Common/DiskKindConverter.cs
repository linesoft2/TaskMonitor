using System;
using System.Globalization;
using System.Windows.Data;

namespace task_monitor
{
    /// <summary>
    /// XAML bridge for the disk detail view: maps a <see cref="DiskKind"/> to its display
    /// string (SSD / HDD / USB / SD / SCM / 未知) in the per-disk tab.
    /// </summary>
    public sealed class DiskKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is DiskKind kind
                ? kind switch
                {
                    DiskKind.Ssd => "SSD",
                    DiskKind.Hdd => "HDD",
                    DiskKind.Scm => "SCM",
                    DiskKind.Usb => "USB",
                    DiskKind.Sd => "SD 卡",
                    _ => "未知",
                }
                : "未知";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
