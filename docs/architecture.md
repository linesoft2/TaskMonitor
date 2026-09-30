# Architecture — the part that spans files

Loaded on demand from [`AGENTS.md`](../AGENTS.md). The rules an agent must not break stay in
AGENTS.md's gotcha index; this file carries the unwinding behind them, plus the startup,
packaging and layout facts that would otherwise sit in the map. When this file and the code
disagree, the code wins — fix this file.

## Two threads, one process

- **Taskbar overlay** — a native Win32 window owned by `TaskbarWindow` on a dedicated STA
  thread with its own message loop, rendered with DirectN, embedded into the taskbar via
  `SetParent`. 3 visual groups → **5 hit slots** (0=CPU 1=内存 2=磁盘 3=GPU 4=网络); `WndProc`
  does the 2D hit-test (left-click toggles a popup, right-click the menu). Slot geometry is
  DYNAMIC (`ComputeLayout` from the sampling mask): the visible stacked metrics pack into the
  two-row grid in their fixed order — a hidden metric leaves no hole (空缺补齐; only the grid's
  last row may stay empty on an odd count), an emptied group frees its width and the window
  shrinks (`ResizeForLayout`; slot IDs stay stable). **Two taskbar families**, detected per
  `Start()` run (TrafficMonitor's `CheckWindows11Taskbar`: Win11 version AND the XAML
  `DesktopWindowContentBridge` child — anything else, Windows 10 or an ExplorerPatcher-restored
  classic taskbar on Win11, is "classical"):
  - **Win11**: parent = `Shell_TrayWnd`; placement left of the tray (default) or the left side —
    far-left corner / snapped left of Start, honored only while the taskbar is centre-aligned
    (comments at `CalcPosition`), TrafficMonitor's Win11 path including the 160px Widgets reserve.
  - **Classical (Win10)**: parent = `ReBarWindow32` (`WorkerW` fallback); no free anchor — the
    `MSTaskSwWClass` band is SHRUNK/SHIFTED to carve out the slot (`ClassicalReposition`),
    re-checked on a 100ms timer, and RESTORED on exit (`RestoreMinWindow`; invariants: gotchas
    §1). 靠左显示 picks the band's Start-side end vs tray-side end; `overlaySnapToStart` is
    Win11-only (the settings page hides it there).
  - **Orientation is orthogonal to the family**: a **side-docked (vertical) taskbar** — the
    classical one since Win10, and on Win11 26H2 the native 设置 → 任务栏位置 左侧/右侧 — transposes
    EITHER family's overlay into the strip stack (`ComputeLayout(mask, vertical, stripWidth)` /
    `DrawVertical` / `HitTestSlot`); the probe is the taskbar rect's aspect, never `classical`
    (getting that wrong is the "覆盖层盖满整条任务栏、任务栏点不动" incident — gotchas §44).
    A ~48-DIP side bar cannot hold the single-line "label … value" pair, so below
    `STRIP_TWO_LINE_MIN_W` each metric becomes a padded label-over-value block
    (rows × `VerticalRowH`: 44 DIP two-line / 16 DIP single-line; the padding is what keeps a
    label with its own value — gotchas §44), and the Win11
    family anchors along the taskbar's axis (`CalcPositionVertical`: tray end by default, the top
    corner / above Start when 靠左显示) since it has no band to shrink. `DetailWindow` anchors its
    popup to the taskbar's screen edge (`GetTaskbarEdge`), and an orientation flip re-docks live
    at runtime (`ReconfigureOrientation`).
  - Lifetime: `Start()` first waits for a real taskbar, and ANY return from `Start()` makes App
    re-enter it after 2s to recreate the overlay (gotchas §2).
  - **悬浮模式 (设置 → 外观)**: the same window class, layout, drawing, hit-testing and sampler,
    but NOT reparented — a top-level, draggable, topmost-by-default desktop widget whose body is
    its OWN drawn card (the detail popup's tint, no blur: the accent material could only be
    shaped by the DWM frame that carries the drop shadow), and the taskbar keeps no copy. The
    form is creation-time state, so flipping the switch rebuilds the window (fast restart);
    `GetTaskbarEdge` gives the detached widget its own popup side. Derivation, probe measurements
    and the six verification-found rules: gotchas §36; the shell's mechanism (frame off + rounded
    window region) lives at `WindowBackdropInterop`.
- **WPF UI thread** — on-demand `DetailWindow`s (one transient + any pinned), the right-click
  `ContextMenu`, and `SettingsWindow` (at most one, re-activated). Startup **pre-warms the WPF
  stack** with one throwaway `DetailWindow.Prewarm()` — the overlay is native, so the first popup
  would otherwise eat the whole cold-start tax (rationale in App.xaml.cs). The right-click menu
  host stays **lazily created** at first right-click — creating it at startup regressed the menu.

