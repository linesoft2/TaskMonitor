using System;
using System.Collections.Generic;

namespace task_monitor
{
    /// <summary>
    /// The full system snapshot the taskbar overlay and detail popup render: CPU,
    /// RAM, and the auto-selected network adapter's live rate. Assembled by
    /// <see cref="SystemSampler"/> from its single-purpose samplers.
    /// </summary>
    internal sealed class SystemSnapshot
    {
        // Sampling cadence this snapshot was produced at (the taskbar overlay's current
        // tick, settings 采样间隔) — the detail views' "N 秒前" chart tooltips multiply
        // tick offsets by it. Stamped by TaskbarWindow right after Sample().
        public int SampleIntervalMs = 1000;

        // CPU
        public double CpuPercent;            // 0–100 overall
        public double[] PerCoreUsage = Array.Empty<double>(); // 0–100 per logical core
        public double[] CpuHistory = Array.Empty<double>();   // recent overall CPU% for the graph
        public string CpuName = "Unknown CPU";
        public uint CpuMhz;                  // base frequency from registry
        public uint CpuCurrentMhz;           // live clock (averaged), for the CPU footer
        public uint NumLogicalProcessors;

        // System summary (CPU detail footer)
        public int ProcessCount;
        public int ThreadCount;
        public int HandleCount;
        public long UptimeMs;                // ms since boot

        // Per-process lists (one NtQuerySystemInformation walk) — top entries by CPU%
        // (CPU detail card), by private working set (RAM detail card), and by I/O
        // read+write rate (Disk detail card).
        public List<ProcessInfo> TopProcesses = new List<ProcessInfo>();
        public List<ProcessInfo> TopMemoryProcesses = new List<ProcessInfo>();
        public List<ProcessInfo> TopDiskProcesses = new List<ProcessInfo>();
        // Per-process network throughput (SRUM real-time API) — top entries by total up+down
        // bytes/s (Network detail card). Separate up/down per row.
        public List<ProcessInfo> TopNetProcesses = new List<ProcessInfo>();
        // Per-process GPU utilization (PDH \GPU Engine(*)\Utilization Percentage — Task
        // Manager's GPU-column source) — top entries by GPU% (GPU detail card), each with
        // its dominant engine name.
        public List<ProcessInfo> TopGpuProcesses = new List<ProcessInfo>();

        // RAM
        public double RamPercent;            // 0–100
        public double TotalRamGb;
        public double UsedRamGb;
        public double[] RamHistory = Array.Empty<double>();   // recent overall RAM% for the graph
        public MemoryDetail MemoryDetail;   // Task Manager memory breakdown (in-use/compressed/committed/cached/pools)

        // Network (the selected adapter's real per-second up/down rate — 自动 by default,
        // the max-traffic Up adapter; or the pinned 适配器 pick, falling back to 自动
        // while it's gone)
        public long NetUpBytesPerSec;        // upload (bytes/s)
        public long NetDownBytesPerSec;      // download (bytes/s)
        public string NetAdapterName = "";   // Description of the selected adapter (for the popup)
        public long[] NetUpHistory = Array.Empty<long>();    // 60-tick rolling upload rate (oldest→newest), for the popup's bidirectional chart
        public long[] NetDownHistory = Array.Empty<long>();  // 60-tick rolling download rate
        // Connection-info band for the Network panel (NetInfoSampler, refreshed ~1 Hz on its
        // own background thread — never mutated after publication)
        public NetInfo NetInfo;

        // Disk (per-physical-disk, Task Manager style — IOCTL_DISK_PERFORMANCE deltas).
        // The headline percent is per the 显示方式 setting (设置 → 采样 → 磁盘): the MEAN
        // or MAX of per-disk utilization, or one specific disk's. The per-disk DiskInfo
        // objects are long-lived (updated in place each tick, INotifyPropertyChanged) so
        // the detail view's tabs bind once and keep their selection.
        public double DiskPercent;             // 0–100, headline utilization (MetricDisplayMode)
        public double[] DiskHistory = Array.Empty<double>(); // 60-tick rolling headline (oldest→newest)
        public List<DiskInfo> Disks = new List<DiskInfo>();

