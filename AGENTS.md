# AGENTS.md

This file provides guidance to AI coding agents when working with code in this repository.

**This file is a MAP, not the manual.** It carries the rules and the pointers; the long
unwindings live in `docs/` (loaded only when you touch that area) and at the code site.
See [Maintaining this file](#maintaining-this-file) before you add anything here.

| When you touch… | Read |
|---|---|
| taskbar geometry, boot/explorer-restart, D2D/DXGI, samplers, theme, process rows | `docs/gotchas.md` |
| a 设置 item's wiring to its sampler | `docs/settings-plumbing.md` |
| any of the six scroll views | `docs/scroll-stack.md` |

## What this is

A Windows taskbar widget: a self-drawn overlay embedded in the taskbar (three stacked groups — CPU/内存, 磁盘/GPU, 网络 ↑/↓) that pops up a fluent acrylic detail window on click. WPF on .NET Framework 4.8. **No main window**; the only persistent UI is the taskbar overlay. The process stays alive via `ShutdownMode="OnExplicitShutdown"`; exit is via the overlay's right-click menu.

## Build & run

```shell
dotnet build -c Debug          # only toolchain on this machine — no VS MSBuild / nuget.exe
bin\Debug\net48\task_monitor.exe
```

SDK-style csproj, `net48`, `UseWPF=true`. No tests.

**A live exe locks the output**, so `dotnet build` fails at the copy step with
`error MSB3021`/`MSB3027` naming `task_monitor.exe (PID)`. That is NOT a compile failure —
the compile succeeded. Exit the running instance first, and always use the sentinel:

```shell
touch bin/Debug/net48/shutdown.sentinel   # 1s tick notices it and exits gracefully
dotnet build -c Debug
bin/Debug/net48/task_monitor.exe &
```

The sentinel is the **default** path: this agent shell may be unelevated while the app is
elevated, and then `taskkill` fails with Access denied (UIPI) — file I/O is not gated that
way, which is exactly why the hook exists (mechanism: `ConsumeShutdownSentinel`,
TaskbarWindow.cs). Relaunching from an **unelevated** shell pops a UAC prompt on the secure
desktop — that one step is the user's; an elevated shell inherits elevation and skips it.

**The app always runs elevated, self-managed** (manifest `asInvoker`): `App.RunElevationGate` (src/App.xaml.cs) runs at the top of `OnStartup`, BEFORE the single-instance mutex — an exiting unelevated launcher must never hold the mutex, or the elevated child takes itself for a second instance. Unelevated + consented → `runas` self-relaunch; never asked → `ConsentDialog` (允许 persists `elevationConsent: true`); 不允许/UAC-cancel exits — the process never runs degraded (SRUM per-process net needs admin). Consent persists to **`settings.yaml` in the exe directory** (`AppSettings`, YamlDotNet — the single store for ALL settings; the file + `.tmp`/`.bad` siblings are runtime artifacts). The manifest's `dpiAware=true` keeps the overlay's D2D text sharp — don't drop it.

Exercising the consent dialog needs an unelevated launch (`explorer.exe "...task_monitor.exe"`); the UAC prompt lives on the secure desktop and cannot be automated. **Any verification needing a screenshot or visual confirmation is the user's job.**

Packages: `iNKORE.UI.WPF` + `iNKORE.UI.WPF.Modern` (control theming, merged in App.xaml), `DirectN` (overlay rendering), `FluentWpfCore` (popup acrylic + `SmoothScrollViewer`), `YamlDotNet`, framework `System.Drawing` (icon fallback only). Build-only: `Fody` + `Costura.Fody` — the build emits ONE exe (~6.1 MB), all managed DLLs woven in as compressed resources. **Costura, not ILRepack, deliberately** — ILRepack merges assemblies and would break the cross-assembly `pack://` URIs (rationale in task_monitor.csproj comments and the FodyWeavers.xml header).

**Single-instance:** a named `Global\` mutex right after the gate (`Global\TaskMonitor.exe__<guid>`, un-owned, existence test only; `Global\` so the guard holds across sessions — reachable only elevated, so `SeCreateGlobalPrivilege` is in hand). A second instance exits **silently** (the overlay is always visible anyway); a consented second launch still UAC-prompts first — inherent to the design.

**Legacy-OS warning:** right after the mutex, a pre-Win11 first launch pops `LegacyOsWarningDialog` once (`TaskbarWindow.IsWin11OrLater` — the raw OS check, no taskbar-shape test) saying Win10 compatibility issues are expected and won't be fixed; `legacyOsWarningShown: true` in settings.yaml suppresses it thereafter. **Win10 support is deprioritized** — the classical taskbar path below still ships, but new work targets Win11 only.

## 发布 / CI/CD

**GitHub 是主仓库，CNB 是镜像。** Remotes: `origin` = GitHub（SSH，`git@github.com:linesoft2/TaskMonitor.git`），`cnb` = CNB（HTTPS——CNB **不支持 SSH**（官方明示），HTTPS git 认证 = 固定用户名 `cnb` + 访问令牌）。日常提交只推 `origin`；两个 GitHub Actions workflow（.github/workflows/）负责同步和发布：

- `sync-cnb.yml`（main 分支 + v* tag 推送）→ `docker://tencentcom/git-sync` 把代码和 tag **force** 推到 CNB（CNB 侧是纯镜像、从无独有提交，force 保证一致）。
- `release.yml`（v* tag 推送）→ windows-latest 上先**校验 tag 与 csproj `<Version>` 一致**（不一致直接 fail——版本号的唯一来源是 `task_monitor.csproj` 的 `<Version>`，SDK 据它生成 Assembly/File/Informational 三个版本属性，关于页显示与更新检测读生成的 `AssemblyInformationalVersion`）→ `dotnet build -c Release`（托管镜像自带 net48 目标包）→ `gh release create` 建 GitHub Release → `tools/publish-cnb-release.ps1` 调 CNB OpenAPI 建 release 并上传 `task_monitor-<tag>-x64.exe`（附件名带架构；先把 tag 推到 CNB——CNB 的 release 必须挂在已存在的 tag 上；release notes = 自上一个 tag 的 `git log`）。
- Secret：`CNB_TOKEN`，需要 **repo-code 读写**（推代码/tag）+ **repo-contents 读写**（release/附件）两个 scope。

发布 = **先改 `task_monitor.csproj` 的 `<Version>`**，再 `git tag vX.Y.Z && git push origin vX.Y.Z`，之后全自动（tag 与版本号不一致流水线会 fail，这是刻意的）。CNB 云端构建集群全是 Linux Docker 节点，跑不了 net48 WPF —— 这就是构建放在 GitHub 托管 Windows runner 的原因。

`publish-cnb-release.ps1` 的三个坑（勿回退，详见脚本注释）：CNB API 必须显式 `Accept: application/json`（否则 406）；`verify_url` 的 asset_path 段是 %2F 编码，.NET `System.Uri` 会把它解码回 `/` 再发送、路径变形导致 500 —— PUT/确认必须走 curl.exe，不能 Invoke-RestMethod；脚本含中文注释，必须保持 **UTF-8 BOM**，否则本机 Windows PowerShell 5.1 按 GBK 误读会把解析搞坏（pwsh 7 不受影响）。

## Architecture (the part that spans files)

**Two threads, one process:**

- **Taskbar overlay** — a native Win32 window owned by `TaskbarWindow` on a dedicated STA thread with its own message loop, rendered with DirectN, embedded into the taskbar via `SetParent`. 3 visual groups → **5 hit slots** (0=CPU 1=内存 2=磁盘 3=GPU 4=网络); `WndProc` does the 2D hit-test (left-click toggles a popup, right-click the menu). Slot geometry is DYNAMIC (`ComputeLayout` from the sampling mask): the visible stacked metrics pack into the two-row grid in their fixed order — a hidden metric leaves no hole (空缺补齐; only the grid's last row may stay empty on an odd count), an emptied group frees its width and the window shrinks (`ResizeForLayout`; slot IDs stay stable). **Two taskbar families**, detected per `Start()` run (TrafficMonitor's `CheckWindows11Taskbar`: Win11 version AND the XAML `DesktopWindowContentBridge` child — anything else, Windows 10 or an ExplorerPatcher-restored classic taskbar on Win11, is "classical"):
  - **Win11**: parent = `Shell_TrayWnd`; placement left of the tray (default) or the left side — far-left corner / snapped left of Start, honored only while the taskbar is centre-aligned (comments at `CalcPosition`), TrafficMonitor's Win11 path including the 160px Widgets reserve.
  - **Classical (Win10)**: parent = `ReBarWindow32` (`WorkerW` fallback); no free anchor — the `MSTaskSwWClass` band is SHRUNK/SHIFTED to carve out the slot (`ClassicalReposition`), re-checked on a 100ms timer, and RESTORED on exit (`RestoreMinWindow`; invariants: gotchas §1). 靠左显示 picks the band's Start-side end vs tray-side end; `overlaySnapToStart` is Win11-only (the settings page hides it there). **Side-docked (vertical) taskbars** transpose the grid into full-width strips (`ComputeLayout(mask, vertical)` / `DrawVertical` / `HitTestSlot`), `DetailWindow` anchors its popup to the taskbar's screen edge (`GetTaskbarEdge`), and dragging the taskbar to another edge re-docks live (`ReconfigureOrientation`).
  - Lifetime: `Start()` first waits for a real taskbar, and ANY return from `Start()` makes App re-enter it after 2s to recreate the overlay (gotchas §2).
- **WPF UI thread** — on-demand `DetailWindow`s (one transient + any pinned), the right-click `ContextMenu`, and `SettingsWindow` (at most one, re-activated). Startup **pre-warms the WPF stack** with one throwaway `DetailWindow.Prewarm()` — the overlay is native, so the first popup would otherwise eat the whole cold-start tax (rationale in App.xaml.cs). The right-click menu host stays **lazily created** at first right-click — creating it at startup regressed the menu.

**Cross-thread contract** (all on `TaskbarWindow`, set by `App`, marshaled to the UI thread via `Dispatcher.BeginInvoke`):

- Taskbar→UI: `Action<int> ToggleCallback` (slot 0–4 or -1), `RightClickRequested`, `SnapshotChanged`, `LatestSnapshot`.
- UI→taskbar: `RequestDeselect(int)` (only if that column is still selected), `RequestSelect(int)`, `SetColumnClickEnabled(int, bool)`, `SetPlacement(onLeft, snapToStart)`, `SetSampleInterval(ms)`, `SetMetricSamplingMask(mask)`, `SetMergeSamePathProcesses(bool)`, `SetDiskDisplay`/`SetGpuDisplay`/`SetNetAdapter`/`SetPublicIpLookup`/`SetClashApi` (all five: `docs/settings-plumbing.md`), `OverlayHwnd`.

**Single source of truth (push, not poll):** `TaskbarWindow` owns the **only** `SystemSampler` (1s tick by default — configurable 0.5/1/2s via 设置→采样间隔, re-armed over `WM_APP_SET_INTERVAL`; `volatile` publish, `SnapshotChanged`). Popups read `LatestSnapshot` and have no timer. The cadence is stamped onto every snapshot (`SampleIntervalMs`) so the "N 秒前" chart tooltips scale correctly. (CPU% also needs an old baseline — a fresh sampler reads ~0%.) A metric switched off (设置→采样) skips its samplers entirely, HIDES its overlay slot (the group/window reflows to fill the space), suppresses its column press, and primes its delta baselines for one tick when re-enabled.

**DetailWindow = shell + per-metric view:** a borderless acrylic shell (FluentWpfCore `WindowMaterial`, `UseWindowComposition=True`) hosting one `IDetailView` (Cpu/Ram/Disk/Gpu/Net). Fresh window+view per open, destroyed on dismiss (never hidden-and-reused — born active, no acrylic flash). Closes on focus loss. Placed **next to the taskbar's screen edge** (`PositionNearTaskbar`: above a bottom taskbar, below a top one, beside a side-docked one, centred on the column via `ColumnCenter`/`ColumnCenterY`) — anchored off the OVERLAY's rect, not the taskbar's, so the overlay's reserved-band bottom-alignment (`TaskbarBand`; gotchas §2) automatically keeps the popup hugging the VISIBLE taskbar when a taskbar-height mod inflates `Shell_TrayWnd`. Unpinned on a bottom taskbar, the flyout's **bottom edge stays anchored** (`SizeChanged` shifts `Top`; other edges grow downward from a fixed Top; mechanism + the pin-band exemption in DetailWindow comments).

**Pinned-mode invariants** (implementation in `DetailWindow`, comments at the sites):

- The shell owns the single pin `ToggleButton`, parked in each fresh view's `PinSlot`; it never moves on screen across the transition.
- Pinning grows a `TitleBarBand` out of the top (composed from `TitleBarButton`, NOT iNKORE's `TitleBarControl` — standalone it NREs; the ✕ works only because DetailWindow's ctor registers the `SystemCommands.CloseWindowCommand` binding). Unpin restores the saved pre-pin position.
- While pinned, the overlay column is **press-disabled** (re-enabled on unpin AND close) and **deselected**; unpin re-selects ("flyout open ⟺ column selected"). `Window_Closing` requests the column-aware deselect so a newer selection is never wiped.

## Project layout (layered, under src/)

All source lives under `src/`, grouped **by layer**; everything else (csproj, slnx, bin/, obj/, FodyWeavers.xml) stays at the repo root. `assets/` holds the artwork: the logo trio — `logo.ico` (the exe icon, referenced from the csproj), `logo.svg`/`logo.png` (source artwork; the Settings 关于 page inlines logo.svg as XAML shapes, no raster is embedded) — plus README artwork: `header.png` (the README banner). All files share the single `task_monitor` namespace — folders are physical only, so XAML `x:Class`/`xmlns:local` never reference a folder.

```
docs/            gotchas.md · settings-plumbing.md · scroll-stack.md (loaded on demand)
src/
  App.xaml(.cs)    entry: elevation gate → mutex → taskbar thread; prewarm; menu host; idle trim
  Logger.cs        file log — logs/task_monitor-yyyy-MM-dd.log next to the exe (gotchas §5)
  AppSettings.cs   settings.yaml store — every key documented per-property in the file
  VersionInfo.cs   版本号读取口（读 SDK 按 csproj `<Version>` 生成的 AssemblyInformationalVersion）
  UpdateChecker.cs 启动时检查更新（gotchas §33）
  StartupTask.cs   开机自启动 logon task (schtasks /XML) — the task itself is the state (gotchas §21)
  Sampling/        per-metric samplers + the SystemSampler facade (its header lists the 12) + ServiceHostMap
  Interop/         P/Invoke boundary per native API (each file's header says what it covers)
  UI/
    TaskbarWindow.cs        overlay + WndProc; owns the SystemSampler
    DetailWindow.xaml(.cs)  acrylic shell hosting one IDetailView
    ConsentDialog           first-run UAC-consent prompt (iNKORE modern window, unelevated)
    LegacyOsWarningDialog   one-time pre-Win11 compatibility warning (iNKORE modern window, elevated)
    UpdateAvailableDialog   发现新版本提醒 (iNKORE modern window — 立即更新/不再提醒/稍后)
    Common/                 UsageColors · formatters · ProcessListTip · PivotNavButtonFix · FollowTagPanel · the scroll stack
    Details/                the five IDetailViews
    Charts/                 hand-drawn DrawingContext charts
    Settings/               SettingsWindow (ONE scroll page, 类别 sections via TextBlock headers: 通用 / 外观 / 采样 / 关于 — SettingsCard/SettingsExpander)
```

**App.xaml under `src/`** needs the explicit `<Page Remove>` + `<ApplicationDefinition Include>` pair in the csproj — it must stay, or the build loses `Main`.

**Adding a metric:** new `XxxSampler` (Sampling/) + `XxxDetailView` (UI/Details/), wired into `SystemSampler.Sample` / `DetailWindow.ShowColumn` — plus a `Mask*` bit + settings.yaml key + a 采样 card for its sampling switch (SettingsExpander only if the metric has sub-settings — CPU/内存 are plain cards), and one row in `docs/settings-plumbing.md`.

## Fluent 2 / iNKORE.UI.WPF.Modern design guidelines

All WPF UI targets **Fluent 2**; before adding any control/layout/icon/interaction, **first check whether iNKORE.UI.WPF.Modern ships a component for it**. Local checkouts (use these first): `D:\ai-ref\UI.WPF.Modern` (source + samples), `D:\ai-ref\Documentation`.

The project tracks NuGet **0.10.2.1** and the checkout matches — beware version drift when that stops being true (git tags `vX.Y.Z` give the packaged API). Quirks, each documented at its site:

- TabControl's strip "+" and per-tab "×" both default VISIBLE — hide them for non-document tabs.
- Plain switchers use `TabControlPivotStyle`/`TabItemPivotStyle` — the default is an opaque TabView lookalike, wrong on acrylic. Compact-metric overrides: see DiskDetailView/GpuDetailView XAML.
- Pivot hit-test flaw (invisible PreviousButton swallows first-tab clicks): both pivot views call `PivotNavButtonFix.Apply` (its header explains the mechanism).

**Window families — never mix iNKORE's `UseModernWindowStyle`/`SystemBackdropType` with FluentWpfCore on the same window:** DetailWindow = borderless FluentWpfCore acrylic; SettingsWindow = iNKORE modern window + Mica; ConsentDialog/LegacyOsWarningDialog/UpdateAvailableDialog = iNKORE modern window (plain). iNKORE also styles the taskbar right-click menu (standard `ContextMenu` via `SetResourceReference`, **by key** — FluentWpfCore's later dictionary merge would shadow keyless defaults). Not iNKORE's `MenuFlyout` (can't place at the cursor; NREs unless owned).

