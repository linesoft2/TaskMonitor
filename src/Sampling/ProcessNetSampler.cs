using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace task_monitor
{
    /// <summary>
    /// Samples per-process network throughput the way Task Manager does: the undocumented
    /// SRU real-time API in <c>srumapi.dll</c> (reverse-engineered — see
    /// SRUM-RealTime-API.md), the source of Task Manager's process-page "网络" column.
    /// Unlike Task Manager (which merges up+down into one Mbps total), this keeps them
    /// separate so the Network detail list can show each process's ↑upload and ↓download.
    ///
    /// The SRU callback fires on its own thread; the accumulators are locked. <see cref="Sample"/>
    /// runs on the taskbar STA thread once per sampling tick (0.5/1/2s — DECOUPLED from the SRU
    /// engine's own ~1s push cadence): it re-diffs the accumulators only when a new frame has
    /// arrived (callback version), divides by the frames' true arrival gap, and HOLDS the last
    /// computed rates between frames — diffing every tick at 0.5s alternated all-zero lists
    /// with doubled rates. It emits a top-N list ranked by total (up+down) bytes/s. Every
    /// walked process is listed (显示所有进程): a PID with no traffic this tick stays at 0 for
    /// <see cref="RecentSeconds"/> after its last activity, and processes that never
    /// touched the network sit at 0 below the active ones. Needs admin (per-process
    /// data is admin-only); if
    /// registration fails the sampler degrades to an empty list — it never throws, so
    /// the popup still shows everything else.
    /// </summary>
    internal sealed class ProcessNetSampler
    {
        private const int TopN = 8;
        // A PID that transferred bytes stays listed (at 0 B/s) for this long after its last
        // activity — like Task Manager, where a recently-active process doesn't vanish the
        // instant its traffic stops.
        private const double RecentSeconds = 10.0;

        // Locks _cur (touched by the SRU callback thread AND by Sample on the STA thread).
        private readonly object _lock = new object();
        // Accumulated per-PID up/down byte counts (accumulated from the callback's per-tick
        // deltas, per SRUM-RealTime-API.md §6). Snapshot-then-difference in Sample → rate.
        private readonly Dictionary<int, (ulong Down, ulong Up)> _cur =
            new Dictionary<int, (ulong, ulong)>();
        // Previous snapshot of _cur (STA-only) for the per-frame difference.
        private Dictionary<int, (ulong Down, ulong Up)> _prev;
        private bool _hasPrev;
        // Stopwatch.GetTimestamp() of the last CONSUMED SRU frame (0 = none yet) — advanced
        // only when the callback delivered new data, so dt spans the real accumulation period.
        private long _lastTicks;
        // SRU-frame version: bumped by the callback per delivered record (under _lock). The
        // SRU engine pushes frames on its OWN cadence (~1s), decoupled from our sampling
        // tick (0.5/1/2s): Sample re-diffs only when this advances and holds the last rates
        // in between — diffing every tick at 0.5s alternated all-zero frames (odd ticks saw
        // no new data) with doubled rates (a ~1s accumulation divided by a 0.5s dt).
        private long _dataVersion;
        private long _consumedVersion; // STA-only: _dataVersion as of the last diff
        // PID → last computed (down, up) bytes/s (STA-only). Held across ticks until the
        // next SRU frame re-diffs it (a zero delta then REMOVES the entry → the row reads 0).
        private readonly Dictionary<int, (double Down, double Up)> _lastRates =
            new Dictionary<int, (double, double)>();
        // PID → Stopwatch timestamp of its last nonzero delta (STA-only). PIDs within
        // RecentSeconds of activity stay in the list even at 0 B/s; older ones are pruned.
        private readonly Dictionary<int, long> _lastActive = new Dictionary<int, long>();

        // PID → exe path cache (path query is an OpenProcess per miss; keep across ticks).
        // A null value is cached too (protected process we can't open) so we don't retry it.
        private readonly Dictionary<int, string> _exeByPid = new Dictionary<int, string>();

        private bool _available;
        private IntPtr _srumModule = IntPtr.Zero;
        private IntPtr _registration = IntPtr.Zero;
        // Kept as a field so the delegate outlives the Register call (the SRU thread invokes
        // it asynchronously for the life of the registration).
        private SrumInterop.SruStatsCallback _callback;

        public ProcessNetSampler()
        {
            try { Register(); }
            catch (Exception ex)
            {
                // best-effort: run disabled; the list stays empty, the popup is unaffected
                Logger.Warn("SRUM 实时 API 注册抛异常——按进程网络流量列表保持为空", ex);
            }
        }

        private void Register()
        {
            _srumModule = SrumInterop.LoadLibraryW("srumapi.dll");
            if (_srumModule == IntPtr.Zero)
            {
                // srumapi.dll missing (Lite edition?) — per-process net stays disabled
                Logger.Warn("srumapi.dll 加载失败（精简版系统？）——按进程网络流量不可用");
                return;
            }

            // Pre-resolve the exports so a missing/broken dll shows up here, not as a
            // MissingMethodException on the first callback.
            IntPtr pReg = SrumInterop.GetProcAddress(_srumModule, "SruRegisterRealTimeStats");
            IntPtr pUnreg = SrumInterop.GetProcAddress(_srumModule, "SruUnregisterRealTimeStats");
            IntPtr pFree = SrumInterop.GetProcAddress(_srumModule, "SruFreeRecordSet");
            if (pReg == IntPtr.Zero || pUnreg == IntPtr.Zero || pFree == IntPtr.Zero)
            {
                // one of the exports is missing — per-process net stays disabled
                Logger.Warn("srumapi.dll 导出缺失（SruRegisterRealTimeStats/SruUnregisterRealTimeStats/SruFreeRecordSet）——按进程网络流量不可用");
                return;
            }

            _callback = new SrumInterop.SruStatsCallback(OnSruCallback);

            SrumInterop.GetSystemTime(out var st);
            int rc = SrumInterop.SruRegisterRealTimeStats(
                SrumInterop.ProviderClassNetwork, ref st, SrumInterop.FlagsRealtimeAdmin,
                IntPtr.Zero, _callback, out _registration, out IntPtr init);

            // The initial record set is ours to free; the ones delivered to the callback are not.
            if (init != IntPtr.Zero) SrumInterop.SruFreeRecordSet(init);

            // rc < 0 = failure (per the reversed notes); also treat a null handle as failure.
            if (rc < 0 || _registration == IntPtr.Zero)
            {
                _registration = IntPtr.Zero;
                Logger.Warn($"SruRegisterRealTimeStats 失败 rc=0x{rc:X8}（需管理员权限；未文档化 API，系统差异见 SrumInterop 头注）——按进程网络流量不可用");
                return;
            }

            _available = true;
            Logger.Info("SRUM 实时 API 注册成功——按进程网络流量已启用");
        }

        // SRU callback thread. Accumulate per-PID deltas (SRUM-RealTime-API.md §6). The
        // record set is API-managed — read it synchronously, never free it.
        private void OnSruCallback(IntPtr context, IntPtr recordSet)
        {
            try
            {
                SrumInterop.EnumerateNetworkRecords(recordSet, (pid, down, up) =>
                {
                    lock (_lock)
                    {
                        if (_cur.TryGetValue(pid, out var v))
                            _cur[pid] = (v.Down + down, v.Up + up);
                        else
                            _cur[pid] = (down, up);
                        _dataVersion++; // per record — a change marks "a new frame arrived"
                    }
                });
            }
            catch (Exception ex)
            {
                // Never let an exception out of a native callback — it would tear the app down.
                Logger.WarnOnce("srum-callback", "SRU 回调内异常（已吞掉，防跨原生边界）——本帧累计丢失", ex);
            }
        }

        /// <summary>
        /// Top-N processes by total (up+down) bytes/s. Every walked PID is listed: live
        /// ones at their rate, recently-active ones at 0 (a PID stays listed for
        /// <see cref="RecentSeconds"/> after its last traffic), and all remaining walked
        /// processes at 0 below them (显示所有进程 — Task Manager parity; with 合并相同程序
        /// on, a merged row's count is the number of RUNNING same-path instances, not just
        /// the traffic-active ones). The SRUM side is empty until the first two SRU frames
        /// have been consumed (the first only establishes the baseline), and empty if SRUM is unavailable.
        /// <para><paramref name="clashRows"/> (Clash/Mihomo proxied per-process rates, already
        /// diffed to bytes/s by <see cref="ClashSampler"/>) are appended as STANDALONE
        /// <see cref="ProcessInfo.ViaClash"/> rows — no dedup or overlay against the same-path
        /// SRUM row (the user's call: a proxy user wants to see both), and unaffected by the
        /// SRUM baseline/unavailability above. They compete in the same sort and top-N cut,
        /// and ProcessListMerger keeps them solo like svchost.</para>
        /// </summary>
        public List<ProcessInfo> Sample(Dictionary<int, string> pidToName, bool mergeByPath,
            IReadOnlyList<ClashProcessTraffic> clashRows)
        {
            // ClashPath != null marks a Clash-sourced row (Pid 0 — the core reports a path,
            // not a PID); ClashName is its metadata.process display name.
            var rows = new List<(int Pid, double Down, double Up, double Total, string ClashPath, string ClashName)>();

            if (_available)
            {
                // Snapshot the accumulators + the frame version (under lock — the callback
                // thread writes _cur/_dataVersion).
                Dictionary<int, (ulong Down, ulong Up)> now;
                long version;
                lock (_lock)
                {
                    now = new Dictionary<int, (ulong Down, ulong Up)>(_cur);
                    version = _dataVersion;
                }

                long curTicks = Stopwatch.GetTimestamp();

                // Re-diff ONLY when the SRU engine pushed a new frame since the last Sample
                // (its ~1s push cadence is independent of our 0.5/1/2s tick). Between frames
                // the previous rates are held as-is, so a fast tick can't zero the list; dt
                // spans the frames' true arrival gap, so a slow tick can't inflate it either.
                if (version != _consumedVersion)
                {
                    double dt = _lastTicks > 0 ? (curTicks - _lastTicks) / (double)Stopwatch.Frequency : 0.0;
                    _lastTicks = curTicks;
                    _consumedVersion = version;

                    // First frame (or a zero/negative interval) only establishes the baseline —
                    // the SRUM rows stay empty; the clash rows below are still emitted (their
                    // rates are pre-diffed, so they don't share this warm-up).
                    if (!_hasPrev || dt <= 0)
                    {
                        _prev = now;
                        _hasPrev = true;
                    }
                    else
                    {
                        foreach (var kv in now)
                        {
                            _prev.TryGetValue(kv.Key, out var p);
                            ulong dDown = kv.Value.Down >= p.Down ? kv.Value.Down - p.Down : 0;
                            ulong dUp = kv.Value.Up >= p.Up ? kv.Value.Up - p.Up : 0;
                            // No delta in the new frame → the PID's traffic genuinely stopped
                            // (a frame DID arrive): drop the held rate so its row reads 0.
                            if (dDown == 0 && dUp == 0) { _lastRates.Remove(kv.Key); continue; }

                            _lastActive[kv.Key] = curTicks;
                            _lastRates[kv.Key] = (dDown / dt, dUp / dt);
                        }

                        _prev = now;
                    }
                }

                // Emit every tick from the held rates (empty until the first diff — the first
                // frame only established the baseline): live PIDs at their last computed rate,
                // recently-idle ones at 0 (Task Manager behavior). Prune the window as we go.
                if (_hasPrev)
                {
                    long expiry = curTicks - (long)(RecentSeconds * Stopwatch.Frequency);
                    var expired = new List<int>();
                    foreach (var kv in _lastActive)
                    {
                        if (kv.Value < expiry) { expired.Add(kv.Key); continue; }
                        _lastRates.TryGetValue(kv.Key, out var r);
                        rows.Add((kv.Key, r.Down, r.Up, r.Down + r.Up, null, null));
                    }
                    foreach (var pid in expired)
                    {
                        _lastActive.Remove(pid);
                        _lastRates.Remove(pid);
                    }

                    // 显示所有进程 (not just the traffic-active): every walked PID gets a row —
                    // those with no SRUM delta and none retained sit at 0 B/s below the active
                    // ones. After the pruning above, _lastActive holds exactly the PIDs already
                    // emitted, so anything absent from it needs a zero row.
                    if (pidToName != null)
                    {
                        foreach (var kv in pidToName)
                        {
                            if (kv.Key <= 4) continue; // Idle/System never own SRUM records (System still appears when SRUM tracks it)
                            if ("Memory Compression".Equals(kv.Value, StringComparison.OrdinalIgnoreCase)) continue; // same exclusion as the walk lists
                            if (_lastActive.ContainsKey(kv.Key)) continue; // already emitted above (live or retained)
                            rows.Add((kv.Key, 0.0, 0.0, 0.0, null, null));
                        }
                    }
                }
            }

            // Clash/Mihomo controller rows: appended verbatim as standalone rows (Pid 0) —
            // no matching against the SRUM rows above, by design.
            if (clashRows != null)
            {
                foreach (var c in clashRows)
                {
                    if (c == null || string.IsNullOrEmpty(c.Path)) continue;
                    rows.Add((0, c.DownBps, c.UpBps, c.DownBps + c.UpBps, c.Path, c.Name));
                }
            }

            // Rank by total throughput desc, then by name for a stable order on ties.
            rows.Sort((a, b) =>
            {
                int c = b.Total.CompareTo(a.Total);
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            });

            // mergeByPath (设置 → 合并相同程序): no early TopN break — same-path
            // groups merge BEFORE the cut, or members below it would be under-counted.
            var result = new List<ProcessInfo>(mergeByPath ? rows.Count : Math.Min(rows.Count, TopN));
            for (int i = 0; i < rows.Count && (mergeByPath || result.Count < TopN); i++)
            {
                var r = rows[i];
                bool viaClash = r.ClashPath != null;
                string exePath = viaClash ? r.ClashPath : ResolveExePath(r.Pid);
                result.Add(new ProcessInfo
                {
                    // A clash row's display name is the core's metadata.process (usually the
                    // exe filename already); the path filename is the fallback.
                    Name = viaClash
                        ? (!string.IsNullOrEmpty(r.ClashName) ? r.ClashName : Path.GetFileName(r.ClashPath))
                        : ResolveName(r.Pid, exePath, pidToName),
                    Pid = r.Pid,
                    ExePath = exePath,
                    NetUpBytesPerSec = (long)Math.Round(r.Up),
                    NetDownBytesPerSec = (long)Math.Round(r.Down),
                    ViaClash = viaClash,
                });
            }
            return mergeByPath
                ? ProcessListMerger.MergeByPath(result, p => p.NetUpBytesPerSec + p.NetDownBytesPerSec, TopN)
                : result;
        }

        // SRUM records carry only a PID, no name. Prefer the kernel ImageName from the
        // process walk (built for free in ProcessCpuSampler) — it covers PPL-protected/
        // system processes (Defender etc.) that OpenProcess can't open, and matches the
        // CPU/RAM lists exactly (same kernel field, no .exe suffix drift). Fall back to the
        // exe path's filename, then "PID {pid}". PID 4 (System) is named defensively in
        // case the walk map is ever unavailable (it normally carries "System" already).
        private static string ResolveName(int pid, string exePath, Dictionary<int, string> pidToName)
        {
            if (pid == 4) return "System";
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