        // GPU (per-adapter, Task Manager style — DXCore QueryState deltas). The headline
        // percent is per the 显示方式 setting: the MAX across adapters by default (Task
        // Manager's sidebar rule), the MEAN, or one specific "GPU N". GpuAvailable is
        // false when no GPU adapter is present (or dxcore.dll is missing) — the overlay then
        // keeps the "--" placeholder. The per-adapter GpuInfo objects are long-lived
        // (INotifyPropertyChanged), same contract as DiskInfo.
        public bool GpuAvailable;
        public double GpuPercent;              // 0–100, headline utilization (MetricDisplayMode)
        public double[] GpuHistory = Array.Empty<double>(); // 60-tick rolling headline (oldest→newest)
        public List<GpuInfo> Gpus = new List<GpuInfo>();
    }

    /// <summary>
    /// Facade that owns the single-purpose samplers (<see cref="CpuSampler"/>,
    /// <see cref="RamSampler"/>, <see cref="NetSampler"/>, the live-clock/uptime
    /// <see cref="SystemSummarySampler"/>, the per-process <see cref="ProcessCpuSampler"/>,
    /// the per-process-network <see cref="ProcessNetSampler"/>, the Task Manager-memory
    /// <see cref="MemoryDetailSampler"/>, the connection-info
    /// <see cref="NetInfoSampler"/>, the per-disk <see cref="DiskSampler"/>, and the
    /// per-GPU <see cref="GpuSampler"/>), plus the svchost-naming <see cref="ServiceHostMap"/>,
    /// the Clash/Mihomo controller poller <see cref="ClashSampler"/>,
    /// and assembles one <see cref="SystemSnapshot"/> per call. Lives on the taskbar STA thread
    /// (single-threaded, except the SRUM callback thread inside ProcessNetSampler and the
    /// ~1 Hz poll thread inside NetInfoSampler); each sub-sampler keeps its own
    /// deltas/baseline internally.
    /// </summary>
    internal sealed class SystemSampler
    {
        private readonly CpuSampler _cpu = new CpuSampler();
        private readonly RamSampler _ram = new RamSampler();
        private readonly NetSampler _net = new NetSampler();
        private readonly SystemSummarySampler _summary = new SystemSummarySampler();
        // Per-process CPU + memory + process/thread/handle totals from the same enumeration
        // (replaces what used to be a separate Process.GetProcesses() in the summary sampler).
        private readonly ProcessCpuSampler _procCpu = new ProcessCpuSampler();
        // Per-process network up/down rate (SRUM real-time API via srumapi.dll).
        private readonly ProcessNetSampler _procNet = new ProcessNetSampler();
        // Clash/Mihomo proxied per-process traffic (external-controller /connections
        // polling on its own background thread — the REST equivalent of sparkle's
        // WebSocket feed). Idles unless 设置 → 网络 gives it an endpoint.
        private readonly ClashSampler _clash = new ClashSampler();
        // Task Manager memory breakdown (committed/limit, cached, pools, in-use, compressed).
        private readonly MemoryDetailSampler _memDetail = new MemoryDetailSampler();
        // Network connection-info band (type/rate, Wi-Fi details, IPs, latencies) — the slow
        // work (WLAN RPC, ICMP pings, the public-IP HTTP lookup) runs on its own thread.
        private readonly NetInfoSampler _netInfo = new NetInfoSampler();
        // Per-physical-disk utilization / read / write / response time (IOCTL_DISK_PERFORMANCE
        // deltas, Task Manager style) + per-disk SSD/HDD classification.
        private readonly DiskSampler _disk = new DiskSampler();
        // Per-GPU utilization / temperature / VRAM (DXCore QueryState deltas, Task Manager
        // style) — utilization is the MAX across each adapter's engines.
        private readonly GpuSampler _gpu = new GpuSampler();
        // Per-process GPU utilization (PDH \GPU Engine(*)\Utilization Percentage, Task
        // Manager's Processes-page GPU column) — DXCore has no per-process engine data,
        // which is why this is a PDH sampler, separate from the adapter-level DXCore one.
        private readonly ProcessGpuSampler _procGpu = new ProcessGpuSampler();
        // svchost → 服务 naming for the five per-process lists (one SCM service enumeration
        // per tick; applied to each FINAL list at the end of Sample(), after the samplers'
        // merge — renaming earlier would break ProcessListMerger's svchost exemption).
        private readonly ServiceHostMap _serviceHosts = new ServiceHostMap();

