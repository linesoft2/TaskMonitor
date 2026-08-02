using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// One row of a per-process detail list — used by the CPU detail (top-by-CPU),
    /// the RAM detail (top-by-memory), and the Network detail (top-by-throughput). The
    /// sampler fills <see cref="Name"/>, <see cref="Pid"/>, <see cref="CpuPercent"/>,
    /// <see cref="WorkingSetBytes"/>, <see cref="NetUpBytesPerSec"/>/
    /// <see cref="NetDownBytesPerSec"/> and <see cref="ExePath"/>; <see cref="Icon"/> is
    /// left null and filled by the UI layer (which owns the icon cache and must extract
    /// on the UI/STA thread).
    /// </summary>
    internal sealed class ProcessInfo
    {
        public string Name { get; set; } = "";
        public int Pid { get; set; }
        public double CpuPercent { get; set; }      // CPU detail list (cycle-based, whole-machine share)
        public long WorkingSetBytes { get; set; }   // RAM detail list (private working set)
        public long NetUpBytesPerSec { get; set; }  // Network detail list (upload bytes/s, from SRUM)
        public long NetDownBytesPerSec { get; set; }// Network detail list (download bytes/s, from SRUM)
        public long DiskReadBytesPerSec { get; set; }  // Disk detail list (disk read bytes/s — from the process walk's PROCESS_DISK_COUNTERS trailer, Task Manager's own disk-column source)
        public long DiskWriteBytesPerSec { get; set; } // Disk detail list (disk write bytes/s)
        public double GpuPercent { get; set; }         // GPU detail list (max over the process's GPU engines — from PDH, Task Manager's GPU-column source)
        public string GpuEngineName { get; set; }      // GPU detail list: dominant engine ("3D", "Copy", …), Task Manager's 引擎 column minus the "GPU N" prefix
        public string ExePath { get; set; }
        public ImageSource Icon { get; set; }
        // >1 when the row merges several same-path processes (设置 → 采样项目 → 合并相同程序 —
        // ProcessListMerger sums the members' values into this row). Shown by the row tag
        // chip as "×N" (TagText); 1 = an ordinary single row.
        public int Count { get; set; } = 1;
        // Non-null when the row is a renamed svchost.exe instance (ServiceHostMap): a single
        // hosted service (Name = its display name, tag 服务) or a -k group (Name = the group,
        // tag 服务组). The hover tooltip (ProcessListTip) reads the services from here.
        public ServiceHostInfo ServiceHost { get; set; }
        // true when the row's net rates come from the Clash/Mihomo controller (ClashSampler)
        // instead of SRUM — a STANDALONE row by design (never deduped/overlaid against the
        // same-path SRUM row, and ProcessListMerger keeps it solo like svchost). Pid is 0 on
        // these rows (the core reports only a path), which also makes ServiceHostMap's
        // PID lookup immune to them. Shown by the row tag chip as "Clash".
        public bool ViaClash { get; set; }
        // Row tag chip (the process-list templates bind TagText/TagVisibility): "Clash" for
        // a controller-sourced row, 服务 / 服务组 for a renamed svchost row, "×N" for a
        // merged row — collapsed for an ordinary row. (The three are mutually exclusive by
        // construction: a clash row never hosts services and never merges.)
        public string TagText => ViaClash ? "Clash"
            : ServiceHost != null ? (ServiceHost.IsGroup ? "服务组" : "服务")
            : Count > 1 ? "×" + Count : null;
        public Visibility TagVisibility => TagText == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Per-process sample plus the process/thread/handle totals — all derived from a
    /// single <c>NtQuerySystemInformation(SystemProcessInformation)</c> enumeration, the
    /// same call Task Manager's <c>WdcProcessMonitor</c> makes. Produces three top-N lists
    /// (by CPU%, by private working set, and by disk read+write rate) from the one walk, so
    /// neither the CPU, the RAM nor the Disk detail panel enumerates processes itself.
    /// <para><see cref="PidToName"/> carries every walked PID's kernel ImageName so the
    /// network sampler (whose SRUM records hold only a PID) can name PPL-protected
    /// processes that <c>OpenProcess</c> refuses — without enumerating processes itself.</para>
    /// </summary>
    internal struct ProcessSample
    {
        public List<ProcessInfo> TopProcesses;        // ranked by CPU% (CPU detail)
        public List<ProcessInfo> TopMemoryProcesses;  // ranked by private working set (RAM detail)
        public List<ProcessInfo> TopDiskProcesses;    // ranked by disk read+write bytes/s (Disk detail)
        public long CompressedBytes;                  // "Memory Compression" process working set = Task Manager "(compressed)"
        // Every walked PID → kernel ImageName (free — already read in Parse). Handed to the
        // network sampler so it doesn't OpenProcess every PID just for a name; the kernel
        // name also reaches PPL-protected/system processes (Defender etc.) that
        // QueryFullProcessImageNameW can't open — the whole point of sharing the walk.
        public Dictionary<int, string> PidToName;
        public int ProcessCount;
        public int ThreadCount;
        public int HandleCount;
    }

    /// <summary>
    /// Samples per-process CPU and memory usage the way Task Manager does: one
    /// <c>NtQuerySystemInformation(SystemProcessInformation)</c> call. For CPU%, each
    /// process's share = its <c>CycleTime</c> delta over the interval ÷ the sum of every
    /// process's cycle delta (Idle included, so the denominator is the whole machine's
    /// cycle budget) — a "100% = whole machine" share that sums to the headline total CPU%
    /// and matches the modern Task Manager display (the cycle/utility path, not the older
    /// kernel+user-time path). For memory, the private working set
    /// (<c>WorkingSetPrivateSize</c> — what Task Manager's Memory column shows) is read
    /// directly, no delta needed. Also produces the process / thread / handle totals from
    /// the same walk, so nothing else enumerates processes. Single-threaded (taskbar STA
    /// thread), sampled every 1s.
    /// </summary>
    internal sealed class ProcessCpuSampler
    {
        private const int TopN = 8;

        // CycleTime (100ns-of-cycles, effectively) per PID from the previous tick, for deltas.
        private readonly Dictionary<int, ulong> _prevCycles = new Dictionary<int, ulong>();
        // IO transfer counters (bytes) per PID from the previous tick, for the disk-rate
        // deltas, plus the tick's wall clock so the rate is a true per-second value even
        // if the timer ever drifts.
        private readonly Dictionary<int, long> _prevIoRead = new Dictionary<int, long>();
        private readonly Dictionary<int, long> _prevIoWrite = new Dictionary<int, long>();
        private ulong _prevIoTickMs;
        // PID → exe path cache (path query is an OpenProcess per miss; keep across ticks).
        // A null value is cached too (protected process we can't open) so we don't retry it.
        private readonly Dictionary<int, string> _exeByPid = new Dictionary<int, string>();

        // mergeByPath (设置 → 采样项目 → 合并相同程序): build the FULL lists (no early TopN
        // break) and merge same-exe-path rows before the cut — merging after would
        // under-count groups whose members rank below it. Off = the exact old behavior.
        public ProcessSample Sample(bool mergeByPath)
        {
            var sample = new ProcessSample
            {
                TopProcesses = new List<ProcessInfo>(),
                TopMemoryProcesses = new List<ProcessInfo>(),
                TopDiskProcesses = new List<ProcessInfo>(),
                PidToName = new Dictionary<int, string>(),
                ProcessCount = 0,
                ThreadCount = 0,
                HandleCount = 0,
            };

            if (!Enumerate(out var entries))
                return sample; // API failure → empty list, zeros; UI shows an empty card for this tick

            // Snapshot every walked PID's kernel ImageName for the network sampler. Free
            // (Parse already read it), and covers PPL-protected/system processes whose path
            // the network sampler can't OpenProcess — the whole point of sharing the walk.
            foreach (var e in entries)
                sample.PidToName[e.Pid] = e.Name;

            // Sum every process's cycle delta (Idle included) → the whole-machine cycle budget.
            ulong totalCycles = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                ulong delta = Delta(entries[i].CycleTime, entries[i].Pid);
                entries[i].Delta = delta;
                totalCycles += delta;
            }

            // Counts come from the same walk. Also pick up the "Memory Compression"
            // pseudo-process's working set — that is exactly Task Manager's "(compressed)"
            // value (the compressed pages live in that process's working set).
            int threadCount = 0, handleCount = 0, processCount = 0;
            long compressedBytes = 0;
            foreach (var e in entries)
            {
                threadCount += e.Threads;
                handleCount += e.Handles;
                if (e.Pid != 0) processCount++; // exclude Idle (matches the old Process.GetProcesses count)
                if (compressedBytes == 0 &&
                    e.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase))
                    compressedBytes = e.WorkingSet < 0 ? 0 : e.WorkingSet;
            }
            sample.ThreadCount = threadCount;
            sample.HandleCount = handleCount;
            sample.ProcessCount = processCount;
            sample.CompressedBytes = compressedBytes;

            // Promote the cursor: this tick's cycles become next tick's baseline.
            _prevCycles.Clear();
            foreach (var e in entries) _prevCycles[e.Pid] = e.CycleTime;

            // Per-process disk rates (disk detail list): BytesRead/BytesWritten deltas from
            // the walk's per-entry PROCESS_DISK_COUNTERS trailer (the exact source of Task
            // Manager's disk column — reversed from Taskmgr.exe's UpdateProcess/SetRUMInfo),
            // over the real elapsed interval. First tick establishes the baseline
            // (rates 0) — same as the cycle path.
            ulong nowMs = SystemInfo.GetTickCount64();
            double ioSeconds = _prevIoTickMs != 0 ? (nowMs - _prevIoTickMs) / 1000.0 : 0;
            if (ioSeconds > 0)
            {
                foreach (var e in entries)
                {
                    if (_prevIoRead.TryGetValue(e.Pid, out long pr) && e.DiskReadBytes >= pr)
                        e.ReadRate = (long)((e.DiskReadBytes - pr) / ioSeconds);
                    if (_prevIoWrite.TryGetValue(e.Pid, out long pw) && e.DiskWriteBytes >= pw)
                        e.WriteRate = (long)((e.DiskWriteBytes - pw) / ioSeconds);
                }
            }
            _prevIoRead.Clear();
            _prevIoWrite.Clear();
            foreach (var e in entries)
            {
                _prevIoRead[e.Pid] = e.DiskReadBytes;
                _prevIoWrite[e.Pid] = e.DiskWriteBytes;
            }
            _prevIoTickMs = nowMs;

            // Top-by-CPU (CPU detail list): rank real processes (exclude Idle) by CPU%.
            entries.Sort((a, b) =>
            {
                int c = b.Delta.CompareTo(a.Delta); // desc by cycle delta (= desc by CPU%)
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            foreach (var e in entries)
            {
                if (e.Pid == 0 || e.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)) continue; // exclude Idle + Memory Compression (a kernel pseudo-process; its WS is read separately as the "(compressed)" value)
                double pct = totalCycles > 0 ? (double)e.Delta / totalCycles * 100.0 : 0.0;
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;

                sample.TopProcesses.Add(new ProcessInfo
                {
                    Name = e.Name,
                    Pid = e.Pid,
                    CpuPercent = pct,
                    ExePath = ResolveExePath(e.Pid, e.Name),
                });
                if (!mergeByPath && sample.TopProcesses.Count >= TopN) break;
            }
            if (mergeByPath)
                sample.TopProcesses = ProcessListMerger.MergeByPath(sample.TopProcesses, p => p.CpuPercent, TopN);

            // Top-by-memory (RAM detail list): same walk, ranked by private working set.
            entries.Sort((a, b) =>
            {
                int c = b.WorkingSet.CompareTo(a.WorkingSet); // desc by working set
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            foreach (var e in entries)
            {
                if (e.Pid == 0 || e.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)) continue; // exclude Idle + Memory Compression (a kernel pseudo-process; its WS is read separately as the "(compressed)" value)
                long ws = e.WorkingSet < 0 ? 0 : e.WorkingSet;

                sample.TopMemoryProcesses.Add(new ProcessInfo
                {
                    Name = e.Name,
                    Pid = e.Pid,
                    WorkingSetBytes = ws,
                    ExePath = ResolveExePath(e.Pid, e.Name),
                });
                if (!mergeByPath && sample.TopMemoryProcesses.Count >= TopN) break;
            }
            if (mergeByPath)
                sample.TopMemoryProcesses = ProcessListMerger.MergeByPath(sample.TopMemoryProcesses, p => p.WorkingSetBytes, TopN);

            // Top-by-disk-rate (Disk detail list): same walk, ranked by read+write bytes/s.
            // These are the trailer PROCESS_DISK_COUNTERS deltas — pure disk bytes, matching
            // Task Manager's per-process disk column (not the header's all-I/O counters).
            entries.Sort((a, b) =>
            {
                int c = (b.ReadRate + b.WriteRate).CompareTo(a.ReadRate + a.WriteRate); // desc by total rate
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            foreach (var e in entries)
            {
                if (e.Pid == 0 || e.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)) continue; // exclude Idle + Memory Compression (same as the other lists)

                sample.TopDiskProcesses.Add(new ProcessInfo
                {
                    Name = e.Name,
                    Pid = e.Pid,
                    DiskReadBytesPerSec = e.ReadRate,
                    DiskWriteBytesPerSec = e.WriteRate,
                    ExePath = ResolveExePath(e.Pid, e.Name),
                });
                if (!mergeByPath && sample.TopDiskProcesses.Count >= TopN) break;
            }
            if (mergeByPath)
                sample.TopDiskProcesses = ProcessListMerger.MergeByPath(sample.TopDiskProcesses, p => p.DiskReadBytesPerSec + p.DiskWriteBytesPerSec, TopN);

            return sample;
        }

        // cycle delta vs the previous tick; 0 on first sighting, clamped ≥ 0.
        private ulong Delta(ulong current, int pid)
        {
            if (_prevCycles.TryGetValue(pid, out ulong prev) && current >= prev)
                return current - prev;
            return 0;
        }

        // ---------- one NtQuerySystemInformation(SystemProcessInformation) walk ----------
        private sealed class RawEntry
        {
            public int Pid;
            public string Name;
            public ulong CycleTime;
            public long WorkingSet;     // WorkingSetPrivateSize (private working set bytes)
            public int Threads;
            public int Handles;
            public ulong Delta;
            public long DiskReadBytes;  // PROCESS_DISK_COUNTERS.BytesRead when the 24H2+ trailer is present (pure disk bytes, Task Manager's disk column); else ReadTransferCount fallback (all I/O)
            public long DiskWriteBytes; // PROCESS_DISK_COUNTERS.BytesWritten, or WriteTransferCount fallback
            public long ReadRate;       // bytes/s over the last interval (this tick's computation)
            public long WriteRate;
        }

        private bool Enumerate(out List<RawEntry> entries)
        {
            entries = null;
            int size = 1 << 20; // 1 MiB is plenty for ~hundreds of processes + their threads
            IntPtr buf = IntPtr.Zero;
            int status;
            try
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    buf = Marshal.AllocHGlobal(size);
                    status = SystemInfo.NtQuerySystemInformation(
                        SystemInfo.SYSTEM_PROCESS_INFORMATION_CLASS, buf, (uint)size, out uint returned);

                    if (status == SystemInfo.STATUS_SUCCESS)
                    {
                        entries = Parse(buf, (int)returned > 0 ? (int)returned : size);
                        return true;
                    }

                    Marshal.FreeHGlobal(buf);
                    buf = IntPtr.Zero;
                    if (status != SystemInfo.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        Logger.WarnOnce("ntqsi-walk",
                            $"NtQuerySystemInformation(SystemProcessInformation) 硬失败 status=0x{status:X8}——按进程 CPU/内存/磁盘列表本 tick 为空");
                        return false; // anything other than "buffer too small" is a hard failure
                    }
                    size <<= 1; // grow and retry
                }
            }
            catch (Exception ex)
            {
                // Swallow — best-effort. Caller gets an empty list for this tick.
                Logger.WarnOnce("ntqsi-walk-ex", "NtQuerySystemInformation 进程遍历抛异常——按进程 CPU/内存/磁盘列表本 tick 为空", ex);
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
            return false;
        }

        // SYSTEM_THREAD_INFORMATION stride on x64 — the per-thread array starts at +0x100
        // (right after the IO counters, = the struct size) with one 0x50-byte record per thread.
        private const int ThreadInfoSize = 0x50;

        private static List<RawEntry> Parse(IntPtr buf, int bytes)
        {
            var list = new List<RawEntry>(128);
            int stride = Marshal.SizeOf<SystemInfo.SYSTEM_PROCESS_INFORMATION>();
            long p = buf.ToInt64();

            while (true)
            {
                // Defensive: stop if the cursor walks past the buffer (corrupt/short read).
                if (p - buf.ToInt64() + stride > bytes) break;

                var spi = Marshal.PtrToStructure<SystemInfo.SYSTEM_PROCESS_INFORMATION>((IntPtr)p);
                int pid = spi.UniqueProcessId == IntPtr.Zero ? 0 : (int)spi.UniqueProcessId.ToInt64();

                string name;
                if (spi.ImageName.Buffer != IntPtr.Zero && spi.ImageName.Length > 0)
                    name = Marshal.PtrToStringUni(spi.ImageName.Buffer, spi.ImageName.Length / 2) ?? "";
                else
                    name = pid == 0 ? "System Idle Process" : $"PID {pid}";

                // Per-process DISK bytes the way Task Manager's disk column gets them (reversed
                // from Taskmgr.exe on this build): on 24H2+ each entry carries a
                // PROCESS_DISK_COUNTERS trailer right after its thread array —
                // { BytesRead@+0, BytesWritten@+8, op counts, ... } (then PROCESS_ENERGY_VALUES).
                // That's pure storage-stack disk I/O, unlike the header's Read/WriteTransferCount
                // which count ALL I/O (file + device + pipe). Taskmgr reads the trailer
                // unconditionally on client SKUs; we verify it's actually there (non-last entry:
                // NextEntryOffset covers it; last entry: the returned byte count covers it) and
                // fall back to the header IO counters on older kernels.
                long trailer = p + stride + (long)spi.NumberOfThreads * ThreadInfoSize;
                long trailerAvail = spi.NextEntryOffset != 0
                    ? p + spi.NextEntryOffset - trailer
                    : buf.ToInt64() + bytes - trailer;
                long diskRead, diskWrite;
                if (trailerAvail >= 2 * sizeof(long))
                {
                    diskRead = Marshal.ReadInt64((IntPtr)trailer);
                    diskWrite = Marshal.ReadInt64((IntPtr)(trailer + sizeof(long)));
                }
                else
                {
                    diskRead = spi.ReadTransferCount;
                    diskWrite = spi.WriteTransferCount;
                }

                list.Add(new RawEntry
                {
                    Pid = pid,
                    Name = name,
                    CycleTime = spi.CycleTime,
                    WorkingSet = spi.WorkingSetPrivateSize,
                    Threads = (int)spi.NumberOfThreads,
                    Handles = (int)spi.HandleCount,
                    DiskReadBytes = diskRead,
                    DiskWriteBytes = diskWrite,
                });

                if (spi.NextEntryOffset == 0) break;
                p += spi.NextEntryOffset;
            }
            return list;
        }

        // ---------- exe path (for the process icon) ----------
        // Cached per PID across ticks; refetched when the cached path's filename no longer
        // matches the current image name (cheap PID-reuse guard). Returns null for processes
        // we can't open (protected/system) — the UI shows its default icon for those.
        private string ResolveExePath(int pid, string imageFileName)
        {
            if (_exeByPid.TryGetValue(pid, out string cached) &&
                (cached == null || string.IsNullOrEmpty(imageFileName) ||
                 cached.EndsWith(imageFileName, StringComparison.OrdinalIgnoreCase)))
                return cached;

            string path = SystemInfo.QueryProcessImageFileName(pid);
            _exeByPid[pid] = path; // cache hit or miss (null) — both stored
            return path;
        }
    }
}
