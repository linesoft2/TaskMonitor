# Gotchas — the long form

The root [`AGENTS.md`](../AGENTS.md) carries each rule below as a **one-line index**.
This file is the un-abridged version, loaded only when an agent actually touches that
area. The **canonical** statement of each rule still lives in a comment at the code
site named in the heading — when the two disagree, the code comment wins and this file
(and the index) must be fixed.

Cross-cutting companions: [`architecture.md`](architecture.md) (the two threads, the
cross-thread contract, the detail/pin model, startup/elevation, layout),
[`settings-plumbing.md`](settings-plumbing.md) (every 设置→sampler chain) and
[`scroll-stack.md`](scroll-stack.md) (the three-file scrolling implementation).

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
hand-roll DWM/Accent P/Invoke for one of those (the one carve-out: DetailWindow **on Win10**
carries NO material at all — §40). The 悬浮模式 widget is the exception that
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
vetoes USB safe-eject. A missing `dxcore.dll` (pre-1903) degrades to the `--` overlay.
`DiskInfo`/`GpuInfo` are long-lived `INotifyPropertyChanged` objects mutated from the
taskbar thread — the tabs bind once and keep selection.

**Win10 GPU reality (measured on 22H2 + VMware SVGA 3D, the 2026-09-30 "未检测到 GPU"):**
dxcore.dll exists since 1903 but Win10's build implements only the base `IDXCoreAdapter`
(`IDXCoreAdapter1` QI → E_NOINTERFACE — SDK docs claim 2004+, wrong) and matches only the
runtime attributes (D3D11/D3D12 GRAPHICS, D3D12 CORE_COMPUTE) in `CreateAdapterList`, never
the Win11 hardware-type GPU attribute. So: always GetAdapter with the base IID, QI
`IDXCoreAdapter1` per adapter with `as`; enumerate four attribute passes deduped by LUID.
Older drivers (VMware) also report the running-time/memory-usage states unsupported —
utilization then falls back to the per-process PDH engine map (§13, summed per engine),
memory to the `AdapterMemoryBudget` state's `currentUsage`. All gates are capability-based
(`IsQueryStateSupported` / the QI result), never OS-version checks.

## 13. Per-process GPU% is PDH, not DXCore

*`src/Sampling/ProcessGpuSampler.cs`*

`\GPU Engine(*)\Utilization Percentage` via `PdhAddEnglishCounterW`; instance-name encoding,
MAX aggregation and `NormalizeEngineName` are in the file. No elevation needed, unlike SRUM.
The same collect also builds the per-(adapter LUID, phys, eng) map (`EngineMap`, on request
via `GpuSampler.NeedsPdhEngineData`) that backs §12's Win10 utilization/name fallback —
PDH eng ordinals and DXCore engine indices are both D3DKMT node ordinals, matching 1:1.

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

**The same teardown also IDLES the two poll threads, and that half is easy to lose.** A
retired `SystemSampler` owns `NetInfoSampler` + `ClashSampler`, each of which starts a
`while (true)` thread in its ctor and has no stop flag — so `SystemSampler.Shutdown()` clears
their inputs (`_netInfo.Adapter = null`, `_clash.SetEndpoint(null, null)`, the documented
"off" state of both) instead of trying to join them. Without those two lines every rebuild
left the retired pair polling at ~1 Hz for the rest of the session — gateway + 公网 ICMP/HTTP
(which must stop when the user switched 公网 IP off, §30) and the Clash endpoint (§31), one
extra pair per 悬浮模式 flip / explorer restart / §43 self-heal. No Join in the teardown path:
a poll cycle can block on ICMP/HTTP and teardown runs on the taskbar STA thread (§43).

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
thread `UpdateAvailableDialog` (iNKORE modern window, same family as `ConsentDialog`).

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

**A first attempt that cannot REACH the host is retried once, 5 minutes later**
(`RetryDelay` / `ScheduleRetry`: a one-shot `System.Threading.Timer`, never a sleeping pool
thread). The logon launch happens before Wi-Fi/VPN/DHCP is up often enough that a transient
miss would otherwise cost the whole session's prompt. The gate is `IsNetworkFailure` — the
DNS/connect/timeout/TLS-handshake `WebExceptionStatus` family plus a nested
`SocketException`/`IOException` — deliberately NOT an HTTP answer we cannot use (403 rate
limit, 404, changed page structure): five minutes changes nothing about those, so
`ProtocolError`/`TrustFailure` are not retried. Single-shot by construction: the retry passes
`canRetry:false`, so a second failure is log-only until the next launch. The retry re-reads the
LIVE config, so 检查更新 OFF during the window cancels it (off must mean zero traffic) and a
源 switch is honored.

