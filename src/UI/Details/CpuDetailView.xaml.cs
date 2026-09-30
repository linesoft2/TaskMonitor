using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// CPU detail panel: overall usage header, 60s history chart, per-core bars,
    /// and a system-summary footer (live speed / processes / threads / handles / uptime).
    /// Self-contained — owns its chart theming and the hover tooltips. Created fresh
    /// per popup open; refreshed on every sampling tick by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class CpuDetailView : UserControl, IDetailView
    {
        // Hover tooltips (both charts) + the per-exe icon cache — shared implementations, see
        // UI/Common. The tip state lives in the host so the per-second Refresh can update a
        // shown value without waiting for the mouse to move (what re-fires MouseMove).
        private readonly ChartTipHost _tip = new ChartTipHost();
        private readonly ProcessIconCache _icons = new ProcessIconCache();

        // Current sampling cadence (from each snapshot) — the "N 秒前" history tooltip
        // scales its tick offset by it (settings 采样间隔).
        private int _intervalMs = 1000;

        // Header slot the shell parks its pin toggle in (IDetailView).
        public ContentControl PinSlot => PinSlotHost;

        public CpuDetailView(bool dark)
        {
            InitializeComponent();

            ApplyTheme(dark);

            _tip.Attach(CpuHistoryChart, CpuHistoryChart.HitTest, HistoryTip, HistoryTipText, i =>
            {
                var values = CpuHistoryChart.Values;
                if (i < 0 || values == null || i >= values.Count) return "";
                var v = values[i];
                return v is null ? "" : $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {v:F0}%";
            });
            _tip.Attach(CpuCoresChart, CpuCoresChart.HitTest, CoresTip, CoresTipText, i =>
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
            _tip.Refresh();

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopProcesses != null)
            {
                foreach (var p in s.TopProcesses)
                    p.Icon = _icons.Resolve(p.ExePath);
                ProcessList.ItemsSource = s.TopProcesses;
            }
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
            CpuHistoryChart.AccentColor = ChartPalette.Accent;
            CpuHistoryChart.GridColor = ChartPalette.Grid;
            // The outer frame is the only 0/100% boundary the chart draws — without this it
            // kept its 40%-black DP default in EVERY theme, i.e. a dark hairline on the dark
            // acrylic card (the 网络 chart has always tinted its own frame the same way).
            CpuHistoryChart.FrameColor = ChartPalette.FaintBase(0x66);
            CpuHistoryChart.FillOpacity = 48.0 / 255.0;

            CpuCoresChart.AccentColor = ChartPalette.Accent;
            CpuCoresChart.TrackColor = ChartPalette.Track;
            // Same accent hue as the line chart's fill, but denser (~50%) so the bars stay
            // readable as a usage indicator; the area fill stays at the fainter 48/255.
            CpuCoresChart.FillOpacity = 128.0 / 255.0;
        }
    }
}