## Cross-thread contract

All on `TaskbarWindow`, set by `App`, marshaled to the UI thread via `Dispatcher.BeginInvoke`:

- Taskbar→UI: `Action<int> ToggleCallback` (slot 0–4 or -1), `RightClickRequested`,
  `SnapshotChanged`, `LatestSnapshot`, `FloatingPositionChanged(x, y)` (a drag ended; App
  persists it), `FloatingDragged(dx, dy)` (each drag step — App moves every open detail window by
  it, so the pair travels together).
- UI→taskbar: `RequestDeselect(int)` (only if that column is still selected), `RequestSelect(int)`,
  `SetColumnClickEnabled(int, bool)`, `SetPlacement(onLeft, snapToStart)`, `SetSampleInterval(ms)`,
  `SetMetricSamplingMask(mask)`, `SetMergeSamePathProcesses(bool)`,
  `SetDiskDisplay`/`SetGpuDisplay`/`SetNetAdapter`/`SetPublicIpLookup`/`SetClashApi` (all five:
  [`settings-plumbing.md`](settings-plumbing.md)), `SetFloatingMode(on, x, y)`/
  `SetFloatingTopmost(bool)`/`SetFloatingEdgeHide(bool)`/`SetFloatingFullscreenHide(bool)`/
  `SetFloatingDark(bool)`/`SetFloatingOpacity(double)` (悬浮模式: `settings-plumbing.md` §8),
  `SetFloatingKeepOut(hwnds)` (the open detail windows — the widget steps aside for them;
  gotchas §36), `OverlayHwnd`.

## Single source of truth (push, not poll)

`TaskbarWindow` owns the **only** `SystemSampler` (1s tick by default — configurable 0.5/1/2s via
设置→采样间隔, re-armed over `WM_APP_SET_INTERVAL`; `volatile` publish, `SnapshotChanged`). Popups
read `LatestSnapshot` and have no timer. The cadence is stamped onto every snapshot
(`SampleIntervalMs`) so the "N 秒前" chart tooltips scale correctly. (CPU% also needs an old
baseline — a fresh sampler reads ~0%.) A metric switched off (设置→采样) skips its samplers
entirely, HIDES its overlay slot (the group/window reflows to fill the space), suppresses its
column press, and primes its delta baselines for one tick when re-enabled.

**The tick also carries the stall diagnostics** (reported 卡死 with an empty log): it stamps
`LastTickTickCount`, which App's UI-thread watchdog reads (`CheckOverlayHealth` — reports a
stalled tick and a vanished overlay HWND, the two states this thread cannot report itself), and
`SystemSampler.Timed` logs any sampler step ≥150 ms by name (a USB disk that slept inside
`IOCTL_DISK_PERFORMANCE` is the known suspect). Don't drop either as dead code.

## DetailWindow = shell + per-metric view

A borderless acrylic shell (FluentWpfCore `WindowMaterial`, `UseWindowComposition=True`) hosting
one `IDetailView` (Cpu/Ram/Disk/Gpu/Net). Fresh window+view per open, destroyed on dismiss (never
hidden-and-reused — born active, no acrylic flash). Closes on focus loss. Placed **next to the
taskbar's screen edge** (`PositionNearTaskbar`: above a bottom taskbar, below a top one, beside a
side-docked one, centred on the column via `ColumnCenter`/`ColumnCenterY`) — anchored off the
OVERLAY's rect, not the taskbar's, so the overlay's reserved-band bottom-alignment (`TaskbarBand`;
gotchas §2) automatically keeps the popup hugging the VISIBLE taskbar when a taskbar-height mod
inflates `Shell_TrayWnd`. Unpinned on a bottom taskbar, the flyout's **bottom edge stays anchored**
(`SizeChanged` shifts `Top`; other edges grow downward from a fixed Top; mechanism + the pin-band
exemption in DetailWindow comments).

## Pinned-mode invariants

Implementation in `DetailWindow`, comments at the sites:

- The shell owns the single pin `ToggleButton`, parked in each fresh view's `PinSlot`; it never
  moves on screen across the transition.
- Pinning grows a `TitleBarBand` out of the top (composed from `TitleBarButton`, NOT iNKORE's
  `TitleBarControl` — standalone it NREs; the ✕ works only because DetailWindow's ctor registers
  the `SystemCommands.CloseWindowCommand` binding). Unpin restores the saved pre-pin position.