`settings.yaml` keys: `updateCheckEnabled` (null=开), `updateSource` (null=cnb, only
"github" is written), `ignoredUpdateVersion`. **"不再提醒" skips only that specific
version** — a newer version still prompts. Three buttons: 立即更新 (opens the release page) /
不再提醒 / 稍后. All exceptions are log-only, never fatal; a failed check is silent
(`更新检测：… 连接失败，5 分钟后重试一次` / `… 读取失败（下次启动重试）` are the only traces).

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

## 40. Win10 的 DetailWindow：无亚克力，`AllowsTransparency` 分层窗口 + 自绘圆角卡片

*`src/UI/DetailWindow.xaml.cs`（ctor 的 Win10 分支、`ApplyTheme`、`ShowColumn` 末尾的重呈）*

症状（2026-09-29，Win10 实机）：detail 弹窗打开后顶部静态内容（"CPU" 标题、图标、pin 按钮）
经常缺失，有时整窗空白，要等下一次采样刷新后动态内容才出现；且窗口始终是直角。

根因有两层，都在 Win10 特有的 accent 路径上：

1. **首帧被 DWM 丢掉。** FluentWpfCore 的 `WindowMaterial` 在 `SourceInitialized` 就一次性调
   `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` + 透明重定向表面 —— 而此刻窗口
   还停在 -10000,-10000 的离屏出生地（防未样式化首帧闪烁的设计）。这个未文档化 API 在 Win10 上
   对"离屏创建、随后裸 `SetWindowPos` 挪上屏"的窗口有已知竞态：首次合成只出模糊底、WPF 内容不上屏。
   之后 WPF 只按脏区域重绘 —— 每 tick 刷新的动态元素（大字百分比、图表、进程行、统计值所在的
   TextBlock）逐个恢复，从不 invalidate 的静态元素（标题、图标、pin 按钮）就一直缺失。这精确解释了
   "只有表头消失、刷新后数字回来"的截图形态。
2. **圆角 API 不存在。** `WindowCorner="Round"` 只是
   `DWMWA_WINDOW_CORNER_PREFERENCE=ROUND`，Win11 专有，Win10 上 `DwmSetWindowAttribute` 静默失败。
   而 region 不能给 blur 塑形（§36 实测）——"保留亚克力 + 圆角"在 Win10 上根本无解。

修复（仅 `TaskbarWindow.IsWin11OrLater` 为 false 时；Win11 路径逐字节不变）：

- ctor 把 `windowMaterial.MaterialMode` 置为 `None` —— FluentWpfCore 的 `Apply()` 变成 no-op，
  accent 整条路径（含其首帧竞态）不复存在；同时开 `AllowsTransparency`（分层窗口）——这是
  Win10 上唯一真正的逐像素 alpha 通道。**别走"手动复刻 FluentWpfCore 的透明表面"
  （`CompositionTarget.BackgroundColor=Transparent` + `DwmExtendFrameIntoClientArea`）那条路**：
  第一版就是它，alpha-0 像素在没有 accent 托底时渲染成黑色——整窗像蒙了层黑纱
  （0xCC 的 tint 叠在黑底上），圆角外一圈黑边（同日实机反馈）。分层窗口下圆角是带 AA 的
  真透明，`RootBorder` 的 `CornerRadius=8` + 1px 描边直接生效。
- 卡片由 WPF 自绘：Win10 下 `RootBorder` 拿共享 tint 常量（`LightAcrylicTint`/`DarkAcrylicTint`，
  alpha 提到 0xFF —— **不透明**：没有模糊托底时半透明只会读成灰纱，2026-09-30 用户拍板），
  描边按 `CardBorderAlpha*` 规则（浅色卡黑线 20%、深色卡白线 12%）——与悬浮小组件同一套常量、
  同一个"无 blur 也要圆角"的取舍（§36）。`ApplyTheme` 按 OS 分流，主题切换两条路径都覆盖。