        // ---- per-metric sampling switches (设置 → 采样) ----
        // One bit per metric, in overlay hit-slot order (CPU/内存/磁盘/GPU/网络 — the same
        // order TaskbarWindow uses, so its mask maps 1:1). A cleared bit skips that metric's
        // samplers entirely: the snapshot keeps its field-initializer defaults and the
        // overlay HIDES the slot (TaskbarWindow.ComputeLayout reflows the remaining slots).
        public const int MaskCpu = 1 << 0;
        public const int MaskRam = 1 << 1;
        public const int MaskDisk = 1 << 2;
        public const int MaskGpu = 1 << 3;
        public const int MaskNet = 1 << 4;
        public const int MaskAll = MaskCpu | MaskRam | MaskDisk | MaskGpu | MaskNet;

        // Written by the WPF UI thread (settings toggles, via TaskbarWindow.SetMetricSamplingMask),
        // read on the taskbar thread each tick — same single-writer volatile contract as
        // TaskbarWindow's placement fields.
        private volatile int _enabledMask = MaskAll;
        // Previous tick's mask (taskbar STA thread only) — a bit that just went 0→1 primes
        // the metric's delta baselines (see Sample).
        private int _lastMask = MaskAll;

        /// <summary>Thread-safe: replace the per-metric sampling mask (<see cref="MaskCpu"/>
        /// …). Applied on the next tick; TaskbarWindow re-applies it to every fresh sampler
        /// (Start() creates a new one per overlay incarnation).</summary>
        public void SetEnabledMask(int mask) => _enabledMask = mask;

        // 设置 → 采样项目 → 合并相同程序 — same volatile single-writer contract as
        // _enabledMask (written via TaskbarWindow.SetMergeSamePathProcesses). Read each
        // tick and handed to the per-process samplers, which merge same-exe-path rows
        // into one (ProcessListMerger) before their top-N cut.
        private volatile bool _mergeByPath;

        /// <summary>Thread-safe: toggle same-path process merging for the five per-process
        /// detail lists. Applied on the next tick; TaskbarWindow re-applies it to every
        /// fresh sampler, same as <see cref="SetEnabledMask"/>.</summary>
        public void SetMergeByPath(bool on) => _mergeByPath = on;

        // 设置 → 采样项目 → 磁盘 → 显示方式 — same volatile single-writer contract as
        // _mergeByPath (written via TaskbarWindow.SetDiskDisplay). Read each tick and
        // handed to DiskSampler, which derives the headline (mean / max / the specific
        // disk's utilization) from the per-disk values it queries regardless.
        private volatile int _diskDisplayMode;    // (int)MetricDisplayMode — 0=Average, the default
        private volatile int _diskDisplayIndex;   // PhysicalDrive N for MetricDisplayMode.Specific

        /// <summary>Thread-safe: set the disk headline display mode (0=Average 1=Max
        /// 2=Specific) and the specific disk's PhysicalDrive index. Applied on the next
        /// tick; TaskbarWindow re-applies it to every fresh sampler, like the mask.</summary>
        public void SetDiskDisplay(int mode, int index)
        {
            _diskDisplayMode = mode;
            _diskDisplayIndex = index;
        }

