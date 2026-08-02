using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// One process's proxied-traffic rate as reported by the Clash/Mihomo core — one row
    /// per exe path, already aggregated and diffed to bytes/s. Published by
    /// <see cref="ClashSampler"/> inside a whole-instance list swap (never mutated after
    /// publication), read on the taskbar STA thread.
    /// </summary>
    internal sealed class ClashProcessTraffic
    {
        public string Path;     // full exe path (the core's metadata.processPath)
        public string Name;     // metadata.process (usually the exe filename); may be empty
        public double UpBps;    // upload bytes/s
        public double DownBps;  // download bytes/s
    }

    /// <summary>
    /// Per-process traffic of the connections a Clash/Mihomo core proxies, polled from its
    /// external controller — the REST equivalent of what sparkle does over the
    /// <c>/connections</c> WebSocket: the endpoint returns the same snapshot shape
    /// (<c>{downloadTotal, uploadTotal, connections[]}</c>) where each connection carries
    /// CUMULATIVE <c>upload</c>/<c>download</c> bytes and <c>metadata.process</c> /
    /// <c>metadata.processPath</c> (populated only when the core's
    /// <c>find-process-mode</c> isn't off). Speeds come from differencing each connection's
    /// counters between consecutive polls (keyed by connection id) over the measured
    /// interval, then summing per process path — sparkle's exact algorithm
    /// (its connections.tsx: Δbytes × 1000/interval, clamped ≥ 0, grouped by process).
    ///
    /// REST polling was chosen over the WebSocket push: the data is identical for this
    /// diff-based use, net48's <see cref="HttpWebRequest"/> +
    /// <see cref="DataContractJsonSerializer"/> cover it with zero new packages, and a
    /// stateless per-poll fetch is self-healing (no reconnect logic) — the same
    /// background-thread pattern as <see cref="NetInfoSampler"/>: slow network work off
    /// the taskbar STA thread, ~1 Hz, never throws, a failed cycle just keeps the
    /// previous publication for a few cycles before decaying to empty.
    ///
    /// The endpoint arrives via <see cref="SetEndpoint"/> (written by SystemSampler every
    /// tick, a cheap volatile pair); a null/empty address idles the loop with zero network
    /// activity (the 网络-sampling-off path — SystemSampler otherwise substitutes
    /// <see cref="DefaultAddress"/> for an unset user value), and an endpoint change resets
    /// the diff baselines so no stale delta leaks across cores. SRUM stays the per-process
    /// source for direct traffic — these rows are
    /// appended to the Network detail list as SEPARATE "Clash"-tagged rows (no dedup, no
    /// overlay — see ProcessNetSampler.Sample), because under a system proxy SRUM can't
    /// attribute the loopback traffic at all and under TUN both sources see the same bytes.
    /// </summary>
    internal sealed class ClashSampler
    {
        // The conventional Clash/Mihomo external-controller address (Clash for Windows,
        // Clash Verge and the dashboards all default to it; sparkle itself uses a named
        // pipe instead). SystemSampler substitutes this when the user hasn't set an
        // address, so a stock core works out of the box.
        public const string DefaultAddress = "127.0.0.1:9090";

        private const int HttpTimeoutMs = 2500;
        // A path whose rate fell to 0 stays published (at 0) for this long after its last
        // nonzero poll — a short grace window in the spirit of ProcessNetSampler's
        // RecentSeconds, so a momentarily-idle process doesn't flicker out of the list.
        private const double GraceSeconds = 3.0;
        // A failing endpoint keeps the last publication for this many consecutive cycles
        // (NetInfoSampler's "stale for one cycle" pattern), then decays to empty — a dead
        // core must not pin stale rates into the list forever.
        private const int MaxStaleFailures = 5;

        private static readonly ClashProcessTraffic[] Empty = new ClashProcessTraffic[0];

        // Written by SystemSampler (taskbar STA thread) every tick; read by the poll
        // thread. A torn address/secret pair for one poll is harmless (loopback).
        private volatile string _apiAddress;
        private volatile string _apiSecret;
        // The latest published per-path rates (never null). One volatile read per
        // SystemSampler tick.
        private volatile IReadOnlyList<ClashProcessTraffic> _latest = Empty;

        // ---- poll-thread state (single-threaded below) ----
        private string _endpointKey;                    // address+"\n"+secret last polled — a change re-baselines
        private readonly Dictionary<string, (long Up, long Down)> _prevConn =
            new Dictionary<string, (long, long)>();     // connection id → last cumulative bytes
        private long _lastOkTicks;                      // Stopwatch timestamp of the previous successful poll (0 = none)
        private int _failCount;                         // consecutive failed polls at the current endpoint
        private sealed class PathState
        {
            public string Name;                         // best metadata.process seen so far
            public double UpBps, DownBps;               // latest summed rate
            public long LastActiveTicks;                // Stopwatch timestamp of the last nonzero rate
        }
        private readonly Dictionary<string, PathState> _paths =
            new Dictionary<string, PathState>(StringComparer.OrdinalIgnoreCase);

        public ClashSampler()
        {
            var t = new Thread(PollLoop) { IsBackground = true, Name = "ClashSampler" };
            t.Start();
        }

        /// <summary>The endpoint to poll — controller address (host:port, with or without
        /// an http:// prefix; null/empty = off) and API secret (null/empty = none).
        /// Thread-safe (two volatile writes).</summary>
        public void SetEndpoint(string address, string secret)
        {
            _apiAddress = address;
            _apiSecret = secret;
        }

        /// <summary>The latest published per-process proxy rates (never null). One
        /// volatile read — cheap enough for the per-second SystemSampler tick.</summary>
        public IReadOnlyList<ClashProcessTraffic> Latest => _latest;

        private void PollLoop()
        {
            while (true)
            {
                long started = Stopwatch.GetTimestamp();
                try { PollOnce(); }
                catch { /* never die — a bad cycle keeps the previous publication */ }

                // ~1 Hz cadence, floored so a fast-failing cycle can't spin. Fixed at 1s
                // regardless of the 采样间隔 setting: the publication is a RATE, so the
                // overlay's tick cadence doesn't matter (and the diff divides by the
                // measured interval anyway).
                long elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
                int delay = (int)(1000 - elapsedMs);
                Thread.Sleep(delay < 250 ? 250 : delay);
            }
        }

        private void PollOnce()
        {
            string address = _apiAddress;
            string secret = _apiSecret;

            if (string.IsNullOrWhiteSpace(address))
            {
                // Off — publish empty once and idle (no network at all).
                if (_latest.Count != 0) _latest = Empty;
                _prevConn.Clear();
                _paths.Clear();
                _endpointKey = null;
                _failCount = 0;
                _lastOkTicks = 0;
                return;
            }

            string key = address + "\n" + secret;
            if (key != _endpointKey)
            {
                // Endpoint changed — reset every baseline so the next publication can't
                // mix counters from two different cores.
                _endpointKey = key;
                _prevConn.Clear();
                _paths.Clear();
                _failCount = 0;
                _lastOkTicks = 0;
                _latest = Empty;
            }

            ConnectionsPayload payload;
            try { payload = Fetch(address, secret); }
            catch { payload = null; }
            if (payload == null)
            {
                if (++_failCount >= MaxStaleFailures)
                {
                    _latest = Empty;
                    _prevConn.Clear();
                    _paths.Clear();
                    _lastOkTicks = 0;
                }
                return; // keep the last publication for the first few failures
            }
            _failCount = 0;

            long now = Stopwatch.GetTimestamp();
            double dt = _lastOkTicks != 0 ? (now - _lastOkTicks) / (double)Stopwatch.Frequency : 0.0;
            _lastOkTicks = now;

            // Difference each connection's cumulative counters against the previous poll
            // (keyed by connection id) and sum the per-path rates. A connection present
            // only in this poll just establishes its baseline (no spike from counting its
            // whole lifetime in one tick); one that vanished mid-interval contributes
            // nothing more — same characteristic as sparkle's snapshot diff.
            var round = new Dictionary<string, (double Up, double Down, string Name)>(StringComparer.OrdinalIgnoreCase);
            var cur = new Dictionary<string, (long Up, long Down)>();
            if (payload.Connections != null)
            {
                foreach (var c in payload.Connections)
                {
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    cur[c.Id] = (c.Upload, c.Download);
                    // No processPath = the core's find-process-mode is off or the lookup
                    // failed — the traffic can't be attributed to a process, so it stays
                    // out of the per-process list entirely (it's still in SRUM's view of
                    // the core process itself).
                    string path = c.Metadata?.ProcessPath;
                    if (string.IsNullOrEmpty(path) || dt <= 0) continue;
                    if (!_prevConn.TryGetValue(c.Id, out var prev)) continue;

                    double up = c.Upload >= prev.Up ? (c.Upload - prev.Up) / dt : 0.0;   // clamp: a restarted
                    double down = c.Download >= prev.Down ? (c.Download - prev.Down) / dt : 0.0; // core resets counters
                    if (up <= 0 && down <= 0) continue;

                    if (round.TryGetValue(path, out var a))
                        round[path] = (a.Up + up, a.Down + down, a.Name);
                    else
                        round[path] = (up, down, c.Metadata.Process);
                }
            }
            _prevConn.Clear();
            foreach (var kv in cur) _prevConn[kv.Key] = kv.Value;

            // Fold this round into the persistent path table: unseen paths decay to 0 and
            // ride the grace window; nonzero ones refresh their activity stamp.
            foreach (var kv in _paths) { kv.Value.UpBps = 0; kv.Value.DownBps = 0; }
            foreach (var kv in round)
            {
                if (!_paths.TryGetValue(kv.Key, out var st))
                {
                    st = new PathState();
                    _paths[kv.Key] = st;
                }
                st.UpBps = kv.Value.Up;
                st.DownBps = kv.Value.Down;
                if (!string.IsNullOrEmpty(kv.Value.Name)) st.Name = kv.Value.Name;
                st.LastActiveTicks = now;
            }

            long expiry = now - (long)(GraceSeconds * Stopwatch.Frequency);
            var list = new List<ClashProcessTraffic>(_paths.Count);
            var prune = new List<string>();
            foreach (var kv in _paths)
            {
                if (kv.Value.LastActiveTicks < expiry) { prune.Add(kv.Key); continue; }
                list.Add(new ClashProcessTraffic
                {
                    Path = kv.Key,
                    Name = kv.Value.Name,
                    UpBps = kv.Value.UpBps,
                    DownBps = kv.Value.DownBps,
                });
            }
            foreach (string p in prune) _paths.Remove(p);
            _latest = list.Count == 0 ? Empty : list;
        }

        // Builds a GET request to {address}{path} (the address may carry an http(s)://
        // prefix or not). Never via the system proxy — the whole point is talking to the
        // local core, and a clash user very likely HAS a system proxy set.
        private static HttpWebRequest BuildRequest(string address, string path, string secret)
        {
            string url = address.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;
            url = url.TrimEnd('/') + path;

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = HttpTimeoutMs;
            req.ReadWriteTimeout = HttpTimeoutMs;
            req.UserAgent = "task_monitor";
            req.Proxy = null;
            if (!string.IsNullOrEmpty(secret))
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + secret;
            return req;
        }

        // One GET {address}/connections. Throws on any failure (the caller counts it) —
        // the endpoint is loopback, so a refusal is instant and the timeout only guards a
        // wedged core.
        private static ConnectionsPayload Fetch(string address, string secret)
        {
            using (var resp = BuildRequest(address, "/connections", secret).GetResponse())
            using (var stream = resp.GetResponseStream())
            {
                var ser = new DataContractJsonSerializer(typeof(ConnectionsPayload));
                return (ConnectionsPayload)ser.ReadObject(stream);
            }
        }

        /// <summary>
        /// One-shot probe for the settings page's 测试连接 button: GET {address}/version
        /// (an empty address probes <see cref="DefaultAddress"/> — the same value the
        /// poller would use, so the test matches what the user gets). Synchronous; the
        /// caller runs it off the UI thread. Returns a display-ready (Ok, Detail) pair —
        /// the core's version string on success, a 中文 reason on failure. Never throws.
        /// </summary>
        public static (bool Ok, string Detail) TestConnection(string address, string secret)
        {
            if (string.IsNullOrWhiteSpace(address)) address = DefaultAddress;
            try
            {
                using (var resp = BuildRequest(address, "/version", secret).GetResponse())
                using (var stream = resp.GetResponseStream())
                {
                    var v = (VersionPayload)new DataContractJsonSerializer(typeof(VersionPayload))
                        .ReadObject(stream);
                    return (true, string.IsNullOrEmpty(v?.Version)
                        ? "连接正常" : $"连接正常（{v.Version}）");
                }
            }
            catch (WebException we)
            {
                if (we.Response is HttpWebResponse r &&
                    (r.StatusCode == HttpStatusCode.Unauthorized || r.StatusCode == HttpStatusCode.Forbidden))
                    return (false, "认证失败，检查 API 密钥。");
                switch (we.Status)
                {
                    case WebExceptionStatus.Timeout:
                        return (false, "连接超时");
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.NameResolutionFailure:
                        return (false, "无法建立连接");
                    default:
                        return (false, "连接失败：" + we.Message);
                }
            }
            catch (Exception ex)
            {
                return (false, "响应无法解析：" + ex.Message);
            }
        }

        // The /connections JSON shape (mihomo & clash share it); only the fields the diff
        // needs are modeled — the rest are ignored by the serializer. The fields are
        // assigned by DataContractJsonSerializer via reflection, never in code (CS0649).
#pragma warning disable CS0649
        [DataContract]
        private sealed class ConnectionsPayload
        {
            [DataMember(Name = "connections")] public List<Connection> Connections;
        }

        [DataContract]
        private sealed class Connection
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "upload")] public long Upload;     // cumulative bytes
            [DataMember(Name = "download")] public long Download; // cumulative bytes
            [DataMember(Name = "metadata")] public ConnectionMeta Metadata;
        }

        [DataContract]
        private sealed class ConnectionMeta
        {
            [DataMember(Name = "processPath")] public string ProcessPath;
            [DataMember(Name = "process")] public string Process;
        }

        // GET /version's shape (mihomo & clash), for the 测试连接 probe.
        [DataContract]
        private sealed class VersionPayload
        {
            [DataMember(Name = "version")] public string Version;
        }
#pragma warning restore CS0649
    }
}
