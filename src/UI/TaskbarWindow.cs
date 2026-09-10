using System;
using System.Runtime.InteropServices;
using System.Threading;
using DirectN;

namespace task_monitor
{
    /// <summary>
    /// Renders a small transparent overlay onto the Windows taskbar showing
    /// live CPU / RAM / Disk / Net usage. Ported 1:1 from task_monitor_3's
    /// <c>native/hub/src/taskbar.rs</c>: a Win32 popup window (created with
    /// <c>WS_EX_NOREDIRECTIONBITMAP</c> and reparented into Shell_TrayWnd via
    /// <c>SetParent</c>) driven by a DirectComposition surface backed by a
    /// D3D11 + D2D1 + DirectWrite pipeline. Refreshes every 1s; tracks DPI and the
    /// system taskbar theme (light/dark — the overlay must match the surface it sits on).
    ///
    /// Two taskbar families are supported (detection mirrors TrafficMonitor's
    /// CheckWindows11Taskbar — Win11 version AND the XAML DesktopWindowContentBridge
    /// child window): the WIN11 taskbar (parent = Shell_TrayWnd, anchored left of
    /// TrayNotifyWnd / near Start) and the CLASSICAL one (Windows 10, or an
    /// ExplorerPatcher-restored classic taskbar on Win11 — parent = ReBarWindow32,
    /// docked by shrinking the MSTaskSwWClass task-buttons band, vertical taskbars
    /// included; TrafficMonitor's CClassicalTaskbarDlg path). The family is fixed
    /// per <see cref="Start"/> run.
    ///
    /// Runs on whatever thread calls <see cref="Start"/> — the caller (App)
    /// is expected to spin up a dedicated STA thread, since this method ends
    /// by pumping a Win32 message loop until the window is destroyed.
    /// </summary>
    public sealed class TaskbarWindow
    {
        // The overlay is a fixed-width-cell grid of up to three VISUAL GROUPS side by
        // side — groups 0/1 are two-row stacked cells, group 2 is the 网络 column
        // (↑ over ↓, one whole-height slot).
        //
        //   hit slot:  0=CPU  1=内存   2=磁盘  3=GPU   4=网络
        //              ┌──────┐       ┌──────┐    ┌────────┐
        //              │ CPU  │       │ 磁盘 │    │ ↑ …MB/s│
        //              ├──────┤       ├──────┤    │ ↓ …MB/s│
        //              │ 内存 │       │ GPU  │    └────────┘
        //              └──────┘       └──────┘
        //
        // Group widths are constants; the four stacked ROW POSITIONS [g0-top, g0-bottom,
        // g1-top, g1-bottom] are filled by the VISIBLE stacked metrics (slots 0-3) in
        // their fixed relative order. A metric whose sampling is off (设置 → 采样) leaves
        // NO hole in the middle — the later metrics shift forward to fill it (空缺补齐),
        // and the grid always keeps its two-row structure (never two-rows-become-one).
        // A stacked group with no metrics left vanishes and the window shrinks to fit
        // (ResizeForLayout); only the grid's LAST row position may stay empty (an odd
        // count can't fill it). Slot IDs are STABLE across hiding (slot 3 is always GPU)
        // — only geometry moves. ComputeLayout derives all geometry from the mask.
        private const float ITEM_GAP = 0f;                 // groups sit flush against each other
        private static readonly float[] GroupWidths = { 70f, 70f, 84f }; // stacked cell, stacked cell, 网络 (DIPs at 96 DPI)
        private const int GroupCount = 3;                  // two stacked cells + 网络
        private const int SlotCount = 5;                   // hit slots: CPU/内存/磁盘/GPU/网络
        // VERTICAL taskbar (a side-docked classical taskbar): the grid transposes into a
        // stack of full-width strips — one per visible stacked metric, two for 网络
        // (↑ over ↓). Text stays horizontal (TrafficMonitor's vertical mode; no rotated
        // text). Strip height = its TASKBAR_WND_HEIGHT/2.
        private const float STRIP_H = 16f;                 // one vertical-mode strip (DIPs at 96 DPI)
        private const int STRIP_H_I = 16;                  // the same, for integer DPI scaling

        // The geometry of one visibility mask. Flat fields (no arrays) — computed per
        // Draw/HitTest call, incl. per mouse-move, so it must not allocate.
        private struct OverlayLayout
        {
            public bool Vertical;               // side-docked classical taskbar (strips), false = the two-row grid
            public float Width;                 // total logical width (0 when every metric is off)
            public float Height;                // total logical height — VERTICAL mode only (horizontal: the band owns the height, 0 here)
            public float Left0, Left1, Left2;   // per-group left edge in DIPs (-1 = group gone); vertical: 0/-1 visibility sentinels only
            // The slot packed at each stacked row position [g0-top, g0-bottom, g1-top,
            // g1-bottom]; -1 = the trailing empty position of an odd count. Vertical mode:
            // the same packing, but each position is one full-width strip.
            public int Pos0, Pos1, Pos2, Pos3;
            public int SlotMask;                // the sampling mask this layout was derived from

            public bool GroupVisible(int g) => GroupLeft(g) >= 0f;
            public float GroupLeft(int g) => g == 0 ? Left0 : g == 1 ? Left1 : Left2;
            public bool SlotVisible(int slot) => (SlotMask & (1 << slot)) != 0;
            public int SlotAt(int pos) => pos == 0 ? Pos0 : pos == 1 ? Pos1 : pos == 2 ? Pos2 : Pos3;
            public int PositionOf(int slot) => Pos0 == slot ? 0 : Pos1 == slot ? 1 : Pos2 == slot ? 2 : Pos3 == slot ? 3 : -1;
        }

        // Visible stacked metrics (slots 0–3) count — the vertical mode's strip-order base.
        private static int PackedStackedCount(int mask) => (mask & 1) + ((mask >> 1) & 1) + ((mask >> 2) & 1) + ((mask >> 3) & 1);

        // Derive all geometry from the sampling mask (bit per slot, SystemSampler.Mask*).
        // vertical = the side-docked classical strips layout; false = the two-row grid.
        private static OverlayLayout ComputeLayout(int mask, bool vertical)
        {
            var l = new OverlayLayout
            {
                Left0 = -1f, Left1 = -1f, Left2 = -1f,
                Pos0 = -1, Pos1 = -1, Pos2 = -1, Pos3 = -1,
                SlotMask = mask,
            };
            // Pack the visible stacked metrics into the row positions in order (空缺补齐).
            int pos = 0;
            for (int slot = 0; slot < 4 && pos < 4; slot++)
            {
                if ((mask & (1 << slot)) == 0) continue;
                if (pos == 0) l.Pos0 = slot; else if (pos == 1) l.Pos1 = slot; else if (pos == 2) l.Pos2 = slot; else l.Pos3 = slot;
                pos++;
            }
            bool net = (mask & (1 << 4)) != 0;      // 网络 column / strips
            if (vertical)
            {
                // One full-width strip per packed metric; 网络 takes two (↑ over ↓).
                // Left* degrade to visibility sentinels — x plays no role vertically.
                l.Left0 = pos >= 1 ? 0f : -1f;
                l.Left1 = pos >= 3 ? 0f : -1f;
                l.Left2 = net ? 0f : -1f;
                float cw = pos > 0 ? GroupWidths[0] : 0f;
                if (net) cw = Math.Max(cw, GroupWidths[2]);
                l.Width = cw;
                l.Height = (pos + (net ? 2 : 0)) * STRIP_H;
                l.Vertical = true;
                return l;
            }
            bool g0 = pos >= 1;                     // at least one stacked metric
            bool g1 = pos >= 3;                     // enough to spill into the second cell
            float x = 0f;
            if (g0) { l.Left0 = x; x += GroupWidths[0]; }
            if (g1) { if (x > 0f) x += ITEM_GAP; l.Left1 = x; x += GroupWidths[1]; }
            if (net) { if (x > 0f) x += ITEM_GAP; l.Left2 = x; x += GroupWidths[2]; }
            l.Width = x;
            return l;
        }

        // Logical rect of a hit slot under a layout, or false when the slot is hidden.
        // Horizontal: a stacked slot's row comes from its PACKED position (positions 0/2 =
        // the top row of their cell, 1/3 = the bottom row); slot 4 (网络) spans the whole
        // height. Vertical: a stacked slot is its packed-position strip; slot 4 is the
        // two-strip ↑/↓ block after the last stacked strip (top/mid/bottom go unused).
        private static bool TrySlotRect(OverlayLayout l, float top, float mid, float bottom, int slot, out D2D_RECT_F r)
        {
            r = default;
            if (l.Vertical)
            {
                if (slot == 4)
                {
                    if (l.Left2 < 0f) return false;
                    float top4 = PackedStackedCount(l.SlotMask) * STRIP_H;
                    r = new D2D_RECT_F { left = 0f, top = top4, right = l.Width, bottom = top4 + 2f * STRIP_H };
                    return true;
                }
                int vpos = l.PositionOf(slot);
                if (vpos < 0) return false;
                float vtop = vpos * STRIP_H;
                r = new D2D_RECT_F { left = 0f, top = vtop, right = l.Width, bottom = vtop + STRIP_H };
                return true;
            }
            if (slot == 4)
            {
                if (l.Left2 < 0f) return false;
                r = new D2D_RECT_F { left = l.Left2, top = top, right = l.Left2 + GroupWidths[2], bottom = bottom };
                return true;
            }
            int pos = l.PositionOf(slot);
            if (pos < 0) return false;
            float gx0 = l.GroupLeft(pos / 2), gx1 = gx0 + GroupWidths[pos / 2];
            r = (pos % 2 == 0)
                ? new D2D_RECT_F { left = gx0, top = top, right = gx1, bottom = mid }
                : new D2D_RECT_F { left = gx0, top = mid, right = gx1, bottom = bottom };
            return true;
        }

        private const uint USER_DEFAULT_SCREEN_DPI = 96;
        private const uint TIMER_ID = 1;                   // refresh + DPI poll
        private const uint TIMER_ID_POS = 2;               // classical family only: band re-dock poll
        private const int DEFAULT_INTERVAL_MS = 1000;      // default sampling cadence
        // TrafficMonitor re-checks the task-buttons band every 100ms: explorer re-expands
        // it as windows open/close, and the (configurable, up to 2s) sample tick would
        // leave it overlapping the overlay for a whole interval.
        private const int POS_INTERVAL_MS = 100;

        // Sampling/refresh cadence in ms (settings 采样间隔; 500/1000/2000 from the combo,
        // clamped for hand-edited yaml). Same volatile contract as _onLeft: written by the
        // UI thread, read on the taskbar thread when (re)arming the WM_TIMER and stamped
        // onto every published snapshot (the detail views' "N 秒前" tooltips need it).
        private volatile int _sampleIntervalMs = DEFAULT_INTERVAL_MS;

        // Process-lifetime, created ONCE. RegisterClassW keeps the FIRST lpfnWndProc when a
        // re-entered Start() hits ERROR_CLASS_ALREADY_EXISTS, so a per-Start() delegate would
        // orphan the thunk the class still points at — once GC collects the old delegate, the
        // next dispatched message jumps into freed memory and AVs inside the reverse-pinvoke
        // stub (surfaces as a stack-less NullReferenceException; the 2026-07-30 crash).
        private static readonly WindowInterop.WndProc _wndProc = new WindowInterop.WndProc(WndProc);
        private GCHandle _stateHandle;

        // --------------------------------------------------------------------
        // Public entry point: full init + blocking message loop.
        // --------------------------------------------------------------------

        /// <summary>
        /// Invoked on the taskbar thread whenever a hit slot is clicked. The argument
        /// is the new selected slot (0–4: CPU/内存/磁盘/GPU/网络), or -1 when the selection
        /// was toggled off. The owner (App) marshals this onto the WPF UI thread to show/hide
        /// the detail popup. Set by App before <see cref="Start"/> runs.
        /// </summary>
        public Action<int> ToggleCallback;

        /// <summary>
        /// Invoked on the taskbar thread right after a fresh snapshot is published, so
        /// the detail window can refresh in lockstep with the taskbar (push, not poll).
        /// The owner (App) marshals this onto the UI thread.
        /// </summary>
        public Action SnapshotChanged;

        /// <summary>
        /// Invoked on the taskbar thread when the overlay is right-clicked. The owner
        /// (App) marshals this onto the UI thread to show the context menu.
        /// </summary>
        public Action RightClickRequested;

        /// <summary>Screen HWND of the embedded overlay, once <see cref="Start"/> created it.</summary>
        public IntPtr OverlayHwnd;

        /// <summary>
        /// The latest system snapshot, published by the taskbar thread on each sample
        /// and read by the detail window (UI thread). Single source of truth — the
        /// overlay and the popup show the same numbers. Reference assignment of a fresh,
        /// never-mutated object, so cross-thread reads are safe.
        /// </summary>
        private volatile SystemSnapshot _latestShared;
        internal SystemSnapshot LatestSnapshot => _latestShared;

        // The SystemSampler is created inside Start() on the taskbar thread and stashed on the
        // per-HWND RenderState for the WM_TIMER path. This field exposes it to UI-thread instance
        // methods (RequestWifiDetails) so DetailWindow can forward the Network-panel-opened signal.
        // Volatile: written once on the taskbar thread, read on the UI thread.
        private volatile SystemSampler _sampler;

        // Placement settings: which side of the taskbar the overlay sits on, and (on the
        // left) which anchor. Same volatile contract as _clickDisabledMask — written by the
        // WPF UI thread (startup / settings page), read on the taskbar thread in CalcPosition.
        private volatile bool _onLeft;
        private volatile bool _snapToStart;

