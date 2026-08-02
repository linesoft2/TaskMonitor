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
    /// GPU detail panel: a header with the live headline utilization (per the 显示方式
    /// setting — MAX across adapters by default, Task Manager's sidebar rule), a 60s
    /// history area chart, one pivot tab per GPU
    /// adapter (name, utilization, temperature, dominant engine, dedicated/shared VRAM —
    /// Task Manager's own metrics via DXCore, see <see cref="GpuSampler"/>), and a
    /// per-process list of each process's GPU utilization with its dominant engine (Task
    /// Manager's Processes-page GPU column, see <see cref="ProcessGpuSampler"/>).
    /// Self-contained — owns its chart theming, the hover tooltips, and the icon cache.
    /// Created fresh per popup open; refreshed each second by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class GpuDetailView : UserControl, IDetailView
    {
        // Per-exe-path icon cache (frozen BitmapSources). Refresh runs every second on the
        // UI thread; after the first tick every path is a cache hit, so icon extraction
        // (a shell call) only happens once per distinct exe while the popup is open.
        private readonly Dictionary<string, ImageSource> _iconByPath =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private ImageSource _defaultIcon;

        // Open-tooltip state, so the per-second Refresh can update the shown value
        // without waiting for the mouse to move (which is what re-fires MouseMove).
        private Popup _tipPopup;
        private TextBlock _tipText;
        private int _tipIndex = -1;
        private Func<int, string> _tipFormat;

        // Current sampling cadence (from each snapshot) — the "N 秒前" history tooltip
        // scales its tick offset by it (settings 采样间隔).
        private int _intervalMs = 1000;

        // Header slot the shell parks its pin toggle in (IDetailView).
        public ContentControl PinSlot => PinSlotHost;

        public GpuDetailView(bool dark)
        {
            InitializeComponent();

            // The pivot style's invisible Previous scroll button covers the first tab's
            // left 20px and swallows clicks — make it click-through (see helper).
            PivotNavButtonFix.Apply(GpuTabs);

            ApplyTheme(dark);

            AttachTip(GpuHistoryChart, HistoryTip, HistoryTipText, i =>
            {
                var values = GpuHistoryChart.Values;
                if (i < 0 || values == null || i >= values.Count) return "";
                var v = values[i];
                return v is null ? "" : $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {v:F0}%";
            });

            ProcessListTip.Attach(GpuProcessList, ProcessTip, ProcessTipDesc, ProcessTipPath);
        }

        void IDetailView.Refresh(SystemSnapshot s)
        {
            _intervalMs = s.SampleIntervalMs;   // the history tooltip scales tick offsets by it
            GpuHeaderPercentText.Text = s.GpuAvailable ? $"{s.GpuPercent:F0}%" : "--";

            // 60-tick history, null-padded during warm-up (same shape as CPU/RAM history).
            const int HistoryCapacity = 60;
            int histCount = s.GpuHistory?.Length ?? 0;
            var historyValues = new double?[HistoryCapacity];
            for (int i = 0; i < HistoryCapacity; i++)
                historyValues[i] = i < HistoryCapacity - histCount ? (double?)null : s.GpuHistory[i - (HistoryCapacity - histCount)];
            GpuHistoryChart.Values = historyValues;

            // Keep the open tooltip in lockstep with this per-second refresh.
            if (_tipPopup is not null && _tipPopup.IsOpen && _tipFormat is not null && _tipText is not null && _tipIndex >= 0)
                _tipText.Text = _tipFormat(_tipIndex);

            // No adapters at all: swap the tabs for the placeholder (the chart above still
            // renders the flat zero history, same as an idle GPU).
            NoGpuPlaceholder.Visibility = s.GpuAvailable ? Visibility.Collapsed : Visibility.Visible;
            GpuTabs.Visibility = s.GpuAvailable ? Visibility.Visible : Visibility.Collapsed;

            // Per-adapter tabs: the GpuInfo objects are long-lived and update themselves via
            // INotifyPropertyChanged, so ItemsSource is only (re)set when the adapter SET
            // changes — the selected tab and its content survive every per-second tick.
            if (TabsNeedRebind(s.Gpus))
            {
                int sel = GpuTabs.SelectedIndex;
                GpuTabs.ItemsSource = s.Gpus;
                GpuTabs.SelectedIndex = sel >= 0 && sel < s.Gpus.Count ? sel : 0;
            }

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopGpuProcesses != null)
            {
                foreach (var p in s.TopGpuProcesses)
                    p.Icon = ResolveIcon(p.ExePath);
                GpuProcessList.ItemsSource = s.TopGpuProcesses;
            }
        }

        // True when the published adapter list no longer matches what the tabs show (first
        // bind, or an adapter added/removed). Item identity comparison: same objects = same set.
        private bool TabsNeedRebind(List<GpuInfo> gpus)
        {
            if (gpus == null) return false;
            if (GpuTabs.ItemsSource is not List<GpuInfo> cur || cur.Count != gpus.Count) return true;
            for (int i = 0; i < cur.Count; i++)
                if (!ReferenceEquals(cur[i], gpus[i])) return true;
            return false;
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

        // ---------- Custom WPF tooltip (same pattern as the CPU/RAM charts) ----------
        private void AttachTip(UsageHistoryChart chart, Popup popup, TextBlock text, Func<int, string> formatByIndex)
        {
            // The chart renders in-proc; we hit-test on every mouse move and place the
            // popup next to the cursor. Keep the hovered index/format so Refresh() can
            // update the tooltip text each second without waiting for the mouse to move.
            var lastMouse = new Point();
            chart.MouseMove += (_, e) =>
            {
                lastMouse = e.GetPosition(chart);
                int index = chart.HitTest(lastMouse);

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

        // ---------- Theming: tooltip surfaces + paint the chart with the iNKORE accent ----------
        public void ApplyTheme(bool dark)
        {
            HistoryTipBorder.Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

            ApplyChartTheme();
        }

        private void ApplyChartTheme()
        {
            GpuHistoryChart.AccentColor = GetAccentColor();
            GpuHistoryChart.GridColor = GetGridColor();
            GpuHistoryChart.FillOpacity = 48.0 / 255.0;
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
    }
}