- `ShowColumn` 定位完成后在 Render 优先级补一刀 `InvalidateVisual()` + `SWP_FRAMECHANGED`：
  即使首帧仍被吞，也会在用户察觉前以最终屏上位置自愈（Win11 不补 —— 不碰在用的路径）。

## 41. 启动预热 / sentinel 与优雅退出的竞态

*`src/UI/DetailWindow.xaml.cs`（`Prewarm`）、`src/App.xaml.cs`（prewarm 排队的 `_stopping` 守卫、
`ClearStaleShutdownSentinel` 调用点）、`src/UI/TaskbarWindow.cs`（`ConsumeShutdownSentinel` /
`ClearStaleShutdownSentinel`）*

2026-09-29 实机事故（Win10，崩溃对话框 + `logs/native-crash.log` 两条 VEH 记录）：新实例启动
仅 2s 就"检测到 shutdown.sentinel——构建触发的优雅退出"，随后弹 UI 线程未处理异常
（`System.NullReferenceException`，栈顶是 WPF 框架内部的
`DeferredAppResourceReference.GetValue`，本进程帧是 `DetailWindow.Prewarm` 里的
`ContentHost.Content = view`）。

两层原因，对应两道修复：

1. **残留 sentinel 杀死了新实例。** 构建流程连跑两次 `touch shutdown.sentinel` 而旧实例只消费了
   第一个，剩下的文件躺在原地，被下一次启动的新实例的 1s tick 当成"立即退出"指令。修复：
   `App.OnStartup` 在拿到单实例 mutex 之后调 `TaskbarWindow.ClearStaleShutdownSentinel()`。
   放在 mutex 之后是关键：此刻能证明没有存活兄弟实例，磁盘上的文件只可能属于某个已退出的
   上一实例（正常构建流里，旧实例永远在新实例启动前消费掉它自己的 sentinel）；而在
   explorer-restart 的 `Start()` 重入路径上绝不清——那可能删掉一个正当的、针对本实例的
   构建退出请求。
2. **Background 优先级的 prewarm 在退出流程中才执行。** `OnExit` 用 `Thread.Join` 等任务栏线程
   拆除 overlay，而 STA 线程的 Join 会继续泵消息——排队的 Background 级 DispatcherOperation
   （启动时排的 `Prewarm`）就这样在 `退出` 日志之后 40ms 才被派发，撞上已在拆除的应用资源，
   WPF 的延迟资源引用解析 NRE。修复是双保险：排队回调开头 `if (_stopping) return;`（常见情形
   直接跳过），且 `Prewarm` 整体 try/catch 只记 `Logger.Warn`——它是纯优化，永远没有资格弹
   崩溃框；try/catch 同时盖住"回调进入时还没 `_stopping`、循环内 `Dispatcher.Invoke` 的嵌套帧
   泵又把 Shutdown 派发进来"这种重入形态。

同类风险已知仍存在但不处理：若退出时恰好有开着的 detail 窗口，`RefreshDetails` 的排队刷新
理论上也能在 OnExit 的 Join 泵里踩到同一个框架 NRE——尚未有实机报告，且窗口打开时用户本就
在场，留待真出现再说。

## 42. 经典任务栏(Win10)下覆盖层必须自画不透明底色，不能全透明

*`src/UI/TaskbarWindow.cs`（`DrawHorizontal` / `DrawVertical` 的 CardBrush 全幅填充；
`ApplyTaskbarTheme` 里 CardBrush 的 taskbar-form 角色注释）*

症状（2026-09-30，Win10 实机，200% DPI 虚拟机）：覆盖层区域里**有时**长期显示一个程序图标
样子的残影（半透明、位置固定、不随 tick 刷新变化），Win11 从没有。用户最初被误导的方向：
是某个活动元素"透"了过来——逐一排除：资讯和兴趣（`DynamicContent2`，窗口虽在但永远隐藏、
且用户已关闭）、托盘图标（位置对不上，托盘从 x=1419 开始）、桌面图标透出（枚举桌面 ListView
全部 8 个图标，没有一个落在任务栏下方）、覆盖层自己画的（网络列只画 ↑↓ 和文本）。

真正的机制是**残留像素**，而且是我们结构性无法清理的那种：

