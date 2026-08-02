using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// A CPU-only sample: overall + per-core usage, recent history, and the
    /// processor's name/base frequency/logical-core count (read once at construction).
    /// </summary>
    internal struct CpuSample
    {
        public double CpuPercent;        // 0–100 overall
        public double[] PerCoreUsage;    // 0–100 per logical core
        public double[] CpuHistory;      // recent overall CPU% for the graph
        public string CpuName;
        public uint CpuMhz;              // base frequency from registry
        public uint NumLogicalProcessors;
    }

    /// <summary>
    /// Samples overall/per-core CPU usage by accumulating time deltas between
    /// calls. Ported from task_monitor_3's <c>cpu_monitor.rs</c>:
    /// CPU = 1 - idle_delta/(kernel_delta + user_delta); kernel_time already
    /// includes idle_time, so the denominator is kernel+user, not +idle.
    /// All native calls go through <see cref="SystemInfo"/>.
    /// </summary>
    internal sealed class CpuSampler
    {
        // Per-core time deltas (sampler-private; not a Win32 struct).
        private struct PerCoreTimes
        {
            public long Idle;
            public long Kernel;
            public long User;
        }

        private const int MaxHistory = 60;

        private readonly Queue<double> _cpuHistory = new Queue<double>(MaxHistory);

        private long _prevIdle;
        private long _prevKernel;
        private long _prevUser;
        private List<PerCoreTimes> _prevPerCore;

        private readonly string _cpuName;
        private readonly uint _cpuMhz;
        private readonly uint _numLogicalProcessors;

        public CpuSampler()
        {
            var sysInfo = new SystemInfo.SYSTEM_INFO();
            SystemInfo.GetSystemInfo(ref sysInfo);
            _numLogicalProcessors = sysInfo.dwNumberOfProcessors;
            if (_numLogicalProcessors == 0) _numLogicalProcessors = 1;

            ReadCpuRegistry(out string name, out uint mhz);
            _cpuName = name;
            _cpuMhz = mhz;

            // Establish baselines so the first Sample() measures a real interval.
            SystemInfo.GetSystemTimes(out var idle, out var kernel, out var user);
            _prevIdle = ToInt64(idle);
            _prevKernel = ToInt64(kernel);
            _prevUser = ToInt64(user);
            _prevPerCore = SamplePerCoreTimes();
        }

        public CpuSample Sample()
        {
            SystemInfo.GetSystemTimes(out var idle, out var kernel, out var user);
            long curIdle = ToInt64(idle);
            long curKernel = ToInt64(kernel);
            long curUser = ToInt64(user);

            long idleDelta = curIdle - _prevIdle;
            long kernelDelta = curKernel - _prevKernel;
            long userDelta = curUser - _prevUser;
            _prevIdle = curIdle;
            _prevKernel = curKernel;
            _prevUser = curUser;

            // kernel_time includes idle_time, so total = kernel + user (not + idle).
            long total = kernelDelta + userDelta;
            double cpu = total > 0 ? (1.0 - (double)idleDelta / total) * 100.0 : 0.0;
            if (cpu < 0) cpu = 0;
            if (cpu > 100) cpu = 100;

            _cpuHistory.Enqueue(cpu);
            while (_cpuHistory.Count > MaxHistory) _cpuHistory.Dequeue();

            var newPerCore = SamplePerCoreTimes();
            double[] perCore = ComputePerCore(_prevPerCore, newPerCore);
            _prevPerCore = newPerCore;

            return new CpuSample
            {
                CpuPercent = cpu,
                PerCoreUsage = perCore,
                CpuHistory = _cpuHistory.ToArray(),
                CpuName = _cpuName,
                CpuMhz = _cpuMhz,
                NumLogicalProcessors = _numLogicalProcessors,
            };
        }

        private static long ToInt64(SystemInfo.FILETIME ft)
            => ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

        // ---------- per-core via NtQuerySystemInformation ----------
        private List<PerCoreTimes> SamplePerCoreTimes()
        {
            int n = (int)_numLogicalProcessors;
            if (n <= 0) return new List<PerCoreTimes>();

            int size = Marshal.SizeOf<SystemInfo.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
            int needed = n * size;
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(needed);
                int status = SystemInfo.NtQuerySystemInformation(
                    SystemInfo.SYSTEM_PROCESSOR_PERFORMANCE_INFO_CLASS, buf, (uint)needed, out uint returned);

                if (status == SystemInfo.STATUS_SUCCESS)
                {
                    int count = (int)(returned / size);
                    if (count <= 0) count = n;
                    var result = new List<PerCoreTimes>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<SystemInfo.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + i * size);
                        result.Add(new PerCoreTimes { Idle = info.IdleTime, Kernel = info.KernelTime, User = info.UserTime });
                    }
                    return result;
                }
            }
            catch
            {
                // Per-core is best-effort; an empty list hides that section in the UI.
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
            return new List<PerCoreTimes>();
        }

        private static double[] ComputePerCore(List<PerCoreTimes> prev, List<PerCoreTimes> cur)
        {
            if (prev == null || cur == null || prev.Count == 0 || prev.Count != cur.Count)
                return Array.Empty<double>();

            var result = new double[cur.Count];
            for (int i = 0; i < cur.Count; i++)
            {
                long idle = cur[i].Idle - prev[i].Idle;
                long kernel = cur[i].Kernel - prev[i].Kernel;
                long user = cur[i].User - prev[i].User;
                long t = kernel + user; // kernel includes idle
                result[i] = t > 0 ? (1.0 - (double)idle / t) * 100.0 : 0.0;
                if (result[i] < 0) result[i] = 0;
                if (result[i] > 100) result[i] = 100;
            }
            return result;
        }

        // ---------- CPU name + base MHz from the registry ----------
        private static void ReadCpuRegistry(out string name, out uint mhz)
        {
            name = "Unknown CPU";
            mhz = 0;

            const string subKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
            if (SystemInfo.RegOpenKeyEx(SystemInfo.HKEY_LOCAL_MACHINE, subKey, 0, SystemInfo.KEY_READ, out IntPtr hKey) != 0 || hKey == IntPtr.Zero)
                return;

            try
            {
                // ProcessorNameString (REG_SZ)
                int cb = 512;
                byte[] buf = new byte[cb];
                if (SystemInfo.RegQueryValueExW(hKey, "ProcessorNameString", IntPtr.Zero, out int type, buf, ref cb) == 0
                    && type == SystemInfo.REG_SZ && cb >= 2)
                {
                    int charCount = cb / 2;
                    var chars = new char[charCount];
                    for (int i = 0; i < charCount; i++)
                        chars[i] = (char)(buf[i * 2] | (buf[i * 2 + 1] << 8));
                    name = new string(chars).TrimEnd('\0').Trim();
                }

                // ~MHz (REG_DWORD, 4 bytes)
                cb = 4;
                byte[] dw = new byte[4];
                if (SystemInfo.RegQueryValueExW(hKey, "~MHz", IntPtr.Zero, out type, dw, ref cb) == 0 && cb == 4)
                    mhz = (uint)(dw[0] | (dw[1] << 8) | (dw[2] << 16) | (dw[3] << 24));
            }
            finally
            {
                SystemInfo.RegCloseKey(hKey);
            }
        }
    }
}
