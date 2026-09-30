using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;

namespace task_monitor
{
    /// <summary>
    /// Application entry point. There is intentionally no main window — this is a
    /// taskbar widget whose only persistent UI is the overlay (rendered on its own
    /// dedicated STA background thread). <see cref="ShutdownMode"/> is
    /// <c>OnExplicitShutdown</c>, so the process stays alive with no open WPF window;
    /// exit is via the overlay's right-click menu. Clicks on the overlay are marshaled
    /// here onto the UI thread to show the on-demand <see cref="DetailWindow"/>.
    /// </summary>
    public partial class App : Application
    {
        private TaskbarWindow _taskbar;
        private Thread _taskbarThread;
        // The transient (unpinned) flyout — at most one; torn down on every toggle.
        private DetailWindow _detail;
        // Pinned windows — one per column at most; survive focus loss and coexist with
        // the transient flyout. All open windows get every snapshot push (one per tick).
        private readonly List<DetailWindow> _pinned = new List<DetailWindow>();

        // Single-instance guard handle — see OnStartup. Kept alive for the whole process
        // lifetime so the named mutex persists; process death closes the handle and the
        // kernel object is destroyed, letting the next launch win it.
        private Mutex _singleInstanceMutex;

        // Set in OnExit so the overlay-recreate loop in StartTaskbar stops re-entering
        // TaskbarWindow.Start() — a shutdown must not spawn a fresh overlay.
        private volatile bool _stopping;

        // The loaded settings.yaml (run directory). Read once by the elevation gate at
        // startup; kept on the instance — the settings pages share this same store.
        private AppSettings _config;

