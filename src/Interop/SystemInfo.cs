using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// P/Invoke for the system-metric APIs the Sampling layer calls: CPU times
    /// (GetSystemTimes, NtQuerySystemInformation), memory status (GlobalMemoryStatusEx),
    /// processor info (GetSystemInfo), and the registry reads behind the CPU name/base
    /// frequency. This is the native boundary for samplers — window/shell interop lives
    /// in WindowInterop.cs.
    /// </summary>
    internal static class SystemInfo
    {
        // ---------- structures ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        // NtQuerySystemInformation(SystemProcessorPerformanceInformation = 8) layout.
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public int InterruptCount;
            public int Reserved;
        }

        // NtQuerySystemInformation(SystemProcessInformation = 5): one of these per process, chained
        // by NextEntryOffset. x64 layout — declared through the IO counters. The per-thread
        // array (0x50-byte SYSTEM_THREAD_INFORMATION records) follows at +0x100, and on 24H2+
        // each entry carries a PROCESS_DISK_COUNTERS trailer right after its threads
        // (BytesRead@+0, BytesWritten@+8 — pure disk bytes, the source of Task Manager's
        // per-process disk column, reversed from Taskmgr.exe), then PROCESS_ENERGY_VALUES.
        // Used by the per-process CPU sampler (the same call Task Manager makes for its
        // process list) and by the disk detail's per-process read/write rates (trailer deltas;
        // ReadTransferCount/WriteTransferCount here count ALL I/O — kept as the fallback).
        [StructLayout(LayoutKind.Sequential, Pack = 0)]
        internal struct SYSTEM_PROCESS_INFORMATION
        {
            public uint NextEntryOffset;          // 0x00
            public uint NumberOfThreads;          // 0x04
            public long WorkingSetPrivateSize;    // 0x08
            public uint HardFaultCount;           // 0x10
            public uint NumberOfThreadsHighWatermark; // 0x14
            public ulong CycleTime;               // 0x18  (CPU cycles consumed — the cycle-based CPU% source)
            public long CreateTime;               // 0x20
            public long UserTime;                 // 0x28
            public long KernelTime;               // 0x30
            public UNICODE_STRING ImageName;      // 0x38
            public int BasePriority;              // 0x48
            // 4 bytes alignment padding here (0x4c) so the IntPtr below lands 8-aligned at 0x50.
            public IntPtr UniqueProcessId;        // 0x50  (PID; 0 == Idle)
            public IntPtr InheritedFromUniqueProcessId; // 0x58
            public uint HandleCount;              // 0x60
            public uint SessionId;                // 0x64
            public long UniqueProcessKey;         // 0x68
            public long PeakVirtualSize;          // 0x70
            public long VirtualSize;              // 0x78
            public uint PageFaultCount;           // 0x80
            // 4 bytes alignment padding here (0x84).
            public long PeakWorkingSet;           // 0x88
            public long WorkingSet;               // 0x90
            public long QuotaPeakPagedPoolUsage;  // 0x98
            public long QuotaPagedPoolUsage;      // 0xA0
            public long QuotaPeakNonPagedPoolUsage; // 0xA8
            public long QuotaNonPagedPoolUsage;   // 0xB0
            public long PagefileUsage;            // 0xB8
            public long PeakPagefileUsage;        // 0xC0
            public long PrivatePageCount;         // 0xC8
            public long ReadOperationCount;       // 0xD0
            public long WriteOperationCount;      // 0xD8
            public long OtherOperationCount;      // 0xE0
            public long ReadTransferCount;        // 0xE8  (cumulative I/O read bytes — the disk read-rate source)
            public long WriteTransferCount;       // 0xF0  (cumulative I/O write bytes)
            public long OtherTransferCount;       // 0xF8
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UNICODE_STRING
        {
            public ushort Length;          // bytes (not including terminator)
            public ushort MaximumLength;
            private int _padding;          // align Buffer to 8
            public IntPtr Buffer;          // wide string, NOT null-terminated guaranteed
        }

        // CallNtPowerInformation(ProcessorInformation) returns one of these per
        // logical processor; we read CurrentMhz for the live CPU clock speed.
        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESSOR_POWER_INFORMATION
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        // ---------- kernel32 ----------
        [DllImport("kernel32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll")]
        internal static extern void GetSystemInfo(ref SYSTEM_INFO lpSystemInfo);

        [DllImport("kernel32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = false)]
        internal static extern ulong GetTickCount64();

        [DllImport("kernel32.dll", SetLastError = false)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        // ---------- idle working-set trim (Task Manager "内存" column) ----------
        /// <summary>
        /// Full GC + <c>SetProcessWorkingSetSize(-1,-1)</c>: collect the managed garbage
        /// first (so its pages are actually free), then hand every page not touched
        /// recently back to the standby list. The app idles almost all the time (a 1s
        /// sample tick), so one-touch pages — startup/JIT, prewarm, a closed popup's UI
        /// tree — are pure resident-ballast between events. Reopening a panel pays a few
        /// ms of soft faults (pages come back from standby, not disk) — an order of
        /// magnitude cheaper than the JIT/BAML cold-start the prewarm exists to remove,
        /// so trimming right after prewarm does NOT undo the prewarm's value. Called by
        /// App at event points only (post-prewarm, last detail window closed) — never
        /// on a timer (periodic trims are pure page-fault churn).
        /// </summary>
        internal static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        // ---------- ntdll ----------
        internal const uint SYSTEM_PROCESSOR_PERFORMANCE_INFO_CLASS = 8;
        internal const uint SYSTEM_PROCESS_INFORMATION_CLASS = 5; // SystemProcessInformation (Task Manager's process list)
        internal const uint SYSTEM_PERFORMANCE_INFO_CLASS = 2;    // SystemPerformanceInformation — paged/non-paged pool + compressed (page counts; same source as Task Manager's memory panel)
        internal const uint SYSTEM_MEMORY_LIST_INFO_CLASS = 80;    // SystemMemoryListInformation — standby aggregate → Task Manager's "Cached"
        internal const int STATUS_SUCCESS = 0;
        internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(
            uint systemInformationClass,
            IntPtr systemInformation,
            uint systemInformationLength,
            out uint returnLength);

        // ---------- kernel32 (per-process exe path — for the process icons) ----------
        // PROCESS_QUERY_LIMITED_INFORMATION lets us read the image name of most processes
        // (including some protected ones) without PROCESS_QUERY_INFORMATION rights.
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        // ---------- shared per-process exe path (process CPU + net samplers) ----------
        /// <summary>
        /// Resolves the full image path of <paramref name="pid"/> via OpenProcess +
        /// QueryFullProcessImageNameW, or null when it can't be opened (protected/system/
        /// dead process). PROCESS_QUERY_LIMITED_INFORMATION reads most processes incl.
        /// some protected ones. Shared by the per-process CPU and network samplers.
        /// </summary>
        internal static string QueryProcessImageFileName(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(260);
                for (int i = 0; i < 3; i++)
                {
                    uint size = (uint)sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                        return sb.ToString();
                    // Too small: grow (QueryFullProcessImageName reports the needed size in `size`).
                    sb.Capacity = Math.Max((int)size, sb.Capacity * 2);
                }
                return null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        // ---------- powrprof (base/nominal CPU frequency) ----------
        // MaxMhz is the processor's nominal frequency (the base, NOT turbo).
        // POWER_INFORMATION_LEVEL.ProcessorInformation = 11.
        internal const int POWER_INFORMATION_PROCESSOR = 11;

        [DllImport("powrprof.dll", SetLastError = false)]
        internal static extern int CallNtPowerInformation(
            int informationLevel,
            IntPtr inputBuffer,
            int inputBufferSize,
            IntPtr outputBuffer,
            int outputBufferSize);

        // ---------- pdh (live, turbo-aware CPU frequency) ----------
        // % Processor Performance tracks turbo/throttle as a percentage of nominal,
        // so the live clock = base MHz * (ProcessorPerformance% / 100). That is how
        // Task Manager shows a turbo-aware speed — the power API's CurrentMhz is
        // capped at base and never reflects turbo boost.
        internal const string CpuProcessorPerformanceCounterPath =
            @"\Processor Information(_Total)\% Processor Performance";
        internal const uint PDH_FMT_DOUBLE = 0x00000200;

        [StructLayout(LayoutKind.Sequential)]
        internal struct PDH_FMT_COUNTERVALUE
        {
            public uint CStatus;     // PDH_CSTATUS_VALID_DATA == 0 means the value is good
            public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        internal static extern uint PdhOpenQueryW(IntPtr lpDataSource, uint dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        internal static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, uint dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll", SetLastError = false)]
        internal static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll", SetLastError = false)]
        internal static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

        // Wildcard-counter read: fills ItemBuffer with PDH_FMT_COUNTERVALUE_ITEM_W records
        // (szName@+0, CStatus@+8, doubleValue@+0x10 on x64, stride 0x18). Pass IntPtr.Zero
        // first to size the buffer (returns PDH_MORE_DATA + the required lpdwBufferSize).
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        internal static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr ItemBuffer);

        internal const uint PDH_MORE_DATA = 0x800007D2;
        internal const uint PDH_CSTATUS_NEW_DATA = 0x00000001; // 0 (VALID_DATA) and 1 are both usable values

        [DllImport("pdh.dll", SetLastError = false)]
        internal static extern uint PdhCloseQuery(IntPtr hQuery);

        // ---------- advapi32 (registry — CPU name + base MHz) ----------
        internal static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));
        internal const int KEY_READ = 0x20019;
        internal const int REG_SZ = 1;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out int lpType,
            byte[] lpData, ref int lpcbData);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern int RegCloseKey(IntPtr hKey);

        // ---------- ntdll (true OS version) ----------
        // The un-manifested-API-safe way to read the real OS version (GetVersionEx lies
        // without a supportedOS GUID; the registry CurrentBuild can be redirected). Used
        // by the taskbar overlay to pick the Win11 vs classical (Win10) taskbar family,
        // mirroring TrafficMonitor's CWinVersionHelper. build comes back with the
        // Win32k version bits in the high word — mask with 0xFFFF at the call site.
        [DllImport("ntdll.dll")]
        internal static extern void RtlGetNtVersionNumbers(out uint major, out uint minor, out uint build);
    }
}