- 覆盖层身体除文字/分隔线外全透明（DComposition premultiplied，每帧 `Clear()` 透明）。
- 它身下是 `ReBarWindow32` 和 `Shell_TrayWnd` 的表面，而这两个窗口都带
  `WS_CLIPCHILDREN`：凡是被子窗口盖住的区域，父窗口**永远不会在那里重绘**。
- 于是覆盖层压住的那块父窗口表面，内容从被盖住的一刻起就被冻住——新表面重建
  （explorer 重启）时可能带着表面池里回收来的旧像素（上个 explorer 的任务按钮/图标残片，
  亮度不衰减，正好读作"某个程序的图标残影"）。"有的时候"= 取决于重建那一刻池子里有什么。
- **没有任何办法擦干净**：被 clip 的区域连 `RedrawWindow`/`WM_ERASEBKGND` 都进不去，
  唯一能变它的时机是表面重建——而重建正是带来下一批回收像素的时刻。任务按钮带收缩后、
  覆盖层停靠前那个微秒级的空窗里理论上能对 ReBar 补一次重绘，但 Shell_TrayWnd 被 ReBar
  整体盖住，那一层的脏永远够不着。

修法因此只有一个：**遮盖**。经典任务栏形态（`!s.Floating && s.Classical`）下
Draw 的第一步用 `CardBrush`（alpha 恒为 1）全幅填满窗口矩形，把身下的一切都盖死——与悬浮
小组件的自绘卡片（§36）、Win10 detail 弹窗的不透明卡片（§40）同一套取舍：没有可靠托底
时就不依赖别人的表面。Win11 路径不受影响，保持全透明。

底色颜色**不能写死**：第一版用了共享 tint（深色 0x202020），用户当场打回"太黑了，还是要
透明的效果"。移植 TrafficMonitor 的 `auto_set_background_color`
（TrafficMonitorDlg.cpp）：每 tick 在任务按钮带与我们窗口边缘之间那 2px 缝隙处
（`rc.left-1`/`rc.right+1`，竖直任务栏取上/下沿）用 `GetPixel(GetDC(NULL))` 读合成后的
任务栏真实颜色，实时染给底色画刷（`SampleTaskbarBackdropColor`；纯黑=重启瞬态，忽略；
颜色没变不动笔）。单像素裸读会被"操作任务栏"污染：explorer 会把按钮带瞬间重展压过缝隙，
那一 tick 读到的是任务按钮的颜色，底色就跟着来回轮换（浅色模式下刺眼）。三重防护：
3 像素平均（抗边缘抗锯齿）、逐通道阈值（近似色不动笔）、**稳定期**——新颜色必须连续
3 个 tick 一致才采用，亚秒级的按钮带重展永远到不了画刷。观感=无缝融入任务栏（"透明"的
效果），实质=不透明遮盖，残影再无可乘。
停靠时在 `RepositionOverlay` 之后立即采样+重画一帧，第一帧可见画面就是无缝的；主题/壁纸/
透明度变化靠每 tick 的采样自然跟上。

附带事故（同日）：explorer 重启还暴露了一个独立 bug——任务栏线程在 explorer 死亡期间
卡在某个阻塞调用里再没醒来（看门狗 17:45:02 报"覆盖层窗口已消失 6s 而 Start() 未返回"），
覆盖层永久丢失且 sentinel 不再被消费，只能任务管理器杀进程。修复见 §43（跨线程探活 +
停滞取证转储 + 45s 僵尸自愈重启）。

## 43. 永不无保护地跨线程调用 explorer 的窗口；僵尸卡死要取证 + 自愈

*`src/UI/TaskbarWindow.cs`（`BandResponsive`；`ClassicalReposition` / `RestoreMinWindow` 的探活；
拆除路径的 `NoteStep`）、`src/App.xaml.cs`（`CheckOverlayHealth` 的取证转储与 45s 自愈重启）、
`src/Interop/CrashTraceInterop.cs`（`RecentMessages`）*

事故（2026-09-30，§42 同日）：explorer 莫名重启（pid 5128→1380，用户未操作——疑似崩溃后
WER 拉起），任务栏线程随之**永久**卡住：心跳停、覆盖层 HWND 消失、`Start()` 不返回、
重建循环死、sentinel 不消费，20+ 分钟无自愈，只能任务管理器杀进程。

