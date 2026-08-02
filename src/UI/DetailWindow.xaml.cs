using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Common.IconKeys;

namespace task_monitor
{
    /// <summary>
    /// Borderless, Acrylic flyout shell. It holds no metric-specific UI — it just hosts
    /// one <see cref="IDetailView"/> (CPU/RAM/Disk/Net) chosen by the clicked column, plus the
    /// window chrome: acrylic tint, placement next to the taskbar's screen edge (above a
    /// bottom taskbar, below a top one, beside a side-docked one), focus-loss dismissal,
    /// and the pin toggle (parked in the hosted view's header, right of its title). A fresh window (and view) is created on every open and
    /// destroyed on dismiss so each appearance is "born active" (no inactive→active
    /// acrylic flash). Styled via FluentWpfCore <c>WindowMaterial</c> (persistent acrylic)
    /// + <c>WindowStyle="None"</c> + rounded corners. Each metric's actual content lives
    /// in its own detail view.
    ///
    /// Pinning turns the flyout into a little window with an iNKORE-drawn title bar: a
    /// Mica-filled band (caption title, caption ✕, drag surface) "grows out of the top" — the window
    /// shifts up by the band height so the content, the pin (next to the view's title)
    /// and the taskbar gap all stay exactly where they were — and the window
    /// survives focus loss. Unpinning collapses the band and returns the window to the
    /// exact spot the flyout occupied before pinning (the pinned window is draggable,
    /// so its current spot is not home). App tracks the transition via
    /// <see cref="PinStateChanged"/> so a new popup can open alongside a pinned one.
    /// </summary>
    public partial class DetailWindow : Window
    {
        private readonly TaskbarWindow _owner;
        private bool _dark;
        private int _column = -1;
        private bool _dismissed;
        private IDetailView _current;

        public DetailWindow(TaskbarWindow owner)
        {
            InitializeComponent();
            _owner = owner;

            // The pin toggle is declared in the shell but shown inside the hosted view's
            // header (right of the title, centered with it). Detach it from the shell grid
            // here; ShowColumn parks it in each fresh view's PinSlot.
            ((Panel)PinButton.Parent).Children.Remove(PinButton);

            // Start offscreen so the first (unstyled) frame never flashes on screen;
            // <see cref="ShowColumn"/> moves it into place before it's seen.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Top = -10000;
            Left = -10000;

            IsVisibleChanged += DetailWindow_IsVisibleChanged;

            // SizeToContent=Height resizes DOWNWARD from Top — but a taskbar flyout must
            // grow/shrink UPWARD (its bottom edge is the anchored one, 8px above the
            // taskbar). See DetailWindow_SizeChanged.
            SizeChanged += DetailWindow_SizeChanged;

            // SystemCommands.CloseWindowCommand (the caption ✕'s Command) is a plain
            // RoutedCommand — WPF's Window does NOT handle it, and with no CommandBinding
            // on the route its CanExecute is false, which DISABLES the button. iNKORE's
            // TitleBarControl registers these bindings on itself in its ctor; we do the
            // same here so the command finds a handler when it bubbles up from the button.
            CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (s, e) => Close()));

            // Drag source for pinned mode. handledEventsToo: controls that swallow the
            // press (list ScrollViewer etc.) still count as drag surfaces; only real
            // interactive controls are filtered out in the handler.
            AddHandler(MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(Window_MouseLeftButtonDown), handledEventsToo: true);

