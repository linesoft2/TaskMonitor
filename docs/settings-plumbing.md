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

Per-tick hand-off to `DiskSampler.Sample(mode, index)` / `GpuSampler.Sample(mode, index)`.
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

## Not in settings.yaml at all

**开机自启动** — the scheduled task itself is the state (`src/StartupTask.cs`). Never add a
key for it.