- While pinned, the overlay column is **press-disabled** (re-enabled on unpin AND close) and
  **deselected**; unpin re-selects ("flyout open ⟺ column selected"). `Window_Closing` requests
  the column-aware deselect so a newer selection is never wiped.

## Startup: elevation gate → mutex

**The app always runs elevated, self-managed** (manifest `asInvoker`): `App.RunElevationGate`
(src/App.xaml.cs) runs at the top of `OnStartup`, BEFORE the single-instance mutex — an exiting
unelevated launcher must never hold the mutex, or the elevated child takes itself for a second
instance. Unelevated + consented → `runas` self-relaunch; never asked → `ConsentDialog` (允许
persists `elevationConsent: true`); 不允许/UAC-cancel exits — the process never runs degraded
(SRUM per-process net needs admin). Consent persists to **`settings.yaml` in the exe directory**
(`AppSettings`, YamlDotNet — the single store for ALL settings; the file + `.tmp`/`.bad` siblings
are runtime artifacts). The manifest's `dpiAware=true` keeps the overlay's D2D text sharp — don't
drop it. Exercising the consent dialog needs an unelevated launch (`explorer.exe
"...task_monitor.exe"`); the UAC prompt lives on the secure desktop and cannot be automated.

**Single-instance:** a named `Global\` mutex right after the gate
(`Global\TaskMonitor.exe__<guid>`, un-owned, existence test only; `Global\` so the guard holds
across sessions — reachable only elevated, so `SeCreateGlobalPrivilege` is in hand). A second
instance exits **silently** (the overlay is always visible anyway); a consented second launch still
UAC-prompts first — inherent to the design.

**The sentinel** is the default way to stop a running instance: this agent shell may be unelevated
while the app is elevated, and then `taskkill` fails with Access denied (UIPI) — file I/O is not
gated that way, which is exactly why the hook exists (mechanism: `ConsumeShutdownSentinel`,
TaskbarWindow.cs). Relaunching from an **unelevated** shell pops a UAC prompt on the secure
desktop — that one step is the user's; an elevated shell inherits elevation and skips it.

## Packages & the single-exe build

`iNKORE.UI.WPF` + `iNKORE.UI.WPF.Modern` (control theming, merged in App.xaml), `DirectN` (overlay
rendering), `FluentWpfCore` (popup acrylic + `SmoothScrollViewer`), `YamlDotNet`, framework
`System.Drawing` (icon fallback only). Build-only: `Fody` + `Costura.Fody` — the build emits ONE
exe (~6.1 MB), all managed DLLs woven in as compressed resources. **Costura, not ILRepack,
deliberately** — ILRepack merges assemblies and would break the cross-assembly `pack://` URIs
(rationale in task_monitor.csproj comments and the FodyWeavers.xml header).

## Project layout (layered, under src/)

All source lives under `src/`, grouped **by layer**; everything else (csproj, slnx, bin/, obj/,
FodyWeavers.xml) stays at the repo root. `assets/` holds the artwork: the logo trio — `logo.ico`
(the exe icon, referenced from the csproj), `logo.svg`/`logo.png` (source artwork; the Settings
关于 page inlines logo.svg as XAML shapes, no raster is embedded) — plus README artwork:
`header.png` (the README banner). All files share the single `task_monitor` namespace — folders are
physical only, so XAML `x:Class`/`xmlns:local` never reference a folder.

```
docs/            gotchas.md · architecture.md · settings-plumbing.md · scroll-stack.md
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
    UpdateAvailableDialog   发现新版本提醒 (iNKORE modern window — 立即更新/不再提醒/稍后)
    Common/                 formatters · ProcessListTip · ProcessIconCache (the shared per-exe icon cache) · ChartTipHost/ChartPalette (the shared chart hover tip + colors) · PivotNavButtonFix · FollowTagPanel · the scroll stack
    Details/                the five IDetailViews
    Charts/                 hand-drawn DrawingContext charts
    Settings/               SettingsWindow (ONE scroll page, 类别 sections via TextBlock headers: 通用 / 外观 / 采样 / 关于 — SettingsCard/SettingsExpander)
```

**App.xaml under `src/`** needs the explicit `<Page Remove>` + `<ApplicationDefinition Include>`
pair in the csproj — it must stay, or the build loses `Main`.

**Adding a metric:** new `XxxSampler` (Sampling/) + `XxxDetailView` (UI/Details/), wired into
`SystemSampler.Sample` / `DetailWindow.ShowColumn` — plus a `Mask*` bit + settings.yaml key + a
采样 card for its sampling switch (SettingsExpander only if the metric has sub-settings — CPU/内存
are plain cards), and one row in [`settings-plumbing.md`](settings-plumbing.md).