## Non-obvious gotchas

The "don't regress" index. **Full unwinding of every entry: [`docs/gotchas.md`](docs/gotchas.md)**
(same numbering) — plus [`docs/settings-plumbing.md`](docs/settings-plumbing.md) for the
设置→sampler chains and [`docs/scroll-stack.md`](docs/scroll-stack.md) for the scrolling stack.
Each rule's canonical statement is a comment at the code site named in the doc.

**Taskbar / boot**

1. **Classical band is ours to restore** — `RestoreMinWindow` before ANY re-measure of our own, in `WM_DESTROY`, before an orientation flip, and on exit.
2. **Boot/explorer-restart:** `Start()` polls for a real taskbar; ANY return → App re-enters after 2s; WndProc delegate is a process-lifetime `static readonly` (a per-`Start()` one orphans the thunk → AV).
3. **Nothing escapes `WndProc`** — per-message try/catch → `CrashReporter`, fire-and-forget; an escaping managed exception is `0xC000041D`.
4. **Global crashes** = log file FIRST, then a code-only `CrashDialog` (AppDomain hooks in App's static ctor); a fatal report blocks the dying thread on the dialog.
5. **`Logger`** — `logs/task_monitor-<date>.log`, 7-day retention, never throws; hot per-tick paths never log (`WarnOnce` for per-tick failures). Keep the log line truthful.
6. **The overlay must never steal focus** — full no-activate set, incl. `SWP_NOACTIVATE` on every `SetWindowPos`.
7. **`SetWindowPos(HWND_TOPMOST)` no-ops when not foreground** — activate/`SetForegroundWindow` first; `EnsureTopmost` verifies the exstyle bit.
8. **Acrylic** — FluentWpfCore `UseWindowComposition=True`, never direct DWM/Accent P/Invoke; the menu host needs `WS_EX_TOOLWINDOW` or it shows in Alt+Tab.

**Samplers / metrics**

9. **`NetSampler` never enumerates NICs per tick** (~275ms = ~97% of idle CPU).
10. **Live CPU speed = PDH `% Processor Performance` × base clock**, not `CallNtPowerInformation`.
11. **Per-process CPU/RAM/disk = ONE `NtQuerySystemInformation` walk**; the disk column is the 24H2+ trailer, NOT SRUM.
12. **Disk/GPU replicate Taskmgr exactly** (`IOCTL_DISK_PERFORMANCE` / DXCore COM); **disk handles are opened and closed per query, never held** (safe-eject).
13. **Per-process GPU% is PDH, not DXCore** — instance-name encoding, MAX aggregation and `NormalizeEngineName` are in `ProcessGpuSampler.cs`; no elevation needed, unlike SRUM.
14. **Process-row tooltip = view-owned `Popup` on `MouseMove`**, never a row `ToolTip`.
15. **Per-process net = SRUM real-time API** (admin is why we elevate); the callback owns its record set, and rates re-diff only when the frame version advances (holding rates between frames).
16. **Wi-Fi `wlanapi` is on-demand only** — never on the per-tick path.
17. **Memory breakdown = `GlobalMemoryStatusEx` + NTQSI class-2/class-80**, not `GetPerformanceInfo`.
18. **Charts are hand-drawn WPF** — hover = `HitTest` + a real `Popup`.
19. **Scroll stack** — `Style="{DynamicResource {x:Type ScrollViewer}}"` on every scroller, or it silently falls back to the Aero bar. → `docs/scroll-stack.md`
20. **Idle trim at event points only**, never on a timer.
21. **开机自启动 = a logon scheduled task, never the Run key** (XML-registered, not schtasks switches).

**UI / theme / settings**

22. **深浅色 = `ThemeManager.Current.ApplicationTheme`**; never `GetActualTheme(window)` on DetailWindow (reads Light forever); the native overlay tracks the SYSTEM theme separately.
23. **Never `<StaticResource …/>`-alias a theme brush in `Window.Resources`** — it freezes the startup scheme; use scheme defaults or `<DynamicResource …/>`.
24. **采样间隔 is runtime state** — rate samplers normalize over REAL elapsed time, never "1 tick = 1s".
25. **Sampling mask ≠ pinned-window click mask** — unpinning must not re-enable a sampling-disabled slot; toggling posts `WM_APP_SET_METRICS` (layout changes), enable primes baselines.
26. **合并相同程序 merges BEFORE the top-8 cut**; svchost.exe is exempt (per-service rows).
27. **svchost→服务命名 runs AFTER the merge** (it exempts svchost by name); the hover 描述 is resolved lazily and cached per service.
28. **磁盘/GPU 显示方式** — the mode only picks the headline; every device is queried each tick, and a mode/index change clears the history.
29. **网络适配器** — the virtual-adapter filter does NOT apply to an explicit pin; absent/down falls back to 自动, re-probed every 30 ticks.
30. **公网 IP off kills ALL public traffic** (IP lookups + the ICMP latency probe); the LAN gateway ping is unaffected.
31. **Clash/Mihomo** — `req.Proxy = null` (never route to the local core through a system proxy); rows are appended standalone and exempt from merging; `null` endpoint = poller asleep.
32. **DISPOSE back-buffer wrappers before `ResizeBuffers`** — wrappers live on `RenderState`, never in locals.
33. **更新检测** — csproj `<Version>` is the only version source; CNB source reads the WEB 307 redirect (OpenAPI is anonymously 401); "不再提醒" skips only that version.
34. **net48's `Run.Text` is not a dependency property** — a `{Binding}` on a `Run` throws `XamlParseException` at startup; set named Runs in code, use two TextBlocks in DataTemplates.
35. **A lost D3D device (驱动更新/重置/TDR) is recoverable, never a crash** — only the raw `ctx.Object.EndDraw`/`SwapChain.Object.Present`/`ResizeBuffers` HRESULTs can tell (DirectN's throwing helpers lose the code as E_FAIL): latch `RenderState.DeviceLost`, let the tick's `RecoverDevice` rebuild the pipeline in place, and release the DComp target/visual with `Marshal.FinalReleaseComObject` (else `DCOMPOSITION_ERROR_WINDOW_ALREADY_COMPOSED`).

## Useful external references

- iNKORE.UI.WPF.Modern: the local checkouts above; github.com/iNKORE-NET/UI.WPF.Modern.
- **SRU real-time API:** reversed from `Taskmgr.exe` in `C:\Users\l\SRUM-RealTime-API.md` — the canonical reference for `ProcessNetSampler` / `SrumInterop`.

## Maintaining this file

**Keep it in sync with the code — update it in the same change, not later.** Stale guidance
is worse than none; fix or delete any claim that no longer matches the code on the spot.

**Route each fact to exactly one place, and point at it elsewhere** (a fact held in two
prose copies WILL drift — it already happened once, to the 合并相同程序 wording):

| The fact is… | Goes in |
|---|---|
| a rule an agent must not break, expressible in 1–2 lines | the gotcha index above |
| the reasoning, incident history, or multi-step mechanism behind that rule | `docs/gotchas.md` |
| how a 设置 item reaches its sampler | `docs/settings-plumbing.md` |
| the design of one module used from many places | a topic doc in `docs/` (e.g. `scroll-stack.md`) |
| derivation, calibration constants, offsets, reversed layouts | **the code site's own comment** — link to it, never copy it |
| what the code already says plainly (signature, field name, obvious branch) | **nowhere** |

Keep this file a *map*: rules + pointers. If it grows past ~12 KB, the new text belongs in
a doc. New gotcha → a numbered line here + a matching `## N.` section in `docs/gotchas.md`.
