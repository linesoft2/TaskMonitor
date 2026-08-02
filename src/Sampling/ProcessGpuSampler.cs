using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// Per-process GPU utilization — the source of Task Manager's Processes-page "GPU" column,
    /// replicated from Taskmgr.exe (WdcProcessMonitor::ProcessGpuInformation →
    /// WdcGpuMonitor::GetInfoForPid, verified in its disassembly). The column is fed by the
    /// PDH wildcard counter <c>\GPU Engine(*)\Utilization Percentage</c>, whose instance names
    /// encode the owner:
    ///
    ///   pid_&lt;dec&gt;_luid_0x&lt;hi&gt;_0x&lt;lo&gt;_phys_&lt;dec&gt;_eng_&lt;dec&gt;_engtype_&lt;name&gt;[_part_&lt;dec&gt;]
    ///
    /// (parsed by Taskmgr's ParseCounterName in gpuperfcounters.cpp — it owns a
    /// CountersGpuAdapterFactory holding exactly this counter plus the memory counters).
    /// Per tick Taskmgr stores the formatted value per (pid, adapter, engine); a process's
    /// displayed GPU% is then MAX over adapters of (MAX over the process's engines on that
    /// adapter) — i.e. the max over ALL of the process's engine instances — and its "GPU
    /// 引擎" column names the engine that produced that max. We aggregate identically.
    ///
    /// PDH percentage counters are two-sample: the first collect only primes them (items
    /// come back with an error CStatus), so the first tick yields an empty list — same
    /// warm-up shape as the other rate samplers. If the counter can't be added (no GPU
    /// engines on the machine) the sampler stays unavailable and the list stays empty —
    /// total, silent degradation, same contract as <see cref="ProcessNetSampler"/>.
    /// Sampled on the taskbar STA thread once per second; single-threaded.
    /// </summary>
    internal sealed class ProcessGpuSampler
    {
        private const int TopN = 8;
        private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
        // PDH_FMT_COUNTERVALUE_ITEM_W stride on x64: szName(8) + CStatus(4+4pad) + double(8).
        private const int ItemStride = 0x18;
        private const int ItemNameOffset = 0x0;
        private const int ItemStatusOffset = 0x8;
        private const int ItemValueOffset = 0x10;

        private readonly IntPtr _query;
        private readonly IntPtr _counter;
        private readonly bool _available;

        // Grow-only buffer for the counter-array read (processes × engines instances).
        private IntPtr _buf = IntPtr.Zero;
        private uint _bufSize;

        // PID → exe path cache (path query is an OpenProcess per miss; keep across ticks).
        // A null value is cached too (protected process we can't open) so we don't retry it.
        private readonly Dictionary<int, string> _exeByPid = new Dictionary<int, string>();

        public ProcessGpuSampler()
        {
            try
            {
                uint rc = SystemInfo.PdhOpenQueryW(IntPtr.Zero, 0, out _query);
                if (rc != 0)
                {
                    Logger.Warn($"PdhOpenQueryW 失败 rc=0x{rc:X8}——按进程 GPU 列表保持为空");
                    return;
                }
                // English counter path so the sampler works on any UI locale (same API the
                // CPU-speed sampler uses; the GPU Engine counter set itself is locale-named).
                rc = SystemInfo.PdhAddEnglishCounterW(_query, CounterPath, 0, out _counter);
                if (rc != 0)
                {
                    Logger.Warn($"PdhAddEnglishCounterW(GPU Engine) 失败 rc=0x{rc:X8}（无 GPU 引擎计数器集？）——按进程 GPU 列表保持为空");
                    return;
                }
                _available = true;
            }
            catch (Exception ex)
            {
                // best-effort: run disabled; the GPU panel's process list just stays empty
                Logger.Warn("PDH 初始化抛异常——按进程 GPU 列表保持为空", ex);
            }
        }

        /// <summary>
        /// Top-N processes by GPU utilization (Task Manager's aggregation: the max over all
        /// of the process's GPU-engine instances), each row carrying the dominant engine
        /// name ("3D", "Copy", "Video Decode", …) for the list's 引擎 column. Empty on the
        /// warm-up tick and whenever the counter set is unavailable.
        /// </summary>
        public List<ProcessInfo> Sample(Dictionary<int, string> pidToName, bool mergeByPath)
        {
            var empty = new List<ProcessInfo>();
            if (!_available) return empty;

            try
            {
                if (SystemInfo.PdhCollectQueryData(_query) != 0) return empty;
                if (!TryReadCounterArray(out uint itemCount)) return empty;

                // Aggregate per PID: max over all its engine instances (Taskmgr's rule —
                // MAX within each adapter, then MAX across adapters, which collapses to a
                // single global max), remembering the engine name that produced the max.
                var byPid = new Dictionary<int, GpuProcAgg>();
                for (uint i = 0; i < itemCount; i++)
                {
                    long item = _buf.ToInt64() + (long)i * ItemStride;
                    uint cstatus = (uint)Marshal.ReadInt32((IntPtr)(item + ItemStatusOffset));
                    if (cstatus != 0 && cstatus != SystemInfo.PDH_CSTATUS_NEW_DATA)
                        continue; // warm-up tick / invalidated instance — skip
                    double pct = BitConverter.Int64BitsToDouble(
                        Marshal.ReadInt64((IntPtr)(item + ItemValueOffset)));
                    if (pct < 0) pct = 0; else if (pct > 100) pct = 100;

                    IntPtr namePtr = Marshal.ReadIntPtr((IntPtr)(item + ItemNameOffset));
                    string inst = namePtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(namePtr);
                    if (string.IsNullOrEmpty(inst) || !TryParseInstance(inst, out int pid, out string engType))
                        continue;
                    if (pid <= 4) continue; // Idle/System never own GPU engines; skip defensively

                    if (!byPid.TryGetValue(pid, out var agg))
                        byPid[pid] = new GpuProcAgg { MaxPct = pct, Engine = engType };
                    else if (pct > agg.MaxPct)
                    {
                        agg.MaxPct = pct;
                        agg.Engine = engType;
                    }
                }

                if (byPid.Count == 0) return empty;

                var rows = new List<KeyValuePair<int, GpuProcAgg>>(byPid);
                rows.Sort((a, b) =>
                {
                    int c = b.Value.MaxPct.CompareTo(a.Value.MaxPct); // desc by GPU%
                    return c != 0 ? c : a.Key.CompareTo(b.Key);
                });

                // mergeByPath (设置 → 合并相同程序): no early TopN break — same-path
                // groups merge BEFORE the cut, or members below it would be under-counted.
                var result = new List<ProcessInfo>(mergeByPath ? rows.Count : Math.Min(rows.Count, TopN));
                for (int i = 0; i < rows.Count && (mergeByPath || result.Count < TopN); i++)
                {
                    int pid = rows[i].Key;
                    var agg = rows[i].Value;
                    string exePath = ResolveExePath(pid);
                    result.Add(new ProcessInfo
                    {
                        Name = ResolveName(pid, exePath, pidToName),
                        Pid = pid,
                        ExePath = exePath,
                        GpuPercent = agg.MaxPct,
                        GpuEngineName = agg.Engine ?? "",
                    });
                }
                return mergeByPath ? ProcessListMerger.MergeByPath(result, p => p.GpuPercent, TopN) : result;
            }
            catch
            {
                return empty; // never let a sampling failure take down the overlay
            }
        }

        private sealed class GpuProcAgg
        {
            public double MaxPct;
            public string Engine;
        }

        // One collect → one array read, growing the buffer when PDH asks for more. The
        // two-step sizing call is required (PDH_MORE_DATA), then a single retry covers the
        // instance set growing between the two calls.
        private bool TryReadCounterArray(out uint itemCount)
        {
            itemCount = 0;
            uint size = 0;
            uint rc = SystemInfo.PdhGetFormattedCounterArrayW(
                _counter, SystemInfo.PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
            if (rc != SystemInfo.PDH_MORE_DATA || size == 0) return false;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (size > _bufSize)
                {
                    if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                    _bufSize = Math.Max(size, 64u * 1024u); // headroom so steady-state never reallocs
                    _buf = Marshal.AllocHGlobal((int)_bufSize);
                }
                uint actual = _bufSize;
                rc = SystemInfo.PdhGetFormattedCounterArrayW(
                    _counter, SystemInfo.PDH_FMT_DOUBLE, ref actual, out itemCount, _buf);
                if (rc == 0) return true;
                if (rc != SystemInfo.PDH_MORE_DATA) return false;
                size = actual; // grew mid-read — loop once with the new size
            }
            return false;
        }

        // "pid_1234_luid_0x00000000_0x0001ABCD_phys_0_eng_2_engtype_3D" → pid, engtype.
        // Everything after "engtype_" is the engine name (Taskmgr's ParseCounterName reads
        // it the same way, synthesizing "Engine %u" when it's empty); a trailing "_part_N"
        // (MIG partition) is trimmed.
        private static bool TryParseInstance(string inst, out int pid, out string engType)
        {
            pid = 0;
            engType = null;

            const string PidToken = "pid_";
            // GPU Engine instance names always lead with the pid token.
            if (!inst.StartsWith(PidToken, StringComparison.Ordinal)) return false;
            int start = PidToken.Length;
            int end = inst.IndexOf('_', start);
            if (end < 0) end = inst.Length;
            if (!int.TryParse(inst.Substring(start, end - start), out pid)) return false;

            const string EngToken = "_engtype_";
            int e = inst.IndexOf(EngToken, StringComparison.Ordinal);
            if (e >= 0)
            {
                int ns = e + EngToken.Length;
                int part = inst.IndexOf("_part_", ns, StringComparison.Ordinal);
                engType = NormalizeEngineName(part >= 0 ? inst.Substring(ns, part - ns) : inst.Substring(ns));
            }
            return true;
        }

        // Counter-name engtype strings are driver-cased ("3d", "videoencode", "compute_0")
        // while the DXCore engine names the adapter tabs show are display-cased ("3D",
        // "Video Encode"). Map the known engine classes to Task Manager's display names so
        // the process list reads consistently with the tabs; anything unknown passes through
        // as-is. A trailing "_N" instance suffix is stripped for the lookup only.
        private static string NormalizeEngineName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string key = raw;
            int us = key.LastIndexOf('_');
            if (us > 0 && us < key.Length - 1 && char.IsDigit(key[us + 1]))
                key = key.Substring(0, us);
            switch (key.ToLowerInvariant())
            {
                case "3d": return "3D";
                case "copy": return "Copy";
                case "videoencode": return "Video Encode";
                case "videodecode": return "Video Decode";
                case "videoprocessing": return "Video Processing";
                case "compute": return "Compute";
                case "graphics": return "Graphics";
                case "security": return "Security";
                case "vr": return "VR";
                default: return raw;
            }
        }

        // GPU counters carry only a PID, no name. Prefer the kernel ImageName from the
        // process walk (built for free in ProcessCpuSampler) — covers PPL-protected/system
        // processes and matches the CPU/RAM lists exactly. Fall back to the exe path's
        // filename, then "PID {pid}" (same chain as ProcessNetSampler).
        private static string ResolveName(int pid, string exePath, Dictionary<int, string> pidToName)
        {
            if (pidToName != null && pidToName.TryGetValue(pid, out string img) && !string.IsNullOrEmpty(img))
                return img;
            if (!string.IsNullOrEmpty(exePath)) return Path.GetFileName(exePath);
            return $"PID {pid}";
        }

        // Cached per PID across ticks. Returns null for processes we can't open (protected/
        // system/dead) — the UI shows its default icon and "PID {pid}" name for those.
        private string ResolveExePath(int pid)
        {
            if (_exeByPid.TryGetValue(pid, out string cached)) return cached;
            string path = SystemInfo.QueryProcessImageFileName(pid);
            _exeByPid[pid] = path; // cache hit or miss (null) — both stored
            return path;
        }
    }
}
