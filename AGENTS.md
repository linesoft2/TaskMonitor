# AGENTS.md

This file provides guidance to AI coding agents when working with code in this repository.

**This file is a MAP, not the manual** — rules and pointers only. The long unwindings live in
`docs/` (loaded only when you touch that area) and at the code site. See
[Maintaining this file](#maintaining-this-file) before adding anything here.

| When you touch… | Read |
|---|---|
| taskbar geometry, boot/explorer-restart, D2D/DXGI, samplers, theme, process rows, 悬浮模式/贴边隐藏/全屏时隐藏, crash & stall forensics | `docs/gotchas.md` |
| anything spanning files: the two threads, cross-thread contract, detail/pin model, startup & elevation, project layout | `docs/architecture.md` |
| a 设置 item's wiring to its sampler | `docs/settings-plumbing.md` |
| any of the six scroll views | `docs/scroll-stack.md` |

## What this is

A Windows taskbar widget: a self-drawn overlay embedded in the taskbar (three stacked groups —
CPU/内存, 磁盘/GPU, 网络 ↑/↓) that pops up a fluent acrylic detail window on click. WPF on .NET
Framework 4.8. **No main window**; the only persistent UI is that overlay — optionally detached
into a free-floating card widget (设置 → 外观 → 悬浮模式, gotchas §36). The process stays alive via
`ShutdownMode="OnExplicitShutdown"`; exit is via the overlay's right-click menu.

## Build & run

```shell
dotnet build -c Debug          # only toolchain on this machine — no VS MSBuild / nuget.exe
bin\Debug\net48\task_monitor.exe
```

SDK-style csproj, `net48`, `UseWPF=true`. No tests.

**A live exe locks the output**, so `dotnet build` fails at the copy step with `error
MSB3021`/`MSB3027` naming `task_monitor.exe (PID)` — that is NOT a compile failure. Stop the
instance first, and always use the sentinel, never `taskkill` (why: `docs/architecture.md`):

```shell
touch bin/Debug/net48/shutdown.sentinel   # 1s tick notices it and exits gracefully
dotnet build -c Debug
bin/Debug/net48/task_monitor.exe &
```

The app always runs **elevated and self-managed**; relaunching from an unelevated shell pops a UAC
prompt on the secure desktop — that one step is the user's. Elevation gate, single-instance mutex,
legacy-OS warning: `docs/architecture.md`.

**UI testing is the USER's job — all of it, not just screenshots.** That includes driving the real
windows (UI Automation, synthetic clicks/drags), screen capture with pixel/colour analysis, and
every judgement about how something looks, animates or feels. The agent's loop is: implement →
`dotnet build` → launch → verify from the **log file and `settings.yaml`** (plus code reading) →
hand the user a short checklist of what to click → wait for their feedback. Do NOT write or run
UI-test harnesses of your own; when something is only observable through the UI, say so and ask.

**Git is READ-ONLY for the agent unless the user asks for a commit in that very turn.** No `git
commit` / `--amend` / `push` / `tag` / `reset` without an explicit request — do the work, leave it in
the working tree, and say it is uncommitted. "Finish the change" (including the docs-sync rule under
[Maintaining this file](#maintaining-this-file)) means *editing* the files, never committing them.

**Editing files here: the shell is Windows PowerShell 5.1 and it is NOT text-safe.**
`Get-Content`/`Set-Content`/`Out-File`/`>` default to the ANSI code page (GBK here) or UTF-16, so
routing a source file through them **double-encodes it and eats characters** — on 2026-09-22 that
destroyed 125 lines of `TaskbarWindow.cs`, and the repair cost a full reconstruction because the work
was uncommitted. **Read and write text ONLY with the UTF-8-safe file tools** (or .NET with an explicit
UTF-8 encoding — `[IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)` is byte-exact, the cmdlet
defaults are not); keep the shell for processes and paths. Snapshot a file before any bulk rewrite.
Recovering: a diff against `git HEAD` reveals damage to **HEAD's** lines only, so "not in the archive"
never proves "we never wrote it".

## 发布 / CI/CD

**GitHub 是主仓库，CNB 是镜像。** `origin` = GitHub（SSH），`cnb` = CNB（HTTPS——CNB **不支持 SSH**，认证 = 固定用户名 `cnb` + 访问令牌）。日常只推 `origin`；`.github/workflows/` 两个 workflow 负责同步与发布（细节看 workflow 本身）：

- `sync-cnb.yml`（main + v* tag）→ `git-sync` 把代码和 tag **force** 推到 CNB（CNB 是纯镜像，从无独有提交）。
- `release.yml`（v* tag）→ 校验 tag 与 csproj `<Version>` 一致（不一致直接 fail）→ `dotnet build -c Release` → `gh release create` → `tools/publish-cnb-release.ps1` 建 CNB release 并上传 `task_monitor-<tag>-x64.exe`。Secret `CNB_TOKEN` 需 repo-code + repo-contents 两个读写 scope。

发布 = **先改 `task_monitor.csproj` 的 `<Version>`**（版本号唯一来源——SDK 据它生成
`AssemblyInformationalVersion`，关于页与更新检测读它），再 `git tag vX.Y.Z && git push origin
vX.Y.Z`，之后全自动。CNB 云端集群全是 Linux 节点跑不了 net48 WPF，所以构建放在 GitHub 托管
Windows runner。`publish-cnb-release.ps1` 的三个坑（勿回退，见脚本注释）：CNB API 必须显式
`Accept: application/json`；PUT/确认必须走 curl.exe（`System.Uri` 会解码 %2F 导致 500）；保持
**UTF-8 BOM**（PowerShell 5.1 会按 GBK 误读）。

## Architecture (the part that spans files)

**Two threads, one process.** ① A native Win32 overlay owned by `TaskbarWindow` on its own STA
message loop (DirectN), embedded with `SetParent`; 3 visual groups → 5 hit slots, geometry DYNAMIC
from the sampling mask; two taskbar families (Win11 `Shell_TrayWnd` / classical Win10 `ReBarWindow32`
band-shrink, restored on exit), plus side-docked and 悬浮模式 forms. ② The WPF UI thread: on-demand
`DetailWindow`s (shell + one `IDetailView`), `SettingsWindow`, the lazily-created right-click menu,
and a startup `Prewarm()`.

**Single source of truth, push not poll:** `TaskbarWindow` owns the **only** `SystemSampler`; popups
read `LatestSnapshot` and have no timer. The tick also carries the stall diagnostics — don't drop
them as dead code.

**Cross-thread contract** (Taskbar→UI callbacks, UI→taskbar `Set*`/`Request*` methods) and the
pinned-mode invariants: `docs/architecture.md`; the 设置→sampler chains: `docs/settings-plumbing.md`.

## Project layout (layered, under src/)

All source is under `src/`, grouped **by layer** (`App.*` · `Logger` · `AppSettings` ·
`VersionInfo` · `UpdateChecker` · `StartupTask` at the top, then `Sampling/` · `Interop/` · `UI/`
with its `Common/` `Details/` `Charts/` `Settings/`); the annotated tree is in
`docs/architecture.md`. Everything else root-level is build plumbing (csproj, slnx, bin/, obj/,
FodyWeavers.xml); `assets/` holds `logo.*` and the README banner. All files share the single
`task_monitor` namespace — folders are physical only, so XAML `x:Class`/`xmlns:local` never
reference a folder.

**App.xaml under `src/`** needs the explicit `<Page Remove>` + `<ApplicationDefinition Include>`
pair in the csproj — it must stay, or the build loses `Main`.

**Adding a metric:** new `XxxSampler` (Sampling/) + `XxxDetailView` (UI/Details/), wired into
`SystemSampler.Sample` / `DetailWindow.ShowColumn` — plus a `Mask*` bit + settings.yaml key + a 采样
card for its sampling switch (SettingsExpander only if it has sub-settings — CPU/内存 are plain
cards), and one row in `docs/settings-plumbing.md`.

## Fluent 2 / iNKORE.UI.WPF.Modern design guidelines

All WPF UI targets **Fluent 2**; before adding any control/layout/icon/interaction, **first check
whether iNKORE.UI.WPF.Modern ships a component for it**. Local checkouts (use these first):
`D:\ai-ref\UI.WPF.Modern` (source + samples), `D:\ai-ref\Documentation`. The project tracks NuGet
**0.10.2.1** and the checkout matches — beware version drift. Quirks, each documented at its site:
TabControl's strip "+" and per-tab "×" default VISIBLE; plain switchers use
`TabControlPivotStyle`/`TabItemPivotStyle` (the default is an opaque TabView lookalike, wrong on
acrylic); the Pivot hit-test flaw (invisible PreviousButton swallows first-tab clicks) needs
`PivotNavButtonFix.Apply`.

**Window families — never mix iNKORE's `UseModernWindowStyle`/`SystemBackdropType` with
FluentWpfCore on the same window:** DetailWindow = borderless FluentWpfCore acrylic (Win10
carve-out: no material, `AllowsTransparency` layered window + self-drawn rounded tint card — §40);
SettingsWindow = iNKORE modern window + Mica; ConsentDialog/LegacyOsWarningDialog/
UpdateAvailableDialog = iNKORE modern window (plain). iNKORE also styles the taskbar right-click menu
(standard `ContextMenu` via `SetResourceReference`, **by key**), not `MenuFlyout` (can't place at the
cursor; NREs unless owned).

## Non-obvious gotchas

The "don't regress" index. **Full unwinding of every entry — the incident, the measurements, the
mechanism — is [`docs/gotchas.md`](docs/gotchas.md), same numbering**; each rule's canonical
statement is a comment at the code site named there. Companion: [`settings-plumbing.md`](docs/settings-plumbing.md),
[`scroll-stack.md`](docs/scroll-stack.md).

**Taskbar / boot**

1. **Classical band is ours to restore** — `RestoreMinWindow` before ANY re-measure of our own, in `WM_DESTROY`, on an orientation flip, and on exit.
2. **Boot/explorer-restart:** `Start()` polls for a real taskbar; ANY return → App re-enters after 2s; the WndProc delegate is a process-lifetime `static readonly` (a per-`Start()` one orphans the thunk → AV).
3. **Nothing escapes `WndProc`** — per-message try/catch → `CrashReporter`; an escaping managed exception is `0xC000041D`.
4. **Global crashes** = log file FIRST, then a code-only `CrashDialog`; a fatal report blocks the dying thread on it.
5. **`Logger`** — `logs/task_monitor-<date>.log`, 7-day retention, never throws; hot per-tick paths never log (`WarnOnce`). Keep the line truthful.
6. **The overlay must never steal focus** — full no-activate set, incl. `SWP_NOACTIVATE` on every `SetWindowPos`; sole exception: 悬浮模式 with 置顶显示 OFF (§36).
7. **`SetWindowPos(HWND_TOPMOST)` no-ops when not foreground** — activate first; `EnsureTopmost` verifies the exstyle bit.
8. **Acrylic** — FluentWpfCore `UseWindowComposition=True` on every WPF window, never hand-rolled Accent P/Invoke (carve-out: DetailWindow on Win10, §40); the native floating overlay has NO material (§36); the menu host needs `WS_EX_TOOLWINDOW`.
44. **竖直（侧边停靠）任务栏与任务栏族无关** — the orientation probe is the taskbar rect's aspect, never `classical` (Win11 26H2's native 左侧/右侧 taskbar is still the Win11 family). Missing it sizes the horizontal grid (~200 DIP) to the 48-DIP column, which covers the WHOLE taskbar and swallows its clicks; a side taskbar always takes the strip stack + the tray-end/top-corner anchor, and a strip narrower than `STRIP_TWO_LINE_MIN_W` renders two-line (label over value).

**Samplers / metrics**

9. **`NetSampler` never enumerates NICs per tick** (~275ms = ~97% of idle CPU).
10. **Live CPU speed = PDH `% Processor Performance` × base clock**, not `CallNtPowerInformation`.
11. **Per-process CPU/RAM/disk = ONE `NtQuerySystemInformation` walk**; the disk column is the 24H2+ trailer, NOT SRUM.
12. **Disk/GPU replicate Taskmgr exactly** (`IOCTL_DISK_PERFORMANCE` / DXCore COM); disk handles are opened and closed per query, never held. On Win10 fetch the base `IDXCoreAdapter` + all four attribute passes; capability gates — never OS-version checks — drive the fallbacks.
13. **Per-process GPU% is PDH, not DXCore** — encoding and aggregation at `ProcessGpuSampler.cs`; no elevation, unlike SRUM.
14. **Process-row tooltip = view-owned `Popup` on `MouseMove`**, never a row `ToolTip`.
15. **Per-process net = SRUM real-time API** (why we elevate); rates re-diff only when the frame version advances. **Unregister on EVERY rebuild** (`ProcessNetSampler.Shutdown`, from `WM_DESTROY`) — srumapi holds a raw pointer to the delegate stub (the 2026-09-22 卡死).
16. **Wi-Fi `wlanapi` is on-demand only** — never on the per-tick path.
17. **Memory breakdown = `GlobalMemoryStatusEx` + NTQSI class-2/class-80**, not `GetPerformanceInfo`.
18. **Charts are hand-drawn WPF** — hover = `HitTest` + a real `Popup`.
19. **Scroll stack** — `Style="{DynamicResource {x:Type ScrollViewer}}"` on every scroller, or it silently falls back to the Aero bar.
20. **Idle trim at event points only, never on a timer** — never burst, never `WaitForPendingFinalizers` (`TrimMemory`: 30 s rate limit, one plain `GC.Collect`).
21. **开机自启动 = a logon scheduled task, never the Run key** (XML-registered, not schtasks switches).

**UI / theme / settings**

22. **深浅色 = `ThemeManager.Current.ApplicationTheme`**; never `GetActualTheme(window)` on DetailWindow; the native overlay tracks the system theme separately.
23. **Never `<StaticResource …/>`-alias a theme brush in `Window.Resources`** — it freezes the startup scheme.
24. **采样间隔 is runtime state** — rate samplers normalize over REAL elapsed time, never "1 tick = 1s".
25. **Sampling mask ≠ pinned-window click mask** — unpinning must not re-enable a sampling-disabled slot (toggle posts `WM_APP_SET_METRICS`; enable primes baselines).
26. **合并相同程序 merges BEFORE the top-8 cut**; svchost.exe is exempt.
27. **svchost→服务命名 runs AFTER the merge**; the hover 描述 is resolved lazily and cached per service.
28. **磁盘/GPU 显示方式** — the mode picks only the headline; every device is queried each tick, and a mode/index change clears the history.
29. **网络适配器** — the virtual-adapter filter does NOT apply to an explicit pin; absent/down → 自动, re-probed every 30 ticks.
30. **公网 IP off kills ALL public traffic** (lookups + the ICMP probe); the LAN gateway ping is unaffected.
31. **Clash/Mihomo** — `req.Proxy = null`; rows appended standalone and exempt from merging; `null` endpoint = poller asleep.
32. **DISPOSE back-buffer wrappers before `ResizeBuffers`** — they live on `RenderState`, never in locals.
33. **更新检测** — csproj `<Version>` is the only version source; CNB reads the WEB 307 redirect; 不再提醒 skips only that version.
34. **net48's `Run.Text` is not a dependency property** — a `{Binding}` on a `Run` throws at startup; set named Runs in code.

**Floating widget / rendering / forensics**

35. **A lost D3D device is recoverable, never a crash** — only the raw `EndDraw`/`Present`/`ResizeBuffers` HRESULTs can tell (DirectN's helpers lose the code as E_FAIL): latch `RenderState.DeviceLost`, rebuild in place, release DComp with `Marshal.FinalReleaseComObject`.
36. **悬浮模式 = the same window, detached** — no `SetParent`, the taskbar keeps no copy, and the body is its OWN drawn card (popup tint, no blur, no system frame: radius from the window region, 1px outline drawn by us last); creation-time state, so a flip rebuilds the window; **the drag moves the pair, the dodge separates it**. Long and load-bearing — read gotchas §36 first.
37. **A native crash is invisible to every managed handler** — `CrashTrace`'s VEH is the only record; don't add a WndProc handler for one. A WER **reflection** is not a second instance. Crashless stalls → `CheckOverlayHealth` + the phase-name ring + `SystemSampler.Timed`.
38. **贴边隐藏 — the dock is DERIVED from the home, the slide owns the position** — a drop within 8 DIP of the work area's 左/右/上 edge docks and slides out (8-DIP strip; hover peeks; mouse-out re-hides); yaml stores only switch + home; the slide and a held button suspend `FloatingReposition`; a slide never reports `FloatingDragged` nor writes the home; the peek arms only after the cursor demonstrably left.
39. **全屏时隐藏 — the probe owns VISIBILITY, never the position** — per-tick, on DWM extended-frame bounds (a MAXIMIZED window never counts); `SW_HIDE`/`SW_SHOWNOACTIVATE` transitions only, while tick, heartbeat and sampling keep running; a held button defers the hide; the dodge is suppressed while hidden.
40. **Win10 的 DetailWindow 没有亚克力** — `MaterialMode=None` + `AllowsTransparency` 分层窗口 + `RootBorder` 自绘圆角卡片；`ShowColumn` 末尾补 `InvalidateVisual` + `SWP_FRAMECHANGED`。
41. **Prewarm 永不抛出，`_stopping` 时跳过；启动时在 mutex 之后清残留 sentinel**。
42. **经典任务栏(Win10)下覆盖层自画不透明底色** — 被 `WS_CLIPCHILDREN` 冻住的残影擦不掉、只能遮盖（`CardBrush` alpha 恒 1 全幅填充）；底色每 tick 采样 `SampleTaskbarBackdropColor`（3-tick 稳定期）；Win11 保持全透明。
43. **窗口死后消息循环不能被晾在空队列上；僵尸卡死要取证+自愈** — 裸 `WM_NCDESTROY` 也走 `TeardownOverlay` + `PostQuitMessage`；对别人窗口的同步调用经 worker 有界等待（`SetParent` 例外）；5s 轻唤醒 + 45s 自愈重启。

## Useful external references

- iNKORE.UI.WPF.Modern: the local checkouts above; github.com/iNKORE-NET/UI.WPF.Modern.
- **SRU real-time API:** reversed from `Taskmgr.exe` in `C:\Users\l\SRUM-RealTime-API.md` — the canonical reference for `ProcessNetSampler` / `SrumInterop`.

## Maintaining this file

**Keep it in sync with the code — update it in the same change, not later.** Stale guidance is worse
than none; fix or delete any claim that no longer matches the code on the spot.

**Route each fact to exactly one place, and point at it elsewhere** (a fact held in two prose copies
WILL drift — it already happened once, to the 合并相同程序 wording):

| The fact is… | Goes in |
|---|---|
| a rule an agent must not break, expressible in 1–2 lines | the gotcha index above |
| the reasoning, incident history, or multi-step mechanism behind that rule | `docs/gotchas.md` |
| the cross-file design (threads, contract, detail/pin model, startup, layout) | `docs/architecture.md` |
| how a 设置 item reaches its sampler | `docs/settings-plumbing.md` |
| the design of one module used from many places | a topic doc in `docs/` (e.g. `scroll-stack.md`) |
| derivation, calibration constants, offsets, reversed layouts | **the code site's own comment** — link to it, never copy it |
| what the code already says plainly (signature, field name, obvious branch) | **nowhere** |

Keep this file a *map*: the gotcha index (one line per rule) is its payload — **everything around it
must stay under ~10 KB of prose**. Text that does not fit belongs in a doc, not here.
New gotcha → a numbered line here + a matching `## N.` section in `docs/gotchas.md`.
