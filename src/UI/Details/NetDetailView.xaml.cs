using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Threading;
using iNKORE.UI.WPF.Modern.Common.IconKeys;

namespace task_monitor
{
    /// <summary>
    /// Network detail panel: a header with the live upload (↑) and download (↓) rates shown
    /// separately, a 60s bidirectional line chart (↑ upload above the centerline, ↓ download
    /// mirrored below, each in its own color and auto-scaled to its own peak, annotated at the
    /// edges), a connection-info band (type · SSID · standard · band · channel width · link
    /// rate; click-to-copy local/public IP + public IPv6, with a header eye toggle that masks
    /// both public IPs; local &amp; public latency — data from
    /// <see cref="NetInfoSampler"/>'s background thread), and a per-process list of each
    /// process's real-time upload/download rate (SRUM real-time API — same source as Task
    /// Manager's 网络 column, but up/down kept separate).
    /// Self-contained — owns its chart theming, the hover tooltip, and the icon cache. Created
    /// fresh per popup open; refreshed on every sampling tick by <see cref="DetailWindow"/>.
    /// </summary>
    public partial class NetDetailView : UserControl, IDetailView
    {
        // Current light/dark flag — GetUpColor picks a green that reads on the acrylic
        // tint; rewritten by ApplyTheme (IDetailView) on a live theme switch.
        private bool _dark;

        // Current sampling cadence (from each snapshot) — the "N 秒前" history tooltip
        // scales its tick offset by it (settings 采样间隔).
        private int _intervalMs = 1000;

        // Hover tooltip + the per-exe icon cache — shared implementations, see UI/Common. The
        // tip state lives in the host so the per-second Refresh can update a shown value
        // without waiting for the mouse to move (what re-fires MouseMove).
        private readonly ChartTipHost _tip = new ChartTipHost();
        private readonly ProcessIconCache _icons = new ProcessIconCache();

        // Click-to-copy feedback: the just-copied IP button flashes "已复制" for a moment,
        // then the timer restores the address (kept in Tag).
        private DispatcherTimer _copiedTimer;
        private ContentControl _copiedButton;

        // Public-IP privacy mask (the eye toggle in the header): static, so the choice
        // survives the fresh view created per popup open. Applies to BOTH public buttons
        // (v4 + v6) — the shown text becomes dots; click-to-copy still copies the real
        // address (Tag always holds it).
        private static bool s_publicIpMasked;

        // Header slot the shell parks its pin toggle in (IDetailView).
        public ContentControl PinSlot => PinSlotHost;

        public NetDetailView(bool dark)
        {
            InitializeComponent();

            ApplyTheme(dark);
            _tip.Attach(NetChart, NetChart.HitTest, HistoryTip, HistoryTipText, FormatTip);

            ProcessListTip.Attach(NetList, ProcessTip, ProcessTipDesc, ProcessTipPath);

            // 连接 spans both columns; keep its trim boundary just left of 速率's label.
            BandGrid.LayoutUpdated += (_, _) => UpdateConnTrimBoundary();
        }

        // 连接 spans both columns, so without a cap it would ellipsize at the window edge,
        // UNDER the 速率 cell — and with a plain column cap it would ellipsize at the
        // mid-column split, wasting the indent's width. Instead the boundary tracks the
        // 速率 label's rendered x (right edge of the transparent indent) minus a small gap:
        // a long SSID only ellipsizes once it actually gets close to 速率. Runs every
        // layout pass; the MaxWidth guard breaks the layout-update loop (a set only
        // happens when the value really changed).
        private const double ConnRateGap = 8;
        private double? _indentWidth;   // width of the transparent indent text (font-fixed per session)

        private void UpdateConnTrimBoundary()
        {
            double rateLabelX = RateCell.TranslatePoint(new Point(0, 0), BandGrid).X
                              + (RateIndentRun.Text.Length > 0 ? IndentWidth : 0);
            double max = rateLabelX - ConnRateGap;
            if (max > 0 && Math.Abs(ConnCell.MaxWidth - max) > 0.01)
                ConnCell.MaxWidth = max;
        }