        /// <summary>
        /// Thread-safe: set the overlay's placement (right of the tray vs left side, and
        /// the left-side anchor) and ask the taskbar thread to re-position. Called by App
        /// once before <see cref="Start"/> (HWND still zero → flags only, the initial
        /// CalcPosition picks them up) and whenever the settings page changes the values.
        /// </summary>
        public void SetPlacement(bool onLeft, bool snapToStart)
        {
            _onLeft = onLeft;
            _snapToStart = snapToStart;
            if (OverlayHwnd != IntPtr.Zero)
                WindowInterop.PostMessageW(OverlayHwnd, WindowInterop.WM_APP_REPOSITION, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Thread-safe: set the sampling cadence (settings 采样间隔) and ask the taskbar
        /// thread to re-arm its WM_TIMER. Called by App once before <see cref="Start"/>
        /// (HWND still zero → field only; the initial SetTimer picks it up) and whenever
        /// the settings page changes the value. SetTimer requires the window-owning
        /// thread, hence the posted message.
        /// </summary>
        public void SetSampleInterval(int ms)
        {
            _sampleIntervalMs = Math.Max(250, Math.Min(10000, ms));
            if (OverlayHwnd != IntPtr.Zero)
                WindowInterop.PostMessageW(OverlayHwnd, WindowInterop.WM_APP_SET_INTERVAL, IntPtr.Zero, IntPtr.Zero);
        }

        // Per-metric sampling switches (设置 → 采样): one bit per hit slot (SystemSampler's
        // Mask constants, in slot order). A cleared bit = sampling off: the slot is HIDDEN
        // (the later metrics shift forward to fill its row position — ComputeLayout; an
        // emptied group frees its width — ResizeForLayout) and its left-click press is
        // suppressed (hover retained). Same volatile single-writer contract as
        // _clickDisabledMask.
        private volatile int _samplingEnabledMask = SystemSampler.MaskAll;

        /// <summary>Thread-safe: replace the per-metric sampling mask and ask the taskbar
        /// thread to re-layout + re-sample immediately (hidden slots vanish without waiting
        /// a tick). Called by App once before <see cref="Start"/> (HWND still zero → field
        /// only; Start picks it up for the initial layout and applies it to the fresh
        /// sampler) and whenever the settings page toggles a metric. Kept separate from
        /// _clickDisabledMask so unpinning a window can't re-enable a sampling-disabled
        /// slot's press.</summary>
        public void SetMetricSamplingMask(int mask)
        {
            _samplingEnabledMask = mask;
            _sampler?.SetEnabledMask(mask);
            if (OverlayHwnd != IntPtr.Zero)
                WindowInterop.PostMessageW(OverlayHwnd, WindowInterop.WM_APP_SET_METRICS, IntPtr.Zero, IntPtr.Zero);
        }

        // 设置 → 采样项目 → 合并相同程序 (ProcessListMerger): same volatile single-writer
        // contract as _samplingEnabledMask. No posted message — the overlay layout doesn't
        // change, so the next tick's snapshot rebuilds the detail lists on its own.
        private volatile bool _mergeSamePathProcesses;

        /// <summary>Thread-safe: toggle same-path process merging in the per-process
        /// detail lists. Called by App once before <see cref="Start"/> (HWND still zero →
        /// field only; Start applies it to the fresh sampler, like the sampling mask) and
        /// whenever the settings page flips the switch.</summary>
        public void SetMergeSamePathProcesses(bool on)
        {
            _mergeSamePathProcesses = on;
            _sampler?.SetMergeByPath(on);
        }

        // 设置 → 采样项目 → 磁盘 → 显示方式 (所有磁盘平均 / 最高 / 某个特定磁盘): same
        // volatile single-writer contract as _mergeSamePathProcesses. No posted message —
        // the overlay layout doesn't change, so the next tick's snapshot redraws with the
        // new headline (DiskSampler clears its history on a mode change, so the detail
        // chart doesn't mix old-semantics values either).
        private volatile int _diskDisplayMode;    // (int)MetricDisplayMode — 0=Average, the default
        private volatile int _diskDisplayIndex;   // PhysicalDrive N for MetricDisplayMode.Specific

        /// <summary>Thread-safe: set the disk headline display mode (0=Average 1=Max
        /// 2=Specific) and the specific disk's PhysicalDrive index. Called by App once
        /// before <see cref="Start"/> (HWND still zero → fields only; Start applies them
        /// to the fresh sampler, like the sampling mask) and whenever the settings page
        /// changes the value.</summary>
        public void SetDiskDisplay(int mode, int index)
        {
            _diskDisplayMode = mode;
            _diskDisplayIndex = index;
            _sampler?.SetDiskDisplay(mode, index);
        }

        // 设置 → 采样项目 → GPU → 显示方式 (所有 GPU 平均 / 最高 / 特定 GPU): same contract
        // as the disk one above — no posted message, the next tick redraws with the new
        // headline (GpuSampler clears its history on a mode change). The GPU default is
        // 最高 (Task Manager's sidebar rule), unlike the disk's 平均.
        private volatile int _gpuDisplayMode = (int)MetricDisplayMode.Max;
        private volatile int _gpuDisplayIndex;    // the "GPU N" number for MetricDisplayMode.Specific

        /// <summary>Thread-safe: set the GPU headline display mode (0=Average 1=Max
        /// 2=Specific) and the specific adapter's "GPU N" number. Called by App once
        /// before <see cref="Start"/> and whenever the settings page changes the value
        /// (same field-then-Start contract as <see cref="SetDiskDisplay"/>).</summary>
        public void SetGpuDisplay(int mode, int index)
        {
            _gpuDisplayMode = mode;
            _gpuDisplayIndex = index;
            _sampler?.SetGpuDisplay(mode, index);
        }

        // 设置 → 采样项目 → 网络 → 适配器 (自动 / 指定某个适配器): same contract as the
        // disk one above. null = 自动 (NetSampler's max-traffic pick; a pinned adapter
        // that's gone falls back to 自动 inside the sampler).
        private volatile string _netAdapterId;

        /// <summary>Thread-safe: pin the sampled network adapter by NetworkInterface.Id
        /// (GUID), or null for 自动. Called by App once before <see cref="Start"/> and
        /// whenever the settings page changes the value (same field-then-Start contract
        /// as <see cref="SetDiskDisplay"/>).</summary>
        public void SetNetAdapter(string id)
        {
            _netAdapterId = id;
            _sampler?.SetNetAdapter(id);
        }

        // 设置 → 采样项目 → 网络 → Clash/Mihomo (开关 + external-controller 地址 + 密钥):
        // same contract as the adapter one above — no posted message; the sampler's poll
        // thread retargets/idles on change. enabled default on; null/empty address = the
        // 127.0.0.1:9090 default.
        private volatile bool _clashEnabled = true;
        private volatile string _clashApiAddress;
        private volatile string _clashApiSecret;

        /// <summary>Thread-safe: switch the Clash/Mihomo integration and set the
        /// external-controller endpoint (host:port — null/empty = the conventional
        /// 127.0.0.1:9090 default) and API secret (null/empty = none). Called by App once
        /// before <see cref="Start"/> and whenever the settings page changes the value
        /// (same field-then-Start contract as <see cref="SetNetAdapter"/>).</summary>
        public void SetClashApi(bool enabled, string address, string secret)
        {
            _clashEnabled = enabled;
            _clashApiAddress = address;
            _clashApiSecret = secret;
            _sampler?.SetClashApi(enabled, address, secret);
        }

        // 设置 → 采样项目 → 网络 → 公网 IP (switch, default on): same contract as the
        // Clash one above — no posted message; NetInfoSampler's poll thread drops its
        // cached address and stops BOTH the HTTP lookups and the 公网延迟 ICMP probe on off.
        private volatile bool _publicIpLookupEnabled = true;

        /// <summary>Thread-safe: switch the public-IP lookup (the Network panel's 公网
        /// IPv4/IPv6). Called by App once before <see cref="Start"/> and whenever the
        /// settings page changes the value (same field-then-Start contract as
        /// <see cref="SetNetAdapter"/>).</summary>
        public void SetPublicIpLookup(bool enabled)
        {
            _publicIpLookupEnabled = enabled;
            _sampler?.SetPublicIpLookup(enabled);
        }

        // Taskbar-thread mirror of RenderState.Vertical for the UI-thread layout accessors
        // below (they can't touch the HWND-bound state). Written in Start() and on an
        // orientation flip (ReconfigureOrientation); read on the WPF UI thread.
        private volatile bool _layoutVertical;

        // Current overlay geometry (layout follows the sampling mask — hidden slots are
        // gone, not blank). Read by DetailWindow to centre its popup over a column.
        internal float LogicalWidth => ComputeLayout(_samplingEnabledMask, _layoutVertical).Width;

        // Total layout height in DIPs — meaningful only in VERTICAL mode (horizontal
        // layouts take the band's height; the layout owns none). DetailWindow reads it
        // for the centre ratio when the taskbar is docked left/right.
        internal float LogicalHeight => ComputeLayout(_samplingEnabledMask, _layoutVertical).Height;

        // X centre of a hit slot — used to centre the detail popup over its column. A
        // stacked slot's centre is its PACKED cell's centre (the cell it shifted into);
        // slot 4 is the 网络 group's. A hidden slot has no centre (0) — it can never be
        // clicked or pinned into existence. Vertical mode: the strip's horizontal centre.
        internal float ColumnCenter(int slot)
        {
            var l = ComputeLayout(_samplingEnabledMask, _layoutVertical);
            if (l.Vertical)
            {
                bool vis = slot == 4 ? l.Left2 >= 0f : l.PositionOf(slot) >= 0;
                return vis ? l.Width / 2f : 0f;
            }
            if (slot == 4)
                return l.Left2 >= 0f ? l.Left2 + GroupWidths[2] / 2f : 0f;
            int pos = l.PositionOf(slot);
            return pos >= 0 ? l.GroupLeft(pos / 2) + GroupWidths[pos / 2] / 2f : 0f;
        }

        // Y centre of a hit slot — VERTICAL mode only (0 otherwise, unused there): the
        // strip's vertical centre; slot 4 (网络) centres over its two-strip ↑/↓ block.
        // Same hidden-slot-returns-0 contract as ColumnCenter.
        internal float ColumnCenterY(int slot)
        {
            var l = ComputeLayout(_samplingEnabledMask, _layoutVertical);
            if (!l.Vertical) return 0f;
            if (slot == 4)
                return l.Left2 >= 0f ? (PackedStackedCount(l.SlotMask) + 1) * STRIP_H : 0f;
            int pos = l.PositionOf(slot);
            return pos >= 0 ? (pos + 0.5f) * STRIP_H : 0f;
        }

        /// <summary>
        /// Thread-safe: clears the selected-column highlight and redraws — but only if
        /// <paramref name="column"/> is still the selected one. Called by the WPF UI
        /// thread when a detail popup closes itself (focus loss / its ✕ button); the
        /// column guard keeps a closing pinned window from clearing a newer selection.
        /// </summary>
        public void RequestDeselect(int column)
        {
            if (OverlayHwnd != IntPtr.Zero)
                WindowInterop.PostMessageW(OverlayHwnd, WindowInterop.WM_APP_DESELECT, new IntPtr(column), IntPtr.Zero);
        }

        /// <summary>
        /// Bitmask of hit slots (bit 0–4) whose left-click press is suppressed — set while
        /// that slot's window is pinned (the pinned window owns it until unpinned or closed).
        /// Hover is unaffected. Single writer (the WPF UI thread), read on the taskbar thread
        /// in WndProc.
        /// </summary>
        private volatile int _clickDisabledMask;

        /// <summary>Thread-safe: suppress or restore a hit slot's press. Idempotent.</summary>
        public void SetColumnClickEnabled(int slot, bool enabled)
        {
            if (slot < 0 || slot >= SlotCount) return;
            int bit = 1 << slot;
            _clickDisabledMask = enabled ? (_clickDisabledMask & ~bit) : (_clickDisabledMask | bit);
        }

        /// <summary>
        /// Thread-safe: mark a column as selected (highlight on) and redraw. Called by
        /// the WPF UI thread when a flyout becomes the selected popup again — i.e. a
        /// window was unpinned back to flyout form (pinning had cleared the highlight,
        /// so without this the open flyout would show no selected column and the next
        /// click would tear down + reopen it instead of toggling it closed).
        /// </summary>
        public void RequestSelect(int column)
        {
            if (OverlayHwnd != IntPtr.Zero)
                WindowInterop.PostMessageW(OverlayHwnd, WindowInterop.WM_APP_SELECT, new IntPtr(column), IntPtr.Zero);
        }

        /// <summary>UI thread: the Network detail panel just opened. Forwards to the sampler's
        /// background thread, which refreshes the Wi-Fi cache on its next tick IF stale (a fresh
        /// cache is reused with zero wlanapi calls). This is what keeps the taskbar location
        /// indicator off unless the user actually opens the Network panel. Thread-safe; a no-op
        /// before Start() has run (_sampler still null, e.g. during Prewarm).</summary>
        public void RequestWifiDetails() => _sampler?.RequestWifiDetails();

        public void Start()
        {
            // === Step 1: locate taskbar, query DPI, compute size ===
            // WAIT for a real taskbar rather than giving up: at boot the scheduled-task
            // logon trigger can fire BEFORE explorer has created (or laid out)
            // Shell_TrayWnd. The old code returned silently here, leaving the process
            // running forever with no overlay and no retry ("auto-start works but
            // nothing shows on the taskbar"). The rect must be non-zero too — an
            // overlay created against an unlaid-out taskbar would get height 0 and
            // stay invisible (RepositionOverlay's height tracking skips a 0-height
            // band — it can't recover from starting at 0). A TRANSIENT wrong height
            // (taller taskbar mid-layout) is fine: the 1s RepositionOverlay re-measures
            // and resizes to the settled height.
            // Unbounded on purpose: without a taskbar an overlay is pointless anyway,
            // and this dedicated thread has nothing else to do meanwhile.
            OverlayHwnd = IntPtr.Zero;   // a re-entered Start() must not post to the previous run's dead HWND
            IntPtr taskbar;
            WindowInterop.RECT taskbarRect;
            int probeWaits = 0;
            while (true)
            {
                taskbar = WindowInterop.FindWindowW("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero
                    && WindowInterop.GetWindowRect(taskbar, out taskbarRect)
                    && taskbarRect.right - taskbarRect.left > 0
                    && taskbarRect.bottom - taskbarRect.top > 0)
                    break;
                if (probeWaits++ == 0)
                    Logger.Info("Shell_TrayWnd 尚未就绪（开机时登录任务可能先于 explorer）——每秒重试等待任务栏");
                Thread.Sleep(1000);
            }
            if (probeWaits > 0)
                Logger.Info($"任务栏已就绪（等待 {probeWaits}s），rect=({taskbarRect.left},{taskbarRect.top})-({taskbarRect.right},{taskbarRect.bottom})");

            // Taskbar family (TrafficMonitor's CheckWindows11Taskbar): the OS says Win11
            // AND the XAML taskbar's DesktopWindowContentBridge child exists → the Win11
            // path. Anything else — Windows 10, or an ExplorerPatcher-restored classic
            // taskbar on Win11 — takes the CLASSICAL path (ReBarWindow32 parent +
            // MSTaskSwWClass band shrink). Both probes retry: at boot the bridge child
            // (Win11) or the ReBar (Win10) can lag Shell_TrayWnd itself. After 10s with
            // neither, fall back to the Win11-style anchors (TrayNotifyWnd exists on both
            // families, so that positioning degrades gracefully anywhere).
            bool classical = false;
            IntPtr hBar = IntPtr.Zero, hMin = IntPtr.Zero;
            WindowInterop.RECT rcBar = default, rcMin = default;
            if (!IsWindows11Taskbar(taskbar))
            {
                for (int i = 0; i < 10; i++)
                {
                    if (IsWindows11Taskbar(taskbar)) break;   // the bridge child appeared meanwhile
                    if (TryFindClassicalChain(taskbar, out hBar, out hMin, out rcBar, out rcMin))
                    {
                        classical = true;
                        break;
                    }
                    Thread.Sleep(1000);
                    WindowInterop.GetWindowRect(taskbar, out taskbarRect);   // boot layout still settling
                }
            }
            if (!classical && !IsWindows11Taskbar(taskbar))
                Logger.Warn("任务栏族探测 10s 超时：既无 Win11 XAML 桥（DesktopWindowContentBridge）也无经典 ReBar 链——回退 Win11 锚点定位（TrayNotifyWnd 两族均有，优雅降级）");

            // Vertical = a side-docked classical taskbar (TrafficMonitor's
            // CheckTaskbarOnTopOrBottom: width >= height → horizontal). A side taskbar
            // transposes the overlay into the strips layout.
            bool vertical = classical && (taskbarRect.right - taskbarRect.left) < (taskbarRect.bottom - taskbarRect.top);
            _layoutVertical = vertical;   // mirror for the UI-thread layout accessors

            uint dpi = WindowInterop.GetDpiForWindow(taskbar);
            if (dpi == 0) dpi = USER_DEFAULT_SCREEN_DPI;
            // Win11: size to the taskbar band explorer actually RESERVES (work-area
            // strip), bottom-aligned inside the window — a taskbar-height mod can
            // leave Shell_TrayWnd taller than the reservation (TaskbarBand remarks).
            TaskbarBand(taskbar, taskbarRect, out int taskbarBandH, out int taskbarBandY);
            ComputeTargetSize(_samplingEnabledMask, vertical, dpi,
                vertical ? rcMin.right - rcMin.left : 0,
                classical ? rcBar.bottom - rcBar.top : taskbarBandH,
                out int physicalWidth, out int physicalHeight);
            // Floor at 1px: with every metric's sampling off the layout is 0-sized and
            // DXGI can't create 0-sized buffers — a 1px stub draws nothing (Draw skips
            // an empty layout) and resizes out the moment a metric comes back.
            physicalWidth = Math.Max(1, physicalWidth);
            physicalHeight = Math.Max(1, physicalHeight);
            float logicalHeight = physicalHeight * (float)USER_DEFAULT_SCREEN_DPI / dpi;
            Logger.Info($"任务栏族={(classical ? "经典(Win10/ExplorerPatcher)" : "Win11")} 竖直={vertical} DPI={dpi} 覆盖层={physicalWidth}x{physicalHeight}px 采样掩码=0x{_samplingEnabledMask:X2} 间隔={_sampleIntervalMs}ms{AnchorDiag()}");

            // Creation coords are screen-absolute (the window starts top-level; the exact
            // dock is applied relative to the parent after SetParent in step 11). The
            // Win11 path computes its anchor now; the classical path lands anywhere sane
            // and lets the forced RepositionOverlay in step 11 place it precisely.
            int xScreen, xRelative = 0, yScreen = taskbarRect.top + (classical ? 0 : taskbarBandY);
            if (!classical)
                (xScreen, xRelative) = CalcPosition(taskbar, taskbarRect, taskbarRect.right - taskbarRect.left, physicalWidth, dpi, _onLeft, _snapToStart);
            else
                xScreen = taskbarRect.left;

            // === Step 2: register window class ===
            IntPtr hInstance = WindowInterop.GetModuleHandleW(null);
            var wc = new WindowInterop.WNDCLASSW
            {
                lpfnWndProc = _wndProc,
                hInstance = hInstance,
                lpszClassName = "TaskMonitorTaskbar",
                hCursor = WindowInterop.LoadCursorW(IntPtr.Zero, WindowInterop.IDC_ARROW),
            };
            // ERROR_CLASS_ALREADY_EXISTS is fine: a re-entered Start() (the overlay is
            // recreated after an explorer restart) finds the class already registered
            // from the previous run — reuse it.
            if (WindowInterop.RegisterClassW(ref wc) == 0
                && Marshal.GetLastWin32Error() != WindowInterop.ERROR_CLASS_ALREADY_EXISTS)
            {
                Logger.Error($"RegisterClassW(TaskMonitorTaskbar) 失败 err={Marshal.GetLastWin32Error()}——本轮 Start() 放弃，等待重建");
                return;
            }

            // === Step 3: create top-level popup (NOREDIRECTIONBITMAP|TOPMOST|TOOLWINDOW|NOACTIVATE) ===
            IntPtr hwnd = WindowInterop.CreateWindowExW(
                WindowInterop.WS_EX_COMPOSITE_EX | WindowInterop.WS_EX_NOACTIVATE,
                "TaskMonitorTaskbar",
                "TaskMonitor",
                WindowInterop.WS_POPUP | WindowInterop.WS_VISIBLE,
                xScreen, yScreen, physicalWidth, physicalHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Logger.Error($"CreateWindowExW 失败 err={Marshal.GetLastWin32Error()}——本轮 Start() 放弃，等待重建");
                return;
            }
            OverlayHwnd = hwnd;

            // === Step 4: render state — the device pipeline is built INTO it ===
            // The pipeline lives on RenderState, never in Start() locals: the message loop
            // below never returns, so a local wrapper would stay rooted for the whole run
            // (the 2026-07-30 flood — ResizeBackBuffer's remark) — and RecoverDevice must be
            // able to dispose and rebuild it in place after a device loss.
            var sampler = new SystemSampler();
            // A fresh sampler defaults to all-metrics-on — apply the settings mask (a
            // re-entered Start after an explorer restart must not silently re-enable
            // metrics the user turned off).
            sampler.SetEnabledMask(_samplingEnabledMask);
            // Same re-apply for 合并相同程序 — a re-entered Start must not silently
            // reset the user's merge toggle either.
            sampler.SetMergeByPath(_mergeSamePathProcesses);
            // Same re-apply for the disk 显示方式 (mode + specific-disk index).
            sampler.SetDiskDisplay(_diskDisplayMode, _diskDisplayIndex);
            // Same re-apply for the GPU 显示方式 and the pinned network adapter.
            sampler.SetGpuDisplay(_gpuDisplayMode, _gpuDisplayIndex);
            sampler.SetNetAdapter(_netAdapterId);
            // Same re-apply for the Clash/Mihomo integration (switch + endpoint).
            sampler.SetClashApi(_clashEnabled, _clashApiAddress, _clashApiSecret);
            // Same re-apply for the 公网 IP lookup switch.
            sampler.SetPublicIpLookup(_publicIpLookupEnabled);
            _sampler = sampler;   // expose to UI-thread instance methods (RequestWifiDetails)
            var state = new RenderState
            {
                TaskbarHwnd = taskbar,
                Classical = classical,
                Vertical = vertical,
                BarHwnd = hBar,
                MinHwnd = hMin,
                LastMinLength = -1,          // no band measurement yet — the first reposition always applies
                PhysicalWidth = physicalWidth,
                PhysicalHeight = physicalHeight,
                LogicalHeight = logicalHeight,
                Dpi = dpi,
                Hovered = -1,
                TrackingMouse = false,
                Sampler = sampler,
                Snapshot = sampler.Sample(),
            };
            state.Selected = -1;
            state.ToggleCallback = ToggleCallback;
            state.RightClickRequested = RightClickRequested;
            state.Owner = this;
            state.Snapshot.SampleIntervalMs = _sampleIntervalMs;   // the views' tooltips read it
            _latestShared = state.Snapshot;   // publish the initial snapshot
            // Stash the state on the HWND BEFORE the pipeline exists: an init failure then
            // still unwinds through WM_DESTROY, which releases whatever the half-built
            // pipeline left behind (ReleaseDeviceResources).
            _stateHandle = GCHandle.Alloc(state);
            WindowInterop.SetWindowLongPtr(hwnd, WindowInterop.GWLP_USERDATA, GCHandle.ToIntPtr(_stateHandle));

            // === Step 5: DirectWrite text formats (font size in DIPs) ===
            // Stacked-group rows are "label … value": label flush-LEFT, value flush-RIGHT, so
            // every value lines up on the group's right edge (one right-aligned numeric column,
            // easy to scan across rows) and every label on its left. All three formats are
            // NO_WRAP: a taskbar cell is ONE line — text that overflows its cell must never
            // wrap to two lines. Labels/values are visually distinguished: labels draw at
            // reduced alpha (LabelBrush below) so they recede and the eye lands on the
            // full-alpha value first.
            var dwrite = DWriteFunctions.DWriteCreateFactory();
            var labelFormat = dwrite.CreateTextFormat("Segoe UI", 13f);
            labelFormat.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
            labelFormat.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            labelFormat.Object.SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
            var valueFormat = dwrite.CreateTextFormat("Segoe UI", 13f);
            valueFormat.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);
            valueFormat.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            valueFormat.Object.SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);

            // Net column's ↑/↓ rates flush-RIGHT to line up on the column's right edge,
            // matching the stacked groups' right-aligned numeric column.
            var netFormat = dwrite.CreateTextFormat("Segoe UI", 13f);
            netFormat.Object.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);
            netFormat.Object.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            netFormat.Object.SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
            // Device-INDEPENDENT (they come from the DWrite factory, not the D3D device):
            // created once here and kept across a device loss — RecoverDevice only rebuilds
            // the pipeline, never the text formats.
            state.LabelFormat = labelFormat;
            state.ValueFormat = valueFormat;
            state.NetFormat = netFormat;

