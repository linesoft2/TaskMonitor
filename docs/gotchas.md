# Gotchas — the long form

The root [`AGENTS.md`](../AGENTS.md) carries each rule below as a **one-line index**.
This file is the un-abridged version, loaded only when an agent actually touches that
area. The **canonical** statement of each rule still lives in a comment at the code
site named in the heading — when the two disagree, the code comment wins and this file
(and the index) must be fixed.

Cross-cutting companions: [`settings-plumbing.md`](settings-plumbing.md) (every
设置→sampler chain) and [`scroll-stack.md`](scroll-stack.md) (the three-file scrolling
implementation).

---

## 1. Classical (Win10) taskbar band invariants

*task_monitor.csproj … `src/UI/TaskbarWindow.cs`: `ClassicalReposition`, `RestoreMinWindow`,
`ReconfigureOrientation`, `TIMER_ID_POS`*

The shrunk `MSTaskSwWClass` task-buttons band is **ours to restore**. `ClassicalReposition(force)`
runs `RestoreMinWindow` FIRST, before any re-measure triggered by OUR size/DPI/settings
change — otherwise the shrink accumulates, because explorer only re-expands the band on
its own layout events. The restore must also run:

- in `WM_DESTROY` (explorer-restart deaths skip it via `IsWindow`),
- before an orientation flip (`ReconfigureOrientation` undoes the OLD axis, then transposes),
- on app exit — App's `OnExit` posts `WM_CLOSE` to the overlay and joins the taskbar
  thread (≤1s); it is a background thread, so a raw process death could otherwise leave
  the band narrowed.

`ReBarWindow32` / `MSTaskSwWClass` are undocumented explorer internals (stable 7→10) —
keep the `WorkerW` / `MSTaskListWClass` fallbacks and `Start()`'s 10s-probe →
Win11-style-anchors degradation for shells without the chain.

The 100 ms `TIMER_ID_POS` re-dock exists because the sample tick (up to 2s) would leave
a re-expanded band overlapping the overlay for a whole interval.

## 2. Boot / explorer-restart resilience: `Start()` waits, then App recreates

*`src/UI/TaskbarWindow.cs`: `Start`, `RepositionOverlay`, `TaskbarBand`, `LogGeometry`*

