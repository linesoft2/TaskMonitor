using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// A RAM-only sample: memory load %, total/used physical bytes (in GiB), and the
    /// recent overall RAM% history (for the detail popup's area chart).
    /// </summary>
    internal struct RamSample
    {
        public double RamPercent;   // 0–100
        public double TotalRamGb;
        public double UsedRamGb;
        public double[] RamHistory; // recent overall RAM% for the graph (oldest→newest)
    }

    /// <summary>
    /// Samples RAM usage straight from <see cref="SystemInfo.GlobalMemoryStatusEx"/>'s
    /// memory load and total/available physical bytes, and keeps a 60-tick rolling
    /// history for the detail popup's area chart. Per-call except for the history buffer.
    /// </summary>
    internal sealed class RamSampler
    {
        private const double BYTES_PER_GB = 1024.0 * 1024.0 * 1024.0;
        private const int MaxHistory = 60;

        private readonly Queue<double> _ramHistory = new Queue<double>(MaxHistory);

        public RamSample Sample()
        {
            var mem = new SystemInfo.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<SystemInfo.MEMORYSTATUSEX>() };
            SystemInfo.GlobalMemoryStatusEx(ref mem);
            double totalGb = mem.ullTotalPhys / BYTES_PER_GB;
            double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / BYTES_PER_GB;

            double ram = mem.dwMemoryLoad;
            if (ram < 0) ram = 0;
            if (ram > 100) ram = 100;

            _ramHistory.Enqueue(ram);
            while (_ramHistory.Count > MaxHistory) _ramHistory.Dequeue();

            return new RamSample
            {
                RamPercent = ram,
                TotalRamGb = totalGb,
                UsedRamGb = usedGb,
                RamHistory = _ramHistory.ToArray(),
            };
        }
    }
}
