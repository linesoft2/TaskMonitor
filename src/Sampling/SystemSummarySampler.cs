using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// The live-clock + uptime parts of the CPU detail footer. (The process /
    /// thread / handle totals used to live here too, but now come from
    /// <see cref="ProcessCpuSampler"/>'s single process enumeration, so this
    /// sampler no longer enumerates processes at all.)
    /// </summary>
    internal struct SystemSummarySample
    {
        public uint CurrentMhz;      // live clock (turbo-aware), for the CPU footer
        public long UptimeMs;        // ms since boot
    }

    /// <summary>
    /// Samples the live CPU frequency (turbo-aware: base frequency ×
    /// <c>% Processor Performance</c>, the same source Task Manager uses) and the
    /// boot uptime. Single-threaded like the other samplers (lives on the taskbar
    /// STA thread); cheap enough to recompute per 1s tick.
    /// </summary>
    internal sealed class SystemSummarySampler
    {
        private readonly int _logicalCores;
        private readonly uint _baseMhz;         // nominal/base frequency (MaxMhz)

        // PDH query for % Processor Performance. The counter needs a delta between
        // two samples, so we open + seed it in the ctor; the first per-second read
        // then has a real interval to diff over.
        private IntPtr _pdhQuery = IntPtr.Zero;
        private IntPtr _perfCounter = IntPtr.Zero;

        public SystemSummarySampler()
        {
            var sysInfo = new SystemInfo.SYSTEM_INFO();
            SystemInfo.GetSystemInfo(ref sysInfo);
            _logicalCores = (int)sysInfo.dwNumberOfProcessors;
            if (_logicalCores <= 0) _logicalCores = 1;

            _baseMhz = QueryPowerMaxMhz();

            bool opened = SystemInfo.PdhOpenQueryW(IntPtr.Zero, 0, out _pdhQuery) == 0
                          && SystemInfo.PdhAddEnglishCounterW(_pdhQuery,
                                SystemInfo.CpuProcessorPerformanceCounterPath, 0, out _perfCounter) == 0;
            if (opened)
            {
                SystemInfo.PdhCollectQueryData(_pdhQuery); // seed baseline (discarded)
            }
            else
            {
                // PDH unavailable on this machine — QueryCurrentMhz falls back to base.
                if (_pdhQuery != IntPtr.Zero) SystemInfo.PdhCloseQuery(_pdhQuery);
                _pdhQuery = IntPtr.Zero;
                _perfCounter = IntPtr.Zero;
            }
        }

        public SystemSummarySample Sample()
        {
            return new SystemSummarySample
            {
                CurrentMhz = QueryCurrentMhz(),
                UptimeMs = (long)SystemInfo.GetTickCount64(),
            };
        }

        // ---------- live CPU frequency ----------
        // % Processor Performance is the ratio of actual cycles to nominal, so it
        // exceeds 100% under turbo boost: live MHz = base * pct / 100. This is the
        // PDH counter Task Manager reads (via PCW); the power API's CurrentMhz is
        // capped at base and would always read as the base speed.
        private uint QueryCurrentMhz()
        {
            double pct = QueryProcessorPerformancePct();
            if (pct > 0 && _baseMhz > 0)
                return (uint)Math.Round(_baseMhz * pct / 100.0);
            return _baseMhz; // fallback: at least base, never a dash
        }

        private double QueryProcessorPerformancePct()
        {
            if (_pdhQuery == IntPtr.Zero || _perfCounter == IntPtr.Zero) return -1;
            if (SystemInfo.PdhCollectQueryData(_pdhQuery) != 0) return -1;
            if (SystemInfo.PdhGetFormattedCounterValue(
                    _perfCounter, SystemInfo.PDH_FMT_DOUBLE, out _, out var val) != 0)
                return -1;
            return val.CStatus == 0 ? val.DoubleValue : -1;
        }

        // Base/nominal frequency: average MaxMhz across logical cores. Read once.
        private uint QueryPowerMaxMhz()
        {
            int elem = Marshal.SizeOf<SystemInfo.PROCESSOR_POWER_INFORMATION>();
            int size = _logicalCores * elem;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (SystemInfo.CallNtPowerInformation(
                        SystemInfo.POWER_INFORMATION_PROCESSOR, IntPtr.Zero, 0, buf, size) != 0)
                    return 0;

                double sum = 0;
                int count = 0;
                for (int i = 0; i < _logicalCores; i++)
                {
                    var info = Marshal.PtrToStructure<SystemInfo.PROCESSOR_POWER_INFORMATION>(buf + i * elem);
                    if (info.MaxMhz > 0) { sum += info.MaxMhz; count++; }
                }
                return count > 0 ? (uint)Math.Round(sum / count) : 0;
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }
    }
}