`TaskbarWindow.Start()` **polls** for a real `Shell_TrayWnd` (exists + non-zero rect)
before creating anything — the scheduled-task logon trigger can fire before explorer
lays the taskbar out; an earlier silent `return` left the process running with no
overlay and no retry, and a zero-rect taskbar would have meant a permanently invisible
0-height overlay (`RepositionOverlay`'s height tracking skips a 0-height band).

The Win11 overlay sizes and positions against the taskbar band explorer actually
**RESERVES** (`TaskbarBand`: for a bottom taskbar, the `rcWork.bottom`→`rcMonitor.bottom`
strip ∩ the window rect; height from it, bottom-aligned via `bandY`). A taskbar-height
mod can leave `Shell_TrayWnd` permanently TALLER than the reservation — the 2026-08-01
report: window 90px @ y990–1080, `rcWork.bottom` 1020 → reserved band 60px. The surplus
top strip floats over the desktop where maximized windows cover it, so a full-height
top-aligned overlay loses its top rows there, and the `DetailWindow` anchored to the
overlay's rect floats just as high. On a stock taskbar the band == the whole window rect.

`RepositionOverlay` re-measures the band every tick and resizes/moves on change (the same
dance as `HandleDpiChange`, DPI unchanged); the throttle compares `LastXRelative` AND
`LastBandY`. `LogGeometry` dumps taskbar window/client rects, client origin, monitor/work
area, the reserved band and the overlay rect (WARN when the overlay falls outside the
monitor) at embed and whenever either rect changes — change-gated, because the tick hits
it every second.

ANY return from `Start()` (an explorer restart destroyed the `Shell_TrayWnd` parent and
our child with it, or init threw) → the `App.StartTaskbar` loop re-enters it after 2s
(`_stopping` gates shutdown). Recreate invariants:

- `RegisterClassW` tolerates `ERROR_CLASS_ALREADY_EXISTS`;
- the WndProc delegate is a process-lifetime `static readonly` **precisely because** the
  class keeps the FIRST thunk on re-entry — a per-`Start()` delegate would be orphaned on
  the next GC, and the next dispatched message would jump into freed memory and AV inside
  the reverse-pinvoke stub (a stack-less NRE; the 2026-07-30 crash);
- `OverlayHwnd` is zeroed between runs;
- the 1s tick self-destructs via `DestroyWindow` when `IsWindow(taskbarHwnd)` goes false
  (covers `SetParent` losing an explorer-restart race).

## 3. Nothing escapes `WndProc`

*`src/UI/TaskbarWindow.cs`: `WndProc` / `WndProcCore`*

On x64 a managed exception cannot unwind across the user32 callback boundary; escaping it
is `0xC000041D` (fatal), and the ~20s WER dump freeze then hangs explorer on our child
window. `WndProc` wraps `WndProcCore` in a per-message try/catch that reports the fault to
the global `CrashReporter` (fire-and-forget — the loop must never block on the crash
dialog, or explorer's `SendMessage` hangs the taskbar) and swallows.

The catch only covers the managed body downward: an entry-glue AV (dead thunk) precedes the
frame's existence. The static delegate (§2), not the catch, closes that hole.

## 4. Global crashes = `CrashReporter` → log file + code-only `CrashDialog`

*`src/UI/CrashReporter.cs`, hooked in `src/App.xaml.cs`*

Every unhandled managed exception is FIRST written to the file log (`Logger.Error` with the
full stack, duplicates included), then pops a dialog showing it. Hooks: AppDomain
`UnhandledException` in App's static ctor (covers even App.xaml BAML failures),
`DispatcherUnhandledException` at the very top of `OnStartup`, `UnobservedTaskException`
(SetObserved + reported), plus the WndProc guard (§3).

A **fatal** (`IsTerminating`) report BLOCKS the dying thread on the dialog (30s `Invoke`
timeout → `MessageBox` fallback) so the user sees why the process vanished; everything else
is `BeginInvoke`. At most one dialog; duplicates counted and dropped. The dialog is
XAML-free so a XAML/resource crash cannot kill the reporter.

## 5. File log = `Logger`

*`src/Logger.cs`*

`logs/task_monitor-yyyy-MM-dd.log` next to the exe — one rolling file per day, files older
than 7 days pruned at startup and at each day rollover. Levels DEBUG/INFO/WARN/ERROR, ALL
written: the volume is bounded by design — hot per-tick paths never log, only state
changes/decisions/degradations/failures do, and per-tick failure paths use
`WarnOnce(key, …)` (first occurrence only).

Thread-safe across the UI/taskbar/SRU-callback threads, AutoFlush per line (a fatal crash
never loses its own stack), NEVER throws (5 consecutive I/O failures → silently off for the
run; a read-only install dir → no log at all). Init runs in App's static ctor before `Main`,
so early startup crashes land in the file.

Coverage concentrates on the compatibility-prone spots: taskbar probe / family detection /
embed / `SetParent` / reposition / DPI / orientation flips (TaskbarWindow), the
undocumented-API degradations (SRUM register + callback, DXCore factory/enum/query,
`IOCTL_DISK_PERFORMANCE` open/query, `NtQuerySystemInformation` walk, SCM enum, PDH GPU
counters, disk hot-plug add/remove), the elevation gate's decisions, the mutex
second-instance exit, the overlay-recreate loop, and every crash report. **When touching any
of those sites, keep the log line truthful.**

## 6. The overlay must never steal focus

*`src/UI/TaskbarWindow.cs`*

Full no-activate set, **including `SWP_NOACTIVATE` on every `SetWindowPos`**.

**One deliberate exception** — 悬浮模式 with 置顶显示 OFF, which must be activatable for a click
to raise it (§36). Every other form keeps the full set.

## 7. `SetWindowPos(HWND_TOPMOST)` silently no-ops when not foreground

*`src/UI/DetailWindow.xaml.cs`: `ShowColumn`, `EnsureTopmost`*

`ShowColumn` activates + `SetForegroundWindow`s FIRST; `EnsureTopmost` verifies the exstyle
bit and never trusts the return value.

## 8. Acrylic unfocused / the menu host

*`src/App.xaml.cs`, `src/UI/DetailWindow.xaml.cs`, `src/Interop/WindowBackdropInterop.cs`*

FluentWpfCore's `UseWindowComposition=True` drives acrylic on every **WPF** window — do not
hand-roll DWM/Accent P/Invoke for one of those. The 悬浮模式 widget is the exception that
proves the rule: it is a NATIVE DirectComposition window, so FluentWpfCore (a WPF attached
property) cannot reach it — `WindowBackdropInterop` makes the identical accent call with the
identical tint constants instead (§36). The right-click menu's invisible host is `Activate()`d,
closes on `Deactivated`, and gets `WS_EX_TOOLWINDOW` at `SourceInitialized` (without it the host
shows in Alt+Tab).

## 9. `NetSampler` never enumerates NICs per tick

*`src/Sampling/NetSampler.cs`*

`NetworkInterface.GetAllNetworkInterfaces()` costs ~275ms — it was ~97% of idle CPU. The NIC
is cached and re-enumerated only in `SelectAdapter`/`TrySelect` (reselect events + the
30-tick pinned-adapter return probe) and by `App.EnumerateNetAdapters` on settings open
(one-time). Never per tick.

## 10. Live CPU speed = PDH `% Processor Performance` × base clock

*`src/Sampling/SystemSummarySampler.cs`*

Not `CallNtPowerInformation` — that caps at base and reports no turbo.

## 11. Per-process CPU/RAM/disk = ONE `NtQuerySystemInformation` walk

*`src/Sampling/ProcessCpuSampler.cs`*

The disk column reads the 24H2+ per-entry `PROCESS_DISK_COUNTERS` trailer — Task Manager's
actual disk-column source, NOT SRUM (layout + fallback in the file). Icons:
`IShellItemImageFactory`; `ExtractAssociatedIcon` is the blurry fallback only.

## 12. Disk and GPU metrics replicate Taskmgr exactly

*`src/Sampling/DiskSampler.cs`, `GpuSampler.cs`, `src/Interop/DxCoreInterop.cs`*

`WdcDiskMonitor` via `IOCTL_DISK_PERFORMANCE`, `WdcGpuMonitor` via DXCore COM — both reversed
from Taskmgr.exe; math, quirks and the net48 COM traps live in those headers.

**Disk handles are opened per query and closed immediately, never held** — a retained handle
vetoes USB safe-eject. GPU adapter-level metrics never touch PDH; a missing `dxcore.dll`
(pre-1903) degrades to the `--` overlay. `DiskInfo`/`GpuInfo` are long-lived
`INotifyPropertyChanged` objects mutated from the taskbar thread — the tabs bind once and
keep selection.

## 13. Per-process GPU% is PDH, not DXCore

*`src/Sampling/ProcessGpuSampler.cs`*

`\GPU Engine(*)\Utilization Percentage` via `PdhAddEnglishCounterW`; instance-name encoding,
MAX aggregation and `NormalizeEngineName` are in the file. No elevation needed, unlike SRUM.

## 14. Process-row tooltip = view-owned `Popup`, never a row `ToolTip`

*`src/UI/Common/ProcessListTip.cs`*

The per-tick rebind cancels real ToolTips; the popup is driven by the list's `MouseMove`.

## 15. Per-process net = the undocumented SRU real-time API

*`src/Sampling/ProcessNetSampler.cs`, `src/Interop/SrumInterop.cs`*

`srumapi.dll`; admin mandatory — the reason the app always runs elevated. Calibration
constants sit at the top of `SrumInterop.cs`: re-dump a record's bytes if the list reads
wrong. The callback fires on SRU's thread (lock accumulators); never `SruFreeRecordSet` the
callback's set.

**The SRU engine pushes frames on its own ~1s cadence, decoupled from the sampling tick** —
`ProcessNetSampler.Sample` re-diffs only when the callback's frame version advances and
HOLDS the last rates between frames (`dt` = the frames' true arrival gap). Diffing every tick
at 0.5s alternated all-zero lists with doubled rates.

The emitted list covers EVERY walked PID (显示所有进程): traffic-active and
recently-retained ones first, the rest at 0 B/s below — so with 合并相同程序 on, a merged net
row's count is the number of RUNNING same-path instances, not just the traffic-active ones
(Idle/System/Memory Compression excluded; SRUM-unavailable still degrades to empty).

**A registration OUTLIVES the sampler it came from — unregister it on every rebuild** (the
2026-09-22 卡死, and the single worst bug of that investigation). `SruRegisterRealTimeStats`
hands srumapi a raw pointer to a managed delegate's marshalling stub, so the registration is a
native reference the GC knows nothing about: the moment the sampler (and with it `_callback`)
becomes unreachable, the next GC frees the stub and srumapi keeps calling it — `0xC0000005`
with `读 0x8`, `Rax/Rcx = 0` (a null function pointer inside stub code), NO managed frame and
therefore NO app-log entry, ~1 s later (the next SRU frame), while WER froze the process for
20–30 s dumping it. It looked exactly like a 悬浮模式 flip crash, because a flip rebuilds the
overlay → `new SystemSampler()` → a NEW registration, and the old one was never released; the
same applies to an explorer restart or any `Start()` re-entry. `TrimMemory`'s forced
`GC.Collect() + WaitForPendingFinalizers()` on every popup close is what made the collection
happen *immediately* after the flip, which is how the user's repro (rapid 切换 detail 窗口)
reproduced it. Fix in place: `ProcessNetSampler.Shutdown()` (unregister + retain the retired
instance so an in-flight callback's stub stays valid) called from `SystemSampler.Shutdown()`
from `WM_DESTROY`, before the state turns to garbage. Found with the `CrashTrace` VEH (the
message ring said `WM_APP_SET_FLOAT_KEEPOUT`, the step marker said `绘制阶段`, the stack scan
showed `srumapi.dll`, and the record's 距上次内存回收 was 875 ms) — see §37.

## 16. Wi-Fi `wlanapi` calls are on-demand, NOT per-tick

*`src/Sampling/NetInfoSampler.cs`*

They are location-sensitive and light the taskbar location indicator. The poll thread does
only location-insensitive work; Wi-Fi details are queried on panel open via
`_wifiDetailsRequested`, cached by adapter Id. Do not move wlanapi back onto the per-tick
path.

## 17. Memory breakdown = three sources, not `GetPerformanceInfo`

*`src/Sampling/MemoryDetailSampler.cs`*

`GlobalMemoryStatusEx` + NTQSI class-2/class-80; this build's offsets are documented at each
read. "(compressed)" = the "Memory Compression" process's working set, not a counter;
已缓存 = standby + modified.

## 18. Charts are hand-drawn WPF

*`src/UI/Charts/*`*

Hover = `HitTest` + a real `Popup` re-rendered per `Refresh`.

## 19. The scrolling stack

*`src/UI/Common/TouchDragScrollViewer.cs`, `SnappyScrollPhysics.cs`,
`ScrollCacheDuringTouch.cs`*

Full design: [`scroll-stack.md`](scroll-stack.md). The one rule that bites: **the six
scrollers must set `Style="{DynamicResource {x:Type ScrollViewer}}"` explicitly** — WPF
keyless styles match `TargetType` exactly and do NOT reach derived controls, so without it
the control silently falls back to the Aero scrollbar.

## 20. Idle trim at event points only, never on a timer

*`src/App.xaml.cs`: `ScheduleIdleTrim`, `src/Interop/SystemInfo.cs`: `TrimMemory`*

`SystemInfo.TrimMemory` runs after the prewarm and when the last detail window closes,
deferred to Background priority (the comments at the site explain why inline would be
wrong).

**Two things it must NOT do again** (the 2026-09-22 卡死 investigation): burst, and wait for
finalizers. Rapid 切换 detail 窗口 closes one popup per switch, so the old code ran a forced
full GC per close; it now refuses a second trim within `TrimMinIntervalMs` (30 s) — the log
line states the limit and the actual gap. And it no longer calls
`GC.WaitForPendingFinalizers()`: waiting meant every unreachable DirectN/COM wrapper was
released ON THE SPOT while the taskbar thread was concurrently inside its own COM calls
(`Draw`), which is precisely the window a use-after-free shows up in. Without the wait those
wrappers are still released — by the finalizer thread, in its own time, exactly as during
normal operation. (The real crash of that night was the leaked SRUM registration behind the
same trigger — §15 — but this is the same "don't force finalization under a live drawer"
rule.)

## 21. 开机自启动 = a Task Scheduler logon task, never the Run key

*`src/StartupTask.cs`*

`RunLevel=HighestAvailable` from an already-elevated process is what avoids the per-boot UAC
prompt. Registered from generated XML, NOT `schtasks` switches: the defaults would kill the
app after 72h (`ExecutionTimeLimit` → PT0S) and skip auto-start on battery (both battery
flags → false). The task itself is the state — no `settings.yaml` key.

## 22. 深浅色 theme

*`src/App.xaml.cs`, `src/UI/DetailWindow.xaml.cs`, `src/UI/TaskbarWindow.cs`*

`ThemeManager.Current.ApplicationTheme` (null = 跟随系统, tracked live by iNKORE). Open
DetailWindows are repainted via the `ActualApplicationThemeChanged` hook →
`DetailWindow.ApplyTheme` → `IDetailView.ApplyTheme` (acrylic tint + tooltip/chart colors
are NOT DynamicResource-driven).

DetailWindow's root sets `Foreground` to `TextFillColorPrimaryBrush` so unset
TextBlocks/FontIcons inherit the theme (WPF's built-in default is hard Black); explicit
local values (Secondary labels etc.) still win.

**Never read the theme off `GetActualTheme(window)` on DetailWindow** — that attached
property is only pushed to IsThemeAware windows, so it reads Light forever; use
`ThemeManager.Current.ActualApplicationTheme`.

The native overlay instead tracks the SYSTEM theme itself (it sits on the taskbar, which
follows the system, not this setting): the 1s tick reads
`HKCU\...\Themes\Personalize\SystemUsesLightTheme` and `ApplyTaskbarTheme` re-tints the D2D
brushes in place (`SetColor`, no recreation) — black text on a light taskbar, white on dark,
same alphas.

## 23. Never `<StaticResource ResourceKey="…"/>`-alias a theme brush in `Window.Resources`

It snapshots the STARTUP scheme's brush instance and shadows the app-level key through every
later scheme swap — a live light→dark switch then paints new foregrounds over the frozen old
background. A fresh window parses fine, which is why "reopen fixes it". Rely on the scheme
defaults (iNKORE already maps e.g. `NavigationViewContentBackground` →
`LayerFillColorDefaultBrush` per scheme) or forward with `<DynamicResource …/>`.

## 24. 采样间隔 is not a compile-time constant

*`src/UI/TaskbarWindow.cs`: `SetSampleInterval` / `WM_APP_SET_INTERVAL`*

The taskbar timer is re-armed at runtime; rate samplers must normalize over REAL elapsed
time (NetSampler does; CPU/GPU/disk already divide by real deltas — never assume 1 tick =
1s).

## 25. Per-metric sampling switches use a mask SEPARATE from the pinned-window click mask

*`src/UI/TaskbarWindow.cs`, `src/Sampling/SystemSampler.cs`*

`TaskbarWindow._samplingEnabledMask` (bit per hit slot, `SystemSampler.Mask*`) vs
`_clickDisabledMask`: unpinning a window must never re-enable a sampling-disabled slot's
press.

A toggle pushes the mask to the sampler AND posts `WM_APP_SET_METRICS`, which re-lays-out and
resizes the overlay immediately (hidden slots free their space — `ResizeForLayout` does the
width-only swap-chain dance; a 0-metric overlay collapses to a 0-width stub since DXGI
cannot size to 0). App closes the column's open windows on disable. `Start()` re-applies the
mask to every fresh `SystemSampler` (the explorer-restart recreate path would otherwise
silently re-enable metrics).

A re-enabled metric **PRIMES its delta baselines for one tick** before publishing values — a
stale baseline would average the whole disabled span into one bogus tick (`SystemSampler.Sample`).

## 26. 合并相同程序 merges BEFORE the top-8 cut

*`src/Sampling/ProcessListMerger.cs`, `src/Sampling/SystemSampler.cs`*

`ProcessListMerger.MergeByPath` groups rows by exe path (path-less protected processes group
by name), sums every value field, re-ranks by the summed key, then cuts; merging after the
cut would under-count groups whose members rank below it. Off = the exact old code path (the
early `TopN` break stays).

Rows carry `ProcessInfo.Count`, shown as a "×N" tag chip in the row templates
(`TagText`/`TagVisibility` — there is no `ProcessInfo.DisplayName`). The merged row's GPU
引擎 follows its biggest member; **服务宿主 svchost.exe 不合并** — each instance hosts
different services, so they keep independent rows and their own rank, and are renamed
afterwards by `ServiceHostMap` (§26).

Plumbing: [`settings-plumbing.md`](settings-plumbing.md) §1.

## 27. svchost → 服务命名 (ServiceHostMap, applied AFTER the merge)

*`src/Sampling/ServiceHostMap.cs`, `src/Interop/ServiceControlManager.cs`*

One `EnumServicesStatusExW(SC_ENUM_PROCESS_INFO)` RPC per tick maps every running Win32
service to its hosting PID (tasklist /svc's and Task Manager's source). `SystemSampler.Sample`
then renames each final `svchost.exe` row to the single service's display name (tag 服务) or
the `-k` group name (tag 服务组) parsed off `QueryServiceConfigW`'s `lpBinaryPathName`.

**The rename must stay AFTER `ProcessListMerger`** — it exempts svchost by row name, so
renaming earlier would collapse all same-path instances into one merged row. Group-name
queries run only for multi-service rows (a handful per tick — Win10 1703+ splits most
services).

The hover 描述 is `QueryServiceConfig2W(SERVICE_CONFIG_DESCRIPTION)` + `SHLoadIndirectString`
for "@…" indirect strings, lazily resolved ONCE per service on the UI thread
(`ProcessListTip`), never per tick. The chip hugs the name's end via
`src/UI/Common/FollowTagPanel.cs` (chip measured first, name ellipsizes into what remains — a
fixed MaxWidth would trim untagged rows early).

## 28. 磁盘/GPU 显示方式

*`src/Sampling/DiskSampler.cs`, `GpuSampler.cs`*

The headline (overlay slot + detail header/chart) is 平均 / 最高利用率 / 特定设备 per
`AppSettings.DiskDisplay` (null = 平均) / `GpuDisplay` (null = 最高 — Task Manager's sidebar
rule), plus the specific pick (`DiskDisplayIndex` = the PhysicalDrive N; `GpuDisplayIndex` =
the "GPU N" tab number — both can shift when the device set changes, and both are kept when
the mode leaves Specific so switching back restores them).

A missing specific device falls back to the metric's default aggregate (disk → the remaining
disks' mean, GPU → the remaining adapters' max) — the pick survives, and its own values
resume when it returns. The samplers query EVERY device each tick regardless (the per-device
tabs need them all) — the mode only picks the headline, and a mode/index change clears the
history so the chart never mixes semantics.

The settings pickers' items come from `LatestSnapshot.Disks`/`Gpus` (empty while that
metric's sampling is off → a （未连接） placeholder item keeps the stored pick visible).
Plumbing: [`settings-plumbing.md`](settings-plumbing.md) §3.

## 29. 网络适配器

*`src/Sampling/NetSampler.cs`*

`AppSettings.NetAdapterId` (a `NetworkInterface.Id` GUID; null = 自动, the sampler's
max-cumulative-traffic Up/non-virtual pick) + `NetAdapterName` (display-only, for the
picker's （未连接） placeholder).

A pinned adapter is used while present AND Up — **the virtual-adapter filter is NOT applied to
an explicit pick** (it is how the user watches a VPN). Gone/down falls back to 自动, probed
every 30 ticks so it resumes when back, and silent-for-30s never re-selects a pinned adapter
(auto mode keeps the old silent reselect). Plumbing:
[`settings-plumbing.md`](settings-plumbing.md) §4.

## 30. 公网 IP 开关

*`src/Sampling/NetInfoSampler.cs`*

`AppSettings.PublicIpEnabled` (null = 开, 仅写关闭态) gates ALL of NetInfoSampler's
public-internet traffic: the what-is-my-ip HTTP lookups (v4 + v6) AND the 公网延迟 ICMP probe
(target www.baidu.com — a hostname, DNS-resolved inside `Ping.Send` on the poll thread; the
LAN-only 本地延迟 gateway ping is unaffected).

Off: the poll thread drops its cached address and resets the next-try timestamp, so the
panel's 公网 IPv4 / 公网延迟 cells go "—" / the v6 row collapses on the next tick, and
re-enabling fetches immediately instead of waiting out the refresh cadence. Plumbing:
[`settings-plumbing.md`](settings-plumbing.md) §5.

## 31. Clash/Mihomo 代理流量

*`src/Sampling/ClashSampler.cs`*

`ClashSampler` REST-polls the external-controller `GET /connections` at ~1s on its own
background thread (the NetInfoSampler pattern). The data is equivalent to sparkle's WS
`/connections` push — the same cumulative-counter snapshot — but REST avoids
reconnect/fragmentation and needs **zero new NuGet packages**: net48 ships `HttpWebRequest` +
`DataContractJsonSerializer` (the latter needs only the framework reference
`System.Runtime.Serialization`, so Costura is unaffected).

Rate = per-connection-id diff of cumulative bytes ÷ measured interval (sparkle's original
algorithm), aggregated by `metadata.processPath`; with `find-process-mode=off` every
`processPath` is empty → the whole thing publishes empty.

These rows are **appended as standalone rows** after `ProcessNetSampler.Sample` (per the
user's requirement: no de-dup, no summing — they coexist with same-path SRUM rows), and
`ProcessListMerger` exempts them solo (same as svchost). Row tag "Clash"
(`ProcessInfo.ViaClash`, highest `TagText` priority; the row's `Pid=0` makes it naturally
immune to `ServiceHostMap`'s PID lookup).

Requests set `req.Proxy = null` explicitly — the user very likely has a system proxy
configured, and the request must never be routed back through the proxy to reach the local
core. The secret goes out as `Authorization: Bearer`.

Polling failures: ≤5 keep the last publish, after that it zeroes; an address/secret change
resets the diff baseline. The settings card's **测试连接** button calls
`ClashSampler.TestConnection` (a one-shot `GET /version` on `Task.Run`, off the UI thread;
an empty address probes the default one, matching what the poller actually uses; 401 →
认证失败, timeout / refused connection get their own Chinese reasons, and the result line is
colored with `SystemFillColorSuccess`/`CriticalBrush`). Plumbing:
[`settings-plumbing.md`](settings-plumbing.md) §6.

## 32. DISPOSE the back-buffer wrappers before `ResizeBuffers`

*`src/UI/TaskbarWindow.cs`: `ResizeBackBuffer`, `RenderState`*

DXGI rejects it with `DXGI_ERROR_INVALID_CALL` while any reference to a back buffer is alive,
and a DirectN wrapper releases only on Dispose/finalization. GC timing is not a plan: under
Debug JIT a `Start()`-local wrapper stayed rooted forever (the message loop never returns)
and bricked the D2D target on the first resize — the 2026-07-30 crash.log flood.

All resizes go through `ResizeBackBuffer`; the wrappers live on `RenderState`, never in
locals. `SetTarget(null)` alone drops only the context's reference.

## 33. 更新检测

*`src/UpdateChecker.cs`, `src/VersionInfo.cs`*

`UpdateChecker.CheckOnce` is kicked off at the tail of `OnStartup`: thread-pool fetch → UI
thread `UpdateAvailableDialog` (iNKORE modern window, same family as `LegacyOsWarningDialog`).

**版本号唯一来源 = csproj `<Version>`** (`VersionInfo.Current` reads the SDK-generated
`AssemblyInformationalVersion`; `release.yml` enforces tag == csproj). Two sources:

- `github` = anonymous `api.github.com/.../releases/latest` (must send a User-Agent or 403);
- **`cnb` (default) = read the WEB layer's `/-/releases/latest` 307 redirect** (the
  `Location` header carries `/-/releases/tag/<tag>` directly — the GitHub convention, a
  single HEAD request, no HTML parsing; if the redirect disappears, fall back to scraping
  the releases list page's tag links for the max version). The CNB OpenAPI is **anonymously
  401** (every releases endpoint in the official swagger declares BearerAuth), so the API is
  not an option.

The system proxy applies (do NOT set `Proxy=null` the way ClashSampler does — the update check
faces the public internet, and the user's proxy helps rather than hurts).

`settings.yaml` keys: `updateCheckEnabled` (null=开), `updateSource` (null=cnb, only
"github" is written), `ignoredUpdateVersion`. **"不再提醒" skips only that specific
version** — a newer version still prompts. Three buttons: 立即更新 (opens the release page) /
不再提醒 / 稍后. All exceptions are log-only, never fatal; a failed check is silent.

## 34. net48's `Run.Text` is not a dependency property

*`src/UI/Details/RamDetailView.xaml.cs`*

A `{Binding}` on a `Run` throws `XamlParseException` at startup. Named Runs are set in code;
DataTemplates use two TextBlocks.

## 35. A lost D3D device is a RECOVERABLE condition, not a crash

*`src/UI/TaskbarWindow.cs`: `Draw`, `IsDeviceLost`/`DeviceGone`/`OnDeviceLost`,
`CreateDeviceResources`/`ReleaseDeviceResources`/`RecoverDevice`, `ResizeBackBuffer`*

The report (2026-08-08, 崩溃报告 #1..#25 — one per tick, `WndProc msg=0x113`): a GPU driver
update removed the device under the running overlay, every tick's `Draw` threw
`Win32Exception … 存在可以恢复的演示错误 (D2DERR_RECREATE_TARGET)` at `EndDraw`, and the
WndProc guard funnelled each one into `CrashReporter` — a crash dialog per second for a
condition D2D itself calls recoverable.

Two separate defects, both fixed here:

1. **The code was unreadable by the time it was an exception.** `ctx.EndDraw()` is DirectN's
   *throwing* extension: it raises a `Win32Exception` whose HResult is E_FAIL (`0x80004005`) —
   the actual `D2DERR_RECREATE_TARGET` is gone, so no handler can separate a driver update
   from a real bug. `Draw` and `ResizeBackBuffer` now call the raw interface
   (`ctx.Object.EndDraw(IntPtr.Zero, IntPtr.Zero)`, `s.SwapChain.Object.Present(0, 0)`,
   `…ResizeBuffers(…)`), classify the HRESULT themselves, and only `ThrowOnError()` on
   anything that is *not* a device loss (a genuine `DXGI_ERROR_INVALID_CALL` still reaches the
   crash log — that path is what found the 2026-07-30 resize bug). Classification is the known
   D2D/DXGI codes plus a catch-all `ID3D11Device::GetDeviceRemovedReason()` probe
   (`DeviceGone`) — D2D answers `D2DERR_WRONG_STATE` once `BeginDraw` has already failed on a
   removed device, and a failure that arrives as an exception has no HRESULT left at all.

2. **Nothing could rebuild the pipeline.** Every device resource lived in `Start()` locals or
   on `RenderState` with no construction path other than `Start()` — and `Start()` only runs
   again after the *window* dies (explorer restart), so a lost device meant a dead overlay
   until the user restarted the app. The pipeline now lives entirely on `RenderState` and is
   built by `CreateDeviceResources(s, hwnd, w, h, dpi)` — the **only** construction site,
   called by `Start()` and by `RecoverDevice`. A loss latches `RenderState.DeviceLost`
   (`OnDeviceLost`, logged once), `Draw` early-returns, and the 1s tick calls `RecoverDevice`:
   release → recreate → re-tint brushes → redraw. Window, taskbar embed, sampler and geometry
   all survive, so recovery is one frozen frame instead of the explorer-restart dance. Failure
   is expected while the driver is still coming back (the machine may sit on the basic display
   driver mid-install): the latch stays set and the next tick retries, logging the full stack
   on attempt 1 and then every 30th attempt. `Start()`'s own creation is wrapped the same way —
   a failed init enters the message loop with the loss latched rather than throwing the window
   away.

Non-obvious pieces, each verified against a forced loss (a temporary test hook, since a driver
update can't be staged):

- **`IDCompositionTarget`/`IDCompositionVisual` must be released deterministically** —
  `Marshal.FinalReleaseComObject` in `ReleaseDeviceResources` (`ReleaseRcw`). DirectComposition
  keeps the HWND bound to its target until the target OBJECT dies, and since DirectN generates
  no `IDisposable` wrapper for either interface, nulling the field only makes the RCW
  *collectable*: the rebuild then fails with `DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED`. The
  first in-place recovery attempt hit exactly this.
- **`ResizeBackBuffer` early-returns while `DeviceLost`** but its callers still record the new
  size/DPI — `RecoverDevice` builds the swap chain from those fields, so a DPI or layout change
  during the outage is deferred, not lost. `HandleDpiChange`'s `SetDpi` and the four
  `s.DComp.Object.Commit()` sites are `?.`-guarded because `ReleaseDeviceResources` nulls every
  pipeline field (which is also what makes them safe mid-rebuild).
- **The DirectWrite formats are device-INDEPENDENT** (they come from the DWrite factory) and
  are deliberately NOT part of the pipeline: `CreateDeviceResources` never touches them.
- **`WM_DESTROY` tears down through the same `ReleaseDeviceResources`**, so a window destroyed
  mid-rebuild (explorer restart) or after a failed init releases whatever exists; the state is
  stashed on the HWND *before* the pipeline is built precisely so that path is reachable.

## 36. 悬浮模式 — the same window, detached

*`src/UI/TaskbarWindow.cs` (`SetFloatingMode`, `Start`'s `floating` branch, `ApplyFloating*`,
`FloatingReposition`, the `WM_LBUTTON*` drag), `src/Interop/WindowBackdropInterop.cs`,
`src/App.xaml.cs`, `src/UI/Settings/SettingsWindow.xaml(.cs)`*

设置 → 外观 → 悬浮模式 turns the overlay into a free-floating desktop widget: **no
`SetParent`**, the taskbar keeps no copy, and the same window class, layout, drawing,
hit-testing and sampler are reused — only the background is added. Drag it anywhere (a drag is
not a click: the press only opens a popup if it never left a 4-DIP threshold), and the new home
goes to `settings.yaml` (`floatingX`/`floatingY`, screen px, written from the drag-end callback).

**The widget's body is its own drawn CARD, and that is a measured decision.** It began as the
DetailWindow's material: `WindowBackdropInterop.SetAcrylic` is the *same* accent call
FluentWpfCore's `WindowMaterial(UseWindowComposition=True)` makes for the popup
(`SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND`) with the *same* tint
constants, verified against the popup with a checkerboard probe + BitBlt statistics (both land
on p50 224 / p90 226 while the raw pattern shows edge energy 22.2 → widget 4.8). The reported
*"悬浮窗口四周的阴影"* ended it: the blur is drawn by DWM BEHIND the window and can only be
SHAPED by DWM's corner-rounding opt-in (`DWMWA_WINDOW_CORNER_PREFERENCE = ROUND`) — and that
opt-in is what makes the system draw this frameless popup as a *framed* window, border and
**drop shadow** included. Three live probes settled it on 26200: `DWMWA_BORDER_COLOR =
DWMWA_COLOR_NONE` takes the 1px outline but not the shadow (checked by eye); `DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED`
plus `SWP_FRAMECHANGED` changes nothing at all; and the rounding preference itself, set to
`DONOTROUND`, removes the shadow AND the rounding together. Rounding it ourselves is no way out
either: `SetWindowRgn` with a rounded region (verified with `WindowFromPoint` — the corner
pixels stop belonging to the widget) DOES clip the overlay's own D2D output (the hover fill came
out rounded, and a 40px test radius made it obvious) but leaves the blurred backdrop square.
So the widget paints the material's TINT itself — the same shared constants at the same 0xCC
alpha, no blur — the frame stays off (`SetCardFrame`), and the corners come from the region
(`SetRoundedRegion`, radius = the drawn radius + slack, change-gated per tick). The 1px outline
that DWM painted as part of that frame and took away with it is now **drawn by the overlay
itself** (`CardBorderWidthDip` in the card pass, alphas in `WindowBackdropInterop`) — a self-drawn
edge cannot drag the shadow back in, which is the whole point of not asking DWM for a frame. The
modern `DWMWA_SYSTEMBACKDROP_TYPE` path was already rejected on measurement — flat solid for a
never-activated `WS_EX_NOREDIRECTIONBITMAP` window (numbers at the code site).

Six things this had to get right, each found by verification rather than reasoning:

1. **The form is creation-time state.** Parent, ex-style and backdrop are fixed when the HWND is
   created, so a flip does not morph the window: `WM_APP_SET_FLOATING` restores the classical
   band, destroys the window and lets App's recreate loop build the other form — with
   `ConsumeQuickRestart()` cutting the backoff from 2s to 150ms (the remaining ~2s of a flip is
   `Start()`'s pre-existing taskbar-family probe, unchanged).
2. **`WS_EX_TOPMOST` is decided at creation.** It rides in `WS_EX_COMPOSITE_EX`, and *demoting*
   on the first tick would show the widget above every window for up to a second — so
   `置顶显示` off clears the bit in the ex-style before `CreateWindowExW`. *Promoting* later is
   subject to the foreground lock (§7), hence `ApplyFloatingTopmost` verifies and the tick
   re-asserts (idempotent: one `GetWindowLongPtr`).
3. **The widget needs its own popup edge.** `GetTaskbarEdge` recognises the floating form by
   `GA_ROOT == overlay` (note: `GetParent` returns 0 for the reparented popup form — `GA_PARENT`
   is the one that follows the link) and then picks the roomier side, so the flyout opens below
   a widget in the upper half and above one near the bottom, using DetailWindow's two existing
   growth modes.
4. **置顶显示 OFF must be ACTIVATABLE** — the one exception to the no-focus-steal rule (§6).
   Windows keeps the FOREGROUND window at the top of the non-topmost band, so a click can only
   bring the widget back above the app the user is working in by ACTIVATING it; with
   `WS_EX_NOACTIVATE` (and `MA_NOACTIVATE`) it stayed buried for the rest of the session —
   the reported *"取消置顶后窗口始终在最下面，即使点击窗口也不会覆盖在他之上的窗口"*. So that
   form drops `WS_EX_NOACTIVATE` at creation and answers `MA_ACTIVATE`; an explicit
   `HWND_TOP` was tried first and is **silently clamped** while another window holds the
   foreground (measured with a cover window activated over half the widget: the covered half
   kept belonging to the cover), and it turned out to be unnecessary — activation alone raises
   it (re-measured with the raise disabled). The live 置顶显示 toggle moves both ex-style bits
   (`ApplyFloatingTopmost`). Consequence to know about: a fully covered non-topmost widget can
   not be clicked back at all (it has no taskbar button) — Win+D or an app restart brings it
   back, and the settings card says so.
5. **The widget draws edge-to-edge.** `DrawHorizontal` insets its content vertically
   (`pad`) because a taskbar form's window IS the whole band — the pad is what keeps the
   hover fill and the hairlines off the band's edges. The floating widget has no band: its
   window edge is the content edge, and since the fill already ran edge-to-edge
   *horizontally* (a group's left/right edges are the window's), the same pad read as the
   reported *"左右边缘没有间隙，但是上下有明显的间隙（hover 的变色覆盖不到）"*. The pad is
   therefore 0 in the floating form (the constant, and why the mid line is unaffected, are
   at the code site); the row heights grow by that much, so 悬浮模式's rows are slightly
   taller than the taskbar form's. Follow-up from the same report: **the fill is a plain
   rect there** — with the fill flush to the edge, the 6 DIP radius carved visible arcs out
   of it where it met the top/bottom edges. The widget's own corners still read as rounded,
   because the rounded WINDOW REGION clips the fill at the window (the same radius the card
   is drawn with), so the outer corners follow the widget instead of an arc. The taskbar
   forms keep the radius: their fill floats inside the band, where the rounding is what
   makes it read as a taskbar item.
6. **The widget and its detail windows move as a pair — except when the widget dodges.** The
   popup is placed against the widget (`GetTaskbarEdge` picks the roomier side) and is then CLAMPED into the monitor work
   area — so on a screen too short to hold it beside the widget it lands *on* the widget, and
   pinning grows it further (the band shifts the window up by its own height). The placement is
   not retried; instead the widget steps aside. App publishes the open detail windows' HWNDs
   (`SetFloatingKeepOut` → `WM_APP_SET_FLOAT_KEEPOUT`: open/close/pin/unpin and every
   `SizeChanged`/`LocationChanged`, coalesced onto one Background-priority push — a synchronous
   push on the close of a column TOGGLE would send the widget home for the few ms before the
   replacement popup opens, a visible blink), while the RECTS are read on the taskbar thread, so
   "same windows, one moved/resized" needs no separate notification and the tick re-derives
   anyway. `ComputeFloatTarget` then picks the position every tick and every push, in this
   order: **home**, if no open window covers it (the "关闭/取消固定后归位" half); otherwise
   **stay exactly where it is**, if no window covers its current spot; otherwise **step
   vertically clear** of the windows covering either spot (nearest side that fits the work area,
   8 DIP of daylight, X untouched). The middle rule is load-bearing, and it took a report to
   find: the popup is placed clear of the widget's **live** rect while the home stays put, so
   deriving *every* open from the home alone re-reads a popup that still clips a few px off the
   home (it was placed clear of the DISPLACED widget, not of the home) — the reported
   *"反复切换 detail 后悬浮窗不停向一侧移动"*, one step per column switch until it hit the screen
   edge. Equally load-bearing: a dodge is never written back as a home (that is `_floatingX/Y`,
   moved only by a drag), which is what makes going back need no bookkeeping, and `Start`
   records the spot it computes as the home (left at `-1`, the derivation would measure from the
   LIVE position and ratchet the same way). A HELD button suspends the derivation — the drag
   writes the live position straight from the cursor and only publishes the new home on release.

   The other half of the pairing: while the user drags the WIDGET, the detail windows travel
   with it. The taskbar thread reports every step of the drag (`FloatingDragged`, in the same
   physical px the window itself was moved by — clamp included) and App hands it to every open
   detail window (`DetailWindow.FollowFloatingDrag`), which moves by `GetWindowRect` + a raw
   `SetWindowPos` in that same currency and clamps itself into its own work area. Two things
   that must stay true: it is a DRAG-only report (a dodge is the one widget move that must not
   carry them — that move exists to separate the two), and a pinned window's saved pre-pin spot
   (`_prePin*`, restored on unpin) shifts by the same delta, or unpinning would land the flyout
   back where the widget used to be. Because the widget's own clamp is unchanged, a formation
   pushed against a screen edge deforms there (the follower sticks, the widget keeps going):
   the keep-out then settles any overlap after the drop.

**透明度 (设置 → 外观 → 悬浮模式 → 透明度)** is brush-alpha scaling in `ApplyTaskbarTheme`,
NOT `WS_EX_LAYERED`/`SetLayeredWindowAttributes`: the widget is a DComp-bound flip-model
swap chain, where DWM's layered-window alpha is not a supported combination, while scaling
the alphas of the brushes the pass already owns is deterministic, free, and rides every
existing re-tint path (live update message, the tick's theme flip, device recovery — all of
them call `ApplyTaskbarTheme`, which reads `_floatingOpacity` through `s.Floating`, so
nothing new had to be plumbed per path). Scope — BACKGROUND-only, settled over two wrong
first cuts: the card's alpha IS the value (cut one scaled the popup tint's 0xCC on top of
it, so even 100% read as translucent; the see-through tint look is still reachable at 80%
on the slider), and the card's outline scales with it, while the TEXT, labels,
hover/selection fills and separators keep their FIXED alphas (cut two faded the text with
the card and mid-range numbers washed out — the user's rule: 文字不要有透明度; fixed fills
also keep the hover feedback from vanishing exactly when the widget is subtle). The card
RGB stays the popup's tint (`CardRgb*`); `CardAlpha` remains the POPUP's constant,
untouched. The floor is 0.2 (`SetFloatingOpacity` clamps) — it keeps a hint of card
grounding the solid text. The taskbar-embedded forms keep `k = 1` — they must match the
opaque taskbar surface. The slider is settings-page only: a percent value has no
right-click-menu shape, and the menu's `Opened` re-read rule doesn't apply to it.

Verified end-to-end on 26200 by driving the real UI (right-click → 设置 → switches) with UI
Automation: blur/tint/text/corners, drag = move + persist + no popup, 置顶显示 on/off live,
模式 flip re-embedding into `Shell_TrayWnd` and returning to the remembered position, and
taskbar mode unchanged.

That run predates the rule in [`AGENTS.md`](../AGENTS.md) ("Build & run" — **UI testing, automated
driving included, is the USER's job**). The findings stay here as the record of why this code is
shaped the way it is; the method is not an instruction to repeat — UI-observable questions go to
the user as a short checklist.

## 37. A native crash is INVISIBLE to every managed handler — CrashTrace is the only record

*`src/Interop/CrashTraceInterop.cs` (VEH + message ring + step markers),
`src/UI/CrashReporter.cs` (installs it), `src/UI/TaskbarWindow.cs` (the ring/step call sites),
`src/App.xaml.cs` (`CheckOverlayHealth`), `src/Sampling/SystemSampler.cs` (`Timed`)*

The 2026-09-22 卡死 (a ~30 s freeze of a live process — 0 CPU, no messages, no log line, then a
silent recovery) produced **nothing** in the app log, because the crash was a native
`0xC0000005` raised in JIT/stub code: .NET 4+ does not deliver corrupted-state exceptions to
managed catches, so `AppDomain.UnhandledException`, the `Dispatcher` hook and the WndProc catch
all stayed silent — the only trace was an Application Error event and a WER dump (which is also
what froze the process: WerFault suspends it, dumps it, and a *reflection* of it appears as a
second `task_monitor.exe` process with 0 CPU whose parent is the app — that reflection is NOT a
second instance, do not chase it). What finally named the bug:

1. **`CrashTrace`'s vectored exception handler** (`logs/native-crash.log`, its own file, plain
   `File.AppendAllText` — never `Logger`: the fault may be inside the logger holding its lock).
   It runs before SEH on the faulting thread and records the exception code, the faulting address
   with module/offset, the access type + touched address, the registers (`Rcx` = first argument),
   a raw stack scan of plausible code addresses, and a best-effort managed stack. It returns
   `EXCEPTION_CONTINUE_SEARCH`: pure observation, Windows keeps handling exactly as before.
   Throttled to 4 records per run, and it filters by exception CODE — managed exceptions travel
   as `0xE0434352` and would otherwise drown it.
2. **The message ring** (`NoteMessage` at the top of WndProc) and **step markers**
   (`NoteStep`, called by every `Timed` sampler step, the tick's phases and the `Start()`
   steps — the NAMES only, no timings): a fault inside the dispatch path leaves no managed frame
   to read and a JIT frame cannot be unwound, so "what was this thread doing" has to be recorded
   as it goes. Both are one array/field write — no allocation, no lock, no logging.
3. **The UI-thread watchdog** (`App.CheckOverlayHealth`, every 2 s): a stalled tick and a
   vanished overlay HWND are the two states the taskbar thread cannot report itself. It logs the
   episode's start and end (`覆盖层心跳停滞/恢复`, `覆盖层窗口已消失…`), which is what bounded
   every stall to the WER window and caught the window-gone zombie.
4. **The sampler's slow-step log** (`采样慢步骤`, `SystemSampler.Timed`): fires only when a step
   exceeds 150 ms, so the hot path stays silent while a stall still leaves the offending step's
   NAME behind (a USB disk that slept inside `IOCTL_DISK_PERFORMANCE` is the known suspect).
   The `Start()` per-step MILLISECOND timings that lived here during the hunt are gone: only the
   phase names remain (they feed the ring), and the baseline those timings produced is kept as a
   comment at window creation (D3D/D2D/DComp ≈ 0.5 s, everything else single-digit ms).

Two more things that belong to this mechanism: the WndProc catch does **not** try to handle a
corrupted-state exception — .NET never delivers those to a managed catch, so there is nothing to
catch there and the VEH record is the only account of such a fault. And
`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\task_monitor.exe` may be
configured to keep mini dumps in `logs/dumps` (a machine-level debugging aid, not part of the
app's contract — safe to delete; there is no debugger on this machine, so the VEH record is what
is actually readable).

## 38. 贴边隐藏 — the dock is DERIVED from the home, and the slide owns the position

*`src/UI/TaskbarWindow.cs` (the `FloatEdge*`/slide helpers after `ClampFloatingToWorkArea`,
`FloatingReposition`, the `WM_LBUTTON*`/`WM_MOUSELEAVE`/`WM_APP_DESELECT`/
`WM_APP_SET_FLOAT_EDGE_HIDE` handlers, `Start`'s `floatDockEdge`), `src/App.xaml.cs`,
`src/UI/Settings/SettingsWindow.xaml(.cs)`*

设置 → 外观 → 悬浮模式 → 贴边隐藏 (also the right-click menu's checkable): a widget dropped
within **8 DIP** of its monitor work area's **左/右/上** edge docks there and slides out, leaving
**8 DIP** of card visible; hovering the strip slides it back out (peek), the mouse leaving slides
it away again. The bottom edge is deliberately not dockable — the taskbar lives there.

The shape to hold onto:

1. **The dock is derived state, never stored.** One rule — "home within `EdgeSnapDip` of an
   eligible work-area edge" (`FloatEdgeOf`, left/right before top) — evaluated at the three
   sites that matter: the drag end, the settings/menu toggle, and `Start()` (so a docked home
   reopens HIDDEN: the creation coords are the hidden position itself — born finished, no
   flash). settings.yaml keeps only the switch + the home; the drag-end callback persists the
   DROP position untouched, and the hidden rest is recomputed from the edge every tick
   (`FloatHiddenPos`), which is why a resize or DPI change self-heals the strip within a tick.
   A drop away from every edge releases the dock — visible is the default.
2. **The slide is a first-class owner of the position.** `FloatingReposition` early-returns
   while a slide is in flight AND while a button is held — the press half matters because a
   press on the hidden strip lives OUTSIDE the work area until the drag threshold passes, and
   the reposition clamp would otherwise yank the widget in from under the pointer mid-press.
   A slide is the widget's OWN move, like the dodge: it never reports `FloatingDragged` (the
   detail windows must not follow a hide/peek) and never writes the home. The animation is a
   10 ms `WM_TIMER` step (`StepFloatSlide`, ease-out: a third of the remaining distance, 2px
   floor — ~100–150 ms typical), killed on completion/press/destruction.
3. **The hidden rest suppresses the dodge** (`ComputeFloatTarget` is not even consulted): an
   off-screen widget has nothing to keep clear of, and a pinned window parked on the strip
   must not walk it along the edge. A PEEK derives the normal target (dodge included — a
   pinned window may have parked on the home while the widget was away), and `WM_MOUSELEAVE`
   re-hides it — suppressed while a column is selected (its flyout is why the widget is out);
   the flyout's close (`WM_APP_DESELECT` → `TryRehideFloat`) re-hides once the cursor is
   elsewhere.
4. **The peek is armed, not naive.** Docking is done with the cursor INSIDE the widget, and it
   can land exactly on the exposed strip — the first stray mouse move there would pop the
   widget straight back out. So the end of a hide slide arms the peek only when the cursor is
   NOT on the widget (`FloatPeekArmed`); a cursor that docked onto the strip stays disarmed
   until one `WM_MOUSELEAVE`. (Trace it: a cursor on the strip never leaves the rect while the
   widget slides out, so the leave that re-arms is always a genuine exit.)
5. **A press during a slide is not a click.** The press snaps the slide to its target first
   (`FinishFloatSlide` — the grab offset needs a stable position) and flags `FloatSwallowClick`:
   the widget was still moving under the cursor, so the slot the release lands on is not the
   one the user aimed at. Dragging out of the hidden strip works — the drag clamp pulls the
   widget into the work area following the grab point, and the drop re-evaluates the dock
   (pull it past `EdgeSnapDip` inside to release).
6. **Side taskbars (Win10-only, deprioritized)**: the edges are the WORK area's, so docking at
   an edge that the taskbar occupies slides the widget under it; with 置顶显示 on the strip
   then draws over the taskbar. Accepted — Win11 (the supported platform) has no side taskbars.

The card greys out outside the floating form (`FloatingEdgeHideCard`, the `FloatingTopmostCard`
rule), the menu item mirrors it with the same `Opened` re-read, and an open settings page is
pushed back via `SyncFloatingEdgeHide`. Verified live: dock left/top, peek/re-hide cycles,
drag-away release, re-dock from peek, and a clean log throughout.

## 39. 全屏时隐藏 — the probe owns VISIBILITY, never the position

*`src/UI/TaskbarWindow.cs` (`FullscreenAppOnScreen`, `UpdateFullscreenHide`, the tick's
floating block, `FloatingReposition`'s `dockedRest`/`hiddenRest` split, `Start`'s
`bornFullscreenHidden`), `src/Interop/ShellInterop.cs` (`SHQueryUserNotificationState`),
`src/Interop/WindowBackdropInterop.cs` (`DwmGetWindowAttribute`), `src/App.xaml.cs`,
`src/UI/Settings/SettingsWindow.xaml(.cs)`*

设置 → 外观 → 悬浮模式 → 全屏时隐藏 (also the right-click menu's checkable): a fullscreen app
in the FOREGROUND on the widget's own monitor (borderless-windowed game, F11 video,
slideshow) hides the widget until the foreground stops being fullscreen. A **maximized**
window never counts — that is the whole reason the probe reads
`DWMWA_EXTENDED_FRAME_BOUNDS` (`WindowBackdropInterop`): the VISIBLE bounds stop at the work
area, while `GetWindowRect` overshoots the monitor by the invisible resize borders and would
make every maximized window read as fullscreen. (The no-DWM fallback keeps the raw rect but
skips `IsZoomed` windows for exactly that reason.) The one case a rect probe cannot see is
EXCLUSIVE-D3D fullscreen — the swapchain bypasses DWM, so the window rect is unreliable —
and that is what `SHQueryUserNotificationState`'s `QUNS_RUNNING_D3D_FULL_SCREEN` covers,
gated to "the game's window is on the same monitor" so a game on another screen leaves this
one's widget alone. Excluded up front: our own process's windows (the pinned detail popups
are topmost; 设置 maximized must not count) and the desktop — clicking it makes
`Progman`/`WorkerW` the foreground window with a rect that IS the whole monitor. Any query
failure fails open (the widget stays visible).

The rules that keep it from tangling with everything else that owns the widget:

1. **Hiding is `SW_HIDE` and only `SW_HIDE`.** No slide (a slide to some edge would crawl
   across the screen during the game's opening frames), no dodge, no home write-back — the
   probe is a THIRD owner beside the dodge and the slide, and it owns visibility alone.
   `UpdateFullscreenHide` calls `ShowWindow` on the state TRANSITION only (`SW_HIDE` in,
   `SW_SHOWNOACTIVATE` out) and logs that transition once (per-tick paths never log, §5).
2. **A hidden widget is an INVISIBLE running one, not a parked one.** Timers fire on hidden
   windows, so the tick, the watchdog heartbeat (`LastTickTickCount`) and the sampling all
   continue — App's zombie check keys on `IsWindow`, never visibility — and the charts come
   back with continuous history. `FloatingReposition` keeps maintaining the position (home
   clamp, docked hidden rest) while hidden, which is what self-heals a resolution the game
   changed by the time it exits. Do NOT pause the timer or skip the heartbeat stamp while
   hidden: that would trip the "覆盖层心跳停滞" watchdog within ~4 s.
3. **The dodge is suppressed while hidden** — `FloatingReposition`'s `hiddenRest` is
   `dockedRest || FloatFullscreenHidden`, the same rule as the docked rest: an invisible
   widget has nothing to keep clear of, and recovery must land on the home. Note the split:
   only `dockedRest` feeds `FloatHiddenPos` — that helper's edge-0 fallthrough would treat a
   fullscreen-hidden un-docked widget as a TOP dock.
4. **A held button defers the hide** (`want && FloatPressed` returns): the drag keeps
   writing the position from the cursor, and the widget must not vanish out from under an
   in-progress press. The next tick re-probes. The SHOW direction never waits — restoring
   over a live press is harmless.
5. **`Start()` opens born hidden when a fullscreen app is already in the foreground** (the
   flip happened mid-game, explorer restarted mid-game): creation already leaves the window
   hidden, so `Start` simply skips its `ShowWindow` and pre-matches
   `FloatFullscreenHidden` — no one-frame flash over the game, no duplicate transition log.
   The same shape as a docked home reopening hidden (§38).
6. **Peek/click machinery is untouched by design.** A hidden window receives no mouse
   input, so no peek can fire while hidden; on recovery the window reappears wherever the
   tick had kept it (home or docked hidden rest — the strip then peeks as usual).

The toggle handler (`WM_APP_SET_FLOAT_FULLSCREEN_HIDE`) just runs the probe once — ON with
a fullscreen foreground hides immediately, OFF restores unconditionally — and the tick
carries it from there. The card greys out outside the floating form
(`FloatingFullscreenHideCard`), the menu item mirrors it with the same `Opened` re-read,
and an open settings page is pushed back via `SyncFloatingFullscreenHide`.
