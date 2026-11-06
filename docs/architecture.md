# Architecture — the part that spans files

Loaded on demand from [`AGENTS.md`](../AGENTS.md). The rules an agent must not break stay in
AGENTS.md's gotcha index; this file carries the unwinding behind them, plus the startup,
packaging and layout facts that would otherwise sit in the map. When this file and the code
disagree, the code wins — fix this file.

## One process: UI + sampling + N overlay threads

- **SamplingController** — a dedicated STA thread owns the only `SystemSampler` and the latest
  snapshot. It never belongs to an overlay HWND, so display changes and Explorer restarts do not
  re-register SRUM or reset sampling history. It is retired only after a sampling failure or on exit.
- **TaskbarOverlayManager** — reconciles the selected displays into one `TaskbarWindow` + STA
  thread + native HWND per target. `TaskbarDisplayCatalog` maps stable monitor interface IDs to
  the current `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`; display events plus a low-frequency rescan
  cover hotplug, primary changes and shell changes. A selected secondary never silently falls back
  to primary; only an entirely unusable selection gets the temporary primary fallback.
- **Taskbar overlay** — each target has a native Win32 window rendered with DirectN and embedded
  into ITS taskbar via `SetParent`. `Start(target)` re-resolves the display's current taskbar on
  every rebuild. 3 visual groups → **5 hit slots** (0=CPU 1=内存 2=磁盘 3=GPU 4=网络); `WndProc`
  does the 2D hit-test (left-click toggles a popup, right-click the menu). Slot geometry is
  DYNAMIC (`ComputeLayout` from the sampling mask): the visible stacked metrics pack into the
  two-row grid in their fixed order — a hidden metric leaves no hole (空缺补齐; an odd count's
  last row, and the trailing hole 任务栏显示 两者 pads before its GPU block, may stay empty), an
  emptied group frees its width and the window
  shrinks (`ResizeForLayout`; slot IDs stay stable). 任务栏显示 两者 packs GPU usage+temperature
  as ONE 网络-style two-row block (a dedicated cell, single "GPU" label vertically centred;
  the vertical flow keeps the pair in one column, the narrow two-line strip draws one
  label+two-values block). **Two taskbar families**, detected per
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
    EITHER family's overlay into a COLUMN GRID (`ComputeVerticalLayout` / `DrawVertical` /
    `HitTestSlot`); the probe is the taskbar rect's aspect, never `classical` (getting that wrong
    is the "覆盖层盖满整条任务栏、任务栏点不动" incident — gotchas §44). The bar's THICKNESS picks the
    form: one ~48-DIP column below `STRIP_TWO_LINE_MIN_W` becomes padded label-over-value blocks
    (44 DIP per row; the padding is what keeps a label with its own value — gotchas §44), a
    70–139-DIP bar keeps one one-line column, and 26H2's WIDENED vertical bar (320px @200% =
    160 DIP) flows the packed rows into two or more 70/84-DIP columns (the row flow is
    `ComputeVerticalLayout`; arithmetic, rounding and the 2026-10-17 incident: gotchas §44).
    紧凑模式 (设置 → 外观, `CompactLayout`) tightens every form's pitch from the horizontal
    cells to these row heights — one `compact` argument threaded through every
    `ComputeLayout`/`ComputeTargetSize` call, values and their text-fit floors documented at
    the constants block in `TaskbarWindow` (plumbing: settings-plumbing §10). The
    Win11 family anchors along the taskbar's axis (`CalcPositionVertical`: tray end by default,
    the top corner / above Start when 靠左显示) since it has no band to shrink. `DetailWindow`
    anchors its popup to the taskbar's screen edge (`GetTaskbarEdge`), and an orientation flip
    re-docks live at runtime (`ReconfigureOrientation`).
  - Lifetime: `Start()` waits for the target taskbar (cancellable). ANY return makes the manager's
    per-target loop re-resolve and re-enter after 2s; deselected/superseded entries cannot revive
    themselves (gotchas §2). Exit stops and joins every overlay before stopping the sampler.
  - **悬浮模式 (设置 → 外观)**: ONE window class instance, layout, drawing and shared snapshot,
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

`App` owns the controller and manager; UI callbacks hop through `Dispatcher.BeginInvoke`:

