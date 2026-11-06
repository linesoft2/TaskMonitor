# Settings plumbing

Every settings.yaml key reaches its sampler through the SAME chain. Learn the pattern
once and each section below only lists that setting's **differences** — do not re-derive
the chain per setting.

## The chain

```
AppSettings (settings.yaml, YamlDotNet)
  → App reads it at startup and on every settings-page change
  → TaskbarOverlayManager (layout broadcast) and/or SamplingController (one sampler setting)
  → SystemSampler volatile field         (single writer = UI thread, reader = sampling thread)
  → per-tick hand-off to the sampler     (SystemSampler.Sample)
  → re-applied only when the controller reconstructs a failed sampler
```

Conventions that hold for all of them:

- **null = the default**, and only the non-default state is written to yaml. A missing
  key must behave exactly like the documented default.
- **Re-apply belongs to the owner.** New overlays inherit layout state from the manager;
  only a sampler failure makes the controller replay sampler configuration. Overlay/Explorer
  rebuilds no longer construct a fresh `SystemSampler`.
- **`WM_APP_SET_METRICS` only when the OVERLAY LAYOUT changes** — i.e. the sampling mask
  (§2) and the GPU 任务栏显示 content (§4: its row count, and so the width, changes with
  it). Everything else takes effect on the next tick — posting a WM_APP for a
  non-layout setting is pointless churn.
- Each sampler's calibration/derivation lives at its own code site; this file only
  records the wiring. Field-by-field yaml semantics are in `src/AppSettings.cs`.

---

## 1. 合并相同程序 — `MergeSamePathProcesses` (null = on)

| | |
|---|---|
| Setter | `TaskbarWindow.SetMergeSamePathProcesses(bool)` |
| Sampler state | `SystemSampler.SetMergeByPath` (volatile, next tick) |
| WM_APP | **no** — the overlay layout does not change |

