using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// One GPU adapter's identity + live metrics. Identity (Name, memory caps) is fixed at
    /// enumeration; the metrics are updated in place each second by <see cref="GpuSampler"/>
    /// (on the taskbar STA thread) and observed by the GPU detail view through
    /// INotifyPropertyChanged — same long-lived-object contract as <see cref="DiskInfo"/>,
    /// so the view's pivot tabs bind once and keep their selection across ticks.
    /// </summary>
    internal sealed class GpuInfo : INotifyPropertyChanged
    {
        public string Name { get; }                 // DXCore DriverDescription ("NVIDIA GeForce …")
        public long SharedTotalBytes { get; }       // SharedSystemMemory — fixed while enumerated

        internal GpuInfo(string name, long sharedTotalBytes)
        {
            Name = name;
            SharedTotalBytes = sharedTotalBytes;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private string _tabTitle = "GPU";
        /// <summary>Tab header, Task Manager style: "GPU 0", "GPU 1". Assigned by the sampler
        /// at each enumeration (the numbering can shift when the adapter set changes).</summary>
        public string TabTitle
        {
            get => _tabTitle;
            internal set { if (_tabTitle != value) { _tabTitle = value; Notify(nameof(TabTitle)); } }
        }

        /// <summary>The N in "GPU N" — assigned with <see cref="TabTitle"/> at each
        /// enumeration. The 显示方式 "特定 GPU" setting identifies its pick by this number
        /// (same shift caveat as TabTitle).</summary>
        public int Index { get; internal set; }

        private double _utilPercent;
        /// <summary>0–100, MAX across the adapter's engines — Task Manager's aggregate rule.</summary>
        public double UtilPercent
        {
            get => _utilPercent;
            internal set { if (_utilPercent != value) { _utilPercent = value; Notify(nameof(UtilPercent)); } }
        }

        private string _temperatureText = "--";
        /// <summary>"62 °C", or "--" when the driver doesn't report one (Taskmgr hides the row
        /// in that case; the popup shows the dash instead — the value staying visible tells the
        /// user the metric exists but is unsupported).</summary>
        public string TemperatureText
        {
            get => _temperatureText;
            internal set { if (_temperatureText != value) { _temperatureText = value; Notify(nameof(TemperatureText)); } }
        }

        private string _topEngineText = "--";
        /// <summary>Dominant engine this tick, e.g. "3D" ("--" while idle / engine-less).</summary>
        public string TopEngineText
        {
            get => _topEngineText;
            internal set { if (_topEngineText != value) { _topEngineText = value; Notify(nameof(TopEngineText)); } }
        }

        private string _dedicatedText = "--";
        /// <summary>"1.2 / 8.0 GB" — resident local-segment bytes over the Taskmgr-style cap.</summary>
        public string DedicatedText
        {
            get => _dedicatedText;
            internal set { if (_dedicatedText != value) { _dedicatedText = value; Notify(nameof(DedicatedText)); } }
        }

        private string _sharedText = "--";
        /// <summary>"0.3 / 15.9 GB" — resident non-local bytes over SharedSystemMemory.</summary>
        public string SharedText
        {
            get => _sharedText;
            internal set { if (_sharedText != value) { _sharedText = value; Notify(nameof(SharedText)); } }
        }

        private void Notify(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// A GPU-only sample: whether any adapter exists, the headline utilization (per the
    /// 显示方式 setting — MAX across adapters by default, Task Manager's sidebar rule),
    /// its 60-tick history, and the per-adapter live metrics (the stable
    /// <see cref="GpuInfo"/> objects, updated in place).
    /// </summary>
    internal struct GpuSample
    {
        public bool Available;                  // false → overlay keeps the "--" placeholder
        public double HeadlinePercent;          // 0–100, per the 显示方式 setting (MetricDisplayMode)
        public double[] History;                // recent HeadlinePercent (oldest→newest), for the chart
        public List<GpuInfo> Gpus;              // per-adapter live metrics
    }

    /// <summary>
    /// Samples per-GPU overall utilization / temperature / memory the way Task Manager's
    /// <c>WdcGpuMonitor</c> does (reversed from Taskmgr.exe — every metric comes from the
    /// DXCore COM API, dxcore.dll; PDH is NOT involved):
    ///
    ///   per engine:  QueryState(AdapterEngineRunningTimeMicroseconds, {physIdx, engineIdx})
    ///                → cumulative busy μs;  engine% = Δbusy / Δwall(QPC) × 100
    ///   adapter %  = MAX(engine%)                       (Taskmgr's aggregation, NOT a mean)
    ///   headline % = MAX(adapter%) across adapters      (Taskmgr's sidebar rule) — the
    ///                default 显示方式; the setting can instead pick the MEAN across
    ///                adapters or one specific "GPU N" (falling back to MAX while absent)
    ///   temperature= QueryState(AdapterTemperatureCelsius) → float °C, when the driver
    ///                supports it (IsQueryStateSupported gates it — Taskmgr hides the row
    ///                when unsupported; we show "--")
    ///   dedicated  = QueryState(AdapterMemoryUsageBytes, {physIdx, Dedicated}).resident
    ///   shared     = QueryState(AdapterMemoryUsageBytes, {physIdx, Shared}).resident
    ///                (Taskmgr reads RESIDENT for adapter totals, committed only per-process)
    ///
    /// The dedicated cap shown as the total replicates Taskmgr's display math:
    /// DedicatedAdapterMemory + DedicatedSystemMemory, rounded UP to a "nice" unit
    /// (min(nextPow2(cap/10 − 1), 512 MB) — that's why Task Manager reads "8.0 GB"); an
    /// integrated adapter with zero dedicated usage reports a 0 total (its memory is all
    /// shared), matching Task Manager's "0.0 / 0.0 GB".
    ///
    /// Enumeration (every 30 ticks, like the disk probe): CreateAdapterList over the
    /// GPU + D3D12_CORE_COMPUTE attributes, deduped by InstanceLuid, software adapters
    /// (IsHardware=false, e.g. Microsoft Basic Render) excluded. Each adapter expands to
    /// PhysicalAdapterCount nodes (LDA) — one GpuInfo per (LUID, physicalIndex), engines
    /// per node via AdapterEngineCount/AdapterEngineName. DXCore's event notifications
    /// exist but a periodic re-probe is simpler and covers the same hot-plug surface.
    ///
    /// Degrades silently to Available=false when dxcore.dll is missing (pre-1903) or the
    /// factory can't be created. Single-threaded (taskbar STA thread), sampled every 1s.
    /// </summary>
    internal sealed class GpuSampler
    {
        private const int MaxHistory = 60;
        private const int ReenumerateEveryTicks = 30;
        private const int MaxConsecutiveFailures = 3;
        private const int MaxEngineNameChars = 32;   // Taskmgr's own cap ("Engine" fallback when empty)

        private sealed class EngineEntry
        {
            public string Name;
            public ulong PrevRunningTimeUs;
            public double PrevClockUs;
            public bool HasBaseline;
            public double Util;                     // this tick, 0–100
        }

        private sealed class AdapterEntry
        {
            public IDXCoreAdapter1 Adapter;         // RCW, released on removal
            public GpuInfo Info;
            public int PhysicalIndex;
            public int SortOrder;                   // stable tab ordering (GPU 0, GPU 1, …)
            public List<EngineEntry> Engines = new List<EngineEntry>();
            public bool EngineTimeSupported;
            public bool MemoryUsageSupported;
            public bool TemperatureSupported;
            public bool IsIntegrated;
            public long DedicatedCapBytes;          // DedicatedAdapterMemory + DedicatedSystemMemory + Taskmgr headroom
            public int Failures;
        }

        private readonly Dictionary<(long luid, int phys), AdapterEntry> _adapters =
            new Dictionary<(long luid, int phys), AdapterEntry>();
        private readonly Queue<double> _history = new Queue<double>(MaxHistory);
        private int _ticksSinceEnumerate = ReenumerateEveryTicks; // enumerate on the first Sample()
        private bool _dxcoreUnavailable;            // dxcore.dll missing / factory failed — stop trying

        // The 显示方式 last used to build the headline (taskbar STA thread only). A change
        // clears the history so the chart never mixes values computed under two semantics.
        private MetricDisplayMode _lastMode = MetricDisplayMode.Max;   // the GPU default
        private int _lastSpecificIndex = -1;

        // Reusable marshaling buffers (single-threaded sampler). In/out for the scalar
        // queries, plus a name buffer the engine-name property writes into by pointer.
        private readonly IntPtr _inBuf = Marshal.AllocHGlobal(64);
        private readonly IntPtr _outBuf = Marshal.AllocHGlobal(64);
        private readonly IntPtr _nameBuf = Marshal.AllocHGlobal((MaxEngineNameChars + 2) * 2);

        private IDXCoreAdapterFactory _factory;
        private long _qpcFreq;

        /// <param name="mode">Headline source (设置 → 采样项目 → GPU → 显示方式): the MAX
        /// (the GPU default) or mean of per-adapter utilization, or one specific
        /// adapter's.</param>
        /// <param name="specificIndex">The "GPU N" number (<see cref="GpuInfo.Index"/>) for
        /// <see cref="MetricDisplayMode.Specific"/>; ignored otherwise. A specific adapter
        /// that isn't currently present falls back to the remaining adapters' max (the GPU
        /// default — the pick survives, its own values resume when it returns).</param>
        public GpuSample Sample(MetricDisplayMode mode, int specificIndex)
        {
            if (mode != _lastMode || specificIndex != _lastSpecificIndex)
            {
                _history.Clear();
                _lastMode = mode;
                _lastSpecificIndex = specificIndex;
            }

            if (!_dxcoreUnavailable)
            {
                if (++_ticksSinceEnumerate >= ReenumerateEveryTicks)
                {
                    Reenumerate();
                    _ticksSinceEnumerate = 0;
                }

                // Every adapter is sampled every tick regardless of the mode — the detail
                // view's per-adapter tabs need all of them live; the mode only picks the
                // headline.
                foreach (var entry in _adapters.Values)
                    SampleAdapter(entry);

                // Failed adapters are removed AFTER the loop (removing mid-iteration would
                // invalidate the dictionary enumerator).
                var dead = new List<(long, int)>();
                foreach (var kv in _adapters)
                    if (kv.Value.Failures >= MaxConsecutiveFailures) dead.Add(kv.Key);
                foreach (var key in dead) RemoveAdapter(key);
            }

            double utilSum = 0;
            double utilMax = 0;
            double specificUtil = 0;
            bool specificFound = false;
            foreach (var entry in _adapters.Values)
            {
                utilSum += entry.Info.UtilPercent;
                if (entry.Info.UtilPercent > utilMax) utilMax = entry.Info.UtilPercent;
                if (entry.Info.Index == specificIndex)
                {
                    specificUtil = entry.Info.UtilPercent;
                    specificFound = true;
                }
            }

            double headline;
            switch (mode)
            {
                case MetricDisplayMode.Average:
                    headline = _adapters.Count > 0 ? utilSum / _adapters.Count : 0;
                    break;
                case MetricDisplayMode.Specific:
                    headline = specificFound ? specificUtil : utilMax;   // gone → the max default
                    break;
                default: // Max — the GPU default
                    headline = utilMax;
                    break;
            }

            _history.Enqueue(headline);
            while (_history.Count > MaxHistory) _history.Dequeue();

            // Publish a FRESH list each tick (the snapshot contract is never-mutated
            // objects; the GpuInfo items inside are the long-lived INPC ones).
            var gpus = new List<GpuInfo>(_adapters.Count);
            var sorted = new List<AdapterEntry>(_adapters.Values);
            sorted.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            foreach (var entry in sorted) gpus.Add(entry.Info);

            return new GpuSample
            {
                Available = _adapters.Count > 0,
                HeadlinePercent = headline,
                History = _history.ToArray(),
                Gpus = gpus,
            };
        }

        // ---------- per-tick: engine running-time deltas + temperature + memory ----------
        private void SampleAdapter(AdapterEntry entry)
        {
            var adapter = entry.Adapter;
            try
            {
                double nowUs = NowMicroseconds();

                // Engines → utilization (MAX rule).
                double maxUtil = 0;
                string topEngine = null;
                if (entry.EngineTimeSupported)
                {
                    for (int e = 0; e < entry.Engines.Count; e++)
                    {
                        var engine = entry.Engines[e];
                        if (!QueryEngineRunningTime(adapter, entry.PhysicalIndex, e, out ulong runningTimeUs))
                            throw new COMException("AdapterEngineRunningTimeMicroseconds failed");

                        engine.Util = 0;
                        if (engine.HasBaseline && runningTimeUs >= engine.PrevRunningTimeUs)
                        {
                            double dt = nowUs - engine.PrevClockUs;
                            if (dt > 0)
                            {
                                double util = (runningTimeUs - engine.PrevRunningTimeUs) / dt * 100.0;
                                engine.Util = util < 0 ? 0 : util > 100 ? 100 : util;
                            }
                        }
                        // A running-time decrease (driver reset) just re-baselines below.
                        engine.PrevRunningTimeUs = runningTimeUs;
                        engine.PrevClockUs = nowUs;
                        engine.HasBaseline = true;

                        if (engine.Util > maxUtil) { maxUtil = engine.Util; topEngine = engine.Name; }
                    }
                }
                entry.Info.UtilPercent = maxUtil;
                entry.Info.TopEngineText = topEngine == null ? "--" : topEngine;

                // Temperature (driver-gated; 0/unsupported reads as "--", like Taskmgr hiding it).
                // The query takes a 4-byte physicalAdapterIndex input, like the engine query.
                if (entry.TemperatureSupported &&
                    QueryTemperature(adapter, entry.PhysicalIndex, out float celsius) && celsius > 0)
                    entry.Info.TemperatureText = $"{(int)celsius} °C";
                else
                    entry.Info.TemperatureText = "--";

                // Memory: resident bytes per segment group + Taskmgr's display totals.
                long dedicatedUsed = 0, sharedUsed = 0;
                if (entry.MemoryUsageSupported)
                {
                    QueryMemoryUsage(adapter, entry.PhysicalIndex, DXCoreMemoryType.Dedicated, out dedicatedUsed);
                    QueryMemoryUsage(adapter, entry.PhysicalIndex, DXCoreMemoryType.Shared, out sharedUsed);
                }
                long dedicatedTotal = entry.IsIntegrated && dedicatedUsed == 0 ? 0 : entry.DedicatedCapBytes;
                entry.Info.DedicatedText = $"{ToGb(dedicatedUsed)} / {ToGb(dedicatedTotal)} GB";
                entry.Info.SharedText = $"{ToGb(sharedUsed)} / {ToGb(entry.Info.SharedTotalBytes)} GB";

                entry.Failures = 0;
            }
            catch (Exception ex)
            {
                // COMException (adapter vanished mid-tick), InvalidComObjectException (RCW
                // released), etc. Zero the metrics; Sample() drops the adapter after a few
                // strikes and the periodic re-enumeration picks it back up if it returns.
                entry.Failures++;
                Logger.WarnOnce($"gpu-query-{entry.Info.TabTitle}",
                    $"GPU 适配器（{entry.Info.TabTitle}）DXCore 查询失败（热插拔/驱动重置？）——指标归零", ex);
                entry.Info.UtilPercent = 0;
                entry.Info.TopEngineText = "--";
            }
        }

        // ---------- DXCore scalar queries (small reusable buffers, no per-call allocation) ----------
        private bool QueryEngineRunningTime(IDXCoreAdapter1 adapter, int physIndex, int engineIndex, out ulong runningTimeUs)
        {
            runningTimeUs = 0;
            Marshal.WriteInt32(_inBuf, 0, physIndex);
            Marshal.WriteInt32(_inBuf, 4, engineIndex);
            Marshal.WriteInt32(_inBuf, 8, 0);   // processId — unused for the adapter-level state
            int hr = adapter.QueryState(DXCoreAdapterState.AdapterEngineRunningTimeMicroseconds,
                (UIntPtr)12, _inBuf, (UIntPtr)8, _outBuf);
            if (hr < 0) return false;
            runningTimeUs = (ulong)Marshal.ReadInt64(_outBuf);
            return true;
        }

        private bool QueryMemoryUsage(IDXCoreAdapter1 adapter, int physIndex, DXCoreMemoryType type, out long residentBytes)
        {
            residentBytes = 0;
            Marshal.WriteInt32(_inBuf, 0, physIndex);
            Marshal.WriteInt32(_inBuf, 4, (int)type);
            int hr = adapter.QueryState(DXCoreAdapterState.AdapterMemoryUsageBytes,
                (UIntPtr)8, _inBuf, (UIntPtr)16, _outBuf);
            if (hr < 0) return false;
            // DXCoreMemoryUsage { committed, resident } — Taskmgr displays RESIDENT for
            // adapter totals (committed is only used for its per-process column).
            residentBytes = Marshal.ReadInt64(_outBuf, 8);
            if (residentBytes < 0) residentBytes = 0;
            return true;
        }

        private bool QueryTemperature(IDXCoreAdapter1 adapter, int physIndex, out float celsius)
        {
            celsius = 0;
            Marshal.WriteInt32(_inBuf, 0, physIndex);
            int hr = adapter.QueryState(DXCoreAdapterState.AdapterTemperatureCelsius,
                (UIntPtr)4, _inBuf, (UIntPtr)4, _outBuf);
            if (hr < 0) return false;
            celsius = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(_outBuf)), 0);
            return true;
        }

        private double NowMicroseconds()
        {
            if (_qpcFreq == 0) DxCoreInterop.QueryPerformanceFrequency(out _qpcFreq);
            DxCoreInterop.QueryPerformanceCounter(out long qpc);
            // μs, as a double (53-bit mantissa ≈ 285 years at μs resolution — no overflow).
            return qpc * 1_000_000.0 / _qpcFreq;
        }

        // ---------- enumeration: two attribute lists, deduped by LUID ----------
        private void Reenumerate()
        {
            try
            {
                DxCoreInterop.EnsureComApartment();
                if (_factory == null)
                {
                    var iid = DxCoreInterop.IID_IDXCoreAdapterFactory;
                    int hr = DxCoreInterop.DXCoreCreateAdapterFactory(ref iid, out _factory);
                    if (hr < 0 || _factory == null)
                    {
                        _dxcoreUnavailable = true;
                        Logger.Warn($"DXCoreCreateAdapterFactory 失败 hr=0x{hr:X8}——GPU 适配器级指标不可用，覆盖层显示 --");
                        return;
                    }
                }

                var seen = new HashSet<(long, int)>();
                EnumerateList(DxCoreInterop.AttributeGpu, seen);
                EnumerateList(DxCoreInterop.AttributeD3D12CoreCompute, seen);

                // Drop adapters that vanished since the last enumeration.
                var gone = new List<(long, int)>();
                foreach (var key in _adapters.Keys)
                    if (!seen.Contains(key)) gone.Add(key);
                foreach (var key in gone) RemoveAdapter(key);

                // (Re)number the tabs in a stable order — Task Manager's "GPU 0", "GPU 1".
                var ordered = new List<(long luid, int phys)>(_adapters.Keys);
                ordered.Sort();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var entry = _adapters[ordered[i]];
                    entry.SortOrder = i;
                    entry.Info.TabTitle = $"GPU {i}";
                    entry.Info.Index = i;   // the 显示方式 "特定 GPU" setting picks by this number
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _dxcoreUnavailable = true;   // pre-1903 Windows — no dxcore.dll at all
                Logger.Warn("dxcore.dll 或其导出缺失（1903 之前的 Windows）——GPU 指标降级为 --", ex);
            }
            catch (Exception ex)
            {
                // Any COM failure here: keep the current adapter set and retry next cycle.
                Logger.WarnOnce("dxcore-enum", "DXCore 适配器枚举失败（保留现有适配器集，下周期重试）", ex);
            }
        }

        private void EnumerateList(Guid attribute, HashSet<(long, int)> seen)
        {
            var listIid = DxCoreInterop.IID_IDXCoreAdapterList;
            // CreateAdapterList takes a raw const GUID* — hand it a buffer holding the
            // attribute bytes (array marshaling would pass metadata, not the GUID).
            byte[] guidBytes = attribute.ToByteArray();
            IntPtr attrBuf = Marshal.AllocHGlobal(16);
            IDXCoreAdapterList list = null;
            try
            {
                Marshal.Copy(guidBytes, 0, attrBuf, 16);
                int hrList = _factory.CreateAdapterList(1, attrBuf, ref listIid, out list);
                if (hrList < 0 || list == null) return;

                uint count = list.GetAdapterCount();
                for (uint i = 0; i < count; i++)
                {
                    var adapterIid = DxCoreInterop.IID_IDXCoreAdapter1;
                    IDXCoreAdapter1 probe = null;
                    try
                    {
                        int hr = list.GetAdapter(i, ref adapterIid, out probe);
                        if (hr < 0 || probe == null) continue;
                        if (!probe.IsValid() || !GetByteProperty(probe, DXCoreAdapterProperty.IsHardware))
                            continue;   // software device (Basic Render) — Taskmgr filters it too

                        long luid = GetInt64Property(probe, DXCoreAdapterProperty.InstanceLuid);
                        if (luid == 0) continue;   // LUID query failed — never dedupe onto key 0
                        int physicalCount = Math.Max(1, GetInt32Property(probe, DXCoreAdapterProperty.PhysicalAdapterCount));
                        for (int phys = 0; phys < physicalCount; phys++)
                        {
                            var key = (luid, phys);
                            seen.Add(key);
                            if (_adapters.ContainsKey(key)) continue;   // already tracked — keep RCW + baselines

                            // Every entry owns its OWN RCW (one fresh GetAdapter reference) so
                            // RemoveAdapter can release it independently — matters for LDA
                            // devices where several entries share one underlying adapter.
                            IDXCoreAdapter1 owned = null;
                            hr = list.GetAdapter(i, ref adapterIid, out owned);
                            if (hr < 0 || owned == null) continue;
                            try
                            {
                                _adapters[key] = CreateEntry(owned, luid, phys);
                                owned = null;   // ownership moved to the entry
                            }
                            finally { if (owned != null) Marshal.ReleaseComObject(owned); }
                        }
                    }
                    finally
                    {
                        if (probe != null) Marshal.ReleaseComObject(probe);
                    }
                }
            }
            finally
            {
                if (list != null) Marshal.ReleaseComObject(list);
                Marshal.FreeHGlobal(attrBuf);
            }
        }

        private AdapterEntry CreateEntry(IDXCoreAdapter1 adapter, long luid, int phys)
        {
            string name = GetStringProperty(adapter, DXCoreAdapterProperty.DriverDescription);
            if (string.IsNullOrEmpty(name)) name = $"GPU (LUID {luid:X})";

            long dedicated = GetInt64Property(adapter, DXCoreAdapterProperty.DedicatedAdapterMemory);
            long dedicatedSystem = GetInt64Property(adapter, DXCoreAdapterProperty.DedicatedSystemMemory);
            long shared = GetInt64Property(adapter, DXCoreAdapterProperty.SharedSystemMemory);
            long cap = ApplyTaskmgrCapRounding(dedicated + dedicatedSystem);

            var entry = new AdapterEntry
            {
                Adapter = adapter,
                Info = new GpuInfo(name, Math.Max(0, shared)),
                PhysicalIndex = phys,
                EngineTimeSupported = adapter.IsQueryStateSupported(DXCoreAdapterState.AdapterEngineRunningTimeMicroseconds),
                MemoryUsageSupported = adapter.IsQueryStateSupported(DXCoreAdapterState.AdapterMemoryUsageBytes),
                TemperatureSupported = adapter.IsQueryStateSupported(DXCoreAdapterState.AdapterTemperatureCelsius),
                IsIntegrated = GetByteProperty(adapter, DXCoreAdapterProperty.IsIntegrated),
                DedicatedCapBytes = cap,
            };

            int engineCount = GetEngineCount(adapter, phys);
            for (int e = 0; e < engineCount; e++)
                entry.Engines.Add(new EngineEntry { Name = GetEngineName(adapter, phys, e) });
            return entry;
        }

        private void RemoveAdapter((long, int) key)
        {
            if (!_adapters.TryGetValue(key, out var entry)) return;
            _adapters.Remove(key);
            if (entry.Adapter != null) Marshal.ReleaseComObject(entry.Adapter);
        }

        // ---------- DXCore property helpers (enumeration-time only) ----------
        private long GetInt64Property(IDXCoreAdapter1 adapter, DXCoreAdapterProperty property)
        {
            int hr = adapter.GetProperty(property, (UIntPtr)8, _outBuf);
            return hr < 0 ? 0 : Marshal.ReadInt64(_outBuf);
        }

        // uint32 properties (PhysicalAdapterCount) — queried with their exact size, like Taskmgr.
        private int GetInt32Property(IDXCoreAdapter1 adapter, DXCoreAdapterProperty property)
        {
            int hr = adapter.GetProperty(property, (UIntPtr)4, _outBuf);
            return hr < 0 ? 0 : Marshal.ReadInt32(_outBuf);
        }

        private bool GetByteProperty(IDXCoreAdapter1 adapter, DXCoreAdapterProperty property)
        {
            int hr = adapter.GetProperty(property, (UIntPtr)1, _outBuf);
            return hr >= 0 && Marshal.ReadByte(_outBuf) != 0;
        }

        private string GetStringProperty(IDXCoreAdapter1 adapter, DXCoreAdapterProperty property)
        {
            int hr = adapter.GetPropertySize(property, out UIntPtr size);
            if (hr < 0 || size == UIntPtr.Zero || (ulong)size > 4096) return null;
            IntPtr buf = Marshal.AllocHGlobal(new IntPtr((long)(ulong)size));
            try
            {
                Marshal.WriteByte(buf, (byte)0);   // keep it terminated even if the driver writes nothing
                hr = adapter.GetProperty(property, size, buf);
                // DXCore DriverDescription is a single-byte (ANSI) string, NOT UTF-16.
                return hr < 0 ? null : Marshal.PtrToStringAnsi(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private int GetEngineCount(IDXCoreAdapter1 adapter, int physIndex)
        {
            Marshal.WriteInt32(_inBuf, 0, physIndex);
            int hr = adapter.GetPropertyWithInput(DXCoreAdapterProperty.AdapterEngineCount,
                (UIntPtr)4, _inBuf, (UIntPtr)4, _outBuf);
            return hr < 0 ? 0 : Math.Max(0, Marshal.ReadInt32(_outBuf));
        }

        private string GetEngineName(IDXCoreAdapter1 adapter, int physIndex, int engineIndex)
        {
            // DXCoreEngineNamePropertyInput { {physIdx, engineIdx}, uint32 length, wchar_t* buffer }
            Marshal.WriteInt32(_inBuf, 0, physIndex);
            Marshal.WriteInt32(_inBuf, 4, engineIndex);
            Marshal.WriteInt32(_inBuf, 8, MaxEngineNameChars);
            Marshal.WriteIntPtr(_inBuf, 16, _nameBuf);   // 8-align: pointer lands at +16 on x64
            Marshal.WriteInt64(_nameBuf, 0, 0);          // zero-terminated even if the driver writes nothing
            int hr = adapter.GetPropertyWithInput(DXCoreAdapterProperty.AdapterEngineName,
                (UIntPtr)24, _inBuf, (UIntPtr)4, _outBuf);
            string name = hr < 0 ? null : Marshal.PtrToStringUni(_nameBuf);
            return string.IsNullOrEmpty(name) ? "Engine" : name;   // Taskmgr's fallback
        }

        // Taskmgr's dedicated-cap display math (FillAdapterProperties): round the raw
        // dedicated total UP to a multiple of min(nextPow2(cap/10 − 1), 512 MB) so the UI
        // reads "8.0 GB" instead of the raw byte count.
        private static long ApplyTaskmgrCapRounding(long capBytes)
        {
            if (capBytes <= 10) return capBytes;
            ulong cap = (ulong)capBytes;
            ulong unit = NextPow2GreaterThan(cap / 10 - 1);
            const ulong maxUnit = 512UL * 1024 * 1024;
            if (unit == 0 || unit > maxUnit) unit = maxUnit;
            ulong rounded = cap % unit == 0 ? cap : (cap / unit + 1) * unit;
            return rounded > long.MaxValue ? capBytes : (long)rounded;
        }

        private static ulong NextPow2GreaterThan(ulong x)
        {
            x |= x >> 1; x |= x >> 2; x |= x >> 4; x |= x >> 8; x |= x >> 16; x |= x >> 32;
            return x + 1;
        }

        private static string ToGb(long bytes)
        {
            if (bytes < 0) bytes = 0;
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F1");
        }
    }
}