机制（最终确认版，取证过程本身就是教材）：`SetWindowPos`/`MoveWindow` 作用于别的线程
拥有的窗口时会同步等应答，explorer 拆除中永不应答 → 调用永久阻塞——这是最初的理论。
第一版（探活+worker）上线后用户复测**依旧卡死**，但看门狗新增的 `CaptureStackOf`（挂起
线程抓原生栈）给出两次 `RIP=win32u 同偏移` + 消息环 `0x113(WM_TIMER)×7 + 0x82
(WM_NCDESTROY)`——再对照 `_step`（从不清除的残留标记）才看出真相：**线程不是卡在调用
里，而是卡在空队列的 `GetMessage` 上**。窗口被 explorer 拆除时，win32k 只派发了裸
`WM_NCDESTROY`（没有先行的 `WM_DESTROY`——2026-09-30 19:40 实机复现确认），定时器随
窗口死亡、队列变空，`PostQuitMessage`（只在我们的 WM_DESTROY 分支里发）从未发出，
`Start()` 永不返回，重建循环、心跳、sentinel 全灭。此前把 `GetWindowRect` 当凶手是被
`_step` 残留值误导（它停在 `经典重定位:测带矩形`）。

最终四层处置：

1. **源头——裸 `WM_NCDESTROY` 也走完整拆除**。`TeardownOverlay`（恢复按钮带 / SRUM
   注销 / 杀定时器 / 释放设备管线，全部有界）从 WM_DESTROY 提取出来，两条消息都调用
   并 `PostQuitMessage`（once-guard 幂等）。实测效果：explorer 一死，`Start()` 立即
   返回、2s 后重建、~5s 内重新嵌入，进程不重启。
2. **消息循环不做会同步进别人窗口的调用（硬化，保留）**。按钮带移动、对外部窗口的
   `GetWindowRect`/`GetClientRect` 等一律经静态 worker 线程（IsBackground）执行，调用
   方用 `WaitForSingleObject` 有界等待（**不用 CLR 的 STA 泵等待**——那正是消息环里
   嵌套 WM_TIMER 的重入通道）。**例外：`SetParent` 必须留在消息循环上**——它调用中途
   要给我们自己的窗口递消息，调用方必须能泵（放 worker 上必死锁，19:31 实机验证）。
   已知残留洞：悬浮模式的 `SHQueryUserNotificationState`（到 explorer 的 RPC）未守护。
3. **看门狗 5s 轻唤醒**。窗口消失超 5s：`PostThreadMessage(WM_QUIT)` 到任务栏线程——
   空队列的 `GetMessage` 立即返回 0，`Start()` 返回即重建（~7s）；线程若真卡在调用
   里，quit 在队列里等它返回，无副作用。
4. **45s 僵尸判定 → 静默自愈重启（兜底，已验证）**。`Logger.ReleaseHandle()`（否则子
   进程首批日志被 `FileShare.Read` 冲突吃掉）+ **先处置单实例 mutex 句柄**（否则子进程
   在父进程死前做存在性检测，把自己当第二实例静默退出——PID=368 之死）→
   `Process.Start(UseShellExecute=false)` 拉起**静默提权**新实例（父提权→子提权，无
   UAC，也不依赖可能正在重启的 explorer 的 shell 启动）→ `Environment.Exit(0)`（不等
   挂起的前台线程）。每次进程生命只触发一次。

另：重建间隙（拆除→2s 退避→探测→首个 tick）是**合法的** tick 空窗，探测循环和
App 重建循环里现在都打心跳点，看门狗不再误报（19:40:30 的假停滞）。

## 44. 竖直（侧边停靠）任务栏与任务栏族无关；窄条必须走双行条带

*`src/UI/TaskbarWindow.cs`（`Start` 的方向探测、`ComputeLayout` / `VerticalSlotRect` /
`ComputeTargetSize`、`DrawVertical` / `DrawStripLines`、`CalcPositionVertical` /
`TaskbarBandCross`、`RepositionOverlay`、`GetBandSize`、`FindClassChild`）*