        // 设置 → 采样项目 → GPU → 显示方式 — same volatile single-writer contract as the
        // disk one above. The GPU default is Max (Task Manager's sidebar rule), unlike
        // the disk's Average.
        private volatile int _gpuDisplayMode = (int)MetricDisplayMode.Max;
        private volatile int _gpuDisplayIndex;    // the "GPU N" number for MetricDisplayMode.Specific

        /// <summary>Thread-safe: set the GPU headline display mode (0=Average 1=Max
        /// 2=Specific) and the specific adapter's "GPU N" number. Applied on the next
        /// tick; TaskbarWindow re-applies it to every fresh sampler, like the mask.</summary>
        public void SetGpuDisplay(int mode, int index)
        {
            _gpuDisplayMode = mode;
            _gpuDisplayIndex = index;
        }

        // 设置 → 采样项目 → 网络 → 适配器 — same volatile single-writer contract as the
        // disk one above. null = 自动 (NetSampler's max-traffic pick).
        private volatile string _netAdapterId;

        /// <summary>Thread-safe: pin the sampled network adapter by NetworkInterface.Id
        /// (GUID), or null for 自动. Applied on the next tick (a pinned adapter that's
        /// gone falls back to 自动 inside NetSampler); TaskbarWindow re-applies it to
        /// every fresh sampler, like the mask.</summary>
        public void SetNetAdapter(string id) => _netAdapterId = id;

        // 设置 → 采样项目 → 网络 → Clash/Mihomo — same volatile single-writer contract
        // as _netAdapterId (written via TaskbarWindow.SetClashApi). Enabled is on by
        // default; a null/empty address = the conventional default
        // (ClashSampler.DefaultAddress, substituted at the per-tick hand-off — a stock
        // core works with zero setup).
        private volatile bool _clashEnabled = true;
        private volatile string _clashApiAddress;
        private volatile string _clashApiSecret;

        /// <summary>Thread-safe: switch the Clash/Mihomo integration and set the
        /// external-controller endpoint (host:port — null/empty = the
        /// <see cref="ClashSampler.DefaultAddress"/> default) and API secret
        /// (null/empty = none). Applied on the next tick; TaskbarWindow re-applies it to
        /// every fresh sampler, like the mask.</summary>
        public void SetClashApi(bool enabled, string address, string secret)
        {
            _clashEnabled = enabled;
            _clashApiAddress = address;
            _clashApiSecret = secret;
        }

        // 设置 → 采样项目 → 网络 → 公网 IP — same volatile single-writer contract as
        // _netAdapterId (written via TaskbarWindow.SetPublicIpLookup). On by default.
        private volatile bool _publicIpLookupEnabled = true;

        /// <summary>Thread-safe: switch the public-internet probes (the Network panel's
        /// 公网 IPv4/IPv6 — NetInfoSampler's what-is-my-ip HTTP queries — AND the 公网延迟
        /// ICMP ping). Applied on the next tick; TaskbarWindow re-applies it
        /// to every fresh sampler, like the mask.</summary>
        public void SetPublicIpLookup(bool enabled) => _publicIpLookupEnabled = enabled;