        // Width of the transparent indent text exactly as the 速率 cell renders it — same
        // string, same font. Must stay in sync with the "频段： " literal in Refresh.
        // (WidthIncludingTrailingWhitespace: the trailing space is the point.)
        private double IndentWidth
        {
            get
            {
                if (_indentWidth == null)
                {
                    var ft = new FormattedText("频段： ", CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(RateCell.FontFamily, RateCell.FontStyle,
                                     RateCell.FontWeight, RateCell.FontStretch),
                        RateCell.FontSize, Brushes.Black,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    _indentWidth = ft.WidthIncludingTrailingWhitespace;
                }
                return _indentWidth.Value;
            }
        }

        void IDetailView.Refresh(SystemSnapshot s)
        {
            _intervalMs = s.SampleIntervalMs;   // the history tooltip scales tick offsets by it
            // Top-right: up and down shown separately (↑ over ↓), default text color.
            NetUpLine.Text = "↑ " + NetRateFormatter.Format(s.NetUpBytesPerSec);
            NetDownLine.Text = "↓ " + NetRateFormatter.Format(s.NetDownBytesPerSec);

            // 60-tick history, null-padded during warm-up (same shape as CPU/RAM history).
            const int HistoryCapacity = 60;
            NetChart.UpValues = PadHistory(s.NetUpHistory, HistoryCapacity);
            NetChart.DownValues = PadHistory(s.NetDownHistory, HistoryCapacity);

            // ---- connection info band (between chart and process list) ----
            var info = s.NetInfo ?? NetInfo.Empty;
            string connText = BuildConnText(info);
            ConnValueRun.Text = connText;
            SetCell(StdCell, StdValueRun, info.WifiStandard);
            SetCell(BandCell, BandValueRun, BandAndWidth(info));
            RateValueRun.Text = FormatLinkRate(info.LinkRxBps, info.LinkTxBps) ?? "—";
            // The indent exists only to line 速率's label up with 频段's value below —
            // drop it when that cell is collapsed (wired), so 速率 sits flush at the
            // column start again. NOTE: the trailing space in "频段： " is significant —
            // the indent Run sits back-to-back with the 速率： Run (XAML would collapse
            // inter-Run whitespace into a rendered space that survives an emptied run),
            // so the indent text itself carries the space that follows 频段：'s label.
            RateIndentRun.Text = BandCell.Visibility == Visibility.Visible ? "频段： " : "";
            // The cell trims a long SSID — the tooltip carries the full text (+ adapter).
            ConnCell.ToolTip = string.IsNullOrEmpty(s.NetAdapterName)
                ? connText
                : connText + "\n" + s.NetAdapterName;
            // LocalIp falls back to a global v6 address when the adapter has no v4 — the
            // label says which one it's showing (公网 IPv4 is always v4: its endpoints are).
            LocalIpLabel.Text = info.LocalIp?.IndexOf(':') >= 0 ? "本机 IPv6：" : "本机 IPv4：";
            SetIpButton(LocalIpButton, info.LocalIp);
            SetIpButton(PublicIpButton, info.PublicIp);
            // Full-width v6 row: absent (no v6 connectivity / lookup pending) → the whole
            // cell collapses, like the Wi-Fi cells — a permanent "—" row would just be a
            // hole on v4-only networks.
            PublicIpV6Cell.Visibility = string.IsNullOrEmpty(info.PublicIpV6)
                ? Visibility.Collapsed : Visibility.Visible;
            // Only the v6 button's tooltip echoes the full address: it's the one button
            // whose text can trim (CharacterEllipsis), so the tooltip doubles as the
            // untrimmed-address readout. The other two sit in horizontal StackPanels
            // (infinite width) and never trim.
            SetIpButton(PublicIpV6Button, info.PublicIpV6, echoAddress: true);
            UpdatePublicIpEye();
            GatewayRttRun.Text = FormatRtt(info.GatewayRttMs);
            PublicRttRun.Text = FormatRtt(info.PublicRttMs);

            // Keep the open tooltip in lockstep with this per-second refresh.
            _tip.Refresh();

            // Per-process list: resolve each row's icon (cache-backed) and bind.
            if (s.TopNetProcesses != null)
            {
                foreach (var p in s.TopNetProcesses)
                    p.Icon = _icons.Resolve(p.ExePath);
                NetList.ItemsSource = s.TopNetProcesses;
            }
        }

        // Pads a sampler history (oldest→newest, may be shorter than the chart capacity) into
        // a fixed-capacity nullable array with nulls on the left during warm-up — so the line
        // grows in from the right edge exactly like the CPU/RAM history charts.
        private static double?[] PadHistory(long[] hist, int cap)
        {
            var arr = new double?[cap];
            int count = hist?.Length ?? 0;
            for (int i = 0; i < cap; i++)
                arr[i] = i < cap - count ? (double?)null : hist[i - (cap - count)];
            return arr;
        }

        // ---------- Connection info band ----------

        // 连接 cell: the type with the SSID in parens — "WLAN (LineSoft-Main-2)" /
        // "有线" / "未连接".
        private static string BuildConnText(NetInfo info)
        {
            if (string.IsNullOrEmpty(info.ConnectionType)) return "未连接";
            return string.IsNullOrEmpty(info.Ssid)
                ? info.ConnectionType
                : $"{info.ConnectionType} ({info.Ssid})";
        }

        // A labeled cell (标签：值) whose value can be absent: absent → the whole cell
        // collapses — no hole, no "—". (The Wi-Fi-only cells collapsing is what gives
        // wired its compact band.)
        private static void SetCell(TextBlock cell, Run valueRun, string value)
        {
            bool has = !string.IsNullOrEmpty(value);
            valueRun.Text = has ? value : "";
            cell.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        // 频段 cell: band and channel width share one phrase ("5 GHz · 80 MHz"); either
        // half may be unknown on its own (6 GHz width is not parsed, roam-edge BSS miss…).
        private static string BandAndWidth(NetInfo info)
        {
            if (string.IsNullOrEmpty(info.WifiBand)) return info.WifiChannelWidth;
            return string.IsNullOrEmpty(info.WifiChannelWidth)
                ? info.WifiBand
                : info.WifiBand + " · " + info.WifiChannelWidth;
        }

        // Negotiated link rate in bits/s (NOT bytes — NetRateFormatter doesn't apply).
        // Rx/Tx shown as one number when they match (the common case), else 接收/发送.
        private static string FormatLinkRate(long rxBps, long txBps)
        {
            if (rxBps <= 0 && txBps <= 0) return null;
            if (rxBps == txBps) return FormatBitsPerSec(rxBps);
            return FormatBitsPerSec(rxBps) + "/" + FormatBitsPerSec(txBps);
        }

        private static string FormatBitsPerSec(long bps)
        {
            if (bps >= 1_000_000_000L) return (bps / 1e9).ToString("0.##") + " Gbps";   // 1 / 2.5 Gbps
            if (bps >= 1_000_000L) return (bps / 1e6).ToString("0.#") + " Mbps";        // 866.7 / 1201 Mbps
            if (bps >= 1_000L) return (bps / 1e3).ToString("0") + " Kbps";
            return bps + " bps";
        }

        private static string FormatRtt(long ms) => ms < 0 ? "—" : ms == 0 ? "<1 ms" : ms + " ms";

        // ---------- IP copy-on-click ----------

        // value == null/"" → shows "—" and disables the button (gray, no hand cursor).
        // The flashing "已复制" button keeps its text — only Tag tracks the new value.
        // Every button's content is a permanent inner TextBlock (see XAML): the value and
        // the "已复制" flash swap its TEXT, so the v6 button's CharacterEllipsis trimming
        // survives the flash cycle. Every tooltip leads with the one-word action "复制";
        // echoAddress appends the full address on a second line (untrimmed readout) —
        // suppressed while masked, or the tooltip would leak the hidden address.
        private void SetIpButton(ContentControl btn, string value, bool echoAddress = false)
        {
            bool has = !string.IsNullOrEmpty(value);
            btn.Tag = has ? value : null;
            if (!ReferenceEquals(_copiedButton, btn) && btn.Content is TextBlock tb)
                tb.Text = has ? IpDisplayText(btn, value) : "—";
            btn.IsEnabled = has;
            btn.ToolTip = has ? (echoAddress && !IsMasked(btn) ? "复制\n" + value : "复制") : null;
        }

        // The eye toggle masks the two PUBLIC IP buttons only (本机 stays visible).
        private bool IsMasked(ContentControl btn)
            => s_publicIpMasked && (ReferenceEquals(btn, PublicIpButton) || ReferenceEquals(btn, PublicIpV6Button));

        private string IpDisplayText(ContentControl btn, string value)
            => IsMasked(btn) ? "••••••••" : value;

        // Eye toggle: Checked/Unchecked (not Click) so UIA TogglePattern / keyboard toggles
        // fire it too; the _eyeSyncing guard keeps UpdatePublicIpEye's programmatic IsChecked
        // sync from re-entering. Icon = current state (Eye = shown, EyeOff = masked);
        // tooltip = the offered action; IsChecked lights the chrome fill while masked.
        private bool _eyeSyncing;

        private void PublicIpEye_Toggled(object sender, RoutedEventArgs e)
        {
            if (_eyeSyncing) return;
            s_publicIpMasked = PublicIpEyeButton.IsChecked == true;
            SetIpButton(PublicIpButton, PublicIpButton.Tag as string);
            SetIpButton(PublicIpV6Button, PublicIpV6Button.Tag as string, echoAddress: true);
            UpdatePublicIpEye();
        }

        private void UpdatePublicIpEye()
        {
            _eyeSyncing = true;
            PublicIpEyeButton.IsChecked = s_publicIpMasked;
            _eyeSyncing = false;
            PublicIpEyeIcon.Icon = s_publicIpMasked
                ? FluentSystemIcons.EyeOff_16_Regular
                : FluentSystemIcons.Eye_16_Regular;
            PublicIpEyeButton.ToolTip = s_publicIpMasked ? "显示公网 IP" : "隐藏公网 IP";
        }

        private void IpButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ContentControl btn) return;
            string value = btn.Tag as string;
            if (string.IsNullOrEmpty(value)) return;
            // Flash FIRST — the copy below runs off the UI thread (see CopyToClipboard), so
            // its worst case (a busy clipboard ≈ 1s of internal retries) never freezes the panel.
            FlashCopied(btn);
            CopyToClipboard(value);
        }