            // === Step 6: the device pipeline (D3D11 → DXGI swap chain → D2D1 → DComp) ===
            // CreateDeviceResources is the SAME method the post-device-loss recovery runs
            // (RecoverDevice) — one construction site, so the two can never drift.
            // A failure here is not fatal and not reportable-as-crash material: a driver
            // update/reset leaves the adapter unavailable for a while, so latch the loss and
            // enter the message loop anyway — the tick rebuilds the pipeline the moment the
            // device answers again. The window stays put; nothing but the drawing depends on
            // the device.
            try
            {
                CreateDeviceResources(state, hwnd, physicalWidth, physicalHeight, dpi);
            }
            catch (Exception ex)
            {
                Logger.Error("D3D/D2D/DComp 管线初始化失败——覆盖层暂不绘制，待设备可用后自动重建", ex);
                OnDeviceLost(state, "启动初始化", default(HRESULT));
            }

            // === Step 7: first frame ===
            if (!state.DeviceLost)
            {
                ApplyTaskbarTheme(state, IsTaskbarLightThemed());   // tint brushes before first frame
                Draw(state);
            }

            // === Step 11: embed into the taskbar + position relative to parent ===
            // Parent: the Win11 taskbar takes us directly; the classical one docks us in
            // the ReBar (the band container), whose task-buttons toolbar is then shrunk
            // to make room (TrafficMonitor's GetParentHwnd split).
            IntPtr prevParent = WindowInterop.SetParent(hwnd, classical ? hBar : taskbar);
            if (prevParent == IntPtr.Zero)
                Logger.Error($"SetParent 嵌入任务栏失败（目标={(classical ? "ReBarWindow32" : "Shell_TrayWnd")}）err={Marshal.GetLastWin32Error()}——覆盖层浮为顶层窗口");
            else
                Logger.Info($"已嵌入任务栏（父={(classical ? "ReBarWindow32" : "Shell_TrayWnd")}）");
            if (classical)
            {
                // Carve out our slot (shrink/shift the band) and dock into it.
                RepositionOverlay(hwnd, state, force: true);
            }
            else
            {
                WindowInterop.MoveWindow(hwnd, xRelative, taskbarBandY, physicalWidth, physicalHeight, true);
                state.LastXRelative = xRelative;
                state.LastBandY = taskbarBandY;
            }
            WindowInterop.SetWindowPos(hwnd, WindowInterop.HWND_TOP, 0, 0, 0, 0,
                WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_SHOWWINDOW | WindowInterop.SWP_NOACTIVATE);
            WindowInterop.ShowWindow(hwnd, WindowInterop.SW_SHOWNOACTIVATE);

            // Baseline placement-geometry dump (see LogGeometry) — later per-tick
            // dumps are change-gated against these rects.
            LogGeometry(taskbar, hwnd, "嵌入");
            if (WindowInterop.GetWindowRect(taskbar, out var diagTb)) state.DiagTaskbarRect = diagTb;
            if (WindowInterop.GetWindowRect(hwnd, out var diagOv)) state.DiagOverlayRect = diagOv;
            state.DiagLogged = true;

            WindowInterop.SetTimer(hwnd, (IntPtr)TIMER_ID, (uint)_sampleIntervalMs, IntPtr.Zero);
            // Classical only: the 100ms band re-dock poll (explorer re-expands the band
            // as windows open/close — see TIMER_ID_POS).
            if (classical)
                WindowInterop.SetTimer(hwnd, (IntPtr)TIMER_ID_POS, POS_INTERVAL_MS, IntPtr.Zero);

            // === Step 12: message loop ===
            while (WindowInterop.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                WindowInterop.TranslateMessage(ref msg);
                WindowInterop.DispatchMessageW(ref msg);
            }

            // The window is gone (its taskbar parent died, or we self-destructed) —
            // clear the HWND so nothing posts to a dead window while the owner sleeps
            // before re-entering Start().
            Logger.Info("覆盖层消息循环结束（窗口已销毁）——Start() 返回");
            OverlayHwnd = IntPtr.Zero;
        }

        // --------------------------------------------------------------------
        // Rendering: the visible groups per the current layout (ComputeLayout — a metric
        // with its sampling off is GONE, not blank: the later metrics shift forward to
        // fill its row position, an emptied group vanishes and the window narrows; the
        // grid keeps its two-row structure everywhere). Rows are "label flush-left,
        // value flush-right"; 网络 keeps its ↑/↓ column. Hover/selection highlights the
        // individual hit slot (one row of a stacked group, or the whole 网络 column).
        // --------------------------------------------------------------------
        private static void Draw(RenderState s)
        {
            // No usable device: the rebuild belongs to RecoverDevice (the tick calls it), and
            // a dead context can only fail again — this is also what keeps the per-mouse-move
            // Draw calls harmless while the device is down.
            if (s.DeviceLost) return;

            var layout = ComputeLayout(s.Owner._samplingEnabledMask, s.Vertical);
            if (layout.Width <= 0f) return;   // every metric off — the overlay is a stub

            var ctx = s.D2dContext;
            ctx.BeginDraw();
            ctx.Clear();   // IntPtr.Zero → transparent

            if (layout.Vertical) DrawVertical(s, layout);
            else DrawHorizontal(s, layout);

            // EndDraw/Present report a LOST DEVICE as an HRESULT — read it instead of letting
            // DirectN's throwing helper turn it into a Win32Exception whose HResult is the
            // useless E_FAIL (see OnDeviceLost). A device-loss code is a recoverable condition,
            // not a crash; anything else still throws (the WndProc guard reports real bugs).
            var endHr = ctx.Object.EndDraw(IntPtr.Zero, IntPtr.Zero);   // NULL tags
            if (IsDeviceLost(s, endHr)) { OnDeviceLost(s, "EndDraw", endHr); return; }
            endHr.ThrowOnError();

            var presentHr = s.SwapChain.Object.Present(0, 0);   // DXGI_STATUS_OCCLUDED is success
            if (IsDeviceLost(s, presentHr)) { OnDeviceLost(s, "Present", presentHr); return; }
            presentHr.ThrowOnError();
        }

        // The two-row grid (the Win11 taskbar + horizontal classical taskbars).
        private static void DrawHorizontal(RenderState s, OverlayLayout layout)
        {
            var ctx = s.D2dContext;
            float pad = 4f;
            float top = pad;
            float bottom = s.LogicalHeight - pad;
            float mid = (top + bottom) / 2f;

            // Highlight (selected) / hover fill, per hit slot. Hidden slots have no rect.
            for (int i = 0; i < SlotCount; i++)
            {
                IComObject<ID2D1Brush> brush = null;
                if (i == s.Selected) brush = s.HighlightBrush;
                else if (i == s.Hovered) brush = s.HoverBrush;
                if (brush == null) continue;
                if (!TrySlotRect(layout, top, mid, bottom, i, out var hlRect)) continue;

                ctx.FillRoundedRectangle(
                    new D2D1_ROUNDED_RECT { rect = hlRect, radiusX = 6f, radiusY = 6f },
                    brush);
            }

            // Grid separators, framing each metric cell. 1px hairlines centred on the
            // boundary (FillRectangle, not DrawLine, so they stay crisp integer pixels).
            // Vertical lines sit at the left edge of every visible group except the first;
            // the horizontal mid line spans every visible group — the grid keeps its
            // two-row structure everywhere (a trailing hole is still a row).
            const float lineHalf = 0.5f;
            bool boundaryBefore = false;
            for (int g = 0; g < GroupCount; g++)
            {
                if (!layout.GroupVisible(g)) continue;
                float gx0 = layout.GroupLeft(g);
                float gx1 = gx0 + GroupWidths[g];
                if (boundaryBefore)
                    ctx.FillRectangle(new D2D_RECT_F { left = gx0 - lineHalf, top = top, right = gx0 + lineHalf, bottom = bottom }, s.SeparatorBrush);
                ctx.FillRectangle(new D2D_RECT_F { left = gx0, top = mid - lineHalf, right = gx1, bottom = mid + lineHalf }, s.SeparatorBrush);
                boundaryBefore = true;
            }

            // One row per visible slot. GPU falls back to "--" when no adapter exists at
            // all (or dxcore.dll is missing) — unrelated to the sampling switches.
            DrawSlotRow(ctx, s, layout, top, mid, bottom, 0, "CPU", $"{s.Snapshot.CpuPercent:F0}%");
            DrawSlotRow(ctx, s, layout, top, mid, bottom, 1, "内存", $"{s.Snapshot.RamPercent:F0}%");
            DrawSlotRow(ctx, s, layout, top, mid, bottom, 2, "磁盘", $"{s.Snapshot.DiskPercent:F0}%");
            DrawSlotRow(ctx, s, layout, top, mid, bottom, 3, "GPU", s.Snapshot.GpuAvailable ? $"{s.Snapshot.GpuPercent:F0}%" : "--");

            // 网络 column: ↑/↓ is the "label" (flush-left), the rate is the "value" (flush-right)
            // — same label/value split as the stacked groups, just stacked ↑ over ↓.
            if (TrySlotRect(layout, top, mid, bottom, 4, out var netRect))
            {
                const float netInset = 5f;
                var netUp   = new D2D_RECT_F { left = netRect.left + netInset, top = top, right = netRect.right - netInset, bottom = mid };
                var netDown = new D2D_RECT_F { left = netRect.left + netInset, top = mid, right = netRect.right - netInset, bottom = bottom };
                ctx.DrawText("↑", s.LabelFormat, netUp, s.LabelBrush);
                ctx.DrawText(NetRateFormatter.Format(s.Snapshot.NetUpBytesPerSec), s.NetFormat, netUp, s.TextBrush);
                ctx.DrawText("↓", s.LabelFormat, netDown, s.LabelBrush);
                ctx.DrawText(NetRateFormatter.Format(s.Snapshot.NetDownBytesPerSec), s.NetFormat, netDown, s.TextBrush);
            }
        }