        public SystemSnapshot Sample()
        {
            int mask = _enabledMask;         // one volatile read, consistent for the tick
            bool mergeByPath = _mergeByPath; // same — one read, used for all three lists
            int prev = _lastMask;
            _lastMask = mask;

            bool cpuOn = (mask & MaskCpu) != 0;
            bool ramOn = (mask & MaskRam) != 0;
            bool diskOn = (mask & MaskDisk) != 0;
            bool gpuOn = (mask & MaskGpu) != 0;
            bool netOn = (mask & MaskNet) != 0;

            // A metric that was JUST re-enabled this tick has stale delta baselines (its
            // samplers weren't called while off — a first Sample now would average the
            // whole disabled span into one meaningless tick). Prime instead: call the
            // samplers to re-establish their baselines, discard the result, and keep the
            // snapshot at defaults ("--") until the next tick — the fresh-sampler behavior.
            bool cpuPrime = cpuOn && (prev & MaskCpu) == 0;
            bool ramPrime = ramOn && (prev & MaskRam) == 0;
            bool diskPrime = diskOn && (prev & MaskDisk) == 0;
            bool gpuPrime = gpuOn && (prev & MaskGpu) == 0;
            bool netPrime = netOn && (prev & MaskNet) == 0;

            // svchost → 服务 naming: refresh the PID→services map once per tick (one
            // EnumServicesStatusExW RPC — same one-call-per-tick budget as the process
            // walk below) whenever at least one metric is on.
            if (mask != 0) _serviceHosts.Refresh();

            // The shared per-process walk feeds the CPU/RAM/磁盘 top lists, the CPU footer
            // counts, RAM's "(compressed)" and the PID→name map for the net/GPU per-process
            // samplers — run it unless EVERYTHING is off (it is the most expensive sampler).
            var proc = mask != 0 ? _procCpu.Sample(mergeByPath) : default(ProcessSample);
            var pidToName = proc.PidToName;

            var snapshot = new SystemSnapshot { NetInfo = NetInfo.Empty };

            if (cpuOn)
            {
                var cpu = _cpu.Sample();
                var summary = _summary.Sample();
                if (!cpuPrime)
                {
                    snapshot.CpuPercent = cpu.CpuPercent;
                    snapshot.PerCoreUsage = cpu.PerCoreUsage;
                    snapshot.CpuHistory = cpu.CpuHistory;
                    snapshot.CpuName = cpu.CpuName;
                    snapshot.CpuMhz = cpu.CpuMhz;
                    snapshot.CpuCurrentMhz = summary.CurrentMhz;
                    snapshot.NumLogicalProcessors = cpu.NumLogicalProcessors;
                    // System summary (clock + uptime from the summary sampler; process/thread/
                    // handle totals from the per-process enumeration)
                    snapshot.ProcessCount = proc.ProcessCount;
                    snapshot.ThreadCount = proc.ThreadCount;
                    snapshot.HandleCount = proc.HandleCount;
                    snapshot.UptimeMs = summary.UptimeMs;
                    snapshot.TopProcesses = proc.TopProcesses;
                }
            }

            if (ramOn)
            {
                var ram = _ram.Sample();
                var memDetail = _memDetail.Sample();
                // Compressed bytes come from the "Memory Compression" process found during the
                // process walk (not from a memory counter) — that is Task Manager's "(compressed)".
                memDetail.CompressedBytes = proc.CompressedBytes;
                if (!ramPrime)
                {
                    snapshot.RamPercent = ram.RamPercent;
                    snapshot.TotalRamGb = ram.TotalRamGb;
                    snapshot.UsedRamGb = ram.UsedRamGb;
                    snapshot.RamHistory = ram.RamHistory;
                    snapshot.MemoryDetail = memDetail;
                    snapshot.TopMemoryProcesses = proc.TopMemoryProcesses;
                }
            }

            if (netOn)
            {
                var (netUp, netDown, netAdapter, netUpHist, netDownHist) = _net.Sample(_netAdapterId);
                // Hand the Clash/Mihomo endpoint to the controller poller (cheap volatile
                // writes; its background thread retargets on change) — the integration off
                // → null (the poll thread idles); an unset address → the conventional
                // 127.0.0.1:9090 default — and take its latest per-process proxy rates for
                // the net sampler to append as standalone "Clash"-tagged rows (no dedup
                // against the SRUM rows).
                _clash.SetEndpoint(
                    !_clashEnabled ? null
                    : string.IsNullOrWhiteSpace(_clashApiAddress) ? ClashSampler.DefaultAddress
                    : _clashApiAddress, _clashApiSecret);
                // The network sampler's SRUM records carry only a PID; hand it the walk's
                // PID→ImageName map so it can name processes (incl. PPL-protected ones it can't
                // OpenProcess) without enumerating processes itself.
                var procNet = _procNet.Sample(pidToName, mergeByPath, _clash.Latest);
                // Hand the current adapter to the connection-info sampler — its background
                // thread does the slow work; Sample() below is just a volatile read.
                _netInfo.Adapter = _net.CurrentAdapter;
                // Same hand-off for the 公网 IP lookup switch (cheap volatile write; the
                // poll thread drops its cached address and stops BOTH the HTTP queries
                // and the 公网延迟 ICMP probe on off).
                _netInfo.PublicIpLookupEnabled = _publicIpLookupEnabled;
                if (!netPrime)
                {
                    snapshot.NetUpBytesPerSec = netUp;
                    snapshot.NetDownBytesPerSec = netDown;
                    snapshot.NetAdapterName = netAdapter;
                    snapshot.NetUpHistory = netUpHist;
                    snapshot.NetDownHistory = netDownHist;
                    snapshot.NetInfo = _netInfo.Sample();
                    snapshot.TopNetProcesses = procNet;
                }
            }
            else
            {
                // Idle the connection-info poll thread: a null adapter makes its PollOnce
                // publish NetInfo.Empty and do nothing else (the SRUM callback keeps
                // accumulating — bounded per-PID; the re-enable prime re-baselines it).
                _netInfo.Adapter = null;
                // Same for the Clash/Mihomo poller: no endpoint → its thread sleeps with
                // zero network activity.
                _clash.SetEndpoint(null, null);
            }

            if (diskOn)
            {
                var disk = _disk.Sample((MetricDisplayMode)_diskDisplayMode, _diskDisplayIndex);
                if (!diskPrime)
                {
                    snapshot.DiskPercent = disk.HeadlinePercent;
                    snapshot.DiskHistory = disk.History;
                    snapshot.Disks = disk.Disks;
                    snapshot.TopDiskProcesses = proc.TopDiskProcesses;
                }
            }

            if (gpuOn)
            {
                var gpu = _gpu.Sample((MetricDisplayMode)_gpuDisplayMode, _gpuDisplayIndex);
                // Per-process GPU% (PDH) — same PID→ImageName hand-off as the network sampler.
                var procGpu = _procGpu.Sample(pidToName, mergeByPath);
                if (!gpuPrime)
                {
                    snapshot.GpuAvailable = gpu.Available;
                    snapshot.GpuPercent = gpu.HeadlinePercent;
                    snapshot.GpuHistory = gpu.History;
                    snapshot.Gpus = gpu.Gpus;
                    snapshot.TopGpuProcesses = procGpu;
                }
            }

            // svchost.exe rows → service/group names (ServiceHostMap), applied HERE on the
            // final lists — after every sampler's merge — because ProcessListMerger exempts
            // svchost by its "svchost.exe" row name; renaming inside a sampler would let all
            // same-path svchost instances collapse into one merged row.
            _serviceHosts.ApplyTo(snapshot.TopProcesses);
            _serviceHosts.ApplyTo(snapshot.TopMemoryProcesses);
            _serviceHosts.ApplyTo(snapshot.TopDiskProcesses);
            _serviceHosts.ApplyTo(snapshot.TopNetProcesses);
            _serviceHosts.ApplyTo(snapshot.TopGpuProcesses);

            return snapshot;
        }

        /// <summary>UI thread (Network panel just opened) asks the connection-info sampler to
        /// refresh its Wi-Fi cache on the background thread — but only if the cache is stale
        /// (wrong adapter / timed out); a fresh cache is reused with zero wlanapi calls. This
        /// is the sole entry point that can trigger the location-sensitive wlanapi path.
        /// Thread-safe (one volatile write).</summary>
        public void RequestWifiDetails() => _netInfo.RequestWifiDetails();
    }
}