            // Resolve the theme once: tint the persistent acrylic, and pass dark to the view.
            // (A live switch re-runs this via ApplyTheme — App hooks the ThemeManager's
            // ActualApplicationThemeChanged.)
            ApplyTheme();
        }

        /// <summary>
        /// (Re)apply the current app theme to the parts NOT driven by DynamicResource: the
        /// FluentWpfCore acrylic tint and the hosted view's tooltip/chart colors. Called by
        /// the ctor, and again by App whenever the effective theme flips (settings 主题
        /// combo, or a system-theme change while 跟随系统). The theme source is
        /// <see cref="ThemeManager.Current"/>.<c>ActualApplicationTheme</c> — NOT
        /// GetActualTheme(this): that attached property is only pushed to IsThemeAware
        /// (iNKORE modern) windows, so on this plain FluentWpfCore window it would read
        /// the Light default forever.
        /// </summary>
        public void ApplyTheme()
        {
            _dark = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark;
            windowMaterial.CompositonColor = _dark
                ? Color.FromArgb(0xCC, 0x20, 0x20, 0x20)
                : Color.FromArgb(0xCC, 0xF3, 0xF3, 0xF3);
            windowMaterial.IsDarkMode = _dark;
            _current?.ApplyTheme(_dark);
        }

        private void DetailWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Updates arrive via the taskbar's push (App.SnapshotChanged → Refresh);
            // just do one immediate refresh on show.
            if (IsVisible) Refresh();
        }

        // ---------- Startup cold-start pre-warm ----------
        /// <summary>
        /// Builds and renders the shell plus every detail view offscreen and
        /// non-activated, then closes. This is the app's first real WPF window, which
        /// would otherwise pay all one-time costs — JIT of the whole show path, BAML
        /// parse, iNKORE style/template realization, FluentWpfCore acrylic setup,
        /// font/glyph formatting — on the user's first click. App calls this once at
        /// startup so that click is as warm as any later one. The window never appears
        /// (the ctor parks it at -10000,-10000) and never steals focus: unlike
        /// <see cref="ShowColumn"/> this does no Activate / foreground steal / topmost.
        /// </summary>
        internal void Prewarm()
        {
            ShowActivated = false;
            Show();
            // Every view is built and laid out once — each has its own BAML, chart
            // controls and style set, so warming only one would leave the other
            // columns' first opens slightly cold.
            foreach (var view in new IDetailView[]
            {
                new CpuDetailView(_dark),
                new RamDetailView(_dark),
                new DiskDetailView(_dark),
                new GpuDetailView(_dark),
                new NetDetailView(_dark),
            })
            {
                ContentHost.Content = view;
                // Expand templates, realize styles and format text now, then queue
                // behind the pending render pass so it commits before we move on.
                UpdateLayout();
                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }
            ContentHost.Content = null;
            // Throwaway: make Closing/Deactivated no-ops (they would ask the overlay
            // to deselect a column this window never selected).
            _dismissed = true;
            Close();
        }

        // ---------- Bottom-anchored growth (unpinned flyout on a BOTTOM taskbar only) ----------
        // SizeToContent=Height grows the window DOWNWARD from its Top, so a content-height
        // change while the flyout is open (wired→wireless shows the Wi-Fi cells, the 公网
        // IPv6 row appears…) sinks the bottom edge into a bottom taskbar. That flyout
        // grows UPWARD instead: on every height change keep the bottom edge anchored by
        // shifting Top by the negative delta (shrink → back down, restoring the 8px gap),
        // clamped at the work-area top. On any other taskbar edge (top/left/right —
        // classical taskbar only) the Top simply stays put and the window grows downward,
        // away from the edge. Pinned windows are exempt — they're draggable, so downward
        // growth from wherever the user put them is correct.
        private double _lastHeight = -1;
        // SetPinned shifts Top itself around the band's height change; the band's
        // SizeChanged arrives on the LATER async layout pass, so a plain "IsPinned"
        // check can't cover the unpin direction (IsPinned is already false by then) —
        // skip exactly one height change after a pin toggle.
        private bool _skipNextHeightAnchor;

        private void DetailWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.HeightChanged) return;
            double newHeight = e.NewSize.Height;
            if (_lastHeight < 0) { _lastHeight = newHeight; return; }   // first layout: ShowColumn positions
            double delta = newHeight - _lastHeight;
            _lastHeight = newHeight;
            if (Math.Abs(delta) < 0.01) return;
            if (_skipNextHeightAnchor) { _skipNextHeightAnchor = false; return; }
            if (IsPinned) return;
            if (_taskbarEdge != 0) return;   // non-bottom taskbar: grow downward from a fixed Top
            Top -= delta;
            ClampToWorkArea(0);
        }

        // ---------- Column switching ----------
        public void ShowColumn(int column)
        {
            _column = column;
            _current = column switch
            {
                0 => new CpuDetailView(_dark),
                1 => new RamDetailView(_dark),
                2 => new DiskDetailView(_dark),
                3 => new GpuDetailView(_dark),
                _ => new NetDetailView(_dark),    // 4
            };
            // Network panel just opened: ask the sampler to refresh Wi-Fi details on its
            // background thread (only if the cache is stale — a hit reuses it with zero wlanapi).
            // wlanapi is location-sensitive, so this is its sole trigger; the idle sampler path
            // never calls it, which keeps the taskbar location indicator off otherwise. Prewarm
            // builds views directly (not via ShowColumn), so this never fires at startup.
            if (column == 4) _owner.RequestWifiDetails();
            ContentHost.Content = _current;

            // Park the pin toggle in the fresh view's header slot (right of its title).
            // Detach from the previous view's slot first — an element can't have two
            // logical parents. (No ?. here: null-conditional assignment is a preview
            // feature that the WPF markup-compile pass rejects.)
            if (PinButton.Parent is ContentControl oldSlot) oldSlot.Content = null;
            _current.PinSlot.Content = PinButton;

            Show();
            // Activate BEFORE positioning: on this Windows build SetWindowPos(HWND_TOPMOST)
            // silently no-ops (returns TRUE, but WS_EX_TOPMOST never gets set) when the
            // window is not the foreground window at call time — exactly what happens to
            // windows opened in the first seconds after the (elevated) process starts,
            // while the implicit show-activation keeps losing the foreground lock. Once we
            // hold the foreground, the topmost band sticks (and survives later deactivation).
            Activate();
            // Best-effort foreground steal (the click that opened us already gave our
            // process foreground rights, so this succeeds).
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) WindowInterop.SetForegroundWindow(hwnd);
            PositionNearTaskbar(column);
            EnsureTopmost();
        }

        // The silent-no-op failure mode above is verified, not trusted: after positioning,
        // check the band actually took and re-apply it if it didn't (foreground may arrive
        // a few dispatcher rounds late in the early-process window).
        private int _topmostRetries;

        private void EnsureTopmost()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            long ex = WindowInterop.GetWindowLongPtr(hwnd, WindowInterop.GWL_EXSTYLE).ToInt64();
            if ((ex & WindowInterop.WS_EX_TOPMOST) != 0) return;   // already in the band
            if (WindowInterop.GetForegroundWindow() != hwnd) WindowInterop.SetForegroundWindow(hwnd);
            WindowInterop.SetWindowPos(hwnd, WindowInterop.HWND_TOPMOST, 0, 0, 0, 0,
                WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE);
            ex = WindowInterop.GetWindowLongPtr(hwnd, WindowInterop.GWL_EXSTYLE).ToInt64();
            if ((ex & WindowInterop.WS_EX_TOPMOST) == 0 && _topmostRetries++ < 10)
                Dispatcher.BeginInvoke(new Action(EnsureTopmost),
                    System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---------- Placement: next to the taskbar's screen edge, aligned to the column ----------
        private void PositionNearTaskbar(int column)
        {
            IntPtr overlay = _owner.OverlayHwnd;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (overlay == IntPtr.Zero || hwnd == IntPtr.Zero) return;

            if (!WindowInterop.GetWindowRect(overlay, out var orect)) return;
            if (!WindowInterop.GetWindowRect(hwnd, out var wrect)) return;

            int w = wrect.right - wrect.left;
            int h = wrect.bottom - wrect.top;
            // Which screen edge the taskbar is docked to (0=bottom 1=top 2=left 3=right):
            // the popup opens INWARD from that edge. Bottom is the only Win11 orientation;
            // the rest are classical-taskbar (Win10) cases. Remembered for the SizeChanged
            // anchor — only a bottom taskbar's flyout grows upward.
            int edge = TaskbarWindow.GetTaskbarEdge(overlay);
            _taskbarEdge = edge;

            int left, top;
            if (edge <= 1)
            {
                // Columns aren't equal width — centre the popup on the clicked column's
                // true centre. The overlay's layout follows the sampling switches (hidden
                // metrics are gone), so both numbers come from the owner's live layout.
                double logicalWidth = _owner.LogicalWidth;
                if (logicalWidth <= 0) return;   // every metric off — nowhere to anchor
                double ratio = _owner.ColumnCenter(column) / logicalWidth;
                double centerX = orect.left + (orect.right - orect.left) * ratio;
                left = (int)Math.Round(centerX - w / 2.0);
                top = edge == 0 ? orect.top - h - 8      // just above a bottom taskbar row
                                : orect.bottom + 8;      // just below a top taskbar row
            }
            else
            {
                // Side-docked (vertical) taskbar: centre on the column strip's Y centre.
                double logicalHeight = _owner.LogicalHeight;
                if (logicalHeight <= 0) return;
                double ratio = _owner.ColumnCenterY(column) / logicalHeight;
                double centerY = orect.top + (orect.bottom - orect.top) * ratio;
                top = (int)Math.Round(centerY - h / 2.0);
                left = edge == 2 ? orect.right + 8       // right of a left-docked taskbar
                                 : orect.left - w - 8;   // left of a right-docked one
            }

            // Clamp inside the overlay's monitor work area (both axes, any edge).
            IntPtr mon = WindowInterop.MonitorFromWindow(overlay, WindowInterop.MONITOR_DEFAULTTONEAREST);
            var mi = new WindowInterop.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowInterop.MONITORINFO>() };
            if (mon != IntPtr.Zero && WindowInterop.GetMonitorInfoW(mon, ref mi))
            {
                int workLeft = mi.rcWork.left + 8;
                int workRight = mi.rcWork.right - 8;
                if (left < workLeft) left = workLeft;
                if (left + w > workRight) left = workRight - w;
                if (top < mi.rcWork.top) top = mi.rcWork.top; // taller popup than work area
                if (top + h > mi.rcWork.bottom) top = mi.rcWork.bottom - h;   // top/side edges can overflow the bottom
            }

            WindowInterop.SetWindowPos(hwnd, WindowInterop.HWND_TOPMOST, left, top, 0, 0,
                WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE);
        }

        // The edge PositionNearTaskbar last placed us against (0=bottom). The unpinned
        // flyout's upward-growth anchor (DetailWindow_SizeChanged) only applies on a
        // bottom taskbar; elsewhere the Top stays put and the window grows downward.
        private int _taskbarEdge;

        // ---------- Live data (refresh the hosted view) ----------
        internal void Refresh()
        {
            var s = _owner.LatestSnapshot;
            if (s == null) return; // overlay hasn't sampled yet
            _current?.Refresh(s);
        }

        // ---------- Pinned mode ----------
        /// <summary>The hit slot this window shows (0–4: CPU/内存/磁盘/GPU/网络), as passed to <see cref="ShowColumn"/>.</summary>
        public int Column => _column;

        /// <summary>True while pinned: focus loss no longer closes the window.</summary>
        public bool IsPinned { get; private set; }

        /// <summary>Raised on the UI thread when the user toggles the pin. App moves the
        /// window between the transient popup slot and its pinned-window list.</summary>
        public event Action<DetailWindow, bool> PinStateChanged;

        private void PinButton_Checked(object sender, RoutedEventArgs e) => SetPinned(true);
        private void PinButton_Unchecked(object sender, RoutedEventArgs e) => SetPinned(false);

        private void SetPinned(bool pinned)
        {
            if (IsPinned == pinned) return;
            IsPinned = pinned;
            // The band adds/removes its height ABOVE the content. On pin, nudge the window
            // up by the same amount so the content (and the taskbar gap) stays put — the
            // bar visually "grows out of the top". On unpin, return to the exact spot the
            // flyout occupied before pinning (a pinned window is freely draggable, so
            // wherever it happens to be now is not home). The pin sits in the view's
            // header in both forms, so it never moves on screen either.
            double bandDelta = TitleBarBand.Height + TitleBarBand.BorderThickness.Bottom;
            // The band toggle's SizeChanged arrives on the async layout pass AFTER this
            // method — the bottom-anchor must not re-anchor a move we just did by hand.
            _skipNextHeightAnchor = true;
            TitleBarBand.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            if (pinned)
            {
                _prePinLeft = Left;
                _prePinTop = Top;
                Top -= bandDelta;
                ClampToWorkArea(bandDelta);
            }
            else
            {
                Left = _prePinLeft;
                Top = _prePinTop;
                ClampToWorkArea(-bandDelta);
            }
            Title = pinned ? (_column switch { 0 => "CPU", 1 => "内存", 2 => "磁盘", 3 => "GPU", _ => "网络" }) : "Detail";
            // Keep the overlay's pressed/selected highlight in step with the form:
            // pinned → the column is press-disabled, so it must not look pressed;
            // unpinned → the flyout is open again, so its column is selected (restores
            // the "flyout open ⟺ column selected" invariant; the next click toggles it
            // closed instead of tearing down + reopening).
            if (pinned) _owner.RequestDeselect(_column);
            else _owner.RequestSelect(_column);
            PinIcon.Icon = pinned ? FluentSystemIcons.Pin_16_Filled : FluentSystemIcons.Pin_16_Regular;
            PinButton.ToolTip = pinned ? "取消固定" : "固定";
            PinStateChanged?.Invoke(this, pinned);
        }

        // Flyout position saved when pinned; restored on unpin.
        private double _prePinLeft;
        private double _prePinTop;

        // Keep the nudged window inside its monitor's work area. heightDelta is the
        // pending SizeToContent change (ActualHeight hasn't re-measured yet).
        private void ClampToWorkArea(double heightDelta)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            IntPtr mon = WindowInterop.MonitorFromWindow(hwnd, WindowInterop.MONITOR_DEFAULTTONEAREST);
            var mi = new WindowInterop.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowInterop.MONITORINFO>() };
            if (mon == IntPtr.Zero || !WindowInterop.GetMonitorInfoW(mon, ref mi)) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            double workTop = mi.rcWork.top / dpi.DpiScaleY;
            double workBottom = mi.rcWork.bottom / dpi.DpiScaleY;
            double height = ActualHeight + heightDelta;
            if (Top < workTop) Top = workTop;
            if (Top + height > workBottom) Top = Math.Max(workTop, workBottom - height);
        }

        // Pinned windows are freely draggable: any left-press that didn't land on an
        // interactive control drags the whole (borderless) window.
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsPinned || e.ButtonState != MouseButtonState.Pressed) return;
            if (IsOnInteractiveControl(e.OriginalSource as DependencyObject)) return;
            DragMove();
        }

        private static bool IsOnInteractiveControl(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ButtonBase || d is ScrollBar) return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---------- Focus loss → hide (event-driven; no polling) ----------
        private void Window_Deactivated(object sender, EventArgs e)
        {
            // A pinned window keeps living on focus loss — it closes via its ✕ button.
            if (_dismissed || IsPinned) return;
            // Any focus loss → tear down (a fresh window is created on next open).
            _dismissed = true;
            _owner.RequestDeselect(_column);
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // If the close didn't come from the deactivate path (✕ button, Alt+F4, App
            // teardown), the overlay may still be highlighting our column — clear it.
            // Column-aware, so it's a no-op if the selection has since moved on.
            if (!_dismissed) _owner.RequestDeselect(_column);
            // New window per open: allow real destruction. Flag dismissed so a
            // deactivation fired during close is a no-op.
            _dismissed = true;
        }
    }
}