- Overlay→UI: `Action<TaskbarWindow,int> ToggleCallback` (the CLICKED source + slot),
  `RightClickRequested`, floating position/drag callbacks, and `AnchorRemoved`. The source is
  captured per entry; stale entries are ignored.
- Sampling→overlays/UI: controller publishes one `SnapshotChanged`. The manager calls
  `PublishSnapshot` for every overlay (each applies it on its own HWND thread over
  `WM_APP_SNAPSHOT`), while App refreshes the detail/settings views exactly once.
- UI→manager: visual/layout settings are broadcast to every overlay; sampler settings go to the
  controller once. Floating settings go only to the single floating entry. Pin click masks are
  broadcast, while select/deselect and popup placement use the clicked source.
- Per-window native state remains in `RenderState` (`GWLP_USERDATA`). Guarded foreign-window
  calls have per-calling-thread channels; CrashTrace steps/message rings are also per native
  thread. `SetParent` remains inline on the owning overlay thread (gotchas §43).

## Single source of truth (push, not poll)

`SamplingController` owns the **only** `SystemSampler` (1s default, configurable 0.5/1/2s).
`SystemSnapshot` is published by reference with `SampleIntervalMs` stamped on it; detail windows
read the controller, never run their own sampling timer. A metric switched off skips its samplers,
hides every overlay's corresponding slot and primes baselines when re-enabled. Initial overlays
render a safe empty snapshot if the first sample has not arrived; they never sample on their own.

Sampling and every overlay have independent `LastTickTickCount` heartbeats. App's watchdog checks
each expected thread, excluding cancelled entries and those legitimately waiting for a taskbar.
`SystemSampler.Timed` still names slow sampler steps; `CrashTrace` and native stacks identify the
specific stalled thread. SRUM unregistration belongs to sampler retirement, never HWND teardown.

## DetailWindow = shell + per-metric view

Each window gets a `SamplingController` for data/Wi-Fi and an optional `TaskbarWindow` anchor for
placement and selection. Prewarm has no anchor. Removing a display closes its transient flyout;
a pinned window remains live on controller data but detaches the dead anchor and disables pinning.

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
- While pinned, that column is **press-disabled on every overlay** (re-enabled on unpin AND close)
  and deselected on the anchor; unpin re-selects it. `Window_Closing` requests a column-aware
  deselect so a newer selection is never wiped. There is still at most one pinned window per column
  globally, not one per display.

## Startup: elevation gate → single-instance guard → first-run guide

**The app always runs elevated, self-managed** (manifest `asInvoker`): `App.RunElevationGate`
(src/App.xaml.cs) runs at the top of `OnStartup`, BEFORE the single-instance mutex — an exiting
unelevated launcher must never hold the mutex, or the elevated child takes itself for a second
instance. Unelevated + consented → `runas` self-relaunch; never asked → `ConsentDialog` (允许
persists `elevationConsent: true`); 不允许/UAC-cancel exits — the process never runs degraded
(SRUM per-process net needs admin). Consent persists to **`settings.yaml` in the exe directory**
(`AppSettings`, YamlDotNet — the single store for ALL settings; the file + `.tmp`/`.bad` siblings
are runtime artifacts). The manifest declares PerMonitorV2 and `App`'s static constructor
enables WPF's per-window DPI scaling before any window exists (the release ships no `.config`),
so `SettingsWindow` and every `DetailWindow` follow their own monitor.
Each native overlay STA also explicitly opts into PerMonitorV2 for its `TaskbarWindow.Start`
lifetime, keeping D2D text, monitor rects and physical-pixel coordinates correct on mixed-DPI
displays. Exercising the consent dialog needs an unelevated launch (`explorer.exe
"...task_monitor.exe"`); the UAC prompt lives on the secure desktop and cannot be automated.

