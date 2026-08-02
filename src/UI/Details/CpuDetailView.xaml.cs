using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace task_monitor
{
    /// <summary>
    /// CPU detail panel: overall usage header, 60s history chart, per-core bars,
    /// and a system-summary footer (live speed / processes / threads / handles / uptime).
    /// Self-contained — owns its chart theming and the hover tooltips. Created fresh
    /// per popup open; refreshed each second by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class CpuDetailView : UserControl, IDetailView
    {
        // Open-tooltip state, so the per-second Refresh can update the shown value
        // without waiting for the mouse to move (which is what re-fires HoveredPointsChanged).
        private Popup _tipPopup;
        private TextBlock _tipText;
        private int _tipIndex = -1;
        private Func<int, string> _tipFormat;

        // Current sampling cadence (from each snapshot) — the "N 秒前" history tooltip
        // scales its tick offset by it (settings 采样间隔).
        private int _intervalMs = 1000;

        // Per-exe-path icon cache (frozen BitmapSources). Refresh runs every second on the
        // UI thread; after the first tick every path is a cache hit, so icon extraction
        // (a shell call) only happens once per distinct exe while the popup is open.
        private readonly Dictionary<string, ImageSource> _iconByPath =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private ImageSource _defaultIcon;

        // Header slot the shell parks its pin toggle in (IDetailView).
        public ContentControl PinSlot => PinSlotHost;

        public CpuDetailView(bool dark)
        {
            InitializeComponent();

            ApplyTheme(dark);

            AttachTip(CpuHistoryChart, HistoryTip, HistoryTipText, i =>
            {
                var values = CpuHistoryChart.Values;
                if (i < 0 || values == null || i >= values.Count) return "";
                var v = values[i];
                return v is null ? "" : $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {v:F0}%";
            });
            AttachTip(CpuCoresChart, CoresTip, CoresTipText, i =>
            {
                var values = CpuCoresChart.Values;
                if (i < 0 || values == null || i >= values.Count) return "";
                return $"CPU {i} · {values[i]:F0}%";
            });

            ProcessListTip.Attach(ProcessList, ProcessTip, ProcessTipDesc, ProcessTipPath);
        }

        void IDetailView.Refresh(SystemSnapshot s)
        {
            _intervalMs = s.SampleIntervalMs;   // the history tooltip scales tick offsets by it
            CpuHeaderPercentText.Text = $"{s.CpuPercent:F0}%";

            // System summary footer (live speed / process / thread / handle / uptime).
            SpeedValueRun.Text = s.CpuCurrentMhz > 0 ? $"{s.CpuCurrentMhz / 1000.0:F2} GHz" : "—";
            ProcessValueRun.Text = s.ProcessCount > 0 ? s.ProcessCount.ToString("N0") : "—";
            ThreadValueRun.Text = s.ThreadCount > 0 ? s.ThreadCount.ToString("N0") : "—";
            HandleValueRun.Text = s.HandleCount > 0 ? s.HandleCount.ToString("N0") : "—";
            UptimeValueRun.Text = FormatUptime(s.UptimeMs);

            const int HistoryCapacity = 60;
            int histCount = s.CpuHistory?.Length ?? 0;
            var historyValues = new double?[HistoryCapacity];
            for (int i = 0; i < HistoryCapacity; i++)
                historyValues[i] = i < HistoryCapacity - histCount ? (double?)null : s.CpuHistory[i - (HistoryCapacity - histCount)];
            CpuHistoryChart.Values = historyValues;

            if (s.PerCoreUsage != null && s.PerCoreUsage.Length > 0)
            {
                PerCoreCard.Visibility = Visibility.Visible;
                CpuCoresChart.Values = s.PerCoreUsage;
            }
            else
            {
                PerCoreCard.Visibility = Visibility.Collapsed;
            }

            // Keep the open tooltip in lockstep with this per-second refresh.
            if (_tipPopup is not null && _tipPopup.IsOpen && _tipFormat is not null && _tipText is not null && _tipIndex >= 0)
                _tipText.Text = _tipFormat(_tipIndex);

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopProcesses != null)
            {
                foreach (var p in s.TopProcesses)
                    p.Icon = ResolveIcon(p.ExePath);
                ProcessList.ItemsSource = s.TopProcesses;
            }
        }

        // ---------- process icons ----------
        // Source size for the cached icon bitmaps. The list slot renders at 16×16 logical
        // pixels, so 48 source pixels covers up to ~3× DPI as a clean downscale (and the
        // shell pulls the jumbo-capable icon variant modern exes ship, not the 16/32px one
        // Icon.ExtractAssociatedIcon is capped at — that one reads blurry on high-DPI).
        private const int IconPixelSize = 48;

        // Resolve an exe's icon from its full path, caching the frozen ImageSource. Paths we
        // can't open an icon for are cached as the default icon so we don't retry every tick.
        private ImageSource ResolveIcon(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return DefaultIcon;
            if (_iconByPath.TryGetValue(exePath, out ImageSource cached)) return cached;

            ImageSource src = TryExtractIcon(exePath) ?? DefaultIcon;
            _iconByPath[exePath] = src;
            return src;
        }

        // High-resolution path: IShellItemImageFactory → HBITMAP → BitmapSource. Returns null
        // when the shell can't produce a bitmap for the path; throws on a mid-conversion
        // failure (caller catches and falls back to the legacy icon).
        private static BitmapSource ExtractHighRes(string path)
        {
            IntPtr hbmp = ShellInterop.GetIconBitmap(path, IconPixelSize);
            if (hbmp == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); // cross-thread safe; the list is produced on the taskbar thread
                return src;
            }
            finally { ShellInterop.DeleteObject(hbmp); }
        }

        private static ImageSource TryExtractIcon(string path)
        {
            // Prefer the high-resolution shell image (IShellItemImageFactory — what Task
            // Manager uses). Any failure here falls through to the proven legacy path below,
            // so the popup never crashes on a single bad icon.
            try
            {
                var hi = ExtractHighRes(path);
                if (hi != null) return hi;
            }
            catch { /* fall through to the legacy path below */ }

            // Fallback: the legacy associated icon (≤32px) when the modern API can't resolve
            // the path (UNC, some packaged apps, missing/inaccessible image).
            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(IconPixelSize, IconPixelSize));
                    src.Freeze();
                    return src;
                }
            }
            catch
            {
                return null; // missing/inaccessible image — fall back to the default icon
            }
        }

        private ImageSource DefaultIcon
        {
            get
            {
                if (_defaultIcon == null)
                {
                    try
                    {
                        // SystemIcons.Application is a shared system icon — do not dispose it.
                        var sysIcon = System.Drawing.SystemIcons.Application;
                        _defaultIcon = Imaging.CreateBitmapSourceFromHIcon(
                            sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(IconPixelSize, IconPixelSize));
                        _defaultIcon.Freeze();
                    }
                    catch { /* keep null; rows with no icon just show a blank slot */ }
                }
                return _defaultIcon;
            }
        }

        // ---------- Custom WPF tooltip (replaces LiveCharts' built-in Skia tooltip) ----------
        private void AttachTip(FrameworkElement chart, Popup popup, TextBlock text, Func<int, string> formatByIndex)
        {
            // The chart controls render in-proc; we hit-test on every mouse move and place
            // the popup next to the cursor. Keep the hovered index/format so Refresh() can
            // update the tooltip text each second without waiting for the mouse to move.
            var lastMouse = new Point();
            chart.MouseMove += (_, e) =>
            {
                lastMouse = e.GetPosition(chart);
                int index = chart switch
                {
                    UsageHistoryChart h => h.HitTest(lastMouse),
                    CpuCoresChart c => c.HitTest(lastMouse),
                    _ => -1,
                };

                if (index < 0)
                {
                    popup.IsOpen = false;
                    _tipPopup = null;
                    return;
                }

                _tipIndex = index;
                _tipFormat = formatByIndex;
                _tipText = text;
                _tipPopup = popup;
                text.Text = formatByIndex(index);
                if (string.IsNullOrEmpty(text.Text))
                {
                    popup.IsOpen = false;
                    _tipPopup = null;
                    return;
                }
                popup.HorizontalOffset = lastMouse.X + 14;
                popup.VerticalOffset = lastMouse.Y + 14;
                popup.IsOpen = true;
            };

            chart.MouseLeave += (_, _) =>
            {
                popup.IsOpen = false;
                _tipPopup = null;
            };
        }

        // ---------- Uptime formatting (ms → "d天 hh:mm:ss" / "h:mm:ss") ----------
        private static string FormatUptime(long ms)
        {
            if (ms < 0) ms = 0;
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.Days >= 1)
                return $"{ts.Days}天 {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        // ---------- Theming: tooltip surfaces + paint the charts with the iNKORE accent ----------
        public void ApplyTheme(bool dark)
        {
            var tipBackground = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            HistoryTipBorder.Background = tipBackground;
            CoresTipBorder.Background = tipBackground;

            ApplyChartTheme();
        }

        private void ApplyChartTheme()
        {
            CpuHistoryChart.AccentColor = GetAccentColor();
            CpuHistoryChart.GridColor = GetGridColor();
            CpuHistoryChart.FillOpacity = 48.0 / 255.0;

            CpuCoresChart.AccentColor = GetAccentColor();
            CpuCoresChart.TrackColor = GetTrackColor();
            // Same accent hue as the line chart's fill, but denser (~50%) so the bars stay
            // readable as a usage indicator; the area fill stays at the fainter 48/255.
            CpuCoresChart.FillOpacity = 128.0 / 255.0;
        }

        private static Color GetAccentColor()
            => (Color?)Application.Current.TryFindResource("SystemAccentColor") ?? Color.FromRgb(0x00, 0x78, 0xD4);

        private static Color GetGridColor()
        {
            // SystemBaseLowColor adapts to the theme; its native ~20% alpha reads too heavy,
            // so we drop it to a faint guide line.
            var color = (Color?)Application.Current.TryFindResource("SystemBaseLowColor")
                        ?? Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            return Color.FromArgb(0x1A, color.R, color.G, color.B);
        }

        private static Color GetTrackColor()
        {
            // A faint, full-height slot behind each bar so individual cores stay distinguishable.
            var color = (Color?)Application.Current.TryFindResource("SystemBaseLowColor")
                        ?? Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            return Color.FromArgb(0x21, color.R, color.G, color.B);
        }
    }
}
