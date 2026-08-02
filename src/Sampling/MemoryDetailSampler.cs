using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// The Task Manager "Memory" breakdown: in-use (compressed), available, committed/limit,
    /// cached, paged pool, non-paged pool. Sources mirror taskmgr's memory panel — verified
    /// against a live Task Manager on Win11 build 26200.
    /// </summary>
    internal struct MemoryDetail
    {
        public long InUseBytes;
        public long CompressedBytes;
        public long AvailableBytes;
        public long CommittedBytes;
        public long CommitLimitBytes;
        public long CachedBytes;
        public long PagedPoolBytes;
        public long NonPagedPoolBytes;
        public long ModifiedBytes;        // modified page list (class-80 @ 0x10) — for the composition bar
    }

    /// <summary>
    /// Reads the Task Manager memory breakdown. Available/committed/commit-limit/in-use come
    /// from <see cref="SystemInfo.GlobalMemoryStatusEx"/> (version-stable); the paged/non-paged
    /// pools come from <c>NtQuerySystemInformation(SystemPerformanceInformation)</c>; the standby
    /// cache ("Cached") comes from <c>NtQuerySystemInformation(SystemMemoryListInformation)</c>.
    /// <see cref="MemoryDetail.CompressedBytes"/> is NOT filled here — it is the "Memory
    /// Compression" process's working set, captured by <see cref="ProcessCpuSampler"/>'s process
    /// walk and stitched in by <see cref="SystemSampler"/>. Stateless; lives on the taskbar STA thread.
    /// </summary>
    /// <remarks>
    /// The class-2 offsets (0x70 paged, 0x74 non-paged) are this build's layout — the struct's
    /// early fields sit +8 bytes vs the legacy phnt definition on modern Windows, pinned by
    /// matching values against Task Manager. NOT portable across Windows versions without
    /// re-verifying.
    /// </remarks>
    internal sealed class MemoryDetailSampler
    {
        private const int PageSize = 4096;
        private const int PerfBufferSize = 0x178;     // class 2
        private const int MemListBufferSize = 0x400;  // class 80

        public MemoryDetail Sample()
        {
            var mem = new SystemInfo.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<SystemInfo.MEMORYSTATUSEX>() };
            SystemInfo.GlobalMemoryStatusEx(ref mem);

            var d = new MemoryDetail
            {
                AvailableBytes   = (long)mem.ullAvailPhys,
                InUseBytes       = (long)(mem.ullTotalPhys - mem.ullAvailPhys),
                CommittedBytes   = (long)(mem.ullTotalPageFile - mem.ullAvailPageFile),
                CommitLimitBytes = (long)mem.ullTotalPageFile,
                // CompressedBytes: filled by SystemSampler from the process walk.
            };

            // class 2 (SystemPerformanceInformation): paged/non-paged pool pages.
            IntPtr perf = Marshal.AllocHGlobal(PerfBufferSize);
            try
            {
                if (SystemInfo.NtQuerySystemInformation(
                        SystemInfo.SYSTEM_PERFORMANCE_INFO_CLASS, perf, (uint)PerfBufferSize, out _) == SystemInfo.STATUS_SUCCESS)
                {
                    d.PagedPoolBytes    = (long)Marshal.ReadInt32(perf, 0x70) * PageSize;
                    d.NonPagedPoolBytes = (long)Marshal.ReadInt32(perf, 0x74) * PageSize;
                }
            }
            finally { Marshal.FreeHGlobal(perf); }

            // class 80 (SystemMemoryListInformation): standby aggregate (8 priority lists,
            // 0x28..0x60) — pure standby, used for the composition bar's 备用 segment and
            // available. The breakdown's 已缓存 = standby + modified (Task Manager's "Cached").
            // modified @ 0x10.
            IntPtr ml = Marshal.AllocHGlobal(MemListBufferSize);
            try
            {
                if (SystemInfo.NtQuerySystemInformation(
                        SystemInfo.SYSTEM_MEMORY_LIST_INFO_CLASS, ml, (uint)MemListBufferSize, out _) == SystemInfo.STATUS_SUCCESS)
                {
                    long standbyPages = 0;
                    for (int off = 0x28; off <= 0x60; off += 8)
                        standbyPages += Marshal.ReadInt64(ml, off);
                    d.CachedBytes = standbyPages * PageSize;
                    d.ModifiedBytes = Marshal.ReadInt64(ml, 0x10) * PageSize;
                }
            }
            finally { Marshal.FreeHGlobal(ml); }

            return d;
        }
    }
}
