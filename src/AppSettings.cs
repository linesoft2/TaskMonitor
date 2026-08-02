using System;
using System.Diagnostics;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace task_monitor
{
    /// <summary>深浅色设置 (settings.yaml <c>theme</c>). null = 跟随系统.</summary>
    public enum AppThemeSetting { Light, Dark }

    /// <summary>磁盘/GPU headline 显示方式 (settings.yaml <c>diskDisplay</c> / <c>gpuDisplay</c>):
    /// what the overlay slot and the detail chart show — the mean / max across disks
    /// (adapters), or one specific one's. null = the metric's default (磁盘 Average, GPU Max).</summary>
    public enum MetricDisplayMode { Average, Max, Specific }

    /// <summary>
    /// Persistent settings store — <c>settings.yaml</c> in the exe's own directory (the
    /// run directory; resolved from <see cref="AppDomain.BaseDirectory"/> because the
    /// working directory is not stable across the runas self-relaunch). This is the
    /// single store for ALL settings (the 设置 pages will hang theirs here); today it
    /// carries the first-run elevation consent, the overlay placement (left/right
    /// side + left-side anchor), the 深浅色 theme (null = 跟随系统), the sampling
    /// interval (null = 1s), the per-metric sampling switches (null = enabled) and the
    /// disk headline display mode (null = 所有磁盘平均; the specific-disk index), the GPU
    /// headline display mode (null = 最高; the specific "GPU N" number) and the network
    /// adapter pick (null = 自动; an adapter GUID), the 公网 IP lookup switch (null = on),
    /// and the Clash/Mihomo integration
    /// (null = on; switch + address + API secret), and the startup update check (null =
    /// on; the CNB/GitHub source; the 不再提醒-skipped tag). The 开机自启动 toggle is deliberately NOT here — the
    /// scheduled task itself is the state (<see cref="StartupTask"/>). YamlDotNet ignores
    /// comment lines on read, so the header written by <see cref="Save"/> round-trips fine.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Whether the user has allowed the app to always run elevated. null = never
        /// asked (first run) → the consent dialog shows. Only ever written as true;
        /// "不允许" is deliberately NOT persisted — the next launch asks again.
        /// </summary>
        public bool? ElevationConsent { get; set; }

        /// <summary>
        /// Whether the legacy-OS (pre-Win11) compatibility warning has been shown. null =
        /// never shown → the first launch on Windows 10 or older shows it once (App's
        /// OnStartup), then true is persisted. Never written on Win11+, where the warning
        /// never triggers.
        /// </summary>
        public bool? LegacyOsWarningShown { get; set; }

        /// <summary>
        /// Show the taskbar overlay on the LEFT side of the taskbar instead of right of
        /// it. null/true = left (the default); only an explicit false is right.
        /// Win11 taskbar: left = the far-left corner / snapped left of Start, honored only
        /// while the taskbar is centre-aligned (TaskbarAl); a left-aligned taskbar has no
        /// room left of the Start button, so the overlay falls back to the right side
        /// automatically. Classical (Win10) taskbar: always honored — left = the
        /// task-buttons band's end next to Start (the top end on a side-docked taskbar),
        /// right = the end next to the notification area.
        /// </summary>
        public bool? OverlayOnLeft { get; set; }

        /// <summary>
        /// When <see cref="OverlayOnLeft"/>: where on the left the overlay sits — WIN11
        /// taskbar only (the classical taskbar has a single left-side spot, so the
        /// settings page hides this there). true = snap just left of the Start button
        /// (follows the centred icon group as it resizes); null/false = the taskbar's
        /// far-left corner (shifted right by a fixed reserve when the Widgets button is
        /// shown, to avoid overlapping it).
        /// </summary>
        public bool? OverlaySnapToStart { get; set; }

        /// <summary>
        /// 深浅色外观: null = 跟随系统 (the iNKORE ThemeManager tracks the system theme
        /// live), Light/Dark = forced. Applied to <c>ThemeManager.Current.ApplicationTheme</c>
        /// at startup and on every settings-page change (App.ApplyThemeSetting).
        /// </summary>
        public AppThemeSetting? Theme { get; set; }

        /// <summary>
        /// Taskbar overlay sampling/refresh cadence in ms (500 / 1000 / 2000 from the
        /// settings combo). null = the 1000ms default. Read by App at startup and pushed
        /// to the taskbar thread's Win32 timer (<see cref="TaskbarWindow.SetSampleInterval"/>).
        /// </summary>
        public int? SampleIntervalMs { get; set; }

        /// <summary>
        /// Per-metric sampling switches (设置 → 采样, one toggle per metric). null = enabled
        /// (the default — only the disabled state is ever written). A disabled metric's
        /// samplers are skipped entirely (<see cref="SystemSampler.SetEnabledMask"/>), its
        /// overlay slot is hidden (the remaining slots reflow to fill the space) and its
        /// column press is suppressed. App converts these to/from the SystemSampler mask
        /// (bit order = the overlay hit slots).
        /// </summary>
        public bool? CpuSamplingEnabled { get; set; }
        public bool? RamSamplingEnabled { get; set; }
        public bool? DiskSamplingEnabled { get; set; }
        public bool? GpuSamplingEnabled { get; set; }
        public bool? NetSamplingEnabled { get; set; }

        /// <summary>
        /// 合并相同程序 (设置 → 采样项目): null = on (the default — same-exe-path rows in
        /// the five per-process detail lists merge into one, values summed, the member count
        /// shown as "name (N)"; only the disabled state is ever written). Merging happens
        /// BEFORE the top-8 cut (<see cref="ProcessListMerger"/>; svchost.exe 不合并).
        /// Pushed live via <see cref="TaskbarWindow.SetMergeSamePathProcesses"/>.
        /// </summary>
        public bool? MergeSamePathProcesses { get; set; }

        /// <summary>
        /// 磁盘显示方式 (设置 → 采样项目 → 磁盘): what the headline disk utilization is —
        /// Average (null, the default — only non-default modes are written), Max (最高利用率
        /// across disks) or Specific (<see cref="DiskDisplayIndex"/>'s one disk; a missing
        /// specific disk falls back to the remaining disks' mean until it returns). Pushed
        /// live via <see cref="TaskbarWindow.SetDiskDisplay"/>.
        /// </summary>
        public MetricDisplayMode? DiskDisplay { get; set; }

        /// <summary>
        /// The PhysicalDrive index (N of \\.\PhysicalDriveN) shown when
        /// <see cref="DiskDisplay"/> is Specific. null/absent = disk 0. Kept when the mode
        /// switches back to Average/Max, so returning to Specific restores the last pick.
        /// </summary>
        public int? DiskDisplayIndex { get; set; }

        /// <summary>
        /// GPU 显示方式 (设置 → 采样项目 → GPU): what the headline GPU utilization is —
        /// Max (null, the default — Task Manager's sidebar rule), Average (所有 GPU 平均) or
        /// Specific (<see cref="GpuDisplayIndex"/>'s one adapter; a missing specific adapter
        /// falls back to the remaining adapters' max until it returns). Pushed live via
        /// <see cref="TaskbarWindow.SetGpuDisplay"/>.
        /// </summary>
        public MetricDisplayMode? GpuDisplay { get; set; }

        /// <summary>
        /// The "GPU N" tab number shown when <see cref="GpuDisplay"/> is Specific.
        /// null/absent = GPU 0. The numbering follows the adapter-set sort order, so it can
        /// shift when the set changes (same caveat as the disk PhysicalDrive index). Kept
        /// when the mode leaves Specific, like <see cref="DiskDisplayIndex"/>.
        /// </summary>
        public int? GpuDisplayIndex { get; set; }

        /// <summary>
        /// 网络适配器 (设置 → 采样项目 → 网络): the NetworkInterface.Id (GUID) of the adapter
        /// to sample, null = 自动 (the default — the sampler picks the Up, non-virtual
        /// adapter carrying the most traffic, re-selecting when it goes silent or vanishes).
        /// A pinned adapter that disappears or goes down falls back to 自动 and resumes
        /// when it returns. Pushed live via <see cref="TaskbarWindow.SetNetAdapter"/>.
        /// </summary>
        public string NetAdapterId { get; set; }

        /// <summary>
        /// Display name of <see cref="NetAdapterId"/> ("Description (Name)") — kept only so
        /// the settings picker can show a "…（未连接）" placeholder when the pinned adapter
        /// is absent; never used for matching (the Id is).
        /// </summary>
        public string NetAdapterName { get; set; }

        /// <summary>
        /// 公网 IP 查询 (设置 → 采样项目 → 网络): null = on (the default — the Network
        /// detail panel shows the public IPv4/IPv6, fetched from the what-is-my-ip
        /// endpoints in <see cref="NetInfoSampler"/>, plus the 公网延迟 ICMP ping to
        /// www.baidu.com); only the disabled state is ever written. Off stops BOTH the
        /// HTTP lookups and the latency probe — no traffic to the public internet at all
        /// (the 公网 IPv4 / 公网延迟 cells show "—" and the v6 row collapses; the LAN-only
        /// 本地延迟 gateway ping is unaffected). Pushed live via
        /// <see cref="TaskbarWindow.SetPublicIpLookup"/>.
        /// </summary>
        public bool? PublicIpEnabled { get; set; }

        /// <summary>
        /// Clash/Mihomo integration switch (设置 → 采样项目 → 网络 → Clash/Mihomo 卡片):
        /// null = on (the default — the core at <see cref="ClashApiAddress"/>, or the
        /// 127.0.0.1:9090 default when that's unset, is polled and its proxied per-process
        /// traffic joins the Network list as "Clash"-tagged rows); only the disabled
        /// state is ever written. Off idles the poller completely (zero network).
        /// Pushed live via <see cref="TaskbarWindow.SetClashApi"/>.
        /// </summary>
        public bool? ClashEnabled { get; set; }

        /// <summary>
        /// Clash/Mihomo external-controller address (设置 → 采样项目 → 网络 → Clash/Mihomo):
        /// "host:port" (an http:// prefix is tolerated), null/empty = the conventional
        /// <see cref="ClashSampler.DefaultAddress"/> (127.0.0.1:9090) — a stock core works
        /// with zero setup, and only a custom address is ever written here. The core's
        /// proxied per-process traffic appears in the Network detail list as standalone
        /// "Clash"-tagged rows (ClashSampler). Pushed live via
        /// <see cref="TaskbarWindow.SetClashApi"/>.
        /// </summary>
        public string ClashApiAddress { get; set; }

        /// <summary>
        /// API secret for <see cref="ClashApiAddress"/> (the controller's
        /// <c>Authorization: Bearer</c> token), null/empty = none. Stored in plain text
        /// here, like the core's own config file does — the endpoint is meant to be
        /// loopback-only.
        /// </summary>
        public string ClashApiSecret { get; set; }

        /// <summary>
        /// 启动时检查更新 (设置 → 通用): null = on (the default — only the disabled state
        /// is ever written). Once per launch <see cref="UpdateChecker"/> reads the latest
        /// release tag from the configured <see cref="UpdateSource"/> and prompts when a
        /// newer version exists. Takes effect on the next startup.
        /// </summary>
        public bool? UpdateCheckEnabled { get; set; }

        /// <summary>
        /// 更新源 (设置 → 通用): "cnb" (null, the default — the releases page's
        /// server-rendered HTML is scraped, since CNB's OpenAPI requires a login even for
        /// public repos) or "github" (the anonymous releases JSON API). Only the
        /// non-default choice is ever written.
        /// </summary>
        public string UpdateSource { get; set; }

        /// <summary>
        /// The release tag the user dismissed with 不再提醒 (e.g. "v1.0.1"), null = none.
        /// That exact version is never prompted again; a still newer one is.
        /// </summary>
        public string IgnoredUpdateVersion { get; set; }

        public static string FilePath { get; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.yaml");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()   // tolerate keys from newer builds
                    .Build();
                return deserializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                       ?? new AppSettings();       // header-only / empty file
            }
            catch (Exception ex)
            {
                // Never lose a hand-edited or corrupt file silently — keep it as .bad.
                try { File.Move(FilePath, FilePath + ".bad"); } catch { /* best effort */ }
                Debug.WriteLine($"settings.yaml unreadable, using defaults: {ex}");
                return new AppSettings();
            }
        }

        public void Save()
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();
            // Write-then-replace: no torn reads, and a crash mid-write leaves the old file.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, serializer.Serialize(this));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
    }
}
