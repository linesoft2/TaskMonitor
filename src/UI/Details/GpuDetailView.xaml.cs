using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
    /// Created fresh per popup open; refreshed on every sampling tick by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class GpuDetailView : UserControl, IDetailView
    {
        // Hover tooltip + the per-exe icon cache — shared implementations, see UI/Common. The
        // tip state lives in the host so the per-second Refresh can update a shown value
        // without waiting for the mouse to move (what re-fires MouseMove).
        private readonly ChartTipHost _tip = new ChartTipHost();
        private readonly ProcessIconCache _icons = new ProcessIconCache();

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

            _tip.Attach(GpuHistoryChart, GpuHistoryChart.HitTest, HistoryTip, HistoryTipText, i =>
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
            _tip.Refresh();

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
                    p.Icon = _icons.Resolve(p.ExePath);
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
            GpuHistoryChart.AccentColor = ChartPalette.Accent;
            GpuHistoryChart.GridColor = ChartPalette.Grid;
            // Theme-adaptive outer frame (see CpuDetailView): the DP default is a fixed
            // 40%-black, which reads as a dark hairline on the dark acrylic card.
            GpuHistoryChart.FrameColor = ChartPalette.FaintBase(0x66);
            GpuHistoryChart.FillOpacity = 48.0 / 255.0;

            // A theme switch re-instantiates the TabControl's template, which drops the
            // pivot hit-test fix (see PivotNavButtonFix).
            PivotNavButtonFix.Reapply(GpuTabs);
        }
    }
}
