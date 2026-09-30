# Settings plumbing

Every settings.yaml key reaches its sampler through the SAME chain. Learn the pattern
once and each section below only lists that setting's **differences** — do not re-derive
the chain per setting.

## The chain

```
AppSettings (settings.yaml, YamlDotNet)
  → App reads it at startup and on every settings-page change
  → TaskbarWindow.SetXxx(...)            (UI thread; marshals to the taskbar thread)
  → SystemSampler volatile field         (single writer = UI thread, reader = taskbar tick)
  → per-tick hand-off to the sampler     (SystemSampler.Sample)
  → re-applied to every FRESH sampler in TaskbarWindow.Start()
```

Conventions that hold for all of them:

- **null = the default**, and only the non-default state is written to yaml. A missing
  key must behave exactly like the documented default.
- **`Start()` re-apply is mandatory.** An explorer restart recreates the overlay and a
  brand-new `SystemSampler`; without the re-apply the recreate path silently reverts to
  defaults (that is how a disabled metric would come back to life).
- **`WM_APP_SET_METRICS` only when the OVERLAY LAYOUT changes** (i.e. only the sampling
  mask, §2). Everything else takes effect on the next tick — posting a WM_APP for a
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

Per-tick hand-off to `DiskSampler.Sample(mode, index)` / `GpuSampler.Sample(mode, index, pdhEngines)`
(the third argument is ProcessGpuSampler's per-(adapter, engine) PDH map — the Win10
utilization/name fallback, gated by `GpuSampler.NeedsPdhEngineData`, gotchas §12).
The samplers query every device each tick regardless; the mode only selects the headline,
and a mode/index change **clears the history** so the chart never mixes semantics. Defaults
and the missing-device fallback: [`gotchas.md` §28](gotchas.md#28-磁盘gpu-显示方式).

## 4. 网络适配器 — `NetAdapterId` + `NetAdapterName` (null = 自动)

| | |
|---|---|
| Setter | `TaskbarWindow.SetNetAdapter(id)` |
| Sampler state | `SystemSampler` volatile id |
| WM_APP | **no** |

Per-tick `NetSampler.Sample(id)`. The picker's items are enumerated by App when the
settings window opens (`App.EnumerateNetAdapters`, non-loopback, sorted) — a one-time
~275ms cost that must stay off the per-tick path. Pinned-adapter semantics:
[`gotchas.md` §29](gotchas.md#29-网络适配器).

## 5. 公网 IP — `PublicIpEnabled` (null = on, 仅写关闭态)

| | |
|---|---|
| Setter | `TaskbarWindow.SetPublicIpLookup(bool)` |
| Sampler state | `SystemSampler` volatile → per-tick `NetInfoSampler.PublicIpLookupEnabled` |
| WM_APP | **no** |

Gates the what-is-my-ip HTTP lookups and the 公网延迟 ICMP probe. Off drops the cached
address and resets the next-try timestamp; re-enabling fetches immediately rather than
waiting out the cadence. [`gotchas.md` §30](gotchas.md#30-公网-ip-开关).

## 6. Clash/Mihomo — `ClashEnabled` (null = on) + `ClashApiAddress` + `ClashApiSecret`

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

## 7. 更新检测 — `UpdateCheckEnabled` + `UpdateSource` + `IgnoredUpdateVersion`

Not part of the sampler chain: `UpdateChecker.CheckOnce` runs **once at startup** from
`OnStartup`, so a change takes effect on the next launch (no live plumbing). See
[`gotchas.md` §33](gotchas.md#33-更新检测).

## 8. 悬浮模式 — `FloatingMode` + `FloatingX`/`FloatingY` + `FloatingTopmost` + `FloatingEdgeHide` + `FloatingFullscreenHide` + `FloatingOpacity`

Not a sampler chain either — this one moves the WINDOW. The shape is still the same
(field → volatile → WM_APP → re-applied on every fresh `Start()`), only the payload differs:

| | |
|---|---|
| Setter | `TaskbarWindow.SetFloatingMode(on, x, y)` / `SetFloatingTopmost(bool)` / `SetFloatingEdgeHide(bool)` / `SetFloatingFullscreenHide(bool)` / `SetFloatingDark(bool)` / `SetFloatingOpacity(double)` |
| Taskbar state | `_floating` / `_floatingTopmost` / `_floatingEdgeHide` / `_floatingFullscreenHide` / `_floatingDark` / `_floatingOpacity` (+ `_floatingX`/`_floatingY` — the home position) |
| WM_APP | **`WM_APP_SET_FLOATING`** = the form flipped → the window is DESTROYED and App's recreate loop builds the other form (150ms, `ConsumeQuickRestart`); **`WM_APP_UPDATE_FLOATING`** = 置顶显示, 透明度, or the app theme changed → re-applied in place, no rebuild; **`WM_APP_SET_FLOAT_EDGE_HIDE`** = 贴边隐藏 flipped → the dock is re-derived from the home (ON at an edge slides out, OFF slides home); **`WM_APP_SET_FLOAT_FULLSCREEN_HIDE`** = 全屏时隐藏 flipped → the fullscreen probe runs at once (ON with a fullscreen foreground hides immediately, OFF restores unconditionally) |

Two entry points, one handler each: the 设置 → 外观 switches AND the overlay right-click
menu's checkable 悬浮模式/置顶显示/贴边隐藏/全屏时隐藏 items (`App.EnsureTaskbarMenu`) both call
`OnFloatingModeChanged`/`OnFloatingTopmostChanged`/`OnFloatingEdgeHideChanged`/
`OnFloatingFullscreenHideChanged`; an open
SettingsWindow is pushed back via `SyncFloatingMode`/`SyncFloatingTopmost`/
`SyncFloatingEdgeHide`/`SyncFloatingFullscreenHide`, and the menu re-reads `_config` on every
`Opened` (置顶显示/贴边隐藏/全屏时隐藏
grey out outside the floating form, same rule as their settings cards).

- **`FloatingMode`** (null = off): 悬浮模式 on/off. The window form — parent, ex-style,
  backdrop — is creation-time state, so this is the one setting that rebuilds the window
  instead of mutating it (`Start()` is the single construction site for either form).
- **`FloatingX`/`FloatingY`** (null = never dragged): the widget's home in screen px. Read by
  `Start()` (which records the opening spot it computed as the home when none is saved), and
  written by **App** from `TaskbarWindow.FloatingPositionChanged` when a drag ends — the only
  settings write triggered by the taskbar thread (marshaled to the UI thread). The home is also
  what the widget returns to after stepping aside for a detail window that covers it
  (`SetFloatingKeepOut` → `WM_APP_SET_FLOAT_KEEPOUT`, a runtime-state push, not a setting); a
  dodge is never written back here.
- **`FloatingTopmost`** (null = on): live, no rebuild — it moves BOTH ex-style bits
  (`WS_EX_TOPMOST` for the band, `WS_EX_NOACTIVATE` because the non-topmost form is an ordinary
  window that must be activatable to be raiseable, §36 of the gotchas).
- **`FloatingEdgeHide`** (null = off): 贴边隐藏, floating form only — a drop within 8 DIP of the
  work area's 左/右/上 edge docks the widget there (slides out, a strip stays; hover peeks it
  back, mouse-out re-hides). Live, no rebuild — and the dock itself is DERIVED from the home
  (`FloatingX`/`FloatingY`, which is why a docked home reopens hidden), so nothing but the
  switch and the home are stored. Mechanism + the slide/peek/armed rules:
  [`gotchas.md` §38](gotchas.md#38-贴边隐藏--the-dock-is-derived-from-the-home-and-the-slide-owns-the-position).
  Settings card + right-click menu item (checkable, greyed outside the floating form).
- **`FloatingFullscreenHide`** (null = on): 全屏时隐藏, floating form only — a fullscreen app
  in the foreground on the widget's monitor (borderless game, F11 video, slideshow; NOT a
  maximized window) hides the widget until the foreground stops being fullscreen. Live, no
  rebuild — the switch is the ONLY stored state; the fullscreen condition is probed by the
  tick (`FullscreenAppOnScreen`), and the hide/restore is a plain `SW_HIDE`/
  `SW_SHOWNOACTIVATE` that touches no positional state. Mechanism + the visibility-only
  rules: [`gotchas.md` §39](gotchas.md#39-全屏时隐藏--the-probe-owns-visibility-never-the-position).
  Settings card + right-click menu item (checkable, greyed outside the floating form).
- **`FloatingOpacity`** (null = 1.0; 0.2–1.0, `double`): the widget's BACKGROUND opacity —
  brush-alpha scaling in `ApplyTaskbarTheme` (NOT `WS_EX_LAYERED`; scope, why and the 0.2
  floor: §36): the card's alpha IS the value (100% = fully opaque card), the card's outline
  scales with it, text/labels/interaction fills keep fixed alphas (文字不要有透明度). Live per
  slider step (the widget re-tints under the thumb; App debounces only the yaml write,
  500ms — the Clash text boxes' reason); clamped in the setter because settings.yaml is
  hand-editable. Settings-page only — a percent slider has no menu shape, and
  `FloatingOpacityCard` greys out outside the floating form like `FloatingTopmostCard`.
- **`FloatingDark`** is NOT in settings.yaml — it is the effective APP theme
  (`ThemeManager.Current.ActualApplicationTheme`), pushed by App at startup and from the
  `ActualApplicationThemeChanged` hook, because the floating widget's material is the detail
  popup's (the taskbar form instead follows the SYSTEM theme).

Full mechanism, the measured material choice and the creation-time rules:
[`gotchas.md` §36](gotchas.md#36-悬浮模式--the-same-window-detached).

## Not in settings.yaml at all

**开机自启动** — the scheduled task itself is the state (`src/StartupTask.cs`). Never add a
key for it.