Handed each tick to the five per-process samplers, which merge same-exe-path rows
(`ProcessListMerger.MergeByPath`) **before** their top-N cut. Semantics and the svchost
exemption: [`gotchas.md` §26](gotchas.md#26-合并相同程序-merges-before-the-top-8-cut).

## 2. 采样 (per-metric switches) — `{Cpu,Ram,Disk,Gpu,Net}SamplingEnabled` (null = enabled)

| | |
|---|---|
| Setter | `TaskbarWindow.SetMetricSamplingMask(int)` → `SystemSampler.SetEnabledMask` |
| Sampler state | `SystemSampler._enabledMask` (volatile, next tick) |
| WM_APP | **YES — `WM_APP_SET_METRICS`**: the slot is hidden and the overlay re-lays-out/resizes |

App converts the five nullable booleans to/from one int mask (`SamplingMaskOf`), bit order
= overlay hit-slot order (`SystemSampler.MaskCpu` … `MaskNet`). This mask is **separate**
from `TaskbarWindow._clickDisabledMask` (pinned-window press suppression) — unpinning must
never re-enable a sampling-disabled slot. Details, including the re-enable baseline
priming: [`gotchas.md` §25](gotchas.md#25-per-metric-sampling-switches-use-a-mask-separate-from-the-pinned-window-click-mask).

## 3. 磁盘 / GPU 显示方式 — `DiskDisplay` + `DiskDisplayIndex`, `GpuDisplay` + `GpuDisplayIndex`

| | |
|---|---|
| Setter | `TaskbarWindow.SetDiskDisplay(mode, index)` / `SetGpuDisplay(mode, index)` |
| Sampler state | `SystemSampler` volatile mode+index pairs |
| WM_APP | **no** — the layout does not change |

Per-tick hand-off to `DiskSampler.Sample(mode, index)` / `GpuSampler.Sample(mode, index,
tempIndex, pdhEngines)` (the last argument is ProcessGpuSampler's per-(adapter, engine) PDH
map — the Win10 utilization/name fallback, gated by `GpuSampler.NeedsPdhEngineData`,
gotchas §12; `tempIndex` belongs to §4 below). The samplers query every device each tick
regardless; the mode only selects the headline,
and a mode/index change **clears the history** so the chart never mixes semantics. Defaults
and the missing-device fallback: [`gotchas.md` §28](gotchas.md#28-磁盘gpu-显示方式).

## 4. GPU 温度（任务栏显示 / 温度来源 / 温度单位）— `GpuTaskbarContent` + `GpuTempIndex` + `GpuTempUnit`

| | |
|---|---|
| Setter | `TaskbarWindow.SetGpuTempDisplay(content, tempIndex, fahrenheit)` → `SystemSampler.SetGpuTempConfig(index, fahrenheit)` |
| Sampler state | `SystemSampler` volatile index+unit; the CONTENT is TaskbarWindow layout state (`_gpuTaskbarContent`, read by every `ComputeLayout`) |
| WM_APP | **YES — `WM_APP_SET_METRICS`**: slot 3's form changes with the content — 温度 swaps the utilization row for a temperature row, 两者 packs BOTH rows as ONE 网络-style two-row block (usage over temperature, the single "GPU" label vertically centred; a dedicated cell, with the horizontal grid padding a trailing hole before it — same width as the old two stacked rows). ResizeForLayout is a no-op when the width didn't move, so the source/unit combo changes reuse the same message harmlessly |

Temperature itself is already sampled every tick for every adapter (DXCore
`AdapterTemperatureCelsius`, driver-gated — the detail popup has always shown it); this
chain only adds the numeric headline (`GpuSample.TempValid/TempCelsius`, always Celsius)
and the taskbar row. `tempIndex` = -1/`null` picks the HOTTEST adapter (mirroring the
utilization's 最高利用率 default); a specific "GPU N" that is absent falls back to the max,
while a present-but-unsupported adapter stays "--". The unit is display-side only —
`GpuTempText.Format` is the ONE formatter for the taskbar row and the detail popup's
per-adapter texts alike. Orthogonal to: `GpuSamplingEnabled` (采样 gates the data, this
gates the row) and `GpuDisplay` (显示方式 still picks the utilization headline). Layout
consequences (5 packed positions, 4 groups, both rows are hit slot 3):
`TaskbarWindow.ComputeLayout` and its ASCII table.

## 5. 网络适配器 — `NetAdapterId` + `NetAdapterName` (null = 自动)

| | |
|---|---|
| Setter | `TaskbarWindow.SetNetAdapter(id)` |
| Sampler state | `SystemSampler` volatile id |
| WM_APP | **no** |

Per-tick `NetSampler.Sample(id)`. The picker's items are enumerated by App when the
settings window opens (`App.EnumerateNetAdapters`, non-loopback, sorted) — a one-time
~275ms cost that must stay off the per-tick path. Pinned-adapter semantics:
[`gotchas.md` §29](gotchas.md#29-网络适配器).

## 6. 公网 IP — `PublicIpEnabled` (null = on, 仅写关闭态)

| | |
|---|---|
| Setter | `TaskbarWindow.SetPublicIpLookup(bool)` |
| Sampler state | `SystemSampler` volatile → per-tick `NetInfoSampler.PublicIpLookupEnabled` |
| WM_APP | **no** |

Gates the what-is-my-ip HTTP lookups and the 公网延迟 ICMP probe. Off drops the cached
address and resets the next-try timestamp; re-enabling fetches immediately rather than
waiting out the cadence. [`gotchas.md` §30](gotchas.md#30-公网-ip-开关).

## 7. Clash/Mihomo — `ClashEnabled` (null = on) + `ClashApiAddress` + `ClashApiSecret`

| | |
|---|---|
| Setter | `TaskbarWindow.SetClashApi(enabled, address, secret)` |
| Sampler state | `SystemSampler` volatile triple |
| WM_APP | **no** |

Per-tick `ClashSampler.SetEndpoint(...)`. **`null` on the sampler means "off"**, and BOTH
the switch being off and network sampling being off call `SetEndpoint(null)` so the poll
thread sleeps completely. A null/empty address is replaced by `ClashSampler.DefaultAddress`
(127.0.0.1:9090) at the SystemSampler hand-off, so a stock core works with no setup.

Settings card behavior: the switch reports immediately; the two TextBoxes report through a
500ms single-shot debounce (a yaml write + sampler retarget must not fire per keystroke);
**测试连接** is a separate one-shot `GET /version` probe on `Task.Run` (never the UI
thread). Full design: [`gotchas.md` §31](gotchas.md#31-clashmihomo-代理流量).

## 8. 更新检测 — `UpdateCheckEnabled` + `UpdateSource` + `IgnoredUpdateVersion`

Not part of the sampler chain: `UpdateChecker.CheckOnce` runs **once at startup** from
`OnStartup`, so a change takes effect on the next launch (no live plumbing). The one exception
is a *pending retry*: after a network-level failure the single retry 5 minutes out re-reads the
live config, so switching 检查更新 off cancels it and a 更新源 change is honored — nothing to
wire up, it reads the same `AppSettings` instance. See
[`gotchas.md` §33](gotchas.md#33-更新检测).

## 9. 悬浮模式 — `FloatingMode` + home + topmost/out-of-bounds/edge/fullscreen/opacity

Not a sampler chain. `TaskbarOverlayManager` stores the complete floating configuration and
applies it to the SINGLE floating entry; entering floating removes all embedded entries first.

| | |
|---|---|
| Setter | `TaskbarOverlayManager.SetFloatingMode(on, int? x, int? y)` and the other `SetFloatingXxx` |
| Window state | `_floating` / `_floatingHomeValid` / `_floatingX`/`_floatingY` / `_floatingAllowOutOfBounds` / the existing topmost, edge, fullscreen, dark and opacity fields |
| WM_APP | `WM_APP_SET_FLOATING` (creation-time form flip), `WM_APP_UPDATE_FLOATING` (live style), `WM_APP_SET_FLOAT_KEEPOUT`, `WM_APP_SET_FLOAT_EDGE_HIDE`, `WM_APP_SET_FLOAT_FULLSCREEN_HIDE`, `WM_APP_SET_FLOAT_OUT_OF_BOUNDS` |

- **`FloatingMode`** (null = off): the manager closes every embedded overlay and creates one
  floating window; turning it off recreates exactly the selected embedded set.
- **`FloatingX`/`FloatingY`** (null = never dragged): a nullable coordinate PAIR, not a `-1`
  sentinel. Negative values are valid on left/top monitors and for out-of-bounds homes. Start
  chooses the work area nearest the saved home rather than always using the primary taskbar.
  Drag end and disabling out-of-bounds report home through `FloatingPositionChanged`; a dodge
  or slide never writes it.
- **`FloatingTopmost`** (null = on): live; changes the topmost and (for the ordinary non-topmost
  form) activation bits, with the tick re-asserting the band.
- **`FloatingAllowOutOfBounds`** (null = off): live through
  `SetFloatingAllowOutOfBounds` → `WM_APP_SET_FLOAT_OUT_OF_BOUNDS`. ON skips the ordinary
  creation/drag/reposition `ClampFloatingToWorkArea`; OFF clamps immediately and normalizes
  live position, home and YAML once. DetailWindow keeps its own work-area clamp, and the
  edge-hide/peek derived positions remain edge-relative.
- **`FloatingEdgeHide`** (null = off): only left/right/top, using ABSOLUTE distance ≤8 DIP, so
  a far out-of-bounds negative coordinate cannot masquerade as an edge drop. The 8-DIP strip,
  slide/peek ownership and arming rules remain unchanged (gotchas §38).
- **`FloatingFullscreenHide`** (null = on): visibility only, using the fullscreen window on the
  widget's own monitor; it never owns or clamps position (gotchas §39).
- **`FloatingOpacity`** (null = 1.0; 0.2–1.0): brush alpha for the self-drawn card, not layered
  window alpha. Applied live per slider step; App debounces only the YAML write.
- **`FloatingDark`** is effective APP theme state, not a yaml key. Embedded overlays follow the
  taskbar's system theme instead.

Full mechanism and the creation-time rules: [`gotchas.md` §36](gotchas.md#36-悬浮模式--the-same-window-detached).

## 10. 紧凑模式 — `CompactLayout` (null = off)

A TaskbarWindow-layout chain, not a sampler one — like §4's content, the value is read by
every `ComputeLayout` call (it threads through `ComputeTargetSize`, Draw, HitTest and the
UI-thread layout accessors as the trailing `compact` argument):

| | |
|---|---|
| Setter | `TaskbarWindow.SetCompactLayout(bool)` |
| Taskbar state | `_compactLayout` (volatile; **no sampler state** — pure geometry) |
| WM_APP | **YES — `WM_APP_SET_METRICS`**: the item pitch changes with it, so the window resizes + redraws at once (`ResizeForLayout` reuses the 采样-toggle dance) |

Tightens the item pitch of EVERY form — taskbar overlay and floating widget alike:
horizontal cells 70/84 → 65/78 DIP (row inset 5 → 3), vertical multi-column grid row
24 → 18 DIP, two-line block 44 → 40 (its pad 4 → 2), and — inside the multi-row strip
blocks — GPU(两者)'s three lines and 网络's ↑/↓ four stack at natural line heights as a
centred cluster instead of span-derived bands (row gaps ~8–10 → ~1.2 DIP; the plain
two-line blocks keep the derived pair). The single-column one-line strip keeps its
16-DIP pitch (already the compact form). The floors of those values are text-fit
measurements at 13px Segoe UI (the widest row is 内存 + 100% = 57.7 DIP; the widest net
value a 10-Gbit NIC's "1192.1 MB/s" ≈ 71.3 DIP) — derivation and constants: the compact
block at the top of `TaskbarWindow`'s layout constants. Settings card (设置 → 外观,
never gated on 悬浮模式 — it applies to both forms) **and** a checkable right-click
menu item, both funnelling into `App.OnCompactLayoutChanged` (which also drops the
transient flyout, like a placement change — its anchor column moved).

## 11. 任务栏显示器 — `TaskbarDisplays`

Not a sampler setting. `TaskbarDisplayCatalog` enumerates every active monitor with
`EnumDisplayMonitors` + `MONITORINFOEX`, obtains its interface ID/name with
`EnumDisplayDevicesW`, and associates current primary/secondary taskbar HWNDs through
`MonitorFromWindow`.

- **Yaml shape:** a list of `{ id, name }`; never an enum index, HWND, HMONITOR or rect. null =
  legacy/default “current primary”. A user change saves the COMPLETE checked set. Disconnected
  entries remain visible in settings and are not deleted; they become active again on return.
- **UI:** `SettingsWindow` builds one `SettingsCard` + `CheckBox` per display (plus disconnected
  saved entries). The last checked item cannot be unchecked. `CatalogChanged` → `RefreshDisplays`
  rebuilds that list live on hotplug/taskbar changes without writing config or resetting selection.
  **Assign a NEW `Items` list, never Clear/Add the old one**: `SettingsExpander`'s `ItemsRepeater`
  only observes replacement, not plain `List<object>` mutations. Cards without a current taskbar
  explain the Windows prerequisite; floating mode disables the expander.
- **Runtime:** `App.OnTaskbarDisplaysChanged` → `TaskbarOverlayManager.SetDisplaySelection` →
  reconcile. Every valid selected target gets its own STA/HWND/render pipeline and the same
  layout/snapshot. If no selected target is currently usable, primary is used temporarily WITHOUT
  rewriting the saved set. A selected secondary never falls back to primary individually.
- **Topology:** `SystemEvents.DisplaySettingsChanged` is only a debounced hint; the 2s catalog
  rescan also covers delayed secondary taskbars and Explorer changes. Entries are compared by
  stable ID and current HWND/HMONITOR association, cancelled entries cannot restart, and every
  fresh entry inherits the manager's complete visual configuration.

## Not in settings.yaml at all

**开机自启动** — the scheduled task itself is the state (`src/StartupTask.cs`). Never add a
key for it.