        // net48's Clipboard.SetText retries a busy clipboard 10× with 100ms sleeps ON THE
        // CALLING THREAD (OleSetClipboard → CLIPBRD_E_CANTSET) — a clipboard manager / RDP /
        // IME holding the clipboard open froze the panel for a full second per click. So the
        // write runs on a throwaway STA worker: OLE clipboard requires STA, and the
        // OleFlushClipboard inside SetText serializes the data eagerly, so it survives the
        // worker's exit. The flash above is optimistic — a failed copy just means it lied
        // for 1.2s.
        private static readonly object s_clipboardLock = new object();   // serialize rapid clicks

        private static void CopyToClipboard(string text)
        {
            var t = new Thread(() =>
            {
                lock (s_clipboardLock)
                {
                    try { Clipboard.SetText(text); }
                    catch { /* nothing useful to do — the optimistic flash has already faded */ }
                }
            }) { IsBackground = true, Name = "ClipboardCopy" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private void FlashCopied(ContentControl btn)
        {
            RestoreCopied();   // only one button flashes at a time
            if (btn.Content is TextBlock tb) tb.Text = "已复制";
            _copiedButton = btn;
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _copiedTimer.Tick += (_, _) => RestoreCopied();
            _copiedTimer.Start();
        }

        private void RestoreCopied()
        {
            if (_copiedTimer != null) { _copiedTimer.Stop(); _copiedTimer = null; }
            if (_copiedButton != null)
            {
                if (_copiedButton.Content is TextBlock tb)
                {
                    // Restore through the same mask-aware path — a public-IP button copied
                    // while masked must fade back to dots, not the real address.
                    string value = _copiedButton.Tag as string;
                    tb.Text = value != null ? IpDisplayText(_copiedButton, value) : "—";
                }
                _copiedButton = null;
            }
        }

        private string FormatTip(int i)
        {
            var up = NetChart.UpValues;
            var down = NetChart.DownValues;
            bool hasUp = up != null && i >= 0 && i < up.Count && up[i].HasValue;
            bool hasDown = down != null && i >= 0 && i < down.Count && down[i].HasValue;
            if (!hasUp && !hasDown) return "";

            string when = HistoryTimeFormatter.Ago(59 - i, _intervalMs);
            string ups = hasUp ? NetRateFormatter.Format((long)up[i].Value) : "—";
            string downs = hasDown ? NetRateFormatter.Format((long)down[i].Value) : "—";
            return $"{when} · ↑ {ups}  ↓ {downs}";
        }

        // ---------- Theming: tooltip surfaces + paint the chart + tie the header's ↑/↓ values to the line colors ----------
        public void ApplyTheme(bool dark)
        {
            _dark = dark;
            HistoryTipBorder.Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

            ApplyChartTheme();
        }

        private void ApplyChartTheme()
        {
            NetChart.UpColor = GetUpColor();                 // green
            NetChart.DownColor = ChartPalette.Accent;
            NetChart.GridColor = ChartPalette.FaintBase(0x1A);
            NetChart.AxisColor = ChartPalette.FaintBase(0x40);
            NetChart.FrameColor = ChartPalette.FaintBase(0x66);
            NetChart.LabelColor = ChartPalette.LabelGray;
            NetChart.UpFillOpacity = 48.0 / 255.0;
            NetChart.DownFillOpacity = 48.0 / 255.0;

            // The header's ↑/↓ values stay the default (primary) text color — only the chart
            // lines are tinted. The arrow glyph still disambiguates direction.
        }

        // Upload gets a distinct green; download keeps the app's accent (ChartPalette.Accent —
        // the on-brand "primary" hue the CPU/RAM lines use). If the user's accent happens to be
        // greenish these two collapse — swap them in that case.
        private Color GetUpColor()
            => _dark ? Color.FromRgb(0x3F, 0xB9, 0x50)   // brighter green reads on dark acrylic
                     : Color.FromRgb(0x0A, 0x8C, 0x4B);   // deeper green reads on light
    }
}