        // The transposed layout for a side-docked classical taskbar: full-width strips —
        // one per visible stacked metric, two for 网络 (↑ over ↓) — with hairlines
        // between strips. Text stays horizontal (TrafficMonitor's vertical mode).
        private static void DrawVertical(RenderState s, OverlayLayout layout)
        {
            var ctx = s.D2dContext;

            // Highlight (selected) / hover fill, per hit slot (strips here, cells above).
            for (int i = 0; i < SlotCount; i++)
            {
                IComObject<ID2D1Brush> brush = null;
                if (i == s.Selected) brush = s.HighlightBrush;
                else if (i == s.Hovered) brush = s.HoverBrush;
                if (brush == null) continue;
                if (!TrySlotRect(layout, 0f, 0f, 0f, i, out var hlRect)) continue;

                ctx.FillRoundedRectangle(
                    new D2D1_ROUNDED_RECT { rect = hlRect, radiusX = 6f, radiusY = 6f },
                    brush);
            }

            // Hairlines between strips (the transposed mid line; 网络's ↑/↓ split included).
            const float lineHalf = 0.5f;
            int strips = PackedStackedCount(layout.SlotMask) + (layout.Left2 >= 0f ? 2 : 0);
            for (int i = 1; i < strips; i++)
            {
                float y = i * STRIP_H;
                ctx.FillRectangle(new D2D_RECT_F { left = 0f, top = y - lineHalf, right = layout.Width, bottom = y + lineHalf }, s.SeparatorBrush);
            }

            // One strip per visible slot (GPU falls back to "--" without an adapter).
            DrawSlotRow(ctx, s, layout, 0f, 0f, 0f, 0, "CPU", $"{s.Snapshot.CpuPercent:F0}%");
            DrawSlotRow(ctx, s, layout, 0f, 0f, 0f, 1, "内存", $"{s.Snapshot.RamPercent:F0}%");
            DrawSlotRow(ctx, s, layout, 0f, 0f, 0f, 2, "磁盘", $"{s.Snapshot.DiskPercent:F0}%");
            DrawSlotRow(ctx, s, layout, 0f, 0f, 0f, 3, "GPU", s.Snapshot.GpuAvailable ? $"{s.Snapshot.GpuPercent:F0}%" : "--");

            // 网络's two strips: ↑/↓ flush-left labels, rates flush-right — the same
            // split as the horizontal column, two strips instead of two half-rows.
            if (TrySlotRect(layout, 0f, 0f, 0f, 4, out var netRect))
            {
                const float netInset = 5f;
                float midY = netRect.top + STRIP_H;
                var netUp   = new D2D_RECT_F { left = netRect.left + netInset, top = netRect.top, right = netRect.right - netInset, bottom = midY };
                var netDown = new D2D_RECT_F { left = netRect.left + netInset, top = midY, right = netRect.right - netInset, bottom = netRect.bottom };
                ctx.DrawText("↑", s.LabelFormat, netUp, s.LabelBrush);
                ctx.DrawText(NetRateFormatter.Format(s.Snapshot.NetUpBytesPerSec), s.NetFormat, netUp, s.TextBrush);
                ctx.DrawText("↓", s.LabelFormat, netDown, s.LabelBrush);
                ctx.DrawText(NetRateFormatter.Format(s.Snapshot.NetDownBytesPerSec), s.NetFormat, netDown, s.TextBrush);
            }
        }

        // One metric's row: label flush-left and value flush-right against the same cell
        // rect — values form one right-aligned column on the group's right edge, labels a
        // left-aligned column on its left, with the gap floating between them. A hidden
        // slot (sampling off) has no rect and draws nothing.
        private static void DrawSlotRow(
            IComObject<ID2D1DeviceContext> ctx, RenderState s, OverlayLayout layout,
            float top, float mid, float bottom, int slot, string label, string value)
        {
            if (!TrySlotRect(layout, top, mid, bottom, slot, out var r)) return;
            const float inset = 5f;
            var cell = new D2D_RECT_F { left = r.left + inset, top = r.top, right = r.right - inset, bottom = r.bottom };
            ctx.DrawText(label, s.LabelFormat, cell, s.LabelBrush);
            ctx.DrawText(value, s.ValueFormat, cell, s.TextBrush);
        }

        // One sampling pass: sample → stamp the cadence → publish → push to the UI thread →
        // redraw. Shared by the WM_TIMER tick and WM_APP_SET_METRICS (an immediate re-sample
        // after a 设置 → 采样 toggle).
        private static void SamplePublishDraw(RenderState s)
        {
            s.Snapshot = s.Sampler.Sample();
            if (s.Owner != null)
            {
                s.Snapshot.SampleIntervalMs = s.Owner._sampleIntervalMs;
                s.Owner._latestShared = s.Snapshot;   // publish
                s.Owner.SnapshotChanged?.Invoke();    // push → UI refresh
            }
            Draw(s);
        }

        // --------------------------------------------------------------------
        // Device loss: the D3D device can vanish under us at any moment — a GPU driver
        // update/reset (the reported case), a TDR, a laptop GPU switch or a remote-session
        // handover. The whole pipeline then has to be rebuilt, which is exactly what the
        // D2D error message says ("recreate, rerender the entire frame and reattempt
        // present").
        //
        // Two rules, both learned from the 2026-08-08 report (崩溃报告 #1..#25, one per tick,
        // WndProc msg=0x113):
        //   1. NEVER let it reach the crash channel. `ctx.EndDraw()` is DirectN's THROWING
        //      helper: it raises a Win32Exception whose HResult is E_FAIL — the real code
        //      (D2DERR_RECREATE_TARGET) is already lost by then, so a handler cannot tell a
        //      driver update from a genuine bug. Read the HRESULT instead (Draw,
        //      ResizeBackBuffer).
        //   2. Rebuild in place. The overlay window, its taskbar embed, the sampler and the
        //      geometry all stay; only the pipeline is recreated, so recovery costs one
        //      frozen frame and needs no re-embed (the expensive/fragile part of the
        //      explorer-restart path).
        // --------------------------------------------------------------------

