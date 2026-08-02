using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// P/Invoke boundary for the undocumented SRU real-time stats API in
    /// <c>srumapi.dll</c> — the same source Task Manager's process-page "网络" column
    /// reads from. Reverse-engineered from <c>Taskmgr.exe</c> (see SRUM-RealTime-API.md):
    /// four named exports, of which the realtime-subscription path uses three
    /// (<c>SruRegisterRealTimeStats</c> / <c>SruUnregisterRealTimeStats</c> /
    /// <c>SruFreeRecordSet</c>). The callback fires on an SRU-managed thread, so the
    /// caller must keep its delegate alive for the registration's lifetime and lock any
    /// shared accumulators. Native surface only — no WPF types (mirrors
    /// <see cref="SystemInfo"/> / <see cref="ShellInterop"/>).
    /// </summary>
    internal static class SrumInterop
    {
        // _SRU_PROVIDER_CLASS: 0 = Network (the only one we register for here).
        internal const int ProviderClassNetwork = 0;

        // Flags: 0x100 = realtime; low byte = data range. Admin per-process range is 0x02,
        // so 0x102 (matches Task Manager's IsUserAdmin() ? 0x102 : 0x101 branch).
        internal const uint FlagsRealtimeAdmin = 0x102;

        // ---------- SYSTEMTIME (StartTime arg) ----------
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEMTIME
        {
            public ushort Year;
            public ushort Month;
            public ushort DayOfWeek;
            public ushort Day;
            public ushort Hour;
            public ushort Minute;
            public ushort Second;
            public ushort Milliseconds;
        }

        [DllImport("kernel32.dll", SetLastError = false)]
        internal static extern void GetSystemTime(out SYSTEMTIME lpSystemTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string lpLibFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr hModule);

        // ---------- callback ----------
        // Native: void CALLBACK SRU_CALLBACK(void* Context, _SRU_STATS_RECORD_SET* RecordSet)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void SruStatsCallback(IntPtr context, IntPtr recordSet);

        // ---------- the three exports (stdcall) ----------
        // Returns: <0 = failure; >=0 = success (per the reverse-engineering notes — a
        // positive value is an HRESULT-encoded success). Caller also treats a null
        // registration handle as failure.
        [DllImport("srumapi.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern int SruRegisterRealTimeStats(
            int providerClass,
            ref SYSTEMTIME startTime,
            uint flags,
            IntPtr context,
            SruStatsCallback callback,
            out IntPtr registrationHandle,
            out IntPtr initialRecordSet);

        [DllImport("srumapi.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern void SruUnregisterRealTimeStats(IntPtr registrationHandle);

        [DllImport("srumapi.dll", CallingConvention = CallingConvention.StdCall)]
        internal static extern void SruFreeRecordSet(IntPtr recordSet);

        // ---------- reversed record/field layout (Network provider) ----------
        // _SRU_STATS_RECORD_SET: RecordCount int @0x00, Records ptr @0x08.
        private const int Offset_RecordSet_Count = 0x00;
        private const int Offset_RecordSet_Records = 0x08;

        // _SRU_STATS_RECORD (stride 0x40 = 64): Payload ptr @0x28 (NULL = global
        // aggregate row — skip), FieldCount ushort @0x30, Fields ptr @0x38.
        private const int RecordStride = 0x40;
        private const int Offset_Record_Payload = 0x28;
        private const int Offset_Record_FieldCount = 0x30;
        private const int Offset_Record_Fields = 0x38;

        // _SRU_FIELD: Id short @0x00, Value ulong @0x08. Confirmed by a record dump on this
        // build: fields step **24 bytes** (the doc's 16-byte guess was wrong — a clean parse
        // only lines up at stride 24). FieldStride is the single calibration point if a
        // future Windows build shifts this (see SRUM-RealTime-API.md §7.3).
        private const int FieldStride = 24;
        private const int Offset_Field_Id = 0x00;
        private const int Offset_Field_Value = 0x08;

        // Network field Ids (calibrated empirically against a known uploader): 3 = upstream /
        // ↑, 4 = downstream / ↓, 6 = ProcessId. The doc's App-History mapping guessed the
        // opposite (3=down, 4=up); this build reports the upload volume on id3 — confirmed
        // by PID 22080, which was uploading ~430 KB/s on id3. Swap these two if a different
        // build ever inverts them.
        private const short FieldId_Up = 3;
        private const short FieldId_Down = 4;
        private const short FieldId_Pid = 6;

        // Sanity caps so a wrong layout can't make the walk run away.
        private const int MaxRecords = 4096;
        private const int MaxFields = 64;

        /// <summary>
        /// Walks a Network record set delivered to the SRU callback, invoking
        /// <paramref name="onProcess"/> once per per-instance (non-aggregate) record with
        /// its (pid, downBytes, upBytes). Called from the SRU callback thread — the
        /// <paramref name="onProcess"/> action must be thread-safe (the sampler locks).
        /// The record set is API-managed and valid only for the duration of the callback;
        /// this reads it synchronously and never frees it.
        /// </summary>
        internal static void EnumerateNetworkRecords(IntPtr recordSet, Action<int, ulong, ulong> onProcess)
        {
            if (recordSet == IntPtr.Zero) return;
            int count = Marshal.ReadInt32(recordSet, Offset_RecordSet_Count);
            if (count <= 0) return;
            if (count > MaxRecords) count = MaxRecords;

            IntPtr records = Marshal.ReadIntPtr(recordSet, Offset_RecordSet_Records);
            if (records == IntPtr.Zero) return;
            long baseAddr = records.ToInt64();

            for (int i = 0; i < count; i++)
            {
                long rec = baseAddr + (long)i * RecordStride;
                var recPtr = (IntPtr)rec;

                IntPtr payload = Marshal.ReadIntPtr(recPtr, Offset_Record_Payload);
                if (payload == IntPtr.Zero) continue; // global aggregate row — no per-instance data

                ushort fieldCount = (ushort)Marshal.ReadInt16(recPtr, Offset_Record_FieldCount);
                if (fieldCount == 0 || fieldCount > MaxFields) continue;

                IntPtr fields = Marshal.ReadIntPtr(recPtr, Offset_Record_Fields);
                if (fields == IntPtr.Zero) continue;

                int pid = 0;
                ulong down = 0, up = 0;
                long f = fields.ToInt64();
                for (int k = 0; k < fieldCount; k++)
                {
                    short id = Marshal.ReadInt16((IntPtr)f, Offset_Field_Id);
                    ulong val = (ulong)Marshal.ReadInt64((IntPtr)f, Offset_Field_Value);
                    switch (id)
                    {
                        case FieldId_Down: down = val; break;
                        case FieldId_Up: up = val; break;
                        case FieldId_Pid: pid = (int)val; break;
                    }
                    f += FieldStride;
                }

                if (pid != 0) onProcess(pid, down, up);
            }
        }
    }
}
