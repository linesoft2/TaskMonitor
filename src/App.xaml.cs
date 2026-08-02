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
        // the transient flyout. All open windows get the per-second snapshot push.
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

            base.OnStartup(e);

            Logger.Info($"启动 — 版本 {VersionInfo.Current}，OS {Environment.OSVersion}，Win11+={TaskbarWindow.IsWin11OrLater}");

            // One-time legacy-OS warning: the widget targets Windows 11; on Windows 10
            // or older (the best-effort, no-longer-maintained classical taskbar path)
            // tell the user once that compatibility issues are expected, then persist
            // the flag so later launches stay quiet. Shown AFTER the gate + mutex, so
            // only the surviving elevated instance ever displays it.
            if (!TaskbarWindow.IsWin11OrLater && _config.LegacyOsWarningShown != true)
            {
                new LegacyOsWarningDialog().ShowDialog();
                _config.LegacyOsWarningShown = true;
                TrySaveConfig();
            }

            // Repaint already-open detail windows on ANY effective theme flip — the 设置
            // 主题 combo, or a system-theme change while 跟随系统 (the iNKORE ThemeManager
            // tracks it and fires this; everything DynamicResource-driven — the settings
            // window, the context menu — restyles itself and needs nothing here).
            ThemeManager.Current.ActualApplicationThemeChanged += (s, e2) =>
            {
                _detail?.ApplyTheme();
                foreach (var w in _pinned) w.ApplyTheme();
            };

            // No main window — this is a taskbar widget. The overlay runs on its own
            // STA thread; DetailWindow is created on demand. ShutdownMode=OnExplicitShutdown
            // (set in App.xaml) keeps the process alive with no persistent WPF window.
            StartTaskbar();

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
                new DetailWindow(_taskbar).Prewarm();
                // Hand the one-touch startup pages (JIT of the show path, BAML parse,
                // prewarm's UI trees) back to the standby list — see ScheduleIdleTrim.
                SystemInfo.TrimMemory();
            }), System.Windows.Threading.DispatcherPriority.Background);

            // Startup update check (设置 → 通用 → 检查更新 / 更新源): one fetch on a pool
            // thread; pops the 发现新版本 dialog when the configured source has a newer
            // tag (不再提醒 for that exact tag persists to settings.yaml). Best-effort —
            // never blocks startup, never crashes the app.
            UpdateChecker.CheckOnce(_config, TrySaveConfig);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info("退出");
            _stopping = true;   // stop the overlay-recreate loop from re-entering Start()
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

            // Already elevated (dev shell, or the relaunched child): nothing to do.
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
            if (new ConsentDialog().ShowDialog() == true)
            {
                Logger.Info("提权门：用户首次同意, 持久化后 runas 自我重启提升权限");
                _config.ElevationConsent = true;
                TrySaveConfig();        // best effort — a read-only install dir degrades to "ask again"
                TryRelaunchElevated();
            }
            // Refused (不允许 / ✕ / Esc), or the UAC prompt was cancelled: exit rather
            // than run degraded — "始终保持在管理员权限的状态下运行".
            Logger.Info("提权门：未提权（拒绝或 UAC 取消）");
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
            // OverlayOnLeft null = 靠左 (the default); only an explicit false is right.
            _taskbar.SetPlacement(_config.OverlayOnLeft != false, _config.OverlaySnapToStart == true);
            _taskbar.SetSampleInterval(_config.SampleIntervalMs ?? 1000);
            _taskbar.SetMetricSamplingMask(SamplingMaskOf(_config));
            _taskbar.SetMergeSamePathProcesses(_config.MergeSamePathProcesses != false); // null = on (the default)
            _taskbar.SetDiskDisplay(DiskDisplayModeIndexOf(_config), _config.DiskDisplayIndex ?? 0);
            _taskbar.SetGpuDisplay(GpuDisplayModeIndexOf(_config), _config.GpuDisplayIndex ?? 0);
            _taskbar.SetNetAdapter(_config.NetAdapterId);   // null = 自动 (the default)
            _taskbar.SetClashApi(_config.ClashEnabled != false, _config.ClashApiAddress, _config.ClashApiSecret); // null = on; null address = the 127.0.0.1:9090 default
            _taskbar.SetPublicIpLookup(_config.PublicIpEnabled != false); // null = on (the default)

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
                            Logger.Warn("任务栏覆盖层 Start() 已返回（窗口销毁——explorer 重启或初始化失败），2s 后重建");
                    }
                    catch (Exception ex)
                    {
                        // The taskbar overlay is non-critical; never let it take the app down.
                        Logger.Error("任务栏覆盖层 Start() 抛异常，2s 后重建", ex);
                    }
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

            // A pinned window for this column already shows it — bring it to front
            // instead of opening a duplicate.
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
            _detail.Closed += (s, e) =>
            {
                var w = (DetailWindow)s;
                if (ReferenceEquals(_detail, w)) _detail = null;
                _pinned.Remove(w);
                // Free the column's press if this window had it pinned (idempotent).
                _taskbar.SetColumnClickEnabled(w.Column, true);
                ScheduleIdleTrim();
            };
            _detail.ShowColumn(column);
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
        }

        private void CloseDetail()
        {
            var d = _detail;
            _detail = null;
            d?.Close();
        }

        // ---------- Idle working-set trim ----------
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

        // ---------- Right-click context menu (设置 / Exit) ----------
        private ContextMenu _taskbarMenu;
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
            _taskbarMenu.Items.Add(settings);
            _taskbarMenu.Items.Add(exit);

            // There is no main window, so the menu needs an invisible host window to
            // attach to. 1×1, transparent, offscreen, topmost, non-activating → unseen.
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
