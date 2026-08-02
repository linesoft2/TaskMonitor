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
    /// RAM detail panel: overall usage header, 60s history chart, a 已用/可用/总量
    /// footer, and a per-process memory list (private working set). Self-contained — owns
    /// its chart theming and the hover tooltip. Created fresh per popup open; refreshed
    /// each second by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class RamDetailView : UserControl, IDetailView
    {
        // Open-tooltip state, so the per-second Refresh can update the shown value
        // without waiting for the mouse to move (which is what re-fires MouseMove).
        private Popup _tipPopup;
        private TextBlock _tipText;
        private int _tipIndex = -1;
        private Func<int, string> _tipFormat;

        // Per-exe-path icon cache (frozen BitmapSources). Refresh runs every second on the
        // UI thread; after the first tick every path is a cache hit, so icon extraction
        // (a shell call) only happens once per distinct exe while the popup is open.
        private readonly Dictionary<string, ImageSource> _iconByPath =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private ImageSource _defaultIcon;

        // Hovered composition-bar segment, so the per-second Refresh can update the open
        // tooltip's value without waiting for the mouse to move.
        private int _compositionTipIndex = -1;

        // Current sampling cadence (from each snapshot) — the "N 秒前" history tooltip
        // scales its tick offset by it (settings 采样间隔).
        private int _intervalMs = 1000;

        // Header slot the shell parks its pin toggle in (IDetailView).
        public ContentControl PinSlot => PinSlotHost;

        public RamDetailView(bool dark)
        {
            InitializeComponent();

            ApplyTheme(dark);

            AttachTip(RamHistoryChart, HistoryTip, HistoryTipText, i =>
            {
                var values = RamHistoryChart.Values;
                if (i < 0 || values == null || i >= values.Count) return "";
                var v = values[i];
                return v is null ? "" : $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {v:F0}%";
            });

            AttachCompositionTip();

            ProcessListTip.Attach(ProcessList, ProcessTip, ProcessTipDesc, ProcessTipPath);
        }

        void IDetailView.Refresh(SystemSnapshot s)
        {
            _intervalMs = s.SampleIntervalMs;   // the history tooltip scales tick offsets by it
            RamHeaderPercentText.Text = $"{s.RamPercent:F0}%";

            // Task Manager memory breakdown. The values are set on named Runs in code
            // because net48's Run.Text is NOT a dependency property (that arrived in
            // .NET Core 3.0) — a {Binding} on a Run throws XamlParseException in
            // InitializeComponent and kills the process. In DataTemplates, where a
            // named Run isn't reachable, use two TextBlocks (label + value) instead.
            var m = s.MemoryDetail;
            InUseValueRun.Text = m.CompressedBytes > 0
                ? $"{MemorySizeFormatter.Format(m.InUseBytes)} ({MemorySizeFormatter.Format(m.CompressedBytes)})"
                : MemorySizeFormatter.Format(m.InUseBytes);
            CommittedValueRun.Text = $"{MemorySizeFormatter.Format(m.CommittedBytes)} / {MemorySizeFormatter.Format(m.CommitLimitBytes)}";
            AvailableValueRun.Text = MemorySizeFormatter.Format(m.AvailableBytes);
            // Task Manager "Cached" = standby + modified (m.CachedBytes is pure standby, used
            // for the bar's 备用; the breakdown adds modified).
            CachedValueRun.Text = MemorySizeFormatter.Format(m.CachedBytes + m.ModifiedBytes);
            PagedPoolValueRun.Text = MemorySizeFormatter.Format(m.PagedPoolBytes);
            NonPagedPoolValueRun.Text = MemorySizeFormatter.Format(m.NonPagedPoolBytes);

            // Memory composition bar (使用中 | 已修改 | 备用 | 可用). 使用中 here is the bar's
            // in-use segment = breakdown InUse (which includes modified) minus modified; the
            // four always sum to total physical. 可用 = available − standby.
            CompositionBar.Values = new long[]
            {
                Math.Max(0, m.InUseBytes - m.ModifiedBytes),
                m.ModifiedBytes,
                m.CachedBytes,
                Math.Max(0, m.AvailableBytes - m.CachedBytes),
            };
            if (CompositionTip.IsOpen && _compositionTipIndex >= 0)
                UpdateCompositionTip(_compositionTipIndex);

            const int HistoryCapacity = 60;
            int histCount = s.RamHistory?.Length ?? 0;
            var historyValues = new double?[HistoryCapacity];
            for (int i = 0; i < HistoryCapacity; i++)
                historyValues[i] = i < HistoryCapacity - histCount ? (double?)null : s.RamHistory[i - (HistoryCapacity - histCount)];
            RamHistoryChart.Values = historyValues;

            // Keep the open tooltip in lockstep with this per-second refresh.
            if (_tipPopup is not null && _tipPopup.IsOpen && _tipFormat is not null && _tipText is not null && _tipIndex >= 0)
                _tipText.Text = _tipFormat(_tipIndex);

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopMemoryProcesses != null)
            {
                foreach (var p in s.TopMemoryProcesses)
                    p.Icon = ResolveIcon(p.ExePath);
                ProcessList.ItemsSource = s.TopMemoryProcesses;
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

        // ---------- Theming: tooltip surfaces + chart + composition bar ----------
        public void ApplyTheme(bool dark)
        {
            HistoryTipBorder.Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

            ApplyChartTheme();
            ApplyCompositionTheme();
        }

        private void ApplyChartTheme()
        {
            RamHistoryChart.AccentColor = GetAccentColor();
            RamHistoryChart.GridColor = GetGridColor();
            RamHistoryChart.FillOpacity = 48.0 / 255.0;
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

        // ---------- Composition bar: theming + per-segment tooltip ----------
        private void ApplyCompositionTheme()
        {
            var accent = GetAccentColor();
            CompositionBar.InUseColor = accent;
            // 备用 = near-blank: a very faint accent tint so it barely reads above the 可用 track.
            CompositionBar.StandbyColor = Color.FromArgb(0x1A, accent.R, accent.G, accent.B);
            var baseLow = (Color?)Application.Current.TryFindResource("SystemBaseLowColor")
                          ?? Color.FromArgb(0x33, 0x00, 0x00, 0x00);
            CompositionBar.TrackColor = Color.FromArgb(0x08, baseLow.R, baseLow.G, baseLow.B);
            CompositionBar.FrameColor = Color.FromArgb(0x22, baseLow.R, baseLow.G, baseLow.B);
        }

        private static readonly string[] CompositionLabels = { "使用中", "已修改", "备用", "可用" };

        private void AttachCompositionTip()
        {
            CompositionBar.MouseMove += (_, e) =>
            {
                var p = e.GetPosition(CompositionBar);
                int idx = CompositionBar.HitTest(p);
                _compositionTipIndex = idx;
                if (idx < 0) { CompositionTip.IsOpen = false; return; }
                UpdateCompositionTip(idx);
                CompositionTip.HorizontalOffset = p.X + 14;
                CompositionTip.VerticalOffset = p.Y + 14;
                CompositionTip.IsOpen = true;
            };
            CompositionBar.MouseLeave += (_, _) =>
            {
                CompositionTip.IsOpen = false;
                _compositionTipIndex = -1;
            };
        }

        private void UpdateCompositionTip(int idx)
        {
            var values = CompositionBar.Values;
            long bytes = (values != null && idx >= 0 && idx < values.Count) ? values[idx] : 0;
            string label = (idx >= 0 && idx < CompositionLabels.Length) ? CompositionLabels[idx] : "";
            CompositionTipText.Text = $"{label} · {MemorySizeFormatter.Format(bytes)}";
        }
    }
}
