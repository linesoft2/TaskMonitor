using System;
using System.Runtime.InteropServices;
using System.Text;

namespace task_monitor
{
    /// <summary>
    /// P/Invoke boundary for the native Wi-Fi API in <c>wlanapi.dll</c> — the live
    /// connection attributes behind the Network panel's Wi-Fi details: SSID, PHY standard
    /// (802.11n/ac/ax/be) and negotiated link rate (<c>WLAN_CONNECTION_ATTRIBUTES</c> via
    /// <c>WlanQueryInterface</c>), plus radio band and channel width (the connected BSS's
    /// <c>WLAN_BSS_ENTRY</c> via <c>WlanGetNetworkBssList</c>: center frequency + beacon
    /// IEs). The wired case needs none of this — <see cref="System.Net.NetworkInformation.NetworkInterface"/>
    /// covers it.
    ///
    /// Structures are read via raw offsets (<see cref="Marshal.ReadInt32"/>/<see cref="Marshal.Copy"/>),
    /// mirroring <see cref="SrumInterop"/> — the offset constants below are the one-line
    /// calibration points if a future Windows build shifts a layout. Native surface only —
    /// no WPF types.
    /// </summary>
    internal static class WlanInterop
    {
        internal const uint ERROR_SUCCESS = 0;
        // Client version 2 = Vista-or-later semantics (the only one current Windows takes).
        internal const uint ClientVersion = 2;

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanOpenHandle(
            uint dwClientVersion, IntPtr pReserved,
            out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanQueryInterface(
            IntPtr hClientHandle, ref Guid pInterfaceGuid, uint OpCode, IntPtr pReserved,
            out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanGetNetworkBssList(
            IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid,
            int dot11BssType, [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled,
            IntPtr pReserved, out IntPtr ppWlanBssList);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr pMemory);

        // wlan_intf_opcode_current_connection → WLAN_CONNECTION_ATTRIBUTES.
        private const uint OpcodeCurrentConnection = 7;
        // dot11_BSS_type_any — list every cached BSS, we match the connected one by BSSID.
        private const int Dot11BssTypeAny = 0;

        // ---------- WLAN_INTERFACE_INFO_LIST ----------
        // DWORD dwNumberOfItems @0x00, DWORD dwIndex @0x04, items @0x08.
        private const int Off_List_Count = 0x00;
        private const int Off_List_FirstItem = 0x08;
        // WLAN_INTERFACE_INFO (532 bytes): GUID @0x00, WCHAR description[256] @0x10,
        // WLAN_INTERFACE_STATE @0x210.
        private const int InterfaceInfo_Size = 532;

        // ---------- WLAN_CONNECTION_ATTRIBUTES (raw offsets) ----------
        // WLAN_INTERFACE_STATE isState @0x00 (1 = wlan_interface_state_connected);
        // WLAN_CONNECTION_MODE @0x04; WCHAR profile[256] @0x08..0x207;
        // WLAN_ASSOCIATION_ATTRIBUTES @0x208:
        //   DOT11_SSID { ULONG length @0x208, UCHAR bytes[32] @0x20C },
        //   DOT11_BSS_TYPE @0x22C, DOT11_MAC_ADDRESS[6] @0x230 (padded to 0x238),
        //   DOT11_PHY_TYPE @0x238, ULONG phyIndex @0x23C, ULONG signalQuality @0x240,
        //   ULONG rxRate @0x244 (kbps), ULONG txRate @0x248 (kbps).
        private const int Off_IsState = 0x00;
        private const int Off_SsidLength = 0x208;
        private const int Off_Ssid = 0x20C;
        private const int Off_Bssid = 0x230;
        private const int Off_PhyType = 0x238;
        private const int Off_RxRateKbps = 0x244;
        private const int Off_TxRateKbps = 0x248;
        private const int WlanInterfaceStateConnected = 1;

        // ---------- WLAN_BSS_LIST / WLAN_BSS_ENTRY (raw offsets) ----------
        // WLAN_BSS_LIST: DWORD dwTotalSize @0x00, DWORD dwNumberOfItems @0x04, entries @0x08.
        private const int Off_BssList_Count = 0x04;
        private const int Off_BssList_FirstEntry = 0x08;
        // WLAN_BSS_ENTRY (360 bytes): DOT11_SSID @0x00 (36), ULONG phyId @0x24,
        // DOT11_MAC_ADDRESS bssid[6] @0x28, (pad) bssType @0x30, phyType @0x34, rssi @0x38,
        // linkQuality @0x3C, inRegDomain @0x40, beaconPeriod @0x42, (pad) timestamp @0x48,
        // hostTimestamp @0x50, capability @0x58, (pad) ULONG chCenterFrequency (kHz) @0x5C,
        // WLAN_RATE_SET @0x60..0x15F (4 + 126×2), ULONG ieOffset @0x160, ULONG ieSize @0x164
        // (ieOffset is relative to the START OF THIS ENTRY; the blob follows the entry).
        private const int BssEntry_Size = 360;
        private const int Off_BssEntry_Bssid = 0x28;
        private const int Off_BssEntry_CenterFreqKhz = 0x5C;
        private const int Off_BssEntry_IeOffset = 0x160;
        private const int Off_BssEntry_IeSize = 0x164;

        // IE ids inside the beacon/probe-response blob.
        private const byte IeId_HtOperation = 61;
        private const byte IeId_VhtOperation = 192;
        // Sanity cap while copying the unmanaged IE blob.
        private const int MaxIeBlobBytes = 4096;

        /// <summary>Live connection attributes of one Wi-Fi interface.</summary>
        internal sealed class WlanConnectionInfo
        {
            public string Ssid;             // UTF-8-decoded (SSIDs are opaque bytes); null when hidden
            public int PhyType;             // DOT11_PHY_TYPE: 7=n, 8=ac, 10=ax, 11=be
            public uint RxRateKbps;         // negotiated receive rate
            public uint TxRateKbps;         // negotiated transmit rate
            public uint CenterFreqKhz;      // BSS center frequency → the band; 0 = unknown
            public int ChannelWidthMHz;     // 20/40/80/160; 0 = unknown (see ParseChannelWidth)
            public bool EightyPlusEighty;   // 80+80 MHz discontiguous
        }

        /// <summary>
        /// Queries the live connection attributes of the Wi-Fi interface whose GUID matches
        /// <paramref name="interfaceGuid"/> (<see cref="System.Net.NetworkInformation.NetworkInterface.Id"/>).
        /// False when the WLAN service has no such interface or it isn't connected. Falls
        /// back to the sole enumerated interface when no GUID matches (covers exotic
        /// GUID-aliasing mismatches). Every buffer handed back by the API is freed with
        /// <c>WlanFreeMemory</c> — never GC-pinned, never cached.
        /// </summary>
        internal static bool TryQueryConnection(IntPtr client, Guid interfaceGuid, out WlanConnectionInfo info)
        {
            info = null;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr pList) != ERROR_SUCCESS
                || pList == IntPtr.Zero)
                return false;
            try
            {
                int count = Marshal.ReadInt32(pList, Off_List_Count);
                IntPtr match = IntPtr.Zero;
                for (int i = 0; i < count; i++)
                {
                    IntPtr pItem = IntPtr.Add(pList, Off_List_FirstItem + i * InterfaceInfo_Size);
                    var guid = (Guid)Marshal.PtrToStructure(pItem, typeof(Guid));
                    if (guid == interfaceGuid) { match = pItem; break; }
                    if (count == 1) match = pItem;   // single-interface fallback (see summary)
                }
                if (match == IntPtr.Zero) return false;
                var ifGuid = (Guid)Marshal.PtrToStructure(match, typeof(Guid));
                return QueryCurrentConnection(client, ifGuid, out info);
            }
            finally { WlanFreeMemory(pList); }
        }

