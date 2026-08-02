using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace task_monitor
{
    /// <summary>
    /// One point-in-time set of connection details for the Network detail panel's info band
    /// (between the chart and the process list). Published by <see cref="NetInfoSampler"/>
    /// as a whole-instance swap — instances are never mutated after publication, so a reader
    /// on any thread always sees a consistent set. Strings are display-ready; null / -1
    /// means "unknown" and the panel shows "—".
    /// </summary>
    internal sealed class NetInfo
    {
        public static readonly NetInfo Empty = new NetInfo();

        public string ConnectionType;   // "WLAN" / "有线" / an enum name (Ppp…) / null = 未连接
        public string Ssid;             // Wi-Fi only, else null
        public string WifiStandard;     // e.g. "Wi-Fi 6 (802.11ax)" — Wi-Fi only
        public string WifiBand;         // "2.4 GHz" / "5 GHz" / "6 GHz" — Wi-Fi only
        public string WifiChannelWidth; // e.g. "80 MHz" — Wi-Fi only (6 GHz width: not parsed)
        public long LinkRxBps;          // negotiated receive link rate, bits/s (0 = unknown)
        public long LinkTxBps;          // negotiated transmit link rate, bits/s
        public string LocalIp;          // IPv4 preferred, global IPv6 when there's no IPv4
        public string PublicIp;         // external what-is-my-ip lookup; null until fetched
        public string PublicIpV6;       // same, over IPv6; null when no v6 connectivity
        public long GatewayRttMs = -1;  // ICMP RTT to the default gateway ("本地延迟")
        public long PublicRttMs = -1;   // ICMP RTT to the public probe ("公网延迟")

        // Field-wise copy — PollOnce publishes an early partial snapshot, so the instance
        // it keeps filling must not be the published one (publication = never mutated).
        public NetInfo ShallowCopy() => (NetInfo)MemberwiseClone();
    }

    /// <summary>
    /// Samples the Network panel's connection-info band: connection type, negotiated link
    /// rate, Wi-Fi SSID/standard/band/channel-width (wlanapi.dll via <see cref="WlanInterop"/>),
    /// local IP, default-gateway and public ICMP latency, and the public IP (an HTTP lookup,
    /// refreshed every <see cref="PublicIpRefreshMinutes"/> min, retried after
    /// <see cref="PublicIpRetrySeconds"/> s on failure, and re-fetched immediately when the
    /// selected adapter changes; the lookup AND the public ICMP latency probe can be
    /// switched off entirely in 设置 → 采样项目 → 网络 → 公网 IP — <see cref="PublicIpLookupEnabled"/>).
    ///
    /// None of this may run on the taskbar STA thread: each ICMP ping can block up to
    /// <see cref="PingTimeoutMs"/>, the HTTP lookup up to <see cref="HttpTimeoutMs"/> per
    /// endpoint, and even the WLAN query is an RPC to wlansvc. So everything happens on a
    /// dedicated background thread at ~1 Hz and the result is published as a volatile
    /// <see cref="NetInfo"/> swap (the same pattern as ProcessNetSampler's SRUM callback
    /// thread). <see cref="Sample"/> — called from SystemSampler's per-second tick — is a
    /// single volatile read. The adapter to inspect is handed over by SystemSampler each
    /// tick (NetSampler's cached selection). The poll thread never throws and never dies —
    /// a failed cycle just leaves the previous values in place for one more second.
    /// </summary>
    internal sealed class NetInfoSampler
    {
        // Public latency probe target — an ICMP ping to www.baidu.com: fast and
        // ICMP-responsive inside CN. A hostname, not a literal: DNS picks the CDN's
        // nearest node and the resolution happens inside Ping.Send, on this background
        // thread (never the taskbar STA). This is the one-line calibration point for
        // the 公网延迟 number.
        private const string PublicProbeHost = "www.baidu.com";

        // Public-IP lookup endpoints, tried in order; each answers with the caller's public
        // address as a short plain-text body (IPv4-oriented — a v6 literal doesn't match the
        // extraction regex and the next endpoint is tried).
        private static readonly string[] PublicIpEndpoints =
        {
            "https://api.ipify.org",
            "https://4.ipw.cn",
            "https://ipv4.icanhazip.com",
        };

        // IPv6 counterparts — v6-only hosts, so a machine without IPv6 connectivity simply
        // fails them all (they're only called when the adapter HAS a global v6 address).
        private static readonly string[] PublicIpV6Endpoints =
        {
            "https://api6.ipify.org",
            "https://6.ipw.cn",
            "https://ipv6.icanhazip.com",
        };

        private const int PingTimeoutMs = 1000;
        private const int HttpTimeoutMs = 3000;
        private const int PublicIpRefreshMinutes = 10;  // re-lookup cadence on success
        private const int PublicIpRetrySeconds = 60;    // retry cadence after a failure

        // Written by SystemSampler (taskbar STA thread) every tick; read by the poll thread.
        private volatile NetworkInterface _adapter;
        // 设置 → 采样项目 → 网络 → 公网 IP switch (default on) — written by SystemSampler's
        // per-tick hand-off, read by the poll thread. Off = NOTHING goes to the public
        // internet: neither the what-is-my-ip HTTP lookups nor the 公网延迟 ICMP probe
        // (the gateway ping is LAN-only and unaffected).
        private volatile bool _publicIpLookupEnabled = true;
        // The latest published set — whole-instance swap, never mutated in place.
        private volatile NetInfo _latest = NetInfo.Empty;

        // ---- poll-thread state (single-threaded below) ----
        // The WLAN client handle is opened ONLY inside TryQueryWifiLive (open → query → close
        // in one go) and Zero at all other times. WlanOpenHandle / WlanQueryInterface /
        // WlanGetNetworkBssList are location-sensitive on Win10+ — any call lights the taskbar
        // location indicator. We touch them only when the user opens the Network panel
        // (RequestWifiDetails → _wifiDetailsRequested), never on the idle poll path.
        private IntPtr _wlan;               // WLAN client handle; Zero except inside TryQueryWifiLive
        private bool _wlanUnavailable;      // wlanapi.dll missing (Server Core) → never retry
        private string _adapterId;          // last-seen adapter Id (a change re-fetches the public IP)
        private string _publicIp;           // last successful lookup (kept across later failures)
        private string _publicIpV6;         // same, IPv6
        private long _publicIpNextTryMs;    // Stopwatch-ms timestamp of the next allowed lookup

        // ---- on-demand Wi-Fi details (location-sensitive; queried only when a panel opens) ----
        // Poll-thread-private cache; no synchronization — the UI thread sees results only via the
        // _latest NetInfo copy. Keyed by adapter Id, NOT SSID: SSID itself needs wlanapi to read,
        // so keying on it would force a query on every open. A same-adapter SSID roam is invisible
        // without a query; WifiCacheTimeoutMs bounds that staleness.
        private sealed class WifiCache
        {
            public string AdapterId;                     // NetworkInterface.Id this cache is for
            public WlanInterop.WlanConnectionInfo Info;  // the queried SSID/PHY/rates/band/width
            public long RefreshedAtMs;                   // Stopwatch-ms of the query
        }
        private WifiCache _wifiCache;
        private const long WifiCacheTimeoutMs = 5 * 60_000L;  // same-adapter roam staleness backstop
        private volatile bool _wifiDetailsRequested;     // UI thread sets; poll thread reads-and-clears

        public NetInfoSampler()
        {
            var t = new Thread(PollLoop) { IsBackground = true, Name = "NetInfoSampler" };
            t.Start();
        }

        /// <summary>The adapter to inspect (NetSampler's current selection), or null.</summary>
        public NetworkInterface Adapter
        {
            set => _adapter = value;
        }

        /// <summary>Whether the public-internet probes run (设置 → 采样项目 → 网络 → 公网 IP;
        /// default on): the what-is-my-ip HTTP lookups AND the 公网延迟 ICMP ping. One
        /// volatile write — cheap enough for the per-second hand-off.</summary>
        public bool PublicIpLookupEnabled
        {
            set => _publicIpLookupEnabled = value;
        }

        /// <summary>The latest published set (never null). One volatile read — cheap enough
        /// for the per-second SystemSampler tick.</summary>
        public NetInfo Sample() => _latest;

        /// <summary>Called from the UI thread when the Network detail panel opens. The next
        /// poll tick refreshes the Wi-Fi cache IF it is stale (wrong adapter / timed out);
        /// a fresh cache is reused without any wlanapi call. Thread-safe (single volatile
        /// write). This is the sole trigger for the location-sensitive wlanapi path.</summary>
        public void RequestWifiDetails() => _wifiDetailsRequested = true;

        private void PollLoop()
        {
            while (true)
            {
                long started = Stopwatch.GetTimestamp();
                try { PollOnce(); }
                catch { /* never die — stale values for one cycle beat an empty band */ }

                // ~1 Hz cadence, floored so a fast-failing cycle can't spin.
                long elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
                int delay = (int)(1000 - elapsedMs);
                Thread.Sleep(delay < 250 ? 250 : delay);
            }
        }

        private void PollOnce()
        {
            NetworkInterface nic = _adapter;
            if (nic == null)
            {
                _latest = NetInfo.Empty;
                return;
            }

            var info = new NetInfo();
            // On-demand Wi-Fi: only when the Network panel was just opened (UI thread sets the
            // flag via RequestWifiDetails). This is the ONE place on the poll path that may touch
            // the location-sensitive wlanapi — and only if the cache is stale (a hit reuses it).
            if (_wifiDetailsRequested)
            {
                _wifiDetailsRequested = false;   // clear first: a failed cycle must not retry forever
                TryRefreshWifiCache(nic);
            }
            FillConnection(nic, info);
            IPAddress gateway = FillAddresses(nic, info, out bool hasGlobalV6);

            // 公网 IP lookup switched off (设置 → 采样项目 → 网络): never touch the HTTP
            // endpoints (the 公网延迟 probe below is gated on the same flag), and drop any
            // cached address so both publications below render
            // "—"/collapse at once; the next-try timestamp resets so a re-enable
            // fetches immediately instead of waiting out the old refresh cadence.
            if (!_publicIpLookupEnabled)
            {
                _publicIp = null;
                _publicIpV6 = null;
                _publicIpNextTryMs = 0;
            }

            // Publish the fast local facts (type/SSID/rate/local IP + the cached public
            // IPs) BEFORE the slow probes below: on the first cycle those take seconds
            // (2× ICMP ≤1s each, then up to 3 HTTP endpoints × HttpTimeoutMs per stack),
            // and until the first publication the panel renders NetInfo.Empty — "未连接"
            // on a perfectly healthy connection. The copy keeps the publication immutable
            // while this instance gets its slow fields filled in.
            info.PublicIp = _publicIp;
            info.PublicIpV6 = hasGlobalV6 ? _publicIpV6 : null;
            _latest = info.ShallowCopy();

            // The two latency probes (named ProbeRttMs — "Ping" would collide with the type).
            // ICMP needs no elevation (the app is elevated anyway). The gateway probe is
            // LAN-only and always runs; the 公网延迟 probe shares the 公网 IP switch — off
            // = the app sends no traffic to the public internet at all.
            info.GatewayRttMs = gateway != null ? ProbeRttMs(gateway.ToString()) : -1;
            info.PublicRttMs = _publicIpLookupEnabled ? ProbeRttMs(PublicProbeHost) : -1;

            // Public IP — cached; an adapter switch re-triggers the lookup immediately.
            if (nic.Id != _adapterId)
            {
                _adapterId = nic.Id;
                _publicIpNextTryMs = 0;
            }
            long nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (_publicIpLookupEnabled && nowMs >= _publicIpNextTryMs)
            {
                string ip = FetchPublicIp();
                // The v6 lookup is skipped entirely without a global v6 address — otherwise
                // a v4-only machine would eat up to 3 endpoints × HttpTimeoutMs of blocking
                // on this thread every retry cycle for a value that can never exist.
                string ip6 = hasGlobalV6 ? FetchPublicIpV6() : null;
                _publicIpNextTryMs = nowMs + (ip != null || ip6 != null
                    ? PublicIpRefreshMinutes * 60_000L
                    : PublicIpRetrySeconds * 1000L);
                if (ip != null) _publicIp = ip;
                if (ip6 != null) _publicIpV6 = ip6;
            }
            if (!hasGlobalV6) _publicIpV6 = null;   // v6 went away — don't show a stale one
            info.PublicIp = _publicIp;
            info.PublicIpV6 = _publicIpV6;

            _latest = info;
        }

        // Connection type + negotiated link rate (+ SSID/standard/band/width for Wi-Fi).
        // Never calls wlanapi — Wi-Fi details come from the on-demand cache filled by
        // TryRefreshWifiCache. No cache yet (panel never opened, or adapter switched) → the
        // Wi-Fi cells stay blank; ConnectionType and the generic fields still populate.
        private void FillConnection(NetworkInterface nic, NetInfo info)
        {
            bool isWifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            // The managed enum spells the wired family *Ethernet* (Ethernet, FastEthernetT,
            // GigabitEthernet…); anything else (Ppp…) shows its enum name.
            info.ConnectionType = isWifi ? "WLAN"
                : nic.NetworkInterfaceType.ToString().IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "有线"
                    : nic.NetworkInterfaceType.ToString();

            if (isWifi && TryApplyWifiCache(nic, info)) return;

            // Wired / Wi-Fi with no usable cache: the NIC's negotiated link speed, same value
            // for both directions.
            try { info.LinkRxBps = info.LinkTxBps = nic.Speed; }
            catch { /* leave 0 — the band omits the rate */ }
        }

        // Copy the cached Wi-Fi details into NetInfo. Match key is adapter Id ONLY — not the
        // timeout: a pinned Network panel keeps showing the values it opened with until the
        // adapter actually changes (the timeout governs only whether the NEXT open re-queries).
        // Returning false leaves the Wi-Fi cells blank and falls back to nic.Speed.
        private bool TryApplyWifiCache(NetworkInterface nic, NetInfo info)
        {
            var cache = _wifiCache;
            if (cache == null || cache.AdapterId != nic.Id) return false;
            var wifi = cache.Info;
            info.Ssid = wifi.Ssid;
            info.WifiStandard = PhyTypeToString(wifi.PhyType);
            info.WifiBand = BandToString(wifi.CenterFreqKhz);
            info.WifiChannelWidth = wifi.ChannelWidthMHz > 0
                ? wifi.EightyPlusEighty ? "80+80 MHz" : wifi.ChannelWidthMHz + " MHz"
                : null;
            info.LinkRxBps = wifi.RxRateKbps * 1000L;
            info.LinkTxBps = wifi.TxRateKbps * 1000L;
            return true;
        }

        // Decide whether THIS poll cycle touches wlanapi. Called only when _wifiDetailsRequested
        // was just set (panel opened). Cache hit (same adapter, fresh) → no query at all. Every
        // wlanapi call in the whole sampler lives inside TryQueryWifiLive below.
        private void TryRefreshWifiCache(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return;  // wired: nothing to cache

            long nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (_wifiCache != null
                && _wifiCache.AdapterId == nic.Id
                && nowMs - _wifiCache.RefreshedAtMs <= WifiCacheTimeoutMs)
                return;   // still fresh — reuse, no wlanapi call

            if (TryQueryWifiLive(nic, out WlanInterop.WlanConnectionInfo fresh) && fresh != null)
            {
                _wifiCache = new WifiCache
                {
                    AdapterId = nic.Id,
                    Info = fresh,
                    RefreshedAtMs = nowMs,
                };
            }
            // On failure leave the previous cache (possibly null); the next open retries.
        }

        // Local IP (IPv4 preferred, global IPv6 when there's no IPv4) + the default gateway.
        // Returns the gateway address (null when there isn't one) for the latency probe;
        // hasGlobalV6 reports whether the adapter holds any non-link-local v6 address
        // (gates the public-v6 lookup — no point querying when v6 can't route).
        private static IPAddress FillAddresses(NetworkInterface nic, NetInfo info, out bool hasGlobalV6)
        {
            IPAddress gateway = null;
            hasGlobalV6 = false;
            try
            {
                var props = nic.GetIPProperties();
                string v4 = null, v6 = null;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) v4 ??= ua.Address.ToString();
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6
                             && !ua.Address.IsIPv6LinkLocal)
                    {
                        v6 ??= ua.Address.ToString();
                        hasGlobalV6 = true;
                    }
                }
                info.LocalIp = v4 ?? v6;

                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        gateway = gw.Address;
                        break;
                    }
                }
            }
            catch { /* a vanishing NIC can throw mid-query — leave everything unknown */ }
            return gateway;
        }

        // One-shot Wi-Fi query: open the client, read the connection, close the client — all
        // wlanapi calls confined here, the handle Zero again before return (finally). Called
        // only from TryRefreshWifiCache, i.e. only when a Network panel just opened AND the
        // cache is stale. Never throws; a failure (wlansvc restarting, service stopped, Server
        // Core) returns false and leaves the previous cache for the next open to retry.
        private bool TryQueryWifiLive(NetworkInterface nic, out WlanInterop.WlanConnectionInfo wifi)
        {
            wifi = null;
            if (_wlanUnavailable || !Guid.TryParse(nic.Id, out Guid guid)) return false;

            try
            {
                EnsureWlanHandle();
                if (_wlan == IntPtr.Zero) return false;
                return WlanInterop.TryQueryConnection(_wlan, guid, out wifi);
            }
            catch (DllNotFoundException) { _wlanUnavailable = true; return false; }
            catch (EntryPointNotFoundException) { _wlanUnavailable = true; return false; }
            finally
            {
                // Always close: _wlan must be Zero between poll cycles so the idle path touches
                // no wlanapi at all. (A stale-handle retry no longer applies — every call opens
                // a fresh handle, so there is nothing to go stale.)
                CloseWlanHandle();
            }
        }

        private void EnsureWlanHandle()
        {
            if (_wlan != IntPtr.Zero || _wlanUnavailable) return;
            try
            {
                if (WlanInterop.WlanOpenHandle(WlanInterop.ClientVersion, IntPtr.Zero,
                        out _, out IntPtr h) != WlanInterop.ERROR_SUCCESS)
                    h = IntPtr.Zero;   // e.g. the WLAN service is stopped — retry next cycle
                _wlan = h;
            }
            catch (DllNotFoundException) { _wlanUnavailable = true; }
            catch (EntryPointNotFoundException) { _wlanUnavailable = true; }
        }

        private void CloseWlanHandle()
        {
            if (_wlan == IntPtr.Zero) return;
            try { WlanInterop.WlanCloseHandle(_wlan, IntPtr.Zero); } catch { }
            _wlan = IntPtr.Zero;
        }

        // One ICMP echo; the RTT in ms, or -1 on timeout/failure. Takes a hostname or a
        // literal — for PublicProbeHost the DNS resolution happens inside Send and can add
        // its own seconds on a broken resolver; acceptable here (only ever called on the
        // poll thread, never the taskbar STA). Never throws.
        private static long ProbeRttMs(string hostOrAddress)
        {
            try
            {
                using (var p = new Ping())
                {
                    PingReply reply = p.Send(hostOrAddress, PingTimeoutMs);
                    return reply != null && reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
                }
            }
            catch { return -1; }
        }

        // First endpoint that answers with an IPv4 literal wins; null when all fail. Never throws.
        private static string FetchPublicIp()
        {
            foreach (string endpoint in PublicIpEndpoints)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(endpoint);
                    req.Timeout = HttpTimeoutMs;
                    req.ReadWriteTimeout = HttpTimeoutMs;
                    req.UserAgent = "task_monitor";
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        // The body is the bare address — validate it anyway rather than trust it.
                        var m = Regex.Match(reader.ReadToEnd(), @"\d{1,3}(?:\.\d{1,3}){3}");
                        if (m.Success && IPAddress.TryParse(m.Value, out _)) return m.Value;
                    }
                }
                catch { /* try the next endpoint */ }
            }
            return null;
        }

        // First endpoint that answers with an IPv6 literal wins; null when all fail. Unlike
        // the v4 path the whole (trimmed) body must BE the address — a regex can't
        // distinguish a v6 literal from surrounding hex-ish text reliably. Never throws.
        private static string FetchPublicIpV6()
        {
            foreach (string endpoint in PublicIpV6Endpoints)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(endpoint);
                    req.Timeout = HttpTimeoutMs;
                    req.ReadWriteTimeout = HttpTimeoutMs;
                    req.UserAgent = "task_monitor";
                    using (var resp = req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream()))
                    {
                        string body = reader.ReadToEnd().Trim();
                        if (IPAddress.TryParse(body, out IPAddress addr)
                            && addr.AddressFamily == AddressFamily.InterNetworkV6)
                            return addr.ToString();   // canonical (compressed) form
                    }
                }
                catch { /* try the next endpoint */ }
            }
            return null;
        }

        // DOT11_PHY_TYPE → the marketing name (7=n, 8=ac, 10=ax, 11=be). 802.11ax over 6 GHz
        // is "Wi-Fi 6E" — indistinguishable from the PHY type alone, so it shows Wi-Fi 6.
        private static string PhyTypeToString(int phyType)
        {
            switch (phyType)
            {
                case 7: return "Wi-Fi 4 (802.11n)";    // dot11_phy_type_ht
                case 8: return "Wi-Fi 5 (802.11ac)";   // dot11_phy_type_vht
                case 10: return "Wi-Fi 6 (802.11ax)";  // dot11_phy_type_he
                case 11: return "Wi-Fi 7 (802.11be)";  // dot11_phy_type_eht
                case 9: return "802.11ad";             // dot11_phy_type_dmg
                case 4: return "802.11a";              // dot11_phy_type_ofdm
                case 5: return "802.11b";              // dot11_phy_type_hrdsss
                case 6: return "802.11g";              // dot11_phy_type_erp
                default: return "802.11";
            }
        }

        // BSS center frequency (kHz) → the band name.
        private static string BandToString(uint khz)
        {
            if (khz == 0) return null;
            if (khz < 3_000_000) return "2.4 GHz";
            if (khz < 5_925_000) return "5 GHz";
            return "6 GHz";
        }
    }
}