        // Global crash hooks that don't need Application.Current — installed in the
        // static ctor so they exist before Main() runs (even an App.xaml BAML failure
        // inside InitializeComponent is caught). The file log is initialized FIRST so
        // even those earliest crashes have somewhere to be written; the Dispatcher hook
        // needs Application.Current, so it is added at the top of OnStartup. Every
        // unhandled managed exception funnels into CrashReporter → log file + crash dialog.
        static App()
        {
            Logger.Init();
            CrashReporter.Install();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // UI-thread crash hook FIRST — before the elevation gate, so even the
            // ConsentDialog path is covered. One fault must never silently kill the
            // process: report it, and only exit when the user picks 退出 in the dialog.
            DispatcherUnhandledException += (s, e2) =>
            {
                e2.Handled = true;
                if (!CrashReporter.Report("UI 线程未处理异常", e2.Exception, fatal: false, block: true))
                    Shutdown();
            };

            // Elevation gate FIRST: the app is designed to always run elevated, and every
            // unelevated path inside it ends in Shutdown(). This MUST come before the
            // single-instance mutex — an unelevated "launcher" instance that is about to
            // exit must never hold the mutex, or the elevated relaunched child would take
            // itself for a second instance and exit silently.
            if (!RunElevationGate()) return;

            // Single-instance guard: a named GLOBAL kernel mutex. Only reachable elevated
            // (the gate above), so the SeCreateGlobalPrivilege the Global\ namespace
            // needs is in hand, and the prefix makes the guard hold across every
            // session/elevation. We create it un-owned (initiallyOwned=false) purely to
            // test existence: createdNew=false means another instance already owns the
            // name. A doomed second instance must never spin up the taskbar thread or
            // open handles. The taskbar overlay is always visible, so the user can
            // already see the running instance; exit this one silently (a message box
            // from an elevated process would be intrusive). A consented user's second
            // launch still UAC-prompts (the runas relaunch in the gate) before dying
            // here — inherent to the always-elevated design.
            _singleInstanceMutex = new Mutex(
                initiallyOwned: false,
                name: @"Global\TaskMonitor.exe__7f3a2c9e-4b1d-4e8a-9f2c-6a5b8c7d1e0a",
                createdNew: out bool createdNew);
            if (!createdNew)
            {
                Logger.Info("全局互斥体已存在——另一实例正在运行，本实例静默退出");
                Shutdown();
                return;
            }

            // With the mutex held, any shutdown.sentinel on disk targeted a PREVIOUS
            // instance (the build flow's touch is always consumed by the still-running
            // old instance before we can start) — a leftover must not self-exit us 2s in
            // (2026-09-29: a double-touched sentinel did exactly that, and the shutdown
            // then raced the queued prewarm into a crash dialog).
            TaskbarWindow.ClearStaleShutdownSentinel();

            base.OnStartup(e);

            Logger.Info($"启动 — 版本 {VersionInfo.Current}，OS {Environment.OSVersion}，Win11+={TaskbarWindow.IsWin11OrLater}");

            // Repaint already-open detail windows on ANY effective theme flip — the 设置
            // 主题 combo, or a system-theme change while 跟随系统 (the iNKORE ThemeManager
            // tracks it and fires this; everything DynamicResource-driven — the settings
            // window, the context menu — restyles itself and needs nothing here).
            ThemeManager.Current.ActualApplicationThemeChanged += (s, e2) =>
            {
                _detail?.ApplyTheme();
                foreach (var w in _pinned) w.ApplyTheme();
                // 悬浮模式的自绘卡片用的是 detail 弹窗那套色调常量，所以它的卡片颜色和文字颜色
                // 跟 APP 主题走（DetailWindow.ApplyTheme 同源），不是任务栏的系统主题。
                _taskbar?.SetFloatingDark(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark);
            };

            StartTaskbar();

            // 卡死看门狗 — see CheckOverlayHealth. A freeze that heals itself leaves NOTHING
            // in the log, because the stalled thread is the very thread that would write it.
            StartOverlayWatchdog();

            // Pre-warm WPF: the overlay is a native Win32 window, so without this the
            // FIRST WPF window ever shown is the user's first click — which then eats
            // the whole cold-start tax (render thread/D3D, JIT, BAML, style realization,
            // acrylic, fonts). One throwaway DetailWindow, built + laid out offscreen
            // with every view once, pays it up front. Deferred past OnStartup so the
            // extra show/close can't disturb WPF's first-window bookkeeping; it still
            // completes long before the user can click.
            // NOTE: the right-click menu host stays LAZILY created (first right-click) —
            // showing it at startup regressed the menu: it stopped opening at all.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // This Background-priority slot can be reached MID-SHUTDOWN: OnExit's
                // Join of the taskbar thread keeps pumping the STA queue, dispatching
                // whatever is still queued (2026-09-29: a stale sentinel exited the app
                // 2s in, and the prewarm then ran against resources that were already
                // being torn down — NRE inside WPF's DeferredAppResourceReference, crash
                // dialog on a graceful exit). Skip when stopping; Prewarm itself also
                // never throws (it is a pure optimization).
                if (_stopping) return;
                new DetailWindow(_taskbar).Prewarm();
                // Hand the one-touch startup pages (JIT of the show path, BAML parse,
                // prewarm's UI trees) back to the standby list — see ScheduleIdleTrim.
                SystemInfo.TrimMemory();
            }), System.Windows.Threading.DispatcherPriority.Background);

            UpdateChecker.CheckOnce(_config, TrySaveConfig);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info("退出");
            _stopping = true;
            // Flush a pending 透明度 debounce first: the timer dies with the dispatcher, so
            // quitting within its 500ms window silently reverted the last slider step even
            // though the comment above the timer promises the value still lands.
            if (_configSaveDebounce?.IsEnabled == true)
            {
                _configSaveDebounce.Stop();
                TrySaveConfig();
            }
            // Classical (Win10) taskbar path: the overlay's WM_DESTROY restores the
            // shrunk task-buttons band. Ask for that destroy and give the taskbar thread
            // a bounded moment to run it — it's a background thread, so without the join
            // process death can skip the restore entirely (the band stays narrow until
            // explorer's next layout pass).
            IntPtr overlay = _taskbar?.OverlayHwnd ?? IntPtr.Zero;
            if (overlay != IntPtr.Zero)
            {
                WindowInterop.PostMessageW(overlay, WindowInterop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _taskbarThread?.Join(1000);
            }
            // We never took ownership of the mutex (initiallyOwned=false), so there's
            // nothing to release — just drop the handle. Done explicitly so the lifetime
            // is unambiguous; process death would close it regardless.
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        // ---------- Elevation gate (first-run consent + runas self-relaunch) ----------
        // The app is designed to ALWAYS run elevated (the SRUM per-process network API
        // returns nothing useful otherwise), but the manifest is asInvoker so the first
        // launch can ask for consent WITHOUT a UAC prompt. The consent ("是否允许") is
        // persisted to settings.yaml in the run directory; refusal is deliberately NOT
        // persisted — the next launch asks again. Every unelevated path ends in
        // Shutdown(): the process never runs degraded.
        private bool RunElevationGate()
        {
            _config = AppSettings.Load();
            // Theme first, before ANY window exists (the ConsentDialog below included) —
            // re-applied to open windows live via the ActualApplicationThemeChanged hook.
            ApplyThemeSetting();

            if (IsElevated()) return true;

            if (_config.ElevationConsent == true)
            {
                // Consented before → every launch self-elevates. A UAC-cancel lands in
                // TryRelaunchElevated's catch; either way this launcher exits.
                Logger.Info("提权门：已持久化同意——runas 自我重启提升权限");
                TryRelaunchElevated();
                Shutdown();
                return false;
            }

            // First run (or previously refused — nothing was written then): ask.
            bool consented = false;
            if (new ConsentDialog().ShowDialog() == true)
            {
                consented = true;
                Logger.Info("提权门：用户首次同意, 持久化后 runas 自我重启提升权限");
                _config.ElevationConsent = true;
                TrySaveConfig();        // best effort — a read-only install dir degrades to "ask again"
                TryRelaunchElevated();
            }
            // Refused (不允许 / ✕ / Esc), or the UAC prompt was cancelled (reported by
            // TryRelaunchElevated's own catch): exit rather than run degraded —
            // "始终保持在管理员权限的状态下运行". Logged only on the refusal path: an
            // unconditional line here made a consented relaunch look refused in the log.
            if (!consented) Logger.Info("提权门：未提权（用户拒绝或对话框取消）");
            Shutdown();
            return false;
        }

        private static bool IsElevated() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

        // Relaunch this exe elevated via the shell's "runas" verb. Process.Start blocks
        // while the UAC prompt is up and throws Win32Exception (ERROR_CANCELLED 1223)
        // when the user declines — swallowed here because the caller exits regardless.
        private static void TryRelaunchElevated()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Verb = "runas",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Logger.Info($"UAC 提示被取消（Win32 err={ex.NativeErrorCode}）——启动器退出");
            }
        }

        private void TrySaveConfig()
        {
            try { _config.Save(); }
            catch (Exception ex) { Logger.Warn("settings.yaml 保存失败（下次启动将再询问）", ex); }
        }

        private void StartTaskbar()
        {
            _taskbar = new TaskbarWindow();
            // Clicks arrive on the taskbar STA thread; hop to the UI thread to
            // drive the WPF detail window. -1 = hide, 0–4 = show that hit slot
            // (CPU/内存/磁盘/GPU/网络).
            _taskbar.ToggleCallback = column =>
                Dispatcher.BeginInvoke(new Action<int>(ToggleDetail), column);
            _taskbar.RightClickRequested = () =>
                Dispatcher.BeginInvoke(new Action(ShowTaskbarMenu));
            // Push: each time the taskbar publishes a fresh snapshot, refresh every open
            // detail window in lockstep (so they always show the same numbers).
            _taskbar.SnapshotChanged = () =>
                Dispatcher.BeginInvoke(new Action(RefreshDetails));
            // Initial overlay placement + sampling cadence from settings.yaml (the
            // settings page reports later changes via OnOverlayPlacementChanged /
            // OnSampleIntervalChanged).
            _taskbar.SetPlacement(_config.OverlayOnLeft != false, _config.OverlaySnapToStart == true);
            _taskbar.SetSampleInterval(_config.SampleIntervalMs ?? 1000);
            _taskbar.SetMetricSamplingMask(SamplingMaskOf(_config));
            _taskbar.SetMergeSamePathProcesses(_config.MergeSamePathProcesses != false); // null = on (the default)
            _taskbar.SetDiskDisplay(DiskDisplayModeIndexOf(_config), _config.DiskDisplayIndex ?? 0);
            _taskbar.SetGpuDisplay(GpuDisplayModeIndexOf(_config), _config.GpuDisplayIndex ?? 0);
            _taskbar.SetNetAdapter(_config.NetAdapterId);   // null = 自动 (the default)
            _taskbar.SetClashApi(_config.ClashEnabled != false, _config.ClashApiAddress, _config.ClashApiSecret); // null = on; null address = the 127.0.0.1:9090 default
            _taskbar.SetPublicIpLookup(_config.PublicIpEnabled != false); // null = on (the default)
            // 悬浮模式 (设置 → 外观): the form Start() builds. -1 = no saved home yet → the
            // widget opens where the taskbar overlay would have been anchored.
            _taskbar.SetFloatingMode(_config.FloatingMode == true, _config.FloatingX ?? -1, _config.FloatingY ?? -1);
            _taskbar.SetFloatingTopmost(_config.FloatingTopmost != false);   // null = 置顶 (the default)
            _taskbar.SetFloatingOpacity(_config.FloatingOpacity ?? 1.0);     // null = 不透明 (the default)
            _taskbar.SetFloatingEdgeHide(_config.FloatingEdgeHide == true);  // null = off (the default)
            _taskbar.SetFloatingFullscreenHide(_config.FloatingFullscreenHide != false);  // null = on (the default)
            // The floating widget's card follows the APP theme (it is painted in the detail
            // popup's tint), not the taskbar's system theme — resolved here and kept live by the
            // ActualApplicationThemeChanged hook in OnStartup.
            _taskbar.SetFloatingDark(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark);
            // A drag ends on the taskbar thread; the position is settings, so it comes back
            // here to be persisted (and only here — pushing it back would fight the drag).
            _taskbar.FloatingPositionChanged = (x, y) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _config.FloatingX = x;
                _config.FloatingY = y;
                TrySaveConfig();
            }));
            // …and while the drag is in progress every open detail window travels with the
            // widget (same delta), so the pair never comes apart mid-drag. Live, on the UI
            // thread: the windows have to move with the mouse, not after it.
            _taskbar.FloatingDragged = (dx, dy) =>
                Dispatcher.BeginInvoke(new Action<int, int>(FollowFloatingDrag), dx, dy);

            var thread = new Thread(() =>
            {
                // Keep an overlay alive for the whole process lifetime. Start() blocks
                // in its message loop; it only returns when the overlay window is gone —
                // an explorer.exe restart destroys the Shell_TrayWnd parent and our
                // child window with it — or when init throws (e.g. the D3D device at
                // very early boot). Re-enter instead of leaving a running process with
                // no widget; the _stopping guard keeps shutdown from spawning a fresh
                // overlay, and the sleep keeps a persistent failure from hot-looping.
                while (!_stopping)
                {
                    try
                    {
                        _taskbar.Start();
                        if (!_stopping)
                        {
                            // A 悬浮模式 flip tears the window down on purpose: come straight
                            // back so the widget reappears in its new form in ~150ms instead of
                            // blinking out for the whole explorer-restart backoff.
                            int delay = _taskbar.ConsumeQuickRestart() ? 150 : 2000;
                            Logger.Warn($"任务栏覆盖层 Start() 已返回（窗口销毁——explorer 重启或初始化失败），{delay}ms 后重建");
                            // Keep the watchdog's heartbeat fed through the rebuild gap —
                            // this thread is alive and working, just between ticks (§43).
                            _taskbar.LastTickTickCount = (long)SystemInfo.GetTickCount64();
                            Thread.Sleep(delay);
                            _taskbar.LastTickTickCount = (long)SystemInfo.GetTickCount64();
                            // Already backed off above. Falling through to the 2s below took
                            // it TWICE, so a 悬浮模式 flip waited ~2.15s (150+2000) instead of
                            // the documented ~150ms — exactly the blink this branch exists to
                            // avoid — and an explorer restart waited ~4s.
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The taskbar overlay is non-critical; never let it take the app down.
                        Logger.Error("任务栏覆盖层 Start() 抛异常，2s 后重建", ex);
                    }
                    // FAILURE backoff only — the ordinary return already slept its own delay.
                    if (!_stopping) Thread.Sleep(2000);
                }
            })
            { IsBackground = true, Name = "TaskbarWindow" };
            thread.SetApartmentState(ApartmentState.STA);
            _taskbarThread = thread;   // OnExit joins it after closing the overlay (band restore)
            thread.Start();
        }

        private void RefreshDetails()
        {
            _detail?.Refresh();
            foreach (var w in _pinned) w.Refresh();
        }

        /// <summary>UI-thread handler for a taskbar column toggle.</summary>
        private void ToggleDetail(int column)
        {
            // A brand-new window is created on every open; the previous (transient) one
            // is torn down. Reusing a hidden-then-reshown window let its acrylic
            // transition inactive→active and flash, whereas a freshly created window
            // shown + activated in one go is born active (no flash). Pinned windows are
            // untouched — they explicitly opted out of the flyout lifecycle.
            CloseDetail();
            if (column < 0) return;

            var existing = _pinned.Find(w => w.Column == column);
            if (existing != null)
            {
                existing.Activate();
                // Best-effort foreground steal (the click already gave our process
                // foreground rights, so this succeeds) — same as ShowColumn does.
                var existingHwnd = new WindowInteropHelper(existing).Handle;
                if (existingHwnd != IntPtr.Zero) WindowInterop.SetForegroundWindow(existingHwnd);
                return;
            }

            _detail = new DetailWindow(_taskbar);
            _detail.PinStateChanged += OnDetailPinStateChanged;
            // Any rect change (the popup's placement, the pin band growing the window upward,
            // content height, a pinned window being dragged) moves the keep-out the floating
            // widget dodges — republish it.
            _detail.SizeChanged += (s, e) => ScheduleDetailKeepOut();
            _detail.LocationChanged += (s, e) => ScheduleDetailKeepOut();
            _detail.Closed += (s, e) =>
            {
                var w = (DetailWindow)s;
                if (ReferenceEquals(_detail, w)) _detail = null;
                _pinned.Remove(w);
                // Free the column's press if this window had it pinned (idempotent).
                _taskbar.SetColumnClickEnabled(w.Column, true);
                ScheduleDetailKeepOut();
                ScheduleIdleTrim();
            };
            _detail.ShowColumn(column);
            ScheduleDetailKeepOut();
        }

        // A window that pins itself leaves the transient slot (so a new popup may open
        // alongside it); unpinning makes it the transient popup again. Unpinning can only
        // be clicked while that window has focus, which guarantees the transient slot is
        // empty by then (any focus shift closes a transient flyout). While a window is
        // pinned its overlay column is press-disabled (hover retained) — the pinned
        // window owns the column until it is unpinned or closed.
        private void OnDetailPinStateChanged(DetailWindow w, bool pinned)
        {
            if (pinned)
            {
                if (ReferenceEquals(_detail, w)) _detail = null;
                if (!_pinned.Contains(w)) _pinned.Add(w);
                _taskbar.SetColumnClickEnabled(w.Column, false);
            }
            else
            {
                _pinned.Remove(w);
                _detail = w;
                _taskbar.SetColumnClickEnabled(w.Column, true);
            }
            // Pin/unpin moves the window (the band grows out of the top; unpin restores the
            // pre-pin spot) — a different keep-out for the floating widget either way.
            ScheduleDetailKeepOut();
        }

        // ---------- 悬浮模式: the detail windows travel with the widget ----------
        // The widget is being dragged, one step at a time (TaskbarWindow.FloatingDragged). Every
        // open detail window is anchored to it — the flyout beside its column, a pinned window
        // wherever the user put it — so they all move by the same delta and the group stays
        // together. (A pinned window survives the drag by definition; the transient flyout
        // survives it too whenever 置顶显示 is on, because the widget is WS_EX_NOACTIVATE and
        // therefore never takes focus away from it. With 置顶显示 off, the click that starts the
        // drag activates the widget and the flyout dismisses itself, as any focus loss does.)
        private void FollowFloatingDrag(int dx, int dy)
        {
            _detail?.FollowFloatingDrag(dx, dy);
            foreach (var w in _pinned) w.FollowFloatingDrag(dx, dy);
        }

        // ---------- 悬浮模式: keep the widget out from under a detail window ----------
        // The widget's home is where the user dragged it, but a detail window can end up
        // covering that spot: its placement is anchored to the widget's column and CLAMPED into
        // the monitor work area, so on a screen too short to hold the popup beside the widget
        // the popup lands on top of it (and pinning grows the window further up). Rather than
        // re-place the popup, the widget steps aside — TaskbarWindow.ComputeFloatTarget — and
        // goes back home once no open window covers it. App owns the window LISTS, so it
        // publishes them; the taskbar thread reads the rects itself (and re-derives every tick,
        // which is what makes a change that raised no WPF event still land).
        //
        // Deferred AND coalesced: toggling a column closes the old popup and opens the new one
        // inside one dispatcher pass, so a synchronous push on the close would send the widget
        // home for a few ms and dodge it right back out (a visible blink). Background priority
        // also puts the push after the layout/render pass, i.e. after the new window's
        // placement has actually reached its HWND.
        private bool _keepOutQueued;

        private void ScheduleDetailKeepOut()
        {
            if (_keepOutQueued) return;
            _keepOutQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _keepOutQueued = false;
                var hwnds = new List<IntPtr>(_pinned.Count + 1);
                AddKeepOut(_detail, hwnds);
                foreach (var w in _pinned) AddKeepOut(w, hwnds);
                _taskbar.SetFloatingKeepOut(hwnds.ToArray());
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private static void AddKeepOut(DetailWindow w, List<IntPtr> into)
        {
            if (w == null) return;
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) into.Add(hwnd);
        }

        private void CloseDetail()
        {
            var d = _detail;
            _detail = null;
            d?.Close();
        }

        // A detail window closing leaves its whole UI tree (BAML-built controls, charts,
        // acrylic resources) resident until a GC happens to run — which, for a process
        // that then sits idle, may be never. Trim once the LAST window is gone. The check
        // is deferred to Background priority: Closed fires synchronously mid-ToggleDetail
        // (old window closes, new one hasn't been created yet), so checking inline would
        // see a false "all closed" and trim right before the replacement window faults
        // every page straight back in. Deferred, the toggle's new window already exists
        // and the trim is correctly skipped.
        private void ScheduleIdleTrim()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_detail != null || _pinned.Count != 0 || _settings != null) return;
                SystemInfo.TrimMemory();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ---------- 卡死看门狗 (the reported 「进程卡死了…又恢复了」) ----------
        // The taskbar thread owns the overlay window, its message loop, the sampler and the
        // drawing. If a call inside it blocks — a USB disk that went to sleep inside
        // IOCTL_DISK_PERFORMANCE, an RPC, the compositor — the widget holds its last frame and
        // ignores clicks for as long as that call takes, and the stalled thread cannot log its
        // own stall. Reported twice with an empty log, so this watchdog reports from the OTHER
        // side, where it can always run: the tick heartbeat (TaskbarWindow.LastTickTickCount)
        // and the overlay HWND, each with the episode's start and end. Healthy = silent.
        // (The taskbar thread contributes the matching half: per-step and per-phase timings
        // inside the tick, which name the blocking call when it finally returns.)
        private System.Windows.Threading.DispatcherTimer _overlayWatch;
        private const int WatchIntervalMs = 2000;
        private long _stallLastTick;    // 0 = no stall in progress; else the last tick before it
        private long _goneSinceTick;    // 0 = the overlay HWND is there; else when it vanished
        private bool _goneLogged;
        private bool _restartedForZombie;   // the 45s zombie self-restart already fired this run

        private void StartOverlayWatchdog()
        {
            _overlayWatch = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(WatchIntervalMs) };
            _overlayWatch.Tick += (s, e) => CheckOverlayHealth();
            _overlayWatch.Start();
        }

        private void CheckOverlayHealth()
        {
            // The UI queue is still pumped during OnExit's Join (§41), so this tick can fire
            // mid-teardown. Without the guard, a quit that crossed the 45s zombie threshold
            // would run the self-heal: spawn a successor process and hard-exit this one —
            // "退出 didn't stick, the widget came back".
            if (_stopping) return;
            var tb = _taskbar;
            if (tb == null) return;
            long now = (long)SystemInfo.GetTickCount64();

            // (1) The tick heartbeat. The threshold is deliberately loose: a 悬浮模式 flip
            //     rebuilds the window and legitimately has a ~2s gap (Start()'s taskbar probe),
            //     and a slow-but-working sampler must not be reported.
            long last = tb.LastTickTickCount;
            if (last != 0)
            {
                int interval = _config.SampleIntervalMs ?? 1000;
                long threshold = Math.Max(4000, interval * 3L);
                if (_stallLastTick == 0)
                {
                    if (now - last > threshold)
                    {
                        _stallLastTick = last;
                        // Forensics FROM THE OTHER SIDE: the blocked thread can never name
                        // its own blocking call, so dump what it last marked (CrashTrace's
                        // step + message ring) plus a NATIVE STACK grab of the taskbar
                        // thread (suspend µs → copy → resume — CaptureStackOf cannot wedge
                        // behind whatever the thread is blocked in).
                        Logger.Warn($"覆盖层心跳停滞：已 {now - last}ms 没有采样 tick（设定 {interval}ms，阈值 {threshold}ms）——任务栏线程卡在某个阻塞调用里，恢复时会补记一行；当前步骤={CrashTrace.CurrentStep}，最近消息=[{CrashTrace.RecentMessages()}]，线程栈={CrashTrace.CaptureStackOf(tb.NativeThreadId)}");
                    }
                }
                else if (now - last <= interval * 2L)
                {
                    Logger.Warn($"覆盖层心跳恢复：本次停滞约 {now - _stallLastTick}ms（从最后一次正常 tick 算起）");
                    _stallLastTick = 0;
                }
            }

            // (2) The zombie state: the window is gone while Start() never returned, so the
            //     recreate loop never fires either — and with the window died the 1s timer that
            //     would have noticed. Nothing in the process can recover from it on its own.
            //     A 悬浮模式 flip takes the window away on purpose for ~2s, so only a
            //     disappearance that PERSISTS gets logged (and only then is the recovery line
            //     worth printing — otherwise every flip would log a phantom "已恢复").
            IntPtr hwnd = tb.OverlayHwnd;
            bool there = hwnd != IntPtr.Zero && WindowInterop.IsWindow(hwnd);
            if (!there)
            {
                if (_goneSinceTick == 0) _goneSinceTick = now;
                else if (!_goneLogged && now - _goneSinceTick > 5000)
                {
                    _goneLogged = true;
                    Logger.Error($"覆盖层窗口已消失 {(now - _goneSinceTick) / 1000}s 而 Start() 未返回——重建循环也停了；先唤醒消息循环，45s 后仍无恢复将自动重启进程（当前步骤={CrashTrace.CurrentStep}，最近消息=[{CrashTrace.RecentMessages()}]）");
                    // The gentle in-process wake BEFORE the process restart: a window gone
                    // this long usually means the loop is parked in GetMessage on an EMPTY
                    // queue — the timers died with the window and, the WM_DESTROY that would
                    // post the quit never having been dispatched (the 2026-09-30 卡死's end
                    // state: RIP=win32u = NtUserGetMessage), nothing will ever wake it. One
                    // PostThreadMessage(WM_QUIT) makes GetMessage return 0: Start() returns,
                    // the recreate loop re-embeds on the new taskbar — ~7s, no restart.
                    // Harmless when the thread is instead blocked in a call: the quit waits
                    // in the queue for its return (the 45s restart stays the backstop).
                    if (tb.NativeThreadId > 0)
                        WindowInterop.PostThreadMessageW((uint)tb.NativeThreadId, WindowInterop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                // Self-heal the unrecoverable zombie: the taskbar thread is blocked inside a
                // call that never returned (the 2026-09-30 卡死 — 20+ minutes, only a manual
                // kill helped), the window is gone, and the recreate loop is dead with it.
                // Nothing INSIDE the process can recover that — a foreground thread can't be
                // killed safely — so respawn the process and hard-exit this one. We are
                // ELEVATED, and a child of an elevated process is elevated too: a plain
                // CreateProcess (UseShellExecute=false — no shell to lean on while explorer
                // may itself be mid-restart) hands the successor full rights SILENTLY, no
                // UAC at all (the gate's IsElevated passes). The single-instance mutex is
                // un-owned and dies with us; Environment.Exit does not wait for the hung
                // foreground thread the way a Main-return would. The log handle goes first —
                // FileShare.Read would eat the child's first lines (Logger.ReleaseHandle).
                // Once per process lifetime: a repeat zombie restarts again.
                if (!_restartedForZombie && now - _goneSinceTick > 45000)
                {
                    _restartedForZombie = true;
                    Logger.Error($"覆盖层窗口消失 {(now - _goneSinceTick) / 1000}s——判定为不可恢复卡死，自动重启进程（父进程已提权，子进程静默接管，无 UAC）");
                    try
                    {
                        var exe = Process.GetCurrentProcess().MainModule.FileName;
                        // Drop the mutex handle FIRST: the named object is an existence
                        // test, so it only dies WITH its last handle — the child checks it
                        // ~0.3s in, while this process still has ~1s of OnExit (the taskbar
                        // Join) left to live; undisposed, the child takes itself for a
                        // second instance and exits silently (2026-09-30 PID=368).
                        _singleInstanceMutex?.Dispose();
                        _singleInstanceMutex = null;
                        // And release the log handle (FileShare.Read eats the child's first
                        // lines) — this process writes NOTHING after this point.
                        Logger.ReleaseHandle();
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe,
                            UseShellExecute = false,
                            WorkingDirectory = Path.GetDirectoryName(exe),
                        });
                        // No "spawned OK" line on purpose: logging it would re-open the
                        // handle ReleaseHandle just freed and eat the child's first lines.
                    }
                    catch (Exception ex) { Logger.Error("自动重启：拉起新进程失败", ex); }
                    Environment.Exit(0);
                }
            }
            else if (_goneLogged)
            {
                Logger.Info($"覆盖层窗口已恢复（消失约 {now - _goneSinceTick}ms）");
                _goneSinceTick = 0;
                _goneLogged = false;
            }
            else
            {
                _goneSinceTick = 0;   // back before the threshold: a form flip, not an incident
            }
        }

        // ---------- Right-click context menu (悬浮模式 / 置顶显示 / 设置 / Exit) ----------
        private ContextMenu _taskbarMenu;
        private MenuItem _menuFloating;
        private MenuItem _menuFloatingTopmost;
        private MenuItem _menuFloatingEdgeHide;
        private MenuItem _menuFloatingFullscreenHide;
        private Window _menuHost;
        // The settings window — at most one; a second menu click re-activates it.
        private SettingsWindow _settings;

        private void ShowTaskbarMenu()
        {
            EnsureTaskbarMenu();
            _menuHost.Activate();            // make it the active window so deactivation closes the menu
            _taskbarMenu.IsOpen = true;      // Placement=MousePoint → opens at the cursor
        }

        private void ShowSettings()
        {
            if (_settings != null)
            {
                _settings.Activate();
                return;
            }
            _settings = new SettingsWindow(
                _config.OverlayOnLeft != false, _config.OverlaySnapToStart == true, OnOverlayPlacementChanged,
                StartupTask.IsEnabled(), OnAutoStartChanged,
                ThemeIndexOf(_config.Theme), OnThemeChanged,
                _config.FloatingMode == true, OnFloatingModeChanged,
                _config.FloatingTopmost != false, OnFloatingTopmostChanged,
                _config.FloatingEdgeHide == true, OnFloatingEdgeHideChanged,
                _config.FloatingFullscreenHide != false, OnFloatingFullscreenHideChanged,
                _config.FloatingOpacity ?? 1.0, OnFloatingOpacityChanged,
                _config.SampleIntervalMs ?? 1000, OnSampleIntervalChanged,
                SamplingMaskOf(_config), OnMetricSamplingChanged,
                _config.MergeSamePathProcesses != false, OnMergeSamePathChanged,
                // The 特定磁盘 picker's items come from the latest snapshot's disk list
                // (empty while disk sampling is off — the picker then keeps only the
                // stored pick's （未连接） placeholder).
                DiskDisplayModeIndexOf(_config), _config.DiskDisplayIndex ?? 0,
                _taskbar.LatestSnapshot?.Disks, OnDiskDisplayChanged,
                // Same for the 特定 GPU picker (empty while GPU sampling is off).
                GpuDisplayModeIndexOf(_config), _config.GpuDisplayIndex ?? 0,
                _taskbar.LatestSnapshot?.Gpus, OnGpuDisplayChanged,
                _config.NetAdapterId, _config.NetAdapterName,
                EnumerateNetAdapters(), OnNetAdapterChanged,
                _config.PublicIpEnabled != false, OnPublicIpLookupChanged,
                _config.ClashEnabled != false, _config.ClashApiAddress, _config.ClashApiSecret, OnClashApiChanged,
                _config.UpdateCheckEnabled != false, OnUpdateCheckChanged,
                UpdateSourceIndexOf(_config), OnUpdateSourceChanged);
            _settings.Closed += (s, e) =>
            {
                _settings = null;
                ScheduleIdleTrim();
            };
            _settings.Show();
            _settings.Activate();
        }

        // 设置 外观 section's placement cards changed: persist, re-anchor the overlay, and
        // drop the transient flyout (its anchor just moved; pinned windows are user-placed
        // and stay where they are).
        private void OnOverlayPlacementChanged(bool onLeft, bool snapToStart)
        {
            _config.OverlayOnLeft = onLeft;
            _config.OverlaySnapToStart = snapToStart;
            TrySaveConfig();
            _taskbar.SetPlacement(onLeft, snapToStart);
            CloseDetail();
        }

        // 设置 外观 → 悬浮模式 (or the right-click menu's checkable item): persist (null =
        // off — only the enabled state is written) and hand the flip to the taskbar thread,
        // which rebuilds the overlay window in its other form (top-level card widget instead
        // of a taskbar child, or back). That also moves the flyout's anchor, so drop the
        // transient popup; pinned windows are user-placed and stay. The saved home
        // (FloatingX/Y) rides along, so turning the mode back on restores where the user
        // last dragged the widget. An open settings window is synced back — a no-op when
        // the change came from there.
        private void OnFloatingModeChanged(bool on)
        {
            _config.FloatingMode = on ? true : (bool?)null;
            TrySaveConfig();
            CloseDetail();
            _taskbar.SetFloatingMode(on, _config.FloatingX ?? -1, _config.FloatingY ?? -1);
            _settings?.SyncFloatingMode(on);
        }

        // 设置 外观 → 悬浮模式 → 置顶显示 (or the right-click menu's checkable item): persist
        // (null = on, the default — only the disabled state is written). No rebuild: the
        // widget just changes z-order band, and the taskbar thread verifies + re-asserts the
        // promotion (the foreground lock can eat it).
        private void OnFloatingTopmostChanged(bool on)
        {
            _config.FloatingTopmost = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbar.SetFloatingTopmost(on);
            _settings?.SyncFloatingTopmost(on);
        }

        // 设置 外观 → 悬浮模式 → 贴边隐藏 (or the right-click menu's checkable item): persist
        // (null = off, the default — only the enabled state is written) and hand it to the
        // taskbar thread, which re-derives the dock from the home position — ON slides a
        // home-at-edge widget out at once, OFF slides a hidden/peeked one back home. The
        // dock itself is derived state (a home within the snap distance of a 左/右/上 work
        // edge), so nothing but the switch is stored.
        private void OnFloatingEdgeHideChanged(bool on)
        {
            _config.FloatingEdgeHide = on ? true : (bool?)null;
            TrySaveConfig();
            _taskbar.SetFloatingEdgeHide(on);
            _settings?.SyncFloatingEdgeHide(on);
        }

        // 设置 外观 → 悬浮模式 → 全屏时隐藏 (or the right-click menu's checkable item):
        // persist (null = on, the default — only the disabled state is written) and hand it
        // to the taskbar thread, whose probe runs at once — ON with a fullscreen foreground
        // hides the widget immediately, OFF restores it no matter what is on screen. The
        // fullscreen state itself is derived live by the tick; only the switch is stored.
        private void OnFloatingFullscreenHideChanged(bool on)
        {
            _config.FloatingFullscreenHide = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbar.SetFloatingFullscreenHide(on);
            _settings?.SyncFloatingFullscreenHide(on);
        }

        // 设置 外观 → 悬浮模式 → 透明度: applied LIVE (a plain re-tint on the taskbar thread —
        // the widget must follow the thumb, so no debounce on the visual), but persisted
        // debounced: the slider reports per step and every step would rewrite settings.yaml
        // (the Clash text boxes debounce for the same reason — only the write is delayed
        // here, not the effect). null = 不透明 (only a non-default value is written).
        // Settings-page only: a percent slider has no right-click-menu shape.
        private void OnFloatingOpacityChanged(double opacity)
        {
            _taskbar.SetFloatingOpacity(opacity);
            _config.FloatingOpacity = opacity < 0.999 ? opacity : (double?)null;
            if (_configSaveDebounce == null)
            {
                _configSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _configSaveDebounce.Tick += (s, e) =>
                {
                    _configSaveDebounce.Stop();
                    TrySaveConfig();
                };
            }
            _configSaveDebounce.Stop();
            _configSaveDebounce.Start();
        }

        // The 透明度 slider's yaml-write debounce (see OnFloatingOpacityChanged). A fired
        // timer outlives the settings window on purpose: the last dragged value still lands.
        private DispatcherTimer _configSaveDebounce;

        // ---------- 设置: 开机自启动 / 主题 / 采样间隔 ----------

        // The scheduled task itself is the auto-start state (nothing in settings.yaml):
        // create/delete it on toggle; on a schtasks failure snap the toggle back to the
        // real state.
        private void OnAutoStartChanged(bool on)
        {
            if (StartupTask.SetEnabled(on)) return;
            _settings?.SyncAutoStart(StartupTask.IsEnabled());
        }

        // 主题 combo: 0=跟随系统 (yaml null — the ThemeManager tracks the system theme
        // live) 1=浅色 2=深色. Persisted + pushed to the ThemeManager; the
        // ActualApplicationThemeChanged hook in OnStartup repaints open detail windows.
        private void OnThemeChanged(int index)
        {
            _config.Theme = index == 1 ? AppThemeSetting.Light
                          : index == 2 ? AppThemeSetting.Dark
                          : (AppThemeSetting?)null;
            TrySaveConfig();
            ApplyThemeSetting();
        }

        private void ApplyThemeSetting()
        {
            ThemeManager.Current.ApplicationTheme =
                _config.Theme == AppThemeSetting.Light ? ApplicationTheme.Light
                : _config.Theme == AppThemeSetting.Dark ? ApplicationTheme.Dark
                : (ApplicationTheme?)null;
        }

        private static int ThemeIndexOf(AppThemeSetting? theme)
            => theme == AppThemeSetting.Light ? 1 : theme == AppThemeSetting.Dark ? 2 : 0;

        // 采样间隔 combo: persist (1000ms stays implicit in the yaml) and re-arm the
        // taskbar overlay's timer.
        private void OnSampleIntervalChanged(int ms)
        {
            _config.SampleIntervalMs = ms == 1000 ? (int?)null : ms;
            TrySaveConfig();
            _taskbar.SetSampleInterval(ms);
        }

        // 合并相同程序 toggle: persist (null = on, the default — only the disabled state
        // is written) and push to the sampler. Open detail windows pick it up on the next
        // snapshot tick — the per-process lists rebuild every Refresh, so no window needs
        // closing.
        private void OnMergeSamePathChanged(bool on)
        {
            _config.MergeSamePathProcesses = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbar.SetMergeSamePathProcesses(on);
        }

        // ---------- 设置 → 采样 → 磁盘: 显示方式 ----------

        // yaml DiskDisplay ↔ the settings combo index (0=所有磁盘平均 = yaml null, the
        // default — only non-default modes are written; 1=最高利用率 2=特定磁盘).
        private static int DiskDisplayModeIndexOf(AppSettings c)
            => c.DiskDisplay == MetricDisplayMode.Max ? 1 : c.DiskDisplay == MetricDisplayMode.Specific ? 2 : 0;

        // 显示方式 / 特定磁盘 combo changed: persist (null = 平均, the default; the picked
        // PhysicalDrive index is kept even when the mode leaves 特定磁盘, so switching back
        // restores it) and push to the taskbar thread. Open disk windows pick it up on the
        // next snapshot tick — the headline and chart rebuild every Refresh.
        private void OnDiskDisplayChanged(int modeIndex, int diskIndex)
        {
            _config.DiskDisplay = modeIndex == 1 ? MetricDisplayMode.Max
                                : modeIndex == 2 ? MetricDisplayMode.Specific
                                : (MetricDisplayMode?)null;
            _config.DiskDisplayIndex = diskIndex;
            TrySaveConfig();
            _taskbar.SetDiskDisplay(modeIndex, diskIndex);
        }

        // ---------- 设置 → 采样 → GPU: 显示方式 ----------

        // yaml GpuDisplay ↔ the settings combo index (1=最高利用率 = yaml null, the GPU
        // default — Task Manager's sidebar rule; 0=所有 GPU 平均 2=特定 GPU).
        private static int GpuDisplayModeIndexOf(AppSettings c)
            => c.GpuDisplay == MetricDisplayMode.Average ? 0 : c.GpuDisplay == MetricDisplayMode.Specific ? 2 : 1;

        // GPU 显示方式 / 特定 GPU combo changed — same contract as the disk one above
        // (the picked "GPU N" number is kept when the mode leaves 特定 GPU).
        private void OnGpuDisplayChanged(int modeIndex, int gpuIndex)
        {
            _config.GpuDisplay = modeIndex == 0 ? MetricDisplayMode.Average
                               : modeIndex == 2 ? MetricDisplayMode.Specific
                               : (MetricDisplayMode?)null;
            _config.GpuDisplayIndex = gpuIndex;
            TrySaveConfig();
            _taskbar.SetGpuDisplay(modeIndex, gpuIndex);
        }

        // ---------- 设置 → 采样 → 网络: 适配器 ----------

        // The 适配器 combo's items: every non-loopback adapter (virtual ones included —
        // an explicit pick is exactly how the user watches a VPN/vEthernet adapter),
        // sorted by display name. Enumerated HERE on settings open: GetAllNetworkInterfaces()
        // is expensive (~275ms on machines with many virtual adapters), so it must never
        // run on the sampler's per-tick path — a one-time cost per settings open is fine.
        private static List<(string Id, string Label)> EnumerateNetAdapters()
        {
            var adapters = new List<(string Id, string Label)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    adapters.Add((nic.Id, $"{nic.Description} ({nic.Name})"));
                }
                adapters.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
            }
            catch (Exception ex) { Logger.Warn("网络适配器枚举失败", ex); }
            return adapters;
        }

        // 适配器 combo changed: persist (null/"" = 自动 — only a pinned pick is written,
        // with its display name for the picker's （未连接） placeholder) and push to the
        // taskbar thread. A pinned adapter that's gone falls back to 自动 inside
        // NetSampler; open network windows follow on the next snapshot tick.
        private void OnNetAdapterChanged(string id, string name)
        {
            _config.NetAdapterId = string.IsNullOrEmpty(id) ? null : id;
            _config.NetAdapterName = string.IsNullOrEmpty(id) ? null : name;
            TrySaveConfig();
            _taskbar.SetNetAdapter(_config.NetAdapterId);
        }

        // Clash/Mihomo integration changed (the switch reports immediately, text edits are
        // debounced by the settings page): persist (enabled null = on — only the disabled
        // state is written; null/"" address = the 127.0.0.1:9090 default, kept across the
        // switch so turning back on restores it) and push to the taskbar thread;
        // ClashSampler's poll thread retargets/idles on the change, and the Network
        // list's "Clash"-tagged rows appear/decay on their own.
        private void OnClashApiChanged(bool enabled, string address, string secret)
        {
            _config.ClashEnabled = enabled ? (bool?)null : false;
            _config.ClashApiAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
            _config.ClashApiSecret = _config.ClashApiAddress == null || string.IsNullOrEmpty(secret)
                ? null : secret;
            TrySaveConfig();
            _taskbar.SetClashApi(enabled, _config.ClashApiAddress, _config.ClashApiSecret);
        }

        // ---------- 设置 → 采样 → 网络: 公网 IP ----------

        // 公网 IP toggle: persist (null = on, the default — only the disabled state is
        // written) and push to the taskbar thread; NetInfoSampler's poll thread drops its
        // cached address and stops BOTH the HTTP lookups and the 公网延迟 ICMP probe, so
        // the Network panel's 公网 IPv4 / 公网延迟 cells go "—" (the v6 row collapses) on
        // the next snapshot tick — and resume immediately when turned back on.
        private void OnPublicIpLookupChanged(bool on)
        {
            _config.PublicIpEnabled = on ? (bool?)null : false;
            TrySaveConfig();
            _taskbar.SetPublicIpLookup(on);
        }

        // ---------- 设置 → 通用: 检查更新 / 更新源 ----------

        // yaml UpdateSource ↔ the settings combo index (0=CNB = yaml null, the default —
        // only "github" is ever written; 1=GitHub).
        private static int UpdateSourceIndexOf(AppSettings c)
            => string.Equals(c.UpdateSource, "github", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        // 检查更新 toggle: persist (null = on, the default — only the disabled state is
        // written). Takes effect on the next startup (the check runs once per launch).
        private void OnUpdateCheckChanged(bool on)
        {
            _config.UpdateCheckEnabled = on ? (bool?)null : false;
            TrySaveConfig();
        }

        // 更新源 combo: persist (null = CNB, the default — only "github" is written).
        private void OnUpdateSourceChanged(int index)
        {
            _config.UpdateSource = index == 1 ? "github" : null;
            TrySaveConfig();
        }

        // ---------- 设置 → 采样: per-metric sampling switches ----------

        // 设置 → 采样 toggles ↔ the SystemSampler mask (one bit per overlay hit slot;
        // a null yaml value means enabled, so only "off" is ever written).
        private static int SamplingMaskOf(AppSettings c)
        {
            int mask = 0;
            if (c.CpuSamplingEnabled != false) mask |= SystemSampler.MaskCpu;
            if (c.RamSamplingEnabled != false) mask |= SystemSampler.MaskRam;
            if (c.DiskSamplingEnabled != false) mask |= SystemSampler.MaskDisk;
            if (c.GpuSamplingEnabled != false) mask |= SystemSampler.MaskGpu;
            if (c.NetSamplingEnabled != false) mask |= SystemSampler.MaskNet;
            return mask;
        }

        // A metric expander's toggle flipped: persist (null = enabled), push the new mask
        // to the taskbar thread (the slot hides and the overlay reflows, its press is
        // suppressed) and close that column's open windows — they have no live data until
        // re-enabled. The Closed handlers do their usual cleanup (unpin mask, idle trim)
        // on the way out.
        private void OnMetricSamplingChanged(int slot, bool on)
        {
            switch (slot)
            {
                case 0: _config.CpuSamplingEnabled = on ? (bool?)null : false; break;
                case 1: _config.RamSamplingEnabled = on ? (bool?)null : false; break;
                case 2: _config.DiskSamplingEnabled = on ? (bool?)null : false; break;
                case 3: _config.GpuSamplingEnabled = on ? (bool?)null : false; break;
                case 4: _config.NetSamplingEnabled = on ? (bool?)null : false; break;
                default: return;
            }
            TrySaveConfig();
            _taskbar.SetMetricSamplingMask(SamplingMaskOf(_config));
            if (on) return;
            if (_detail?.Column == slot) CloseDetail();
            foreach (var w in _pinned.FindAll(w => w.Column == slot)) w.Close();
        }

        private void EnsureTaskbarMenu()
        {
            if (_taskbarMenu != null) return;

            // The context menu uses iNKORE.UI.WPF.Modern's Fluent 2 styles for the
            // standard WPF ContextMenu/MenuItem (merged via ui:XamlControlsResources in
            // App.xaml — the same look as iNKORE's MenuFlyout). They're applied by key
            // here because the FluentWpfCore resource dictionary is merged *after*
            // XamlControlsResources and would otherwise win for the keyless defaults.
            // 悬浮模式/置顶显示/贴边隐藏/全屏时隐藏 mirror the 设置 → 外观 switches
            // (checkable items funnelling into the same change handlers; a checkable
            // MenuItem flips IsChecked before Click fires, so the handler reads the new
            // state). Their checkmarks are refreshed on every open below — the settings
            // window can change them too.
            _menuFloating = new MenuItem
            {
                Header = "悬浮模式",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.WindowMultiple_16_Regular, FontSize = 16 },
            };
            _menuFloating.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloating.Click += (s, e) => OnFloatingModeChanged(_menuFloating.IsChecked);

            _menuFloatingTopmost = new MenuItem
            {
                Header = "置顶显示",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.Pin_16_Regular, FontSize = 16 },
            };
            _menuFloatingTopmost.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloatingTopmost.Click += (s, e) => OnFloatingTopmostChanged(_menuFloatingTopmost.IsChecked);

            _menuFloatingEdgeHide = new MenuItem
            {
                Header = "贴边隐藏",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.PanelLeftContract_16_Regular, FontSize = 16 },
            };
            _menuFloatingEdgeHide.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloatingEdgeHide.Click += (s, e) => OnFloatingEdgeHideChanged(_menuFloatingEdgeHide.IsChecked);

            _menuFloatingFullscreenHide = new MenuItem
            {
                Header = "全屏时隐藏",
                IsCheckable = true,
                Icon = new FontIcon { Icon = FluentSystemIcons.FullScreenMinimize_16_Regular, FontSize = 16 },
            };
            _menuFloatingFullscreenHide.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            _menuFloatingFullscreenHide.Click += (s, e) => OnFloatingFullscreenHideChanged(_menuFloatingFullscreenHide.IsChecked);

            var separator = new Separator();
            separator.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemSeparatorStyle");

            var settings = new MenuItem
            {
                Header = "设置",
                Icon = new FontIcon { Icon = FluentSystemIcons.Settings_16_Regular, FontSize = 16 },
            };
            settings.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            settings.Click += (s, e) => ShowSettings();

            var exit = new MenuItem
            {
                Header = "退出",
                Icon = new FontIcon { Icon = FluentSystemIcons.DoorArrowLeft_16_Regular, FontSize = 16 },
            };
            exit.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
            exit.Click += (s, e) => Shutdown();

            _taskbarMenu = new ContextMenu();
            _taskbarMenu.SetResourceReference(FrameworkElement.StyleProperty, "DefaultContextMenuStyle");
            _taskbarMenu.Items.Add(_menuFloating);
            _taskbarMenu.Items.Add(_menuFloatingTopmost);
            _taskbarMenu.Items.Add(_menuFloatingEdgeHide);
            _taskbarMenu.Items.Add(_menuFloatingFullscreenHide);
            _taskbarMenu.Items.Add(separator);
            _taskbarMenu.Items.Add(settings);
            _taskbarMenu.Items.Add(exit);

            // Reflect the live state on every open: 置顶显示/贴边隐藏 only mean anything in the
            // floating form, so they grey out with the mode — the same rule as the settings
            // page's FloatingTopmostCard/FloatingEdgeHideCard. (Programmatic IsChecked sets
            // don't fire Click.)
            _taskbarMenu.Opened += (s, e) =>
            {
                bool isFloating = _config.FloatingMode == true;
                _menuFloating.IsChecked = isFloating;
                _menuFloatingTopmost.IsChecked = _config.FloatingTopmost != false;
                _menuFloatingTopmost.IsEnabled = isFloating;
                _menuFloatingEdgeHide.IsChecked = _config.FloatingEdgeHide == true;
                _menuFloatingEdgeHide.IsEnabled = isFloating;
                _menuFloatingFullscreenHide.IsChecked = _config.FloatingFullscreenHide != false;
                _menuFloatingFullscreenHide.IsEnabled = isFloating;
            };

            // There is no main window, so the menu needs an invisible host window to attach to.
            _menuHost = new Window
            {
                Width = 1, Height = 1,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000,
            };
            _menuHost.SourceInitialized += (s, e) =>
            {
                // WS_EX_TOOLWINDOW keeps the invisible host out of Alt+Tab
                // (ShowInTaskbar=false alone only hides the taskbar button).
                var hwnd = new WindowInteropHelper(_menuHost).Handle;
                var ex = (uint)WindowInterop.GetWindowLongPtr(hwnd, WindowInterop.GWL_EXSTYLE);
                WindowInterop.SetWindowLongPtr(hwnd, WindowInterop.GWL_EXSTYLE,
                    new IntPtr(ex | WindowInterop.WS_EX_TOOLWINDOW));
            };
            _menuHost.Show();
            _taskbarMenu.PlacementTarget = _menuHost;
            _taskbarMenu.Placement = PlacementMode.MousePoint;

            // The host must be activated for "click elsewhere → close" to work.
            // When it deactivates (user clicked another window), close the menu.
            _menuHost.Deactivated += (s, e) => _taskbarMenu.IsOpen = false;
        }
    }
}