        // The classic "recreate the target" codes. D2D can also answer something generic once
        // BeginDraw has already failed on a removed device (WRONG_STATE), which is why the
        // classification falls back to asking D3D directly (DeviceGone).
        private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);
        private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        private const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
        private const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
        private const int DXGI_ERROR_DRIVER_INTERNAL_ERROR = unchecked((int)0x887A0020);

        private static bool IsDeviceLost(RenderState s, HRESULT hr)
        {
            if (!hr.IsError) return false;
            switch (hr.Value)
            {
                case D2DERR_RECREATE_TARGET:
                case DXGI_ERROR_DEVICE_REMOVED:
                case DXGI_ERROR_DEVICE_HUNG:
                case DXGI_ERROR_DEVICE_RESET:
                case DXGI_ERROR_DRIVER_INTERNAL_ERROR:
                    return true;
                default:
                    return DeviceGone(s);
            }
        }

        // D3D's own verdict (S_OK = alive) — the catch-all for a loss reported under an
        // unrecognized HRESULT, and the only check available when a failure arrives as an
        // exception (DirectN's throwing helpers) rather than a return value.
        private static bool DeviceGone(RenderState s)
        {
            try
            {
                var hr = s.D3dDevice?.Object?.GetDeviceRemovedReason();
                return hr.HasValue && hr.Value.IsError;
            }
            catch { return false; }
        }

        // Latch the loss: drawing stops (Draw early-returns) until RecoverDevice succeeds.
        // Logged once per loss — the latch is what makes a driver update ONE line instead of
        // one per tick.
        private static void OnDeviceLost(RenderState s, string stage, HRESULT hr)
        {
            if (s.DeviceLost) return;
            s.DeviceLost = true;
            s.DeviceLostTicks = 0;
            string code = hr.Value != 0 ? $"{hr.Name} 0x{(uint)hr.Value:X8}" : "设备已移除";
            Logger.Warn($"D3D 设备丢失（{stage}：{code}）——通常是显卡驱动更新/重置或 TDR；暂停绘制，待设备可用后自动重建管线（窗口与任务栏嵌入保持不变）");
        }

        // Build the whole device pipeline (D3D11 → DXGI swap chain → D2D1 → DComp) for the
        // given physical size/DPI and point the context at the swap chain's back buffer.
        // Called from Start() and from RecoverDevice — keep it the ONLY construction site.
        // On entry the pipeline fields must be empty (ReleaseDeviceResources ran); a throw
        // midway leaves them partially filled, and the next attempt cleans up first.
        private static void CreateDeviceResources(RenderState s, IntPtr hwnd, int width, int height, uint dpi)
        {
            // DXGI can't create 0-sized buffers: with every metric's sampling off the layout
            // collapses to a stub (0-width), which the window floors at 1px.
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            s.D3dDevice = D3D11Functions.D3D11CreateDevice(
                null,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                out _);
            s.DxgiDevice = s.D3dDevice.As<IDXGIDevice>();      // for D2D device + DComposition
            s.DxgiDevice1 = s.D3dDevice.As<IDXGIDevice1>();    // for swap chain

            s.DxgiFactory = DXGIFunctions.CreateDXGIFactory2<IDXGIFactory2>();
            s.SwapChain = s.DxgiFactory.Object.CreateSwapChainForComposition<IDXGISwapChain1>(s.DxgiDevice1, new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Stereo = false,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = Constants.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
                Flags = 0,
            });

            // D2D1: factory → device → context → bitmap target (the back buffer, wrapped).
            s.D2dFactory = D2D1Functions.D2D1CreateFactory1(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED);
            s.D2dDevice = s.D2dFactory.Object.CreateDevice<ID2D1Device>(s.DxgiDevice);
            s.D2dContext = s.D2dDevice.CreateDeviceContext<ID2D1DeviceContext>(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE);
            s.D2dContext.Object.SetDpi(dpi, dpi);
            // The back-buffer wrappers live on the state, NOT in locals: DXGI rejects a
            // ResizeBuffers while ANY reference to a back buffer is alive, and a DirectN
            // wrapper only releases on Dispose/finalization (ResizeBackBuffer's remark).
            s.BackBufferSurface = s.SwapChain.GetBuffer<IDXGISurface>(0);
            s.BackBufferBitmap = s.D2dContext.CreateBitmapFromDxgiSurface<ID2D1Bitmap1>(s.BackBufferSurface, BitmapProps(dpi));
            s.D2dContext.SetTarget(s.BackBufferBitmap);

            // Brushes (varying alpha; colour re-tinted by ApplyTaskbarTheme). LabelBrush is
            // the dimmed companion of TextBrush: labels recede, values pop. Created
            // black-on-light; the CALLER re-tints them (ApplyTaskbarTheme) before drawing.
            s.TextBrush = s.D2dContext.CreateSolidColorBrush(new _D3DCOLORVALUE { r = 0f, g = 0f, b = 0f, a = 1f });
            s.LabelBrush = s.D2dContext.CreateSolidColorBrush(new _D3DCOLORVALUE { r = 0f, g = 0f, b = 0f, a = 0.7f });
            s.HighlightBrush = s.D2dContext.CreateSolidColorBrush(new _D3DCOLORVALUE { r = 0f, g = 0f, b = 0f, a = 0.15f });
            s.HoverBrush = s.D2dContext.CreateSolidColorBrush(new _D3DCOLORVALUE { r = 0f, g = 0f, b = 0f, a = 0.07f });
            s.SeparatorBrush = s.D2dContext.CreateSolidColorBrush(new _D3DCOLORVALUE { r = 0f, g = 0f, b = 0f, a = 0.12f });

            // DirectComposition → bind the swap chain to the window. The target and the visual
            // are kept so ReleaseDeviceResources can UNBIND them before a rebuilt pipeline
            // binds its own target to the same HWND (the old ones are still alive as RCWs).
            Functions.DCompositionCreateDevice(s.DxgiDevice, typeof(IDCompositionDevice).GUID, out IntPtr dcompPtr).ThrowOnError();
            s.DComp = new ComObject<IDCompositionDevice>(
                (IDCompositionDevice)Marshal.GetTypedObjectForIUnknown(dcompPtr, typeof(IDCompositionDevice)));
            // Stored the moment they exist (not after the wiring below): a throw mid-wiring must
            // still leave teardown able to unbind them.
            s.DComp.Object.CreateTargetForHwnd(hwnd, true, out var target).ThrowOnError();
            s.DCompTarget = target;
            s.DComp.Object.CreateVisual(out var visual).ThrowOnError();
            s.DCompVisual = visual;
            visual.SetContent(s.SwapChain.Object).ThrowOnError();
            target.SetRoot(visual).ThrowOnError();
            s.DComp.Object.Commit().ThrowOnError();
        }

        // Tear the pipeline down — every step best-effort: a REMOVED device answers calls with
        // an error HRESULT (or DirectN's throwing helper) and must not stop the teardown, and
        // WM_DESTROY runs this on the way out too. Order matters: composition first (it holds
        // the swap chain and the HWND binding), then the D2D target/brushes/context, then
        // DXGI, and the D3D device last. Every field is nulled, which is also what makes the
        // `?.` guards at the Commit/SetDpi call sites safe while the pipeline is down.
        private static void ReleaseDeviceResources(RenderState s)
        {
            try { s.DCompVisual?.SetContent(null); } catch { }
            try { s.DCompTarget?.SetRoot(null); } catch { }
            try { s.DComp?.Object?.Commit(); } catch { }
            // Release the target/visual RCWs NOW, not at GC time. DirectComposition keeps the
            // HWND bound to its target until the target object is destroyed, and the rebuilt
            // pipeline's CreateTargetForHwnd then fails with
            // DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED (0x889B0011) — verified against a
            // forced loss: nulling the fields (RCWs are only collectable, not released) is NOT
            // enough. These two are separate objects handed out by the DComp device, so the
            // final release cannot disturb anything else we hold.
            ReleaseRcw(s.DCompTarget);
            ReleaseRcw(s.DCompVisual);
            s.DCompVisual = null;
            s.DCompTarget = null;
            s.DComp?.Dispose();
            s.DComp = null;

            try { s.D2dContext?.SetTarget(null); } catch { }
            s.BackBufferBitmap?.Dispose();
            s.BackBufferBitmap = null;
            s.BackBufferSurface?.Dispose();
            s.BackBufferSurface = null;
            s.TextBrush?.Dispose();
            s.TextBrush = null;
            s.LabelBrush?.Dispose();
            s.LabelBrush = null;
            s.HighlightBrush?.Dispose();
            s.HighlightBrush = null;
            s.HoverBrush?.Dispose();
            s.HoverBrush = null;
            s.SeparatorBrush?.Dispose();
            s.SeparatorBrush = null;
            s.D2dContext?.Dispose();
            s.D2dContext = null;
            s.SwapChain?.Dispose();
            s.SwapChain = null;
            s.D2dDevice?.Dispose();
            s.D2dDevice = null;
            s.D2dFactory?.Dispose();
            s.D2dFactory = null;
            s.DxgiFactory?.Dispose();
            s.DxgiFactory = null;
            s.DxgiDevice1 = null;    // raw QI'd RCWs (DirectN generated no wrapper here):
            s.DxgiDevice = null;     // dropping the last field reference is what releases them
            s.D3dDevice?.Dispose();
            s.D3dDevice = null;
        }

        // Deterministically drop a raw (un-wrapped) COM reference — DirectN generates no
        // IDisposable wrapper for IDCompositionTarget/IDCompositionVisual, and for the
        // composition target the release timing is semantically load-bearing (above).
        private static void ReleaseRcw(object rcw)
        {
            try { if (rcw != null) Marshal.FinalReleaseComObject(rcw); }
            catch { /* already released / not a COM object — nothing to do */ }
        }

        // Rebuild the pipeline after a device loss, in place. Called from the tick while
        // DeviceLost is set; failure is NOT fatal and NOT crash material — a driver update
        // leaves the adapter unavailable for a while (mid-install the machine may even sit on
        // the basic display driver), so the caller just retries on the next tick. The swap
        // chain is created from the CURRENT size/DPI fields, so a DPI or layout change that
        // landed during the outage is applied by the rebuild itself (ResizeBackBuffer's
        // early-return keeps those fields current while the device is down).
        private static bool RecoverDevice(IntPtr hwnd, RenderState s)
        {
            try
            {
                ReleaseDeviceResources(s);
                CreateDeviceResources(s, hwnd, s.PhysicalWidth, s.PhysicalHeight, s.Dpi);
                s.DeviceLost = false;
                s.DeviceLostTicks = 0;
                ApplyTaskbarTheme(s, IsTaskbarLightThemed());   // fresh brushes are black-on-light
                Draw(s);
                if (s.DeviceLost) return false;   // the redraw lost it again (repeated TDR) — retry
                Logger.Info("D3D 设备已重建——覆盖层恢复绘制（窗口与任务栏嵌入未变）");
                return true;
            }
            catch (Exception ex)
            {
                s.DeviceLostTicks++;
                // Full detail on the first failure, then every 30th attempt: a driver swap can
                // keep the device unavailable for a minute and a per-tick WARN would flood.
                if (s.DeviceLostTicks == 1 || s.DeviceLostTicks % 30 == 0)
                    Logger.Warn($"D3D 设备重建失败（第 {s.DeviceLostTicks} 次尝试，设备可能仍不可用）——下一 tick 继续重试", ex);
                return false;
            }
        }

        // --------------------------------------------------------------------
        // Recreate render target for a new DPI (WM_DPICHANGED / poll).
        // --------------------------------------------------------------------

        // Retarget the D2D context at a fresh swap-chain back buffer of the given size.
        // The OLD surface/bitmap wrappers are DISPOSED before ResizeBuffers: DXGI rejects
        // it with DXGI_ERROR_INVALID_CALL while ANY reference to a back buffer is still
        // alive — and a DirectN wrapper only releases its reference on Dispose (or GC
        // finalization, whose timing can't be depended on; the 2026-07-30 crash.log flood
        // came from a Start()-local wrapper pinned alive forever by the never-returning
        // message loop under Debug JIT). SetTarget(null) alone is NOT enough — that drops
        // only the context's reference, not the wrapper's.
        private static void ResizeBackBuffer(RenderState s, int width, int height, uint dpi)
        {
            // Device down: nothing to resize. Deliberately a silent no-op rather than a bounce
            // to the caller — the callers still record the new size/DPI fields, and
            // RecoverDevice builds its swap chain from exactly those (a DPI or layout change
            // during the outage is therefore not lost, just deferred).
            if (s.DeviceLost) return;

            s.D2dContext.SetTarget(null);
            s.BackBufferBitmap?.Dispose();
            s.BackBufferSurface?.Dispose();
            s.BackBufferBitmap = null;
            s.BackBufferSurface = null;

            // HRESULT, not the throwing extension: a device loss surfaces HERE too (the driver
            // usually dies between two ticks, and a taskbar-height/DPI change is a resize).
            var resizeHr = s.SwapChain.Object.ResizeBuffers(
                2, (uint)width, (uint)height,
                DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 0);
            if (IsDeviceLost(s, resizeHr)) { OnDeviceLost(s, "ResizeBuffers", resizeHr); return; }
            resizeHr.ThrowOnError();

            try
            {
                s.BackBufferSurface = s.SwapChain.GetBuffer<IDXGISurface>(0);
                s.BackBufferBitmap = s.D2dContext.CreateBitmapFromDxgiSurface<ID2D1Bitmap1>(s.BackBufferSurface, BitmapProps(dpi));
                s.D2dContext.SetTarget(s.BackBufferBitmap);
            }
            catch (Exception) when (DeviceGone(s))
            {
                // GetBuffer/CreateBitmapFromDxgiSurface go through DirectN's throwing helpers,
                // so the code is already lost — ask D3D instead (classification catch-all).
                OnDeviceLost(s, "GetBuffer/CreateBitmapFromDxgiSurface", default(HRESULT));
            }
        }

        private static void HandleDpiChange(IntPtr hwnd, uint newDpi, RenderState s)
        {
            GetBandSize(s, out int bandW, out int bandH);
            if (!s.Vertical && bandH <= 0) return;   // the band isn't laid out yet
            if (s.Vertical && bandW <= 0) return;
            ComputeTargetSize(s.Owner._samplingEnabledMask, s.Vertical, newDpi, bandW, bandH,
                out int newPhysicalWidth, out int newPhysicalHeight);
            Logger.Info($"DPI 变化 {s.Dpi}→{newDpi}——覆盖层重设 {newPhysicalWidth}x{newPhysicalHeight}px");

            // Empty layout (every metric's sampling off): no buffers to resize — just
            // track the new dpi/size and collapse the window to a stub (the size fields
            // must stay current: a later re-enable draws against them).
            if (newPhysicalWidth <= 0 || newPhysicalHeight <= 0)
            {
                s.Dpi = newDpi;
                s.PhysicalWidth = Math.Max(0, newPhysicalWidth);
                s.PhysicalHeight = Math.Max(0, newPhysicalHeight);
                s.LogicalHeight = s.PhysicalHeight * (float)USER_DEFAULT_SCREEN_DPI / newDpi;
                RepositionOverlay(hwnd, s, force: true);
                return;
            }

            // Null-while-down safe (ReleaseDeviceResources nulls the context; a dead device
            // answers SetDpi with an ignored error): the new DPI still lands in s.Dpi below and
            // the rebuilt context picks it up through CreateDeviceResources.
            s.D2dContext?.Object?.SetDpi(newDpi, newDpi);
            ResizeBackBuffer(s, newPhysicalWidth, newPhysicalHeight, newDpi);

            s.PhysicalWidth = newPhysicalWidth;
            s.PhysicalHeight = newPhysicalHeight;
            s.LogicalHeight = newPhysicalHeight * (float)USER_DEFAULT_SCREEN_DPI / newDpi;
            s.Dpi = newDpi;

            RepositionOverlay(hwnd, s, force: true);   // the anchor depends on our size

            s.DComp?.Object?.Commit();   // null while the device is down (ReleaseDeviceResources)
            Draw(s);
        }

        // --------------------------------------------------------------------
        // Resize the overlay to the current layout's size (a 设置 → 采样 toggle hid or
        // revealed slots). The HandleDpiChange dance driven by ComputeTargetSize: the
        // swap chain must be resized with the window, and the anchor recomputed (it
        // depends on our size). Horizontal modes only ever change width; a vertical
        // classical taskbar changes height. A no-op when nothing actually changed —
        // e.g. toggling CPU↔内存 inside a group keeps the window the same size.
        // --------------------------------------------------------------------
        private static void ResizeForLayout(IntPtr hwnd, RenderState s)
        {
            GetBandSize(s, out int bandW, out int bandH);
            ComputeTargetSize(s.Owner._samplingEnabledMask, s.Vertical, s.Dpi, bandW, bandH,
                out int newWidth, out int newHeight);
            if (newWidth == s.PhysicalWidth && newHeight == s.PhysicalHeight) return;
            Logger.Debug($"采样布局变化——覆盖层重设 {s.PhysicalWidth}x{s.PhysicalHeight}→{newWidth}x{newHeight}px");

            // Every metric off: collapse to a 0-sized stub. DXGI can't resize to 0, so
            // the old buffers stay until a metric comes back; Draw no-ops meanwhile.
            if (newWidth <= 0 || newHeight <= 0)
            {
                s.PhysicalWidth = Math.Max(0, newWidth);
                s.PhysicalHeight = Math.Max(0, newHeight);
                RepositionOverlay(hwnd, s, force: true);
                return;
            }

            ResizeBackBuffer(s, newWidth, newHeight, s.Dpi);

            s.PhysicalWidth = newWidth;
            s.PhysicalHeight = newHeight;
            s.LogicalHeight = newHeight * (float)USER_DEFAULT_SCREEN_DPI / s.Dpi;
            RepositionOverlay(hwnd, s, force: true);   // the anchor depends on our size
            s.DComp?.Object?.Commit();   // null while the device is down (ReleaseDeviceResources)
        }

        // --------------------------------------------------------------------
        // Position — WIN11 taskbar family only (the classical family docks via
        // ClassicalReposition). Right (the default): just left of the system tray.
        // Left (opt-in via the settings page → settings.yaml): the taskbar's far-left
        // corner, or snapped just left of the Start button — honored only while the
        // Windows taskbar is CENTRE-aligned; a left-aligned taskbar has no room left of
        // Start, so left placement silently falls back to the right side. Ported
        // from TrafficMonitor's Win11 path (Win11TaskbarDlg::AdjustTaskbarWndPos),
        // including its Widgets-button reserve: with the Widgets button shown, the
        // far-left anchor shifts right by 160px, and the right-side position on a
        // left-aligned taskbar shifts left by the same reserve.
        // --------------------------------------------------------------------
        private static (int screen, int relative) CalcPosition(IntPtr taskbar, WindowInterop.RECT taskbarRect, int taskbarWidth, int windowWidth, uint dpi, bool onLeft, bool snapToStart)
        {
            int taskbarLeft = taskbarRect.left;
            int spacing = DpiScaleInt(2, dpi);

            if (onLeft && IsTaskbarCenterAligned())
            {
                int xrel = -1;
                if (snapToStart)
                {
                    // Snap just left of the Start button — follows the centred icon
                    // group as icons come and go (tracked by the per-tick poll).
                    IntPtr start = WindowInterop.FindWindowExW(taskbar, IntPtr.Zero, "Start", null);
                    if (start != IntPtr.Zero && WindowInterop.GetWindowRect(start, out var startRect))
                        xrel = startRect.left - taskbarLeft - windowWidth - spacing;
                }
                else
                {
                    // Far-left corner; reserve 160px for the Widgets button when it is
                    // shown (TrafficMonitor's taskbar_left_space_win11), else we'd
                    // render underneath it.
                    xrel = spacing + (IsWidgetsButtonShown() ? WidgetsReserve(dpi) : 0);
                }
                // Start not found / no room on the left → fall through to the right side.
                if (xrel >= 0 && xrel + windowWidth <= taskbarWidth)
                    return (taskbarLeft + xrel, xrel);
            }
            onLeft = false;   // resolved to the right-hand anchor below

            int xs;
            int xr;
            IntPtr tray = WindowInterop.FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && WindowInterop.GetWindowRect(tray, out var trayRect))
            {
                xs = trayRect.left - windowWidth - spacing;
                xr = trayRect.left - taskbarLeft - windowWidth - spacing;
            }
            else
            {
                int fallback = DpiScaleInt(88, dpi);
                xs = taskbarRect.right - windowWidth - fallback;
                xr = taskbarWidth - windowWidth - fallback;
            }
            // TrafficMonitor's avoid_overlap_with_widgets: a LEFT-aligned Win11 taskbar
            // with the Widgets button shown lays its icon group out from the far left,
            // so the 160px reserve keeps us clear of the widgets button there. The
            // !onLeft guard matters: when the caller asked for the left side but the
            // Start button is missing / there is no room, the code above lands on this
            // right-hand anchor instead — and the reserve has no business being applied
            // there (it just shoves the overlay leftwards, off the screen on a narrow
            // taskbar; a real log had the overlay land at x=-1199 that way).
            if (IsWidgetsButtonShown() && !onLeft && !IsTaskbarCenterAligned())
            {
                int reserve = WidgetsReserve(dpi);
                xs -= reserve;
                xr -= reserve;
            }
            // The tray-based anchor can land outside the taskbar (narrow taskbar, tray
            // read failure, the reserve above) and an off-screen overlay renders
            // nothing — keep it inside, degraded rather than invisible.
            xr = Math.Max(0, Math.Min(xr, taskbarWidth - windowWidth));
            xs = Math.Max(0, Math.Min(xs, taskbarRect.right - windowWidth));
            return (xs, xr);
        }

        // TrafficMonitor's registry checks (WindowsSettingHelper.cpp), same key and same
        // missing-value defaults: TaskbarAl absent = centre-aligned, TaskbarDa absent =
        // Widgets button shown. Read per CalcPosition call (startup / DPI change / 1s
        // poll) — a Registry.GetValue is sub-millisecond.
        // The widgets check carries one extra guard on top of the value, and it is NOT
        // optional (TrafficMonitor's IsTaskbarWidgetsBtnShown): without the Windows Web
        // Experience Pack the widgets button cannot exist at all, so the "absent = shown"
        // default must not apply. Windows only writes TaskbarDa once the user has touched
        // 任务栏设置 → 小组件, so on a machine where it was never set the value is absent
        // AND the pack may well not be installed — reading the default alone reserves
        // 160px for a button that can never appear. That is the reported symptom
        // （未开启小组件却空出小组件的位置）; upstream fixed the same thing in
        // TrafficMonitor issue #1958.
        private static bool IsTaskbarCenterAligned() => ReadExplorerAdvancedDword("TaskbarAl", 1) != 0;
        private static bool IsWidgetsButtonShown() =>
            IsWebExperiencePackDetected() && ReadExplorerAdvancedDword("TaskbarDa", 1) != 0;

        private static int WidgetsReserve(uint dpi) => DpiScaleInt(160, dpi);

        // Widgets-reserve diagnostics for the startup log (AnchorDiag) — resolved once and
        // remembered, so that line states the same value CalcPosition acts on instead of
        // re-deriving it. A plain bool, not Lazy<T>: the startup log asks from the taskbar
        // thread while the UI thread may hold the accessors, and the lock also protects the
        // reason string. See EvaluateWidgetsReserve.
        private static readonly object _widgetsReserveSync = new object();
        private static bool _widgetsReserveShown;
        private static bool _widgetsReserveEvaluated;
        private static string _widgetsReserveReason = "";
        private static bool? _webExperiencePackDetected;

        private static string AnchorDiag()
        {
            bool shown = EvaluateWidgetsReserve();
            string align = IsTaskbarCenterAligned() ? "居中" : "靠左";
            if (!shown)
                return $"，任务栏对齐={align}，小组件预留=无（{_widgetsReserveReason}）";
            uint dpi = WindowInterop.GetDpiForWindow(WindowInterop.FindWindowW("Shell_TrayWnd", null));
            return $"，任务栏对齐={align}，小组件预留={WidgetsReserve(dpi)}px";
        }

        private static bool EvaluateWidgetsReserve()
        {
            lock (_widgetsReserveSync)
            {
                if (!_widgetsReserveEvaluated)
                {
                    bool pack = IsWebExperiencePackDetected();
                    _widgetsReserveShown = pack && ReadExplorerAdvancedDword("TaskbarDa", 1) != 0;
                    _widgetsReserveReason = !pack
                        ? "未安装 Web Experience 包"        // the widgets button cannot exist here
                        : _widgetsReserveShown ? "TaskbarDa≠0" : "TaskbarDa=0";
                    _widgetsReserveEvaluated = true;
                }
                return _widgetsReserveShown;
            }
        }

        // "Is the Windows Web Experience Pack installed for the current user?" —
        // TrafficMonitor's WindowsWebExperienceDetector::IsDetected, which asks the WinRT
        // PackageManager for the package family name + the Microsoft Windows publisher
        // subject. net48 has no WinRT projection, so the same question goes to the user's
        // AppX package repository, which is what PackageManager itself reads: one subkey
        // per package registered for this user, named <full package name>. Asked once, and
        // only from EvaluateWidgetsReserve.
        //
        // The path is the CLASSES route, not Software\Microsoft\Windows\...\AppModel:
        // HKCU\Software\Classes\Local Settings is a junction and the repository is
        // reachable ONLY through it — the direct Software\Microsoft\... spelling of the
        // same path opens as null (verified on 26200). A null key just means "no pack".
        //
        // Conservative on failure: a package this user cannot see counts as absent, and
        // absent means NO reserve — i.e. exactly today's behaviour when TaskbarDa is 0.
        private static bool IsWebExperiencePackDetected()
        {
            lock (_widgetsReserveSync)
            {
                if (_webExperiencePackDetected == null)
                {
                    const string PackagePrefix = "MicrosoftWindows.Client.WebExperience_";
                    bool detected = false;
                    try
                    {
                        using (var repo = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                        {
                            detected = HasSubKeyWithPrefix(repo, PackagePrefix);
                        }
                    }
                    catch { detected = false; }
                    _webExperiencePackDetected = detected;
                }
                return _webExperiencePackDetected.Value;
            }
        }

        private static bool HasSubKeyWithPrefix(Microsoft.Win32.RegistryKey key, string prefix)
        {
            if (key == null) return false;      // no repository at all (not Windows 11?)
            string[] names;
            try { names = key.GetSubKeyNames(); } catch { return false; }
            if (names == null) return false;
            foreach (string name in names)
                if (name != null && name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static int ReadExplorerAdvancedDword(string valueName, int defaultValue)
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    valueName, defaultValue);
                return v is int i ? i : defaultValue;
            }
            catch { return defaultValue; }
        }

        // The taskbar follows the SYSTEM theme, not the in-app 主题 setting — the overlay
        // must match the surface it sits on. HKCU\...\Themes\Personalize\SystemUsesLightTheme:
        // 1 = light taskbar, 0 = dark, absent = dark (Win10's default; Win11 always writes
        // it). Read at startup and on the 1s tick, same sub-millisecond pattern as the
        // Explorer anchors above; accent-coloured/high-contrast taskbars are out of scope.
        private static bool IsTaskbarLightThemed()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 0);
                return v is int i && i != 0;
            }
            catch { return false; }
        }

        // Black text on a light taskbar, white on a dark one; the alphas are fixed so
        // selection/hover/separators keep their relative subtlety on either background.
        private static void ApplyTaskbarTheme(RenderState s, bool light)
        {
            s.LightTaskbar = light;
            float c = light ? 0f : 1f;
            SetBrushColor(s.TextBrush, c, 1f);
            SetBrushColor(s.LabelBrush, c, 0.7f);
            SetBrushColor(s.HighlightBrush, c, 0.15f);
            SetBrushColor(s.HoverBrush, c, 0.07f);
            SetBrushColor(s.SeparatorBrush, c, 0.12f);
        }

        private static void SetBrushColor(IComObject<ID2D1Brush> brush, float rgb, float a)
        {
            // Every brush is solid-colour (Start's step 8), so the As<> QI is a same-object
            // cast and SetColor mutates in place — a theme flip needs no brush recreation.
            var color = new _D3DCOLORVALUE { r = rgb, g = rgb, b = rgb, a = a };
            brush.As<ID2D1SolidColorBrush>().SetColor(ref color);
        }

        // --------------------------------------------------------------------
        // Keep the overlay glued to its anchor. The anchors move without notice: the
        // user flipping the taskbar between centre/left alignment, the centred icon
        // group resizing (the Start button slides), the tray gaining/losing icons.
        // The 1s timer and WM_APP_REPOSITION (settings changed) both land here; the
        // LastXRelative throttle (TrafficMonitor's m_last_start_pos equivalent) keeps
        // the no-change case to a few syscalls — MoveWindow only fires on a real move.
        // The Win11 path also re-measures the taskbar's HEIGHT and resizes the buffers
        // when it changes (the boot-time probe can catch a transient taller taskbar).
        // The classical family has its own path (ClassicalReposition): its "anchor" is
        // the task-buttons band it shrunk, re-checked on the 100ms TIMER_ID_POS.
        // --------------------------------------------------------------------
        private static void RepositionOverlay(IntPtr hwnd, RenderState s, bool force)
        {
            if (s == null) return;
            if (s.Classical)
            {
                ClassicalReposition(hwnd, s, force);
                return;
            }
            if (!WindowInterop.GetWindowRect(s.TaskbarHwnd, out var taskbarRect)) return;

            // Track the taskbar's HEIGHT too, not just the anchors. Start() sizes the
            // buffers from a rect probed once — that probe can catch a transient taller
            // taskbar (boot layout settling, a tablet-optimized/taskbar-mod height that
            // collapses later), and the overlay then stays too tall. The BAND height is
            // used, and the overlay is bottom-aligned into it (TaskbarBand remarks — a
            // taskbar-height mod can leave Shell_TrayWnd permanently taller than the
            // reserved band, the surplus strip floating over the desktop where
            // maximized windows cover it). Same resize dance as HandleDpiChange,
            // DPI unchanged.
            TaskbarBand(s.TaskbarHwnd, taskbarRect, out int taskbarHeight, out int bandY);
            if (taskbarHeight > 0 && taskbarHeight != s.PhysicalHeight)
            {
                ComputeTargetSize(s.Owner._samplingEnabledMask, false, s.Dpi, 0, taskbarHeight,
                    out int newW, out int newH);
                if (newW > 0 && newH > 0)
                {
                    Logger.Info($"任务栏高度变化 {s.PhysicalHeight}→{newH}px——覆盖层跟随重设");
                    ResizeBackBuffer(s, newW, newH, s.Dpi);
                    s.PhysicalWidth = newW;
                    s.PhysicalHeight = newH;
                    s.LogicalHeight = newH * (float)USER_DEFAULT_SCREEN_DPI / s.Dpi;
                    s.DComp?.Object?.Commit();   // null while the device is down
                    Draw(s);
                }
            }

            // Placement diagnostics: dump the full geometry whenever the taskbar's or
            // our own rect CHANGES (this runs every tick — change-gated so the steady
            // state stays quiet). Catches external nudges (taskbar mods) and confirms
            // the height-tracking resize above actually landed.
            if (WindowInterop.GetWindowRect(hwnd, out var overlayRect)
                && (!s.DiagLogged
                    || !RectsEqual(taskbarRect, s.DiagTaskbarRect)
                    || !RectsEqual(overlayRect, s.DiagOverlayRect)))
            {
                LogGeometry(s.TaskbarHwnd, hwnd, "重定位");
                s.DiagTaskbarRect = taskbarRect;
                s.DiagOverlayRect = overlayRect;
                s.DiagLogged = true;
            }

            int taskbarWidth = taskbarRect.right - taskbarRect.left;
            var (_, xRel) = CalcPosition(s.TaskbarHwnd, taskbarRect, taskbarWidth, s.PhysicalWidth, s.Dpi,
                s.Owner._onLeft, s.Owner._snapToStart);
            if (!force && xRel == s.LastXRelative && bandY == s.LastBandY) return;
            Logger.Debug($"重定位（Win11 锚点{(force ? "，强制" : "")}）：xRel {s.LastXRelative}→{xRel}，y {s.LastBandY}→{bandY}，尺寸={s.PhysicalWidth}x{s.PhysicalHeight}");
            s.LastXRelative = xRel;
            s.LastBandY = bandY;
            WindowInterop.MoveWindow(hwnd, xRel, bandY, s.PhysicalWidth, s.PhysicalHeight, true);
        }

        // The band of the taskbar window that explorer actually RESERVES — for a
        // bottom-docked taskbar, the strip between the work area's bottom and the
        // monitor's bottom, intersected with the taskbar's window rect; yOffset is
        // the band's top relative to the window (the overlay bottom-aligns into it).
        // A taskbar-height mod can leave Shell_TrayWnd TALLER than the reservation
        // (the 2026-08-01 report: window rect 90px @ y990-1080, but rcWork.bottom
        // 1020 → reserved band only 60px): the VISIBLE taskbar is the reserved
        // bottom band, and the surplus top strip floats over the desktop — the
        // taskbar's always-on-top doesn't reliably extend there, so maximized
        // windows cover whatever we draw in it. An overlay sized to the full window
        // rect and top-aligned at y=0 loses its top rows exactly there ("window
        // taller than the taskbar, part not visible"). On a stock taskbar the band
        // equals the whole window rect (height = full, yOffset = 0).
        private static void TaskbarBand(IntPtr taskbar, WindowInterop.RECT taskbarRect,
            out int height, out int yOffset)
        {
            height = taskbarRect.bottom - taskbarRect.top;
            yOffset = 0;
            IntPtr mon = WindowInterop.MonitorFromWindow(taskbar, WindowInterop.MONITOR_DEFAULTTONEAREST);
            var mi = new WindowInterop.MONITORINFO { cbSize = (uint)Marshal.SizeOf<WindowInterop.MONITORINFO>() };
            if (mon == IntPtr.Zero || !WindowInterop.GetMonitorInfoW(mon, ref mi)) return;
            int bandTop = Math.Max(mi.rcWork.bottom, taskbarRect.top);
            int bandBottom = Math.Min(mi.rcMonitor.bottom, taskbarRect.bottom);
            int vis = bandBottom - bandTop;
            if (vis > 0 && vis < height)
            {
                height = vis;
                yOffset = bandTop - taskbarRect.top;
            }
        }

        private static bool RectsEqual(WindowInterop.RECT a, WindowInterop.RECT b)
            => a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;

        // Band-height-only convenience for the callers that don't position (GetBandSize).
        private static int TaskbarBandHeight(IntPtr taskbar, WindowInterop.RECT taskbarRect)
        {
            TaskbarBand(taskbar, taskbarRect, out int h, out _);
            return h;
        }

        // Full placement-geometry dump (embed + every rect change): the taskbar's
        // window rect AND client rect (a non-client border would offset our child
        // coords), the client origin in screen space, the monitor and work area,
        // the visible height the overlay actually lays out to, and our own overlay
        // rect — WARN when any part of it falls outside the monitor (the
        // "part of the window is cut off" symptom).
        private static void LogGeometry(IntPtr taskbar, IntPtr overlayHwnd, string context)
        {
            string msg = $"几何（{context}）：";
            if (WindowInterop.GetWindowRect(taskbar, out var tr))
            {
                msg += $"任务栏窗口=({tr.left},{tr.top})-({tr.right},{tr.bottom}) {tr.right - tr.left}x{tr.bottom - tr.top}px";
                TaskbarBand(taskbar, tr, out int bandH, out int bandY);
                if (bandH != tr.bottom - tr.top || bandY != 0)
                    msg += $"。系统保留带=高{bandH}px y偏移{bandY}（窗口比保留区域高，覆盖层底对齐保留带）";
            }
            if (WindowInterop.GetClientRect(taskbar, out var cr))
            {
                var pt = new WindowInterop.POINT { x = 0, y = 0 };
                WindowInterop.ClientToScreen(taskbar, ref pt);
                msg += $"，任务栏客户区={cr.right - cr.left}x{cr.bottom - cr.top}px 原点=({pt.x},{pt.y})";
            }
            IntPtr mon = WindowInterop.MonitorFromWindow(taskbar, WindowInterop.MONITOR_DEFAULTTONEAREST);
            var mi = new WindowInterop.MONITORINFO { cbSize = (uint)Marshal.SizeOf<WindowInterop.MONITORINFO>() };
            bool hasMon = mon != IntPtr.Zero && WindowInterop.GetMonitorInfoW(mon, ref mi);
            if (hasMon)
                msg += $"，显示器=({mi.rcMonitor.left},{mi.rcMonitor.top})-({mi.rcMonitor.right},{mi.rcMonitor.bottom})，工作区=({mi.rcWork.left},{mi.rcWork.top})-({mi.rcWork.right},{mi.rcWork.bottom})";
            bool clipped = false;
            if (overlayHwnd != IntPtr.Zero && WindowInterop.GetWindowRect(overlayHwnd, out var ov))
            {
                msg += $"，覆盖层=({ov.left},{ov.top})-({ov.right},{ov.bottom}) {ov.right - ov.left}x{ov.bottom - ov.top}px";
                if (hasMon && (ov.left < mi.rcMonitor.left || ov.top < mi.rcMonitor.top
                    || ov.right > mi.rcMonitor.right || ov.bottom > mi.rcMonitor.bottom))
                {
                    clipped = true;
                    msg += "——覆盖层超出显示器边界，部分内容不可见";
                }
            }
            if (clipped) Logger.Warn(msg);
            else Logger.Info(msg);
        }

        // --------------------------------------------------------------------
        // CLASSICAL taskbar family (Windows 10, or an ExplorerPatcher-restored classic
        // taskbar on Win11): a 1:1 port of TrafficMonitor's CClassicalTaskbarDlg. There
        // is no free spot to anchor against — the task-buttons toolbar (MSTaskSwWClass)
        // is SHRUNK/SHIFTED to carve out our slot, and restored on exit (ResetTaskbarPos).
        // Placement: 靠左显示 off → the band's far end (next to the notification area —
        // the bottom end on a side-docked taskbar), on → its near end (next to Start —
        // the top end). All coordinates are relative to the ReBar's client area, so
        // top/bottom/left/right docking needs no edge math of its own.
        // --------------------------------------------------------------------
        private static void ClassicalReposition(IntPtr hwnd, RenderState s, bool force)
        {
            // A forced re-dock (our size/DPI/settings changed) first UNDOES our own
            // shrink — re-measuring a still-shrunk band would shrink it further every
            // time (explorer only re-expands it on its own layout events).
            if (force) RestoreMinWindow(s);
            if (!WindowInterop.GetWindowRect(s.MinHwnd, out var rcMin)) return;
            if (!WindowInterop.GetWindowRect(s.BarHwnd, out var rcBar)) return;

            if (!s.Vertical)
            {
                // TrafficMonitor's m_last_width throttle: explorer's re-expansion shows up
                // as a band width different from what we left (full width - our width).
                if (!force && rcMin.right - rcMin.left == s.LastMinLength) return;
                int bandW = rcMin.right - rcMin.left;
                s.MinOriRect = rcMin;            // explorer's current full rect — the exit-restore target
                s.MinOriValid = true;
                s.MinSpace = rcMin.left - rcBar.left;
                s.LastMinLength = bandW - s.PhysicalWidth;
                int x;
                if (!s.Owner._onLeft)
                {
                    WindowInterop.MoveWindow(s.MinHwnd, s.MinSpace, 0, Math.Max(0, bandW - s.PhysicalWidth), rcMin.bottom - rcMin.top, true);
                    x = s.MinSpace + bandW - s.PhysicalWidth + 2;   // TrafficMonitor's +2px nudge toward the tray
                }
                else
                {
                    WindowInterop.MoveWindow(s.MinHwnd, s.MinSpace + s.PhysicalWidth, 0, Math.Max(0, bandW - s.PhysicalWidth), rcMin.bottom - rcMin.top, true);
                    x = s.MinSpace;
                }
                int y = Math.Max(0, (rcBar.bottom - rcBar.top - s.PhysicalHeight) / 2);   // centred in the ReBar
                WindowInterop.MoveWindow(hwnd, x, y, s.PhysicalWidth, s.PhysicalHeight, true);
                Logger.Debug($"经典重定位{(force ? "（强制）" : "")}：任务按钮带 {bandW}→{Math.Max(0, bandW - s.PhysicalWidth)}px，覆盖层 ({x},{y}) {s.PhysicalWidth}x{s.PhysicalHeight}");
            }
            else
            {
                if (!force && rcMin.bottom - rcMin.top == s.LastMinLength) return;
                int bandH = rcMin.bottom - rcMin.top;
                s.MinOriRect = rcMin;
                s.MinOriValid = true;
                s.MinSpace = rcMin.top - rcBar.top;
                s.LastMinLength = bandH - s.PhysicalHeight;
                int y;
                if (!s.Owner._onLeft)
                {
                    WindowInterop.MoveWindow(s.MinHwnd, 0, s.MinSpace, rcMin.right - rcMin.left, Math.Max(0, bandH - s.PhysicalHeight), true);
                    y = s.MinSpace + bandH - s.PhysicalHeight + 2;
                }
                else
                {
                    WindowInterop.MoveWindow(s.MinHwnd, 0, s.MinSpace + s.PhysicalHeight, rcMin.right - rcMin.left, Math.Max(0, bandH - s.PhysicalHeight), true);
                    y = s.MinSpace;
                }
                int x = Math.Max(DpiScaleInt(2, s.Dpi), (rcMin.right - rcMin.left - s.PhysicalWidth) / 2);
                WindowInterop.MoveWindow(hwnd, x, y, s.PhysicalWidth, s.PhysicalHeight, true);
                Logger.Debug($"经典重定位（竖直{(force ? "，强制" : "")}）：任务按钮带 {bandH}→{Math.Max(0, bandH - s.PhysicalHeight)}px，覆盖层 ({x},{y}) {s.PhysicalWidth}x{s.PhysicalHeight}");
            }
        }

        // Return the task-buttons band to its original rect (TrafficMonitor's
        // ResetTaskbarPos): on exit, before an orientation flip re-docks along the other
        // axis, and before a forced re-measure. Nothing else re-expands the band promptly
        // once we're gone. A dead band (explorer restart) fails IsWindow and is skipped.
        private static void RestoreMinWindow(RenderState s)
        {
            if (!s.MinOriValid || s.MinHwnd == IntPtr.Zero || !WindowInterop.IsWindow(s.MinHwnd)) return;
            Logger.Debug($"恢复任务按钮带原始尺寸 {s.MinOriRect.right - s.MinOriRect.left}x{s.MinOriRect.bottom - s.MinOriRect.top}（{(s.Vertical ? "竖直" : "水平")}轴）");
            if (!s.Vertical)
                WindowInterop.MoveWindow(s.MinHwnd, s.MinSpace, 0,
                    s.MinOriRect.right - s.MinOriRect.left, s.MinOriRect.bottom - s.MinOriRect.top, true);
            else
                WindowInterop.MoveWindow(s.MinHwnd, 0, s.MinSpace,
                    s.MinOriRect.right - s.MinOriRect.left, s.MinOriRect.bottom - s.MinOriRect.top, true);
            s.MinOriValid = false;
        }

        // A taskbar dragged to another screen edge flips orientation (horizontal grid ⟷
        // vertical strips): undo the old-axis dock, resize the buffers to the transposed
        // layout and re-dock. Detected on the 100ms TIMER_ID_POS.
        private static void ReconfigureOrientation(IntPtr hwnd, RenderState s)
        {
            RestoreMinWindow(s);            // undo along the OLD axis
            s.LastMinLength = -1;
            s.Vertical = !s.Vertical;
            Logger.Info($"任务栏拖到另一屏幕边缘——方向翻转为{(s.Vertical ? "竖直" : "水平")}，重排覆盖层");
            s.Owner._layoutVertical = s.Vertical;   // mirror for the UI-thread accessors

            GetBandSize(s, out int bandW, out int bandH);
            ComputeTargetSize(s.Owner._samplingEnabledMask, s.Vertical, s.Dpi, bandW, bandH,
                out int newWidth, out int newHeight);
            if (newWidth > 0 && newHeight > 0)
                ResizeBackBuffer(s, newWidth, newHeight, s.Dpi);
            s.PhysicalWidth = Math.Max(0, newWidth);
            s.PhysicalHeight = Math.Max(0, newHeight);
            s.LogicalHeight = s.PhysicalHeight * (float)USER_DEFAULT_SCREEN_DPI / s.Dpi;

            RepositionOverlay(hwnd, s, force: true);
            s.DComp?.Object?.Commit();   // null while the device is down
            Draw(s);
        }

        // The band measurement feeding ComputeTargetSize: horizontal modes need the parent
        // band's height (Win11: the taskbar itself; classical: the ReBar); vertical mode
        // needs the task-buttons toolbar's width (the strips' x clamp — TrafficMonitor
        // centres within rcMin the same way).
        private static void GetBandSize(RenderState s, out int bandW, out int bandH)
        {
            bandW = 0; bandH = 0;
            if (s.Vertical)
            {
                if (WindowInterop.GetWindowRect(s.MinHwnd, out var rcMin))
                    bandW = rcMin.right - rcMin.left;
            }
            else
            {
                IntPtr band = s.Classical ? s.BarHwnd : s.TaskbarHwnd;
                if (WindowInterop.GetWindowRect(band, out var rc))
                {
                    bandW = rc.right - rc.left;
                    bandH = s.Classical ? rc.bottom - rc.top : TaskbarBandHeight(band, rc);
                }
            }
        }

        // Target physical size for the current layout. Horizontal: width is layout-driven,
        // height is the band's. Vertical: height is layout-driven (the strip stack),
        // width is the content width clamped into the band (a narrower band clips text —
        // TrafficMonitor clamps its item rects the same way). A 0 in the layout-driven
        // dimension = the every-metric-off stub (the caller keeps the old buffers).
        private static void ComputeTargetSize(int mask, bool vertical, uint dpi, int bandW, int bandH,
            out int width, out int height)
        {
            var l = ComputeLayout(mask, vertical);
            if (!vertical)
            {
                width = DpiScaleInt((int)l.Width, dpi);
                height = bandH;
            }
            else
            {
                int contentW = DpiScaleInt((int)l.Width, dpi);
                int maxW = bandW - DpiScaleInt(2, dpi);
                width = maxW > 0 && contentW > maxW ? maxW : contentW;
                height = DpiScaleInt((int)l.Height, dpi);
            }
        }

        // TrafficMonitor's CClassicalTaskbarDlg::InitTaskbarWnd chain: Shell_TrayWnd →
        // ReBarWindow32 (WorkerW fallback) → MSTaskSwWClass (MSTaskListWClass fallback).
        // These are undocumented explorer internals (stable 7→10) — the fallbacks, and
        // the caller's "chain not found" degradation, must stay.
        private static bool TryFindClassicalChain(IntPtr taskbar,
            out IntPtr hBar, out IntPtr hMin,
            out WindowInterop.RECT rcBar, out WindowInterop.RECT rcMin)
        {
            hBar = WindowInterop.FindWindowExW(taskbar, IntPtr.Zero, "ReBarWindow32", null);
            if (hBar == IntPtr.Zero)
                hBar = WindowInterop.FindWindowExW(taskbar, IntPtr.Zero, "WorkerW", null);
            hMin = IntPtr.Zero;
            rcBar = default; rcMin = default;
            if (hBar == IntPtr.Zero) return false;
            hMin = WindowInterop.FindWindowExW(hBar, IntPtr.Zero, "MSTaskSwWClass", null);
            if (hMin == IntPtr.Zero)
                hMin = WindowInterop.FindWindowExW(hBar, IntPtr.Zero, "MSTaskListWClass", null);
            if (hMin == IntPtr.Zero) return false;
            return WindowInterop.GetWindowRect(hBar, out rcBar)
                && rcBar.right - rcBar.left > 0 && rcBar.bottom - rcBar.top > 0
                && WindowInterop.GetWindowRect(hMin, out rcMin)
                && rcMin.right - rcMin.left > 0 && rcMin.bottom - rcMin.top > 0;
        }

        // ---------- taskbar family + screen edge ----------
        // TrafficMonitor's CWinVersionHelper::IsWindows11OrLater (RtlGetNtVersionNumbers).
        private static readonly bool _isWin11OrLater = DetectWin11OrLater();

        // The raw OS check (no taskbar-shape test) — App uses it for the one-time
        // legacy-OS compatibility warning at startup.
        internal static bool IsWin11OrLater => _isWin11OrLater;

        private static bool DetectWin11OrLater()
        {
            try
            {
                SystemInfo.RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);
                build &= 0xFFFF;
                return major > 10 || (major == 10 && minor > 0) || (major == 10 && build >= 21996);
            }
            catch { return true; }   // can't tell → assume the modern family (today's default)
        }

        // UI-thread version (the settings page): finds the taskbar itself.
        internal static bool IsWindows11Taskbar() => IsWindows11Taskbar(WindowInterop.FindWindowW("Shell_TrayWnd", null));

        // TrafficMonitor's CheckWindows11Taskbar: the OS says Win11 AND the XAML taskbar
        // is present. On a Win11 WITHOUT the bridge child (ExplorerPatcher's restored
        // classic taskbar) the classical chain exists and is the one that works.
        private static bool IsWindows11Taskbar(IntPtr taskbar)
            => _isWin11OrLater && taskbar != IntPtr.Zero
                && WindowInterop.FindWindowExW(taskbar, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero;

        // Screen edge the overlay's taskbar is docked to, for DetailWindow's popup
        // placement: 0=bottom (the Win11 taskbar's only orientation), 1=top, 2=left,
        // 3=right. Read off the TASKBAR's rect, not the overlay's — a side-docked overlay
        // narrower than its band (centred strips) doesn't touch the screen edge itself.
        // Orientation comes from the rect's aspect (a taskbar spans the full edge, so a
        // vertical bar reaches BOTH horizontal edges — edge picks the side it hugs).
        internal static int GetTaskbarEdge(IntPtr overlayHwnd)
        {
            if (overlayHwnd == IntPtr.Zero) return 0;
            IntPtr taskbar = WindowInterop.GetAncestor(overlayHwnd, WindowInterop.GA_ROOT);   // reparented → Shell_TrayWnd
            if (taskbar == IntPtr.Zero) taskbar = overlayHwnd;
            if (!WindowInterop.GetWindowRect(taskbar, out var r)) return 0;
            IntPtr mon = WindowInterop.MonitorFromWindow(taskbar, WindowInterop.MONITOR_DEFAULTTONEAREST);
            var mi = new WindowInterop.MONITORINFO { cbSize = (uint)Marshal.SizeOf<WindowInterop.MONITORINFO>() };
            if (mon == IntPtr.Zero || !WindowInterop.GetMonitorInfoW(mon, ref mi)) return 0;
            const int tol = 4;
            int w = r.right - r.left, h = r.bottom - r.top;
            if (w >= h)
                return r.top <= mi.rcMonitor.top + tol ? 1 : 0;    // horizontal: top vs bottom
            return r.left <= mi.rcMonitor.left + tol ? 2 : 3;      // vertical: left vs right
        }

        // --------------------------------------------------------------------
        // Window procedure. The try/catch is NOT optional: on x64 a managed exception
        // cannot unwind across the user32 callback boundary — escaping it turns into
        // STATUS_FATAL_USER_CALLBACK_EXCEPTION (0xC000041D), an unconditional process
        // kill, and the ~20s WER dump freeze hangs explorer on our child window (the
        // 2026-07-30 crash). Catching INSIDE the boundary keeps both alive; the fault
        // is reported to the global CrashReporter (crash dialog) fire-and-forget — the
        // message loop must never block on the user here, or a SendMessage from
        // explorer hangs the taskbar. Note the catch only sees faults from the managed
        // body downward; an AV in the entry glue itself (before this frame exists —
        // e.g. a dead thunk) is out of its reach, which is what the static _wndProc
        // prevents.
        // --------------------------------------------------------------------
        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                return WndProcCore(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                CrashReporter.Report($"WndProc msg=0x{msg:X}", ex, fatal: false, block: false);
                return IntPtr.Zero;   // "handled" — the overlay is non-critical, never take the app down
            }
        }

        private static IntPtr WndProcCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WindowInterop.WM_MOUSEACTIVATE:
                    // Never let clicks on the overlay steal focus — neither the overlay
                    // nor its taskbar parent should activate. This is why the MainWindow
                    // keeps its startup focus and why the detail popup stays active (and
                    // its acrylic doesn't flash) when switching columns.
                    return (IntPtr)WindowInterop.MA_NOACTIVATE;
                case WindowInterop.WM_MOUSEMOVE:
                {
                    var s = StateOf(hwnd);
                    if (s != null)
                    {
                        if (!s.TrackingMouse)
                        {
                            var tme = new WindowInterop.TRACKMOUSEEVENT
                            {
                                cbSize = (uint)Marshal.SizeOf<WindowInterop.TRACKMOUSEEVENT>(),
                                dwFlags = WindowInterop.TME_LEAVE,
                                hwndTrack = hwnd,
                                dwHoverTime = 0,
                            };
                            WindowInterop.TrackMouseEvent(ref tme);
                            s.TrackingMouse = true;
                        }
                        int lp = lParam.ToInt32();
                        short x = (short)(lp & 0xFFFF);
                        short y = (short)((lp >> 16) & 0xFFFF);
                        int hov = HitTestSlot(s, x, y);
                        if (s.Hovered != hov)
                        {
                            s.Hovered = hov;
                            Draw(s);
                        }
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_MOUSELEAVE:
                {
                    var s = StateOf(hwnd);
                    if (s != null)
                    {
                        s.TrackingMouse = false;
                        if (s.Hovered != -1)
                        {
                            s.Hovered = -1;
                            Draw(s);
                        }
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_LBUTTONDOWN:
                {
                    // Capture so the matching button-up arrives here even if the
                    // pointer strays into a neighbour column while pressed.
                    WindowInterop.SetCapture(hwnd);
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_LBUTTONUP:
                {
                    WindowInterop.ReleaseCapture();
                    var s = StateOf(hwnd);
                    if (s != null)
                    {
                        int lp = lParam.ToInt32();
                        short x = (short)(lp & 0xFFFF);
                        short y = (short)((lp >> 16) & 0xFFFF);
                        int clicked = HitTestSlot(s, x, y);

                        // A slot whose window is pinned is press-disabled (hover still
                        // works) — the pinned window owns it until unpinned or closed.
                        // Same for a slot whose sampling is off (设置 → 采样): it is
                        // hidden and has no data, so a press must do nothing. (Hit-test
                        // already never returns hidden slots; this is the second line.)
                        int suppressed = s.Owner._clickDisabledMask | ~s.Owner._samplingEnabledMask;
                        if (clicked >= 0 && (suppressed & (1 << clicked)) != 0)
                            return IntPtr.Zero;

                        // Toggle: clicking the active column deselects (closes popup).
                        int newSel = (s.Selected == clicked) ? -1 : clicked;
                        s.Selected = newSel;
                        Draw(s);

                        // The click made our process the last-input owner; release the
                        // foreground lock so the WPF popup (same process) can activate.
                        WindowInterop.AllowSetForegroundWindow(WindowInterop.ASFW_ANY);

                        // Tell the UI thread which column to show (-1 = hide).
                        s.ToggleCallback?.Invoke(newSel);
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_RBUTTONUP:
                {
                    // Right-click → ask the UI thread to show the context menu.
                    StateOf(hwnd)?.RightClickRequested?.Invoke();
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_APP_DESELECT:
                {
                    // UI thread asked us to clear the highlight for a specific column
                    // (its popup closed) — ignore it if the selection has since moved on.
                    var s = StateOf(hwnd);
                    if (s != null && s.Selected == wParam.ToInt32())
                    {
                        s.Selected = -1;
                        Draw(s);
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_APP_SELECT:
                {
                    // UI thread marked a column selected (its window became a flyout
                    // again after unpinning) — force the highlight to that column.
                    var s = StateOf(hwnd);
                    int col = wParam.ToInt32();
                    if (s != null && col >= 0 && s.Selected != col)
                    {
                        s.Selected = col;
                        Draw(s);
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_DPICHANGED:
                {
                    uint newDpi = (uint)(wParam.ToInt64() & 0xFFFF);
                    if (newDpi > 0) HandleDpiChange(hwnd, newDpi, StateOf(hwnd));
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_DPICHANGED_AFTERPARENT:
                {
                    uint newDpi = WindowInterop.GetDpiForWindow(hwnd);
                    if (newDpi > 0) HandleDpiChange(hwnd, newDpi, StateOf(hwnd));
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_APP_REPOSITION:
                {
                    // UI thread changed the placement settings (SetPlacement) — re-anchor.
                    RepositionOverlay(hwnd, StateOf(hwnd), force: true);
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_APP_SET_INTERVAL:
                {
                    // UI thread changed the sampling interval (SetSampleInterval) — re-arm
                    // the timer (KillTimer+SetTimer; SetTimer with a new elapse restarts it).
                    var s = StateOf(hwnd);
                    if (s != null && s.Owner != null)
                    {
                        WindowInterop.KillTimer(hwnd, (IntPtr)TIMER_ID);
                        WindowInterop.SetTimer(hwnd, (IntPtr)TIMER_ID, (uint)s.Owner._sampleIntervalMs, IntPtr.Zero);
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_APP_SET_METRICS:
                {
                    // UI thread flipped a per-metric sampling switch (SetMetricSamplingMask;
                    // the sampler already has the new mask) — resize the window to the new
                    // layout (hidden slots free their space), then re-sample + redraw now.
                    var s = StateOf(hwnd);
                    if (s != null)
                    {
                        // Drop a stale selection/hover on a now-hidden slot (normally App
                        // closing the column's windows already deselected via RequestDeselect
                        // — belt-and-braces for the race).
                        int mask = s.Owner._samplingEnabledMask;
                        if (s.Selected >= 0 && (mask & (1 << s.Selected)) == 0) s.Selected = -1;
                        if (s.Hovered >= 0 && (mask & (1 << s.Hovered)) == 0) s.Hovered = -1;
                        ResizeForLayout(hwnd, s);
                        SamplePublishDraw(s);
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_TIMER:
                {
                    if (wParam == (IntPtr)TIMER_ID_POS)
                    {
                        // Classical family only: re-dock against the band (TIMER_ID_POS).
                        // A taskbar dragged to another screen edge flips orientation —
                        // reconfigure (transpose + resize + re-dock), don't just move.
                        var ps = StateOf(hwnd);
                        if (ps != null && ps.Classical)
                        {
                            if (WindowInterop.GetWindowRect(ps.TaskbarHwnd, out var rcT))
                            {
                                bool vert = rcT.right - rcT.left < rcT.bottom - rcT.top;
                                if (vert != ps.Vertical)
                                {
                                    ReconfigureOrientation(hwnd, ps);
                                    return IntPtr.Zero;
                                }
                            }
                            RepositionOverlay(hwnd, ps, force: false);
                        }
                        return IntPtr.Zero;
                    }
                    // Graceful self-exit: a non-elevated build step drops a sentinel file
                    // next to the exe; this elevated process sees it (file I/O isn't gated by
                    // UIPI the way cross-integrity SendMessage is) and shuts down on the UI
                    // thread, freeing the locked output exe for the rebuild.
                    if (ConsumeShutdownSentinel())
                    {
                        Logger.Info("检测到 shutdown.sentinel——构建触发的优雅退出");
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(
                            new Action(() => System.Windows.Application.Current.Shutdown()));
                        return IntPtr.Zero;
                    }

                    var s = StateOf(hwnd);
                    if (s != null)
                    {
                        // explorer died and took the taskbar with it. A reparented child
                        // is destroyed WITH its parent, so still being alive here means
                        // SetParent lost the race (we're a floating top-level popup) —
                        // self-destruct; the owner re-enters Start() and re-embeds us on
                        // the new taskbar.
                        if (!WindowInterop.IsWindow(s.TaskbarHwnd))
                        {
                            Logger.Warn("任务栏父窗口已失效（explorer 重启且 SetParent 竞速落败，覆盖层浮为顶层窗口）——自毁，等待重建");
                            WindowInterop.DestroyWindow(hwnd);
                            return IntPtr.Zero;
                        }

                        // A LOST DEVICE owns this tick (显卡驱动更新/重置/TDR): rebuild the
                        // pipeline first — DPI tracking, repositioning and the sample-draw all
                        // funnel into Draw/ResizeBuffers, which a dead device can only reject.
                        // While the rebuild keeps failing (the driver is still coming back)
                        // only the sampler runs, so the detail popups keep ticking; the
                        // overlay itself holds its last frame until the device returns.
                        if (s.DeviceLost && !RecoverDevice(hwnd, s))
                        {
                            SamplePublishDraw(s);   // sampling + publish; Draw no-ops while lost
                            return IntPtr.Zero;
                        }

                        uint curDpi = WindowInterop.GetDpiForWindow(hwnd);
                        if (curDpi > 0 && curDpi != s.Dpi)
                            HandleDpiChange(hwnd, curDpi, s);

                        // Track the system taskbar theme (light/dark) — like the anchors,
                        // it changes without notice; re-tint the brushes on a flip. Draw
                        // below runs every tick anyway, so no extra redraw is needed.
                        bool lightTaskbar = IsTaskbarLightThemed();
                        if (lightTaskbar != s.LightTaskbar)
                            ApplyTaskbarTheme(s, lightTaskbar);

                        // Track the placement anchors (Start button / tray / alignment) —
                        // they move without notice; MoveWindow only fires on a real change.
                        RepositionOverlay(hwnd, s, force: false);

                        SamplePublishDraw(s);
                    }
                    return IntPtr.Zero;
                }

                case WindowInterop.WM_DESTROY:
                {
                    // Classical path: give the task-buttons band its size back BEFORE we
                    // disappear (RestoreMinWindow) — nothing else re-expands it promptly.
                    // Explorer-restart: the band is already dead, IsWindow skips it.
                    Logger.Debug("WM_DESTROY——覆盖层销毁，释放 D3D/D2D/DComp 资源");
                    var dying = StateOf(hwnd);
                    if (dying != null) RestoreMinWindow(dying);
                    WindowInterop.KillTimer(hwnd, (IntPtr)TIMER_ID);
                    WindowInterop.KillTimer(hwnd, (IntPtr)TIMER_ID_POS);
                    IntPtr ptr = WindowInterop.GetWindowLongPtr(hwnd, WindowInterop.GWLP_USERDATA);
                    if (ptr != IntPtr.Zero)
                    {
                        var handle = GCHandle.FromIntPtr(ptr);
                        var s = handle.Target as RenderState;
                        // The pipeline may be mid-rebuild (or half-built after a failed init):
                        // ReleaseDeviceResources disposes whatever exists and tolerates a dead
                        // device, and the handle is freed even if something in there throws.
                        try { if (s != null) ReleaseDeviceResources(s); }
                        catch (Exception ex) { Logger.Warn("覆盖层退出时释放 D3D/D2D/DComp 资源失败（进程即将退出，忽略）", ex); }
                        handle.Free();
                        WindowInterop.SetWindowLongPtr(hwnd, WindowInterop.GWLP_USERDATA, IntPtr.Zero);
                    }
                    WindowInterop.PostQuitMessage(0);
                    return IntPtr.Zero;
                }

                default:
                    return WindowInterop.DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        private static RenderState StateOf(IntPtr hwnd)
        {
            IntPtr ptr = WindowInterop.GetWindowLongPtr(hwnd, WindowInterop.GWLP_USERDATA);
            if (ptr == IntPtr.Zero) return null;
            return GCHandle.FromIntPtr(ptr).Target as RenderState;
        }

        // ---------- shutdown sentinel (lets a non-elevated build step exit this elevated process) ----------
        private const string ShutdownSentinelName = "shutdown.sentinel";

        // Returns true (and deletes the sentinel) if a "shutdown.sentinel" file sits next to
        // the exe. A non-elevated build step writes it, this 1s tick notices it within ~1s
        // and the caller shuts the app down — avoiding the right-click → 退出 dance every
        // rebuild (and working around the file lock from the elevated process).
        private static bool ConsumeShutdownSentinel()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    System.AppDomain.CurrentDomain.BaseDirectory, ShutdownSentinelName);
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    return true;
                }
            }
            catch { /* never let the sentinel check disrupt the timer tick */ }
            return false;
        }

        private static D2D1_BITMAP_PROPERTIES1 BitmapProps(uint dpi) => new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = dpi,
            dpiY = dpi,
            bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            colorContext = IntPtr.Zero,
        };

        private static int DpiScaleInt(int value, uint dpi)
            => (int)(value * dpi / USER_DEFAULT_SCREEN_DPI);

        // 2D hit-test over the CURRENT layout. Horizontal: x picks a visible group (the
        // last visible group extends to the window's right edge, so the whole width is
        // covered); inside a stacked group, y picks the row POSITION, which maps to
        // whichever metric packed into it — the trailing empty position of an odd count
        // hits nothing (-1); the 网络 group is one whole-column slot regardless of y.
        // Vertical: y picks the strip — a packed stacked metric, or either of 网络's two
        // strips (one slot either way); x is free (the strips span the window's width).
        private static int HitTestSlot(RenderState s, int x, int y)
        {
            var layout = ComputeLayout(s.Owner._samplingEnabledMask, s.Vertical);
            if (layout.Vertical)
            {
                if (x < 0 || x >= s.PhysicalWidth || y < 0) return -1;
                int stripH = DpiScaleInt(STRIP_H_I, s.Dpi);
                if (stripH <= 0) return -1;
                int idx = y / stripH;
                int packed = PackedStackedCount(layout.SlotMask);
                if (idx < packed) return layout.SlotAt(idx);
                if (layout.Left2 >= 0f && idx >= packed && idx < packed + 2) return 4;
                return -1;
            }
            for (int g = 0; g < GroupCount; g++)
            {
                if (!layout.GroupVisible(g)) continue;
                int left = DpiScaleInt((int)layout.GroupLeft(g), s.Dpi);
                bool lastVisible = layout.GroupLeft(g) + GroupWidths[g] >= layout.Width - 0.01f;
                int right = lastVisible ? s.PhysicalWidth : DpiScaleInt((int)(layout.GroupLeft(g) + GroupWidths[g]), s.Dpi);
                if (x < left || x >= right) continue;

                if (g == 2) return 4;                          // 网络: whole column
                int midPhys = s.PhysicalHeight / 2;
                return layout.SlotAt(g * 2 + (y < midPhys ? 0 : 1));   // -1 = trailing hole
            }
            return -1;
        }

        /// <summary>Holds the DirectX resources and mutable UI state for one window.</summary>
        private sealed class RenderState
        {
            // ---- device pipeline: created by CreateDeviceResources, torn down as a set by
            // ReleaseDeviceResources. It lives here (never in Start() locals) so a device loss
            // can rebuild it in place — and so nothing stays rooted by the never-returning
            // message loop. Every field below is null while the pipeline is down.
            public IComObject<ID3D11Device> D3dDevice;
            public IDXGIDevice DxgiDevice;      // raw QI'd RCW — for the D2D device + DComposition
            public IDXGIDevice1 DxgiDevice1;    // raw QI'd RCW — for the swap chain
            public IComObject<IDXGIFactory2> DxgiFactory;
            public IComObject<ID2D1Factory1> D2dFactory;
            public IComObject<ID2D1Device> D2dDevice;
            public IComObject<ID2D1DeviceContext> D2dContext;
            public IComObject<IDXGISwapChain1> SwapChain;
            public ComObject<IDCompositionDevice> DComp;
            public IDCompositionTarget DCompTarget;   // raw RCWs (DirectN exposes no wrapper): kept
            public IDCompositionVisual DCompVisual;   // only to UNBIND them before a rebuild binds
                                                      // its own target to the same HWND
            // The D2D target's back buffer, wrapped. Replaced on every resize; the old
            // wrappers must be DISPOSED before ResizeBuffers (see ResizeBackBuffer).
            public IComObject<IDXGISurface> BackBufferSurface;
            public IComObject<ID2D1Bitmap1> BackBufferBitmap;
            // DirectWrite formats: device-INDEPENDENT, created once per Start() and kept across
            // a device loss (CreateDeviceResources never touches them).
            public IComObject<IDWriteTextFormat> LabelFormat;
            public IComObject<IDWriteTextFormat> ValueFormat;
            public IComObject<IDWriteTextFormat> NetFormat;   // left-aligned, for the net column
            public IComObject<ID2D1Brush> TextBrush;
            public IComObject<ID2D1Brush> LabelBrush;   // dimmed TextBrush for metric labels / ↑↓
            public IComObject<ID2D1Brush> HighlightBrush;
            public IComObject<ID2D1Brush> HoverBrush;
            public IComObject<ID2D1Brush> SeparatorBrush;
            // The D3D device is gone (driver update/reset/TDR): drawing is suspended and the
            // tick's RecoverDevice rebuilds the pipeline. Set by OnDeviceLost, cleared on a
            // successful rebuild; DeviceLostTicks counts failed attempts for log throttling.
            public bool DeviceLost;
            public int DeviceLostTicks;
            public IntPtr TaskbarHwnd;
            // ---- classical taskbar family (Win10 / restored-classic taskbar on Win11) ----
            public bool Classical;              // false = the Win11 taskbar path
            public bool Vertical;               // side-docked classical taskbar (strips layout)
            public IntPtr BarHwnd;              // ReBarWindow32 (WorkerW fallback) — our parent
            public IntPtr MinHwnd;              // MSTaskSwWClass (MSTaskListWClass fallback) — the shrunk task-buttons band
            public WindowInterop.RECT MinOriRect;   // the band's pre-shrink rect (the exit-restore target)
            public bool MinOriValid;
            public int MinSpace;                // the band's left/top offset inside the ReBar at last apply
            public int LastMinLength;           // the band width/height we left behind (explorer re-expansion detector)
            public int PhysicalWidth;
            public int PhysicalHeight;
            public int LastXRelative;   // taskbar-relative x last applied by MoveWindow (Win11 path)
            public int LastBandY;       // taskbar-relative y last applied (band bottom-alignment, Win11 path)
            // Placement diagnostics (LogGeometry): the rects at the last dump — the
            // per-tick dump is change-gated on these so the steady state stays quiet.
            public bool DiagLogged;
            public WindowInterop.RECT DiagTaskbarRect;
            public WindowInterop.RECT DiagOverlayRect;
            public float LogicalHeight;
            public uint Dpi;
            public int Hovered;
            public bool TrackingMouse;
            public int Selected;
            public bool LightTaskbar;   // taskbar theme the brushes are currently tinted for
            public Action<int> ToggleCallback;
            public Action RightClickRequested;
            public TaskbarWindow Owner;
            public SystemSampler Sampler;
            public SystemSnapshot Snapshot;
        }
    }
}