        // WLAN_CONNECTION_ATTRIBUTES for one interface: SSID/PHY/rates (+ the BSSID, which
        // keys the BSS-list lookup for band/width).
        private static bool QueryCurrentConnection(IntPtr client, Guid ifGuid, out WlanConnectionInfo info)
        {
            info = null;
            var bssid = new byte[6];
            uint rc = WlanQueryInterface(client, ref ifGuid, OpcodeCurrentConnection, IntPtr.Zero,
                                         out _, out IntPtr pData, IntPtr.Zero);
            if (rc != ERROR_SUCCESS || pData == IntPtr.Zero) return false;
            try
            {
                // Only a connected interface carries live attributes.
                if (Marshal.ReadInt32(pData, Off_IsState) != WlanInterfaceStateConnected) return false;

                int ssidLen = Marshal.ReadInt32(pData, Off_SsidLength);
                if (ssidLen < 0 || ssidLen > 32) ssidLen = 0;
                var ssidBytes = new byte[ssidLen];
                Marshal.Copy(IntPtr.Add(pData, Off_Ssid), ssidBytes, 0, ssidLen);
                Marshal.Copy(IntPtr.Add(pData, Off_Bssid), bssid, 0, 6);

                info = new WlanConnectionInfo
                {
                    Ssid = ssidLen > 0 ? Encoding.UTF8.GetString(ssidBytes) : null,
                    PhyType = Marshal.ReadInt32(pData, Off_PhyType),
                    RxRateKbps = (uint)Marshal.ReadInt32(pData, Off_RxRateKbps),
                    TxRateKbps = (uint)Marshal.ReadInt32(pData, Off_TxRateKbps),
                };
            }
            finally { WlanFreeMemory(pData); }

            // Band + channel width live in the connected BSS's cached entry (best-effort:
            // the cache can lag a roam; a miss just leaves those fields unknown).
            FillBandAndWidth(client, ifGuid, bssid, info);
            return true;
        }

