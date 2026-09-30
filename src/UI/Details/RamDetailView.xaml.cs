using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// RAM detail panel: overall usage header, 60s history chart, a 已用/可用/总量
    /// footer, and a per-process memory list (private working set). Self-contained — owns
    /// its chart theming and the hover tooltip. Created fresh per popup open; refreshed
    /// on every sampling tick by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class RamDetailView : UserControl, IDetailView
    {
        // Hover tooltip + the per-exe icon cache — shared implementations, see UI/Common. The
        // tip state lives in the host so the per-second Refresh can update a shown value
        // without waiting for the mouse to move (what re-fires MouseMove).
        private readonly ChartTipHost _tip = new ChartTipHost();
        private readonly ProcessIconCache _icons = new ProcessIconCache();

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

            _tip.Attach(RamHistoryChart, RamHistoryChart.HitTest, HistoryTip, HistoryTipText, i =>
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
            _tip.Refresh();

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopMemoryProcesses != null)
            {
                foreach (var p in s.TopMemoryProcesses)
                    p.Icon = _icons.Resolve(p.ExePath);
                ProcessList.ItemsSource = s.TopMemoryProcesses;
            }
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
            RamHistoryChart.AccentColor = ChartPalette.Accent;
            RamHistoryChart.GridColor = ChartPalette.Grid;
            // Theme-adaptive outer frame (see CpuDetailView): the DP default is a fixed
            // 40%-black, which reads as a dark hairline on the dark acrylic card.
            RamHistoryChart.FrameColor = ChartPalette.FaintBase(0x66);
            RamHistoryChart.FillOpacity = 48.0 / 255.0;
        }

        // ---------- Composition bar: theming + per-segment tooltip ----------
        private void ApplyCompositionTheme()
        {
            var accent = ChartPalette.Accent;
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
