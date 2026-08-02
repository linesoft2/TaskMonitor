using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace task_monitor
{
    /// <summary>
    /// Samples live network throughput for one adapter — auto-selected (the default) or
    /// pinned by the 设置 → 采样项目 → 网络 → 适配器 pick.
    /// Ported from TrafficMonitor's adapter selection + per-second octet-delta logic:
    ///   - <see cref="SelectAdapter"/> picks the Up adapter with the largest cumulative
    ///     BytesReceived+BytesSent (the one that's carried the most traffic since boot),
    ///     skipping loopback / tunnel / virtual adapters.
    ///   - <see cref="Sample"/> returns that adapter's per-second up/down rate by
    ///     differencing the cumulative byte counters between calls.
    ///   - If the selected adapter goes silent for 30s, it re-selects (auto mode only —
    ///     a pinned adapter stays put while silent).
    ///
    /// A pinned adapter (NetworkInterface.Id GUID from the settings) is used while it
    /// exists and is Up; when it disappears or goes down the sampler falls back to 自动
    /// (the pick survives in settings.yaml) and re-checks every 30 ticks so the pinned
    /// adapter resumes when it returns.
    ///
    /// Uses the managed <see cref="NetworkInterface"/> API (equivalent to TrafficMonitor's
    /// GetAdaptersInfo + GetIfTable, no P/Invoke). Runs on the taskbar STA thread inside
    /// SystemSampler — single-threaded, no concurrency.
    /// </summary>
    internal sealed class NetSampler
    {
        private const int ZeroSpeedReselectSeconds = 30;
        private const int PreferredCheckEveryTicks = 30;   // pinned-adapter return probe cadence

        // Substrings (case-insensitive) that mark an adapter as virtual/loopback/tunnel.
        // Matched against Description first, Name as a fallback — some virtual adapters
        // (e.g. vEthernet(WSL)) only carry the telltale token in Name.
        private static readonly string[] VirtualKeywords =
        {
            "vmware", "virtualbox", "vbox",
            "hyper-v", "vethernet", "wsl", "docker",
            "tunnel", "teredo", "isatap", "6to4",
            "loopback", "pseudo", "tap", "virtual", "vpn",
            "环回", "隧道", "虚拟",
        };

        private const int MaxHistory = 60;  // 60-tick rolling history for the detail chart

        private string _selectedId;        // NetworkInterface.Id (GUID) — stable locator
        private string _selectedName;      // Description, for display
        // 设置 → 采样项目 → 网络 → 适配器: the pinned NetworkInterface.Id, null = 自动.
        // Handed in per tick by SystemSampler (the volatile settings push); a change
        // forces a reselection.
        private string _preferredId;
        // While a pinned adapter is missing (we're on the 自动 fallback), ticks since the
        // last "did it come back?" probe — one enumeration every 30 ticks, not per tick.
        private int _ticksSincePreferredCheck = PreferredCheckEveryTicks;
        // The resolved adapter, cached so Sample() doesn't re-enumerate every tick.
        // NetworkInterface.GetAllNetworkInterfaces() is expensive on machines with many
        // virtual adapters (Hyper-V/WSL2/Docker) — re-resolving only on reselect/exception
        // cut the per-tick cost from ~275ms to <1ms (measured).
        private NetworkInterface _selectedNic;
        private long _prevBytesRecv;
        private long _prevBytesSent;
        private bool _hasPrev;             // first sample can only establish a baseline
        private long _lastTicks;           // Stopwatch timestamp of the previous Sample
        private double _silentSeconds;     // consecutive seconds with no traffic

        // Per-second up/down rolling history (oldest→newest) for the detail popup's
        // bidirectional chart. Mirrors CpuSampler/RamSampler. Cleared on adapter reselect
        // so the chart never stitches two adapters' counters together.
        private readonly Queue<long> _upHistory = new Queue<long>(MaxHistory);
        private readonly Queue<long> _downHistory = new Queue<long>(MaxHistory);

        public NetSampler()
        {
            SelectAdapter();
        }

        /// <summary>
        /// The cached selected adapter, or null when none is selected. Read by
        /// <see cref="NetInfoSampler"/> (handed over by SystemSampler each tick) for the
        /// connection-info band — its background thread re-queries the NIC directly; the
        /// object itself is just a query façade.
        /// </summary>
        public NetworkInterface CurrentAdapter => _selectedNic;

        /// <summary>
        /// Pick the adapter to sample: the pinned one while it exists and is Up, else the
        /// auto rule — among Up, non-virtual, non-loopback adapters, the one with the
        /// largest cumulative BytesReceived+BytesSent. Resets the sampling baseline.
        /// </summary>
        private void SelectAdapter()
        {
            if (_preferredId != null && TrySelect(_preferredId)) return;
            SelectAdapterAuto();
        }

        // The auto rule (TrafficMonitor's pick): max cumulative traffic among Up,
        // non-virtual, non-loopback adapters.
        private void SelectAdapterAuto()
        {
            string bestId = null;
            string bestName = null;
            NetworkInterface bestNic = null;
            long bestBytes = -1;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (IsVirtualAdapter(nic)) continue;

                long bytes;
                try
                {
                    var stats = nic.GetIPStatistics();
                    bytes = stats.BytesReceived + stats.BytesSent;
                }
                catch
                {
                    // Some tunnel/pseudo adapters throw on statistics queries — skip them.
                    continue;
                }

                if (bytes > bestBytes)
                {
                    bestBytes = bytes;
                    bestId = nic.Id;
                    bestName = nic.Description;
                    bestNic = nic;
                }
            }

            ApplySelection(bestId, bestId != null ? bestName : null, bestNic);
        }

        // The pinned rule: select the adapter with this exact Id, but only while it is
        // present and Up (down/disabled reads as "missing" and the caller falls back to
        // 自动). The virtual-adapter filter is deliberately NOT applied — an explicit pick
        // is exactly how the user watches a VPN/vEthernet adapter.
        private bool TrySelect(string id)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Id != id) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) return false;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
                ApplySelection(nic.Id, nic.Description, nic);
                _ticksSincePreferredCheck = 0;
                return true;
            }
            return false;
        }

        private void ApplySelection(string id, string name, NetworkInterface nic)
        {
            _selectedId = id;
            _selectedName = name;
            _selectedNic = nic;
            _hasPrev = false;
            _silentSeconds = 0;
            // The new adapter's byte counters start fresh — drop any history carried over
            // from the previously selected adapter so the chart doesn't mix two adapters.
            _upHistory.Clear();
            _downHistory.Clear();
        }

        /// <summary>
        /// Returns (upBytesPerSec, downBytesPerSec, adapterName, upHistory, downHistory).
        /// Up = sent, Down = received. In 自动 mode (preferredAdapterId null) it re-selects
        /// if the selected adapter has vanished or has been silent for 30s; a pinned
        /// adapter stays put while silent, falls back to 自动 while gone/down, and resumes
        /// when it returns. The histories are the 60-tick rolling up/down rate
        /// (oldest→newest) for the detail popup's bidirectional chart. Rates are normalized
        /// over the REAL elapsed time between calls, so the settings 采样间隔 (0.5s/1s/2s
        /// taskbar tick) never distorts them.
        /// </summary>
        /// <param name="preferredAdapterId">设置 → 网络 → 适配器: the pinned
        /// NetworkInterface.Id (GUID), null/empty = 自动.</param>
        public (long upBytesPerSec, long downBytesPerSec, string adapterName, long[] upHistory, long[] downHistory) Sample(string preferredAdapterId)
        {
            // A changed pick (including → 自动): drop the current selection so the code
            // below re-resolves against the new preference.
            if (string.IsNullOrEmpty(preferredAdapterId)) preferredAdapterId = null;
            if (preferredAdapterId != _preferredId)
            {
                _preferredId = preferredAdapterId;
                _selectedId = null;
                _selectedName = null;
                _selectedNic = null;
            }

            // Nothing selected yet (e.g. no Up adapter at startup, or the pick just
            // changed) — retry each tick.
            if (_selectedId == null)
            {
                SelectAdapter();
                if (_selectedId == null) return (0, 0, "未连接", _upHistory.ToArray(), _downHistory.ToArray());
            }
            else if (_preferredId != null)
            {
                if (_selectedId == _preferredId)
                {
                    // The pinned adapter is in use: a pulled cable / disabled NIC counts
                    // as "gone" — fall back to 自动 (TrySelect inside requires Up).
                    // OperationalStatus re-queries the one cached NIC — cheap, unlike
                    // GetAllNetworkInterfaces().
                    bool pinnedGone;
                    try { pinnedGone = _selectedNic.OperationalStatus != OperationalStatus.Up; }
                    catch { pinnedGone = true; }
                    if (pinnedGone) SelectAdapter();
                }
                else
                {
                    // On the 自动 fallback while pinned: probe every 30 ticks whether the
                    // pinned adapter came back (one enumeration at this cadence, not per
                    // tick — the per-tick path stays enumeration-free).
                    if (++_ticksSincePreferredCheck >= PreferredCheckEveryTicks)
                    {
                        _ticksSincePreferredCheck = 0;
                        TrySelect(_preferredId);
                    }
                }
            }

            // Read byte counters straight off the cached adapter — no per-tick enumeration.
            // GetAllNetworkInterfaces() is expensive (rebuilds managed objects for every
            // adapter, and dev machines carry many virtual ones); we re-resolve only when the
            // cached NIC throws (link pulled / adapter disabled / VPN dropped) or on the 30s
            // silent-traffic reselect below.
            NetworkInterface nic = _selectedNic;
            if (nic == null)
            {
                SelectAdapter();
                return (0, 0, _selectedName ?? "未连接", _upHistory.ToArray(), _downHistory.ToArray());
            }

            long curRecv, curSent;
            try
            {
                var stats = nic.GetIPStatistics();
                curRecv = stats.BytesReceived;
                curSent = stats.BytesSent;
            }
            catch
            {
                SelectAdapter();
                return (0, 0, _selectedName ?? "未连接", _upHistory.ToArray(), _downHistory.ToArray());
            }

            // First sample after (re)select only establishes the baseline — no delta yet.
            if (!_hasPrev)
            {
                _prevBytesRecv = curRecv;
                _prevBytesSent = curSent;
                _hasPrev = true;
                _lastTicks = Stopwatch.GetTimestamp();
                return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
            }

            // Real elapsed time since the previous sample — the taskbar tick is configurable
            // (采样间隔), so a raw per-tick delta would read half/double the true rate.
            long nowTicks = Stopwatch.GetTimestamp();
            double dt = (nowTicks - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = nowTicks;
            if (dt <= 0) return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());

            // Per-second rate = byte delta / dt. The underlying MIB_IFROW counters are 32-bit
            // on net48, so a wrap/reset can produce a negative delta — clamp it.
            long dRecv = curRecv - _prevBytesRecv;
            long dSent = curSent - _prevBytesSent;
            if (dRecv < 0) dRecv = 0;
            if (dSent < 0) dSent = 0;
            _prevBytesRecv = curRecv;
            _prevBytesSent = curSent;
            long upRate = (long)(dSent / dt);
            long downRate = (long)(dRecv / dt);

            // Auto-reselect when the chosen adapter carries no traffic for a while — it may
            // have gone idle while another adapter is now the active path. AUTO mode only:
            // a pinned adapter is allowed to sit silent (its user asked for exactly it).
            if (dRecv == 0 && dSent == 0)
            {
                _silentSeconds += dt;
                if (_preferredId == null && _silentSeconds >= ZeroSpeedReselectSeconds)
                {
                    SelectAdapter();
                    return (0, 0, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());
                }
            }
            else
            {
                _silentSeconds = 0;
            }

            // Record this tick's rate into the rolling history (up = sent, down = received).
            _upHistory.Enqueue(upRate);
            _downHistory.Enqueue(downRate);
            while (_upHistory.Count > MaxHistory) _upHistory.Dequeue();
            while (_downHistory.Count > MaxHistory) _downHistory.Dequeue();

            return (upRate, downRate, _selectedName, _upHistory.ToArray(), _downHistory.ToArray());   // up = sent, down = received
        }

        // Virtual/loopback/tunnel sniff: check Description first, then Name.
        private static bool IsVirtualAdapter(NetworkInterface nic)
        {
            string desc = nic.Description ?? string.Empty;
            string name = nic.Name ?? string.Empty;
            foreach (var kw in VirtualKeywords)
            {
                if (desc.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