        // Center frequency (band) + channel width (beacon IEs) of the connected BSS.
        // Returns the driver's cached BSS list — no new scan is triggered.
        private static void FillBandAndWidth(IntPtr client, Guid ifGuid, byte[] bssid, WlanConnectionInfo info)
        {
            if (WlanGetNetworkBssList(client, ref ifGuid, IntPtr.Zero, Dot11BssTypeAny, false,
                                      IntPtr.Zero, out IntPtr pList) != ERROR_SUCCESS
                || pList == IntPtr.Zero)
                return;
            try
            {
                int count = Marshal.ReadInt32(pList, Off_BssList_Count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pEntry = IntPtr.Add(pList, Off_BssList_FirstEntry + i * BssEntry_Size);
                    if (!BssidEquals(pEntry, bssid)) continue;

                    info.CenterFreqKhz = (uint)Marshal.ReadInt32(pEntry, Off_BssEntry_CenterFreqKhz);
                    int ieOffset = Marshal.ReadInt32(pEntry, Off_BssEntry_IeOffset);
                    int ieSize = Marshal.ReadInt32(pEntry, Off_BssEntry_IeSize);
                    if (ieOffset >= BssEntry_Size && ieSize > 0 && ieSize <= MaxIeBlobBytes)
                    {
                        var ies = new byte[ieSize];
                        Marshal.Copy(IntPtr.Add(pEntry, ieOffset), ies, 0, ieSize);
                        ParseChannelWidth(ies, info);
                    }
                    return;   // the connected BSSID was found — done either way
                }
            }
            finally { WlanFreeMemory(pList); }
        }

        private static bool BssidEquals(IntPtr pEntry, byte[] bssid)
        {
            for (int j = 0; j < 6; j++)
                if (Marshal.ReadByte(pEntry, Off_BssEntry_Bssid + j) != bssid[j]) return false;
            return true;
        }

        // Channel width from the beacon IEs: VHT Operation (id 192 — also present on 5 GHz
        // 802.11ax BSSs for backward compatibility) wins; HT Operation (id 61) gives 20/40.
        // The 6 GHz width lives in the HE Operation element's "6GHz Operation Information" —
        // deliberately not parsed (the band still shows there). Bounds-checked throughout:
        // the blob comes straight from the driver.
        private static void ParseChannelWidth(byte[] ies, WlanConnectionInfo info)
        {
            int htWidth = 0;
            int i = 0;
            while (i + 2 <= ies.Length)
            {
                byte id = ies[i];
                int len = ies[i + 1];
                if (i + 2 + len > ies.Length) break;   // truncated blob — keep what we have
                if (id == IeId_VhtOperation && len >= 3)
                {
                    switch (ies[i + 2])   // VHT channel width: 0=20/40, 1=80, 2=160, 3=80+80
                    {
                        case 1: info.ChannelWidthMHz = 80; return;
                        case 2: info.ChannelWidthMHz = 160; return;
                        case 3: info.ChannelWidthMHz = 80; info.EightyPlusEighty = true; return;
                        // case 0: defer to the HT Operation result below
                    }
                }
                else if (id == IeId_HtOperation && len >= 2)
                {
                    // Secondary Channel Offset (bits 0–1 of byte 1): 0 = no secondary → 20 MHz.
                    htWidth = (ies[i + 3] & 0x03) != 0 ? 40 : 20;
                }
                i += 2 + len;
            }
            if (info.ChannelWidthMHz == 0) info.ChannelWidthMHz = htWidth;
        }
    }
}