事故（2026-09-30）：用户把任务栏切到 **Windows 11 26H2 新出的左侧竖直模式**，覆盖层仍按
水平网格算 —— `Start()` 的竖直判定当时写成 `vertical = !floating && classical && …`，而
Win11 原生竖直任务栏仍是 Win11 族（XAML `DesktopWindowContentBridge` 在，`classical=false`），
于是 `vertical=false`，窗口按「带宽 = 任务栏**高**度」落成 **448×2168px**：既高出 96px 的
任务栏列、压到桌面上，又因为子窗口矩形内所有点击都归自己，**整条任务栏点不动了**
（日志里的证据：`任务栏族=Win11 竖直=False … 覆盖层=448x2168px`）。

方向探针必须只看**任务栏窗口的宽高比**（TrafficMonitor 的 `CheckTaskbarOnTopOrBottom`：
宽 ≥ 高 = 水平），族与方向是两个正交的维度：竖直的可能是经典族（Win10 起），水平的也可能是
Win11 族（26H2 的 设置 → 任务栏位置）。

竖直任务栏的几何（都是「水平那一套转 90°」）：

- **尺寸**：宽 = 内容宽被 `TaskbarBandCross`（工作区左边到显示器左边、与任务栏矩形相交的
  保留带）夹住；高 = 条带栈，由布局算。宽度反过来决定**条带形态**——这一步是循环的，
  所以 `ComputeTargetSize` 先夹宽度、再用夹后的宽度建布局（`Draw`/`HitTestSlot` 也从
  窗口实际宽度反推，三处必须一致）。
- **窄条双行**：侧边任务栏只有 ~48 DIP 厚（= 水平任务栏的高度），而单行「标签 … 数值」
  需要 ~72 DIP，所以 `STRIP_TWO_LINE_MIN_W` 以下的条带改成**双行块**：11px 标签居中在上、
  13px 数值居中在下，速率用紧凑写法 `FormatCompact`（"11.4M"，完整写法 ~56 DIP 塞不进
  46 DIP 的条）。块的几何是「一行一个 ROW」：`VerticalRows` × `VerticalRowH`（双行时
  `STRIP_BLOCK_H`=44 DIP，单行时 `STRIP_H`=16 DIP），块内 = 上下各 `STRIP_BLOCK_PAD`(4) +
  两行 + `STRIP_LINE_GAP`(1)，行高由 `DrawStripLines` 反推。**这个留白不是装饰**：最初按
  2×STRIP_H=32 DIP 无留白实现，标签和数值挤成一坨、块间分隔线像是两边都不属于，用户直接
  反馈「布局不合理」；48 DIP 是宽松版，44 DIP 是随后「稍微紧凑一点」的结果，`STRIP_LINE_GAP`
  4→1 则是再之后「文案和数值之间间距少一点点」——`STRIP_BLOCK_H`（整体疏密）与
  `STRIP_LINE_GAP`（标签↔数值）就是那两个旋钮。分隔线画在**行边界**（`i * VerticalRowH`），
  所以永远不会把标签和它自己的数值切开；网络是上下两个这样的块（↑ 块 / ↓ 块）。
- **锚点**：`CalcPositionVertical` = `CalcPosition` 转 90°。靠左显示 off（默认）→ 托盘上方
  （通知区那一端）；on → 任务栏顶端，前提是图标簇确实在下方开始（`ClusterTop` 取
  Start / ReBar / 搜索宿主里最靠上的那个），否则回退到托盘端——与水平版「Start 左边没地方
  → 回退右侧」同构。Widgets 预留（160px）在竖直任务栏不存在，故不适用。
- **每 tick 维护**：`RepositionOverlay` 现在两族共用——先比宽高比判定方向翻转
  （`ReconfigureOrientation` 转置 + 重设缓冲 + 重锚），再纵向重算尺寸（缩放/DPI 变化会
  改变条带形态）与锚点。Win11 族没有 100ms 的 `TIMER_ID_POS`，1s 采样 tick 是它唯一的观察者。

顺带修掉一个观测到的坑：`FindWindowExW(parent, 0, "Start", NULL)` 在 26300 的左侧任务栏上
**查不到** Start 窗口（传本地化标题才查得到；`TrayNotifyWnd` / `ReBarWindow32` /
`TrayDummySearchControl` 则正常），导致「贴近开始按钮」静默失效。`FindClassChild` 先试
`FindWindowEx`，失败再手工走 `GW_CHILD`/`GW_HWNDNEXT` 比对类名（USER 锁内读，不走 §43 的
跨线程 worker）。

