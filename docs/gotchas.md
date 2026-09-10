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

## 7. `SetWindowPos(HWND_TOPMOST)` silently no-ops when not foreground

*`src/UI/DetailWindow.xaml.cs`: `ShowColumn`, `EnsureTopmost`*

`ShowColumn` activates + `SetForegroundWindow`s FIRST; `EnsureTopmost` verifies the exstyle
bit and never trusts the return value.

## 8. Acrylic unfocused / the menu host

*`src/App.xaml.cs`, `src/UI/DetailWindow.xaml.cs`*

FluentWpfCore's `UseWindowComposition=True` drives acrylic — do not P/Invoke DWM/Accent
directly. The right-click menu's invisible host is `Activate()`d, closes on `Deactivated`,
and gets `WS_EX_TOOLWINDOW` at `SourceInitialized` (without it the host shows in Alt+Tab).

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

*`src/App.xaml.cs`: `ScheduleIdleTrim`*

`SystemInfo.TrimMemory` runs after the prewarm and when the last detail window closes,
deferred to Background priority (the comments at the site explain why inline would be
wrong).

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