**Single-instance:** a named `Global\` mutex right after the gate
(`Global\TaskMonitor.exe__<guid>`, un-owned, existence test only; `Global\` so the guard holds
across sessions — reachable only elevated, so `SeCreateGlobalPrivilege` is in hand). The matching
auto-reset event is created BEFORE the mutex, closing the startup race: any second process that can
see the mutex can signal the running instance without losing the notification. The second process
then exits; the first shows one `AlreadyRunningDialog` (repeat signals re-activate it) listing each
overlay's live display, placement and screen coordinates. Floating locations resolve their display
from the home point, so an edge-hidden widget is not attributed to its neighbour; hidden/rebuilding
states are labelled rather than guessed. A consented second launch still UAC-prompts first —
inherent to the design.

**First-run guide:** after the mutex/sentinel guard and BEFORE `StartTaskbar`,
`StartupGuideWindow` (iNKORE modern window + Mica) lets a new user choose 悬浮窗 or 任务栏窗口,
then the taskbar's 最左端 / 开始按钮旁 / 靠近系统托盘 anchor. The cards' diagrams are XAML
vector shapes and follow the app theme; 开始按钮旁 is disabled when `SupportsSnapToStart` finds
no compatible Win11 taskbar. Completing writes the existing `floatingMode` / `overlayOnLeft` /
`overlaySnapToStart` keys before the first overlay is created; skipping keeps the defaults, and
both paths write `startupGuideCompleted: true`. `AppSettings.IsNewInstall` is runtime-only and
true when no/empty settings file existed at load. A fresh unelevated consent writes
`startupGuideCompleted: false` so the elevated relaunch still shows the guide; an existing
(legacy) config with no such key never gets a surprise guide after an upgrade.

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
  App.xaml(.cs)    entry: elevation gate → mutex → first-run guide → controller/manager; prewarm; menu host; idle GC
  Logger.cs        file log — logs/task_monitor-yyyy-MM-dd.log next to the exe (gotchas §5)
  AppSettings.cs   settings.yaml store — every key documented per-property in the file
  VersionInfo.cs   版本号读取口（读 SDK 按 csproj `<Version>` 生成的 AssemblyInformationalVersion）
  UpdateChecker.cs 启动时检查更新（gotchas §33）
  StartupTask.cs   开机自启动 logon task (schtasks /XML) — the task itself is the state (gotchas §21)
  Sampling/        per-metric samplers + SystemSampler facade + SamplingController (unique owner) + ServiceHostMap
  Interop/         P/Invoke boundary per native API (each file's header says what it covers)
  UI/
    TaskbarWindow.cs        one overlay + WndProc + per-HWND RenderState (no sampler ownership)
    TaskbarOverlayManager.cs selected-display entries, rebuild, settings broadcast, exit joins
    TaskbarDisplayCatalog.cs monitor IDs ↔ current taskbar HWNDs
    DetailWindow.xaml(.cs)  acrylic shell hosting one IDetailView
    ConsentDialog           first-run UAC-consent prompt (iNKORE modern window, unelevated)
    AlreadyRunningDialog    二次启动通知（iNKORE modern window — 各覆盖层的实时位置）
    StartupGuideWindow      first-run display-mode/placement cards with vector diagrams (iNKORE + Mica)
    UpdateAvailableDialog   发现新版本提醒 (iNKORE modern window — 立即更新/不再提醒/稍后)
    Common/                 formatters · ProcessListTip · ProcessKillMenu (the process lists' right-click 结束进程: list-level menu + confirm card, same rebind-proof pattern as ProcessListTip) · ProcessIconCache (the shared per-exe icon cache) · ChartTipHost/ChartPalette (the shared chart hover tip + colors) · PivotNavButtonFix · FollowTagPanel · the scroll stack
    Details/                the five IDetailViews
    Charts/                 hand-drawn DrawingContext charts
    Settings/               SettingsWindow (iNKORE NavigationView 类别 shell: 通用 / 外观 / 采样项目 + pane-footer 关于 — one visibility-switched TouchDragScrollViewer page each; the categories are SettingsCard/SettingsExpander, 关于 is a centred hero page — logo/name/version pill + live CPU waveform + link buttons, no cards)
```

**App.xaml under `src/`** needs the explicit `<Page Remove>` + `<ApplicationDefinition Include>`
pair in the csproj — it must stay, or the build loses `Main`.

**Adding a metric:** new `XxxSampler` (Sampling/) + `XxxDetailView` (UI/Details/), wired into
`SystemSampler.Sample` / `DetailWindow.ShowColumn` — plus a `Mask*` bit + settings.yaml key + a
采样 card for its sampling switch (SettingsExpander only if the metric has sub-settings — CPU/内存
are plain cards), and one row in [`settings-plumbing.md`](settings-plumbing.md).
