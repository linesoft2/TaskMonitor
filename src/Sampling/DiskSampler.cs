using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace task_monitor
{
    /// <summary>Physical disk kind, classified exactly the way Task Manager does
    /// (bus type first, seek-penalty fallback → SSD/HDD).</summary>
    internal enum DiskKind
    {
        Unknown,
        Ssd,
        Hdd,
        Scm,
        Usb,
        Sd,
    }

    /// <summary>
    /// One physical disk's identity + live metrics. Identity (Index/Name/Kind) is fixed
    /// at enumeration; the metrics are updated in place each second by
    /// <see cref="DiskSampler"/> (on the taskbar STA thread) and observed by the disk
    /// detail view through INotifyPropertyChanged (WPF marshals the change notification
    /// to the UI thread — this is why tabs can bind once and never rebind, preserving
    /// the selected tab across ticks).
    /// </summary>
    internal sealed class DiskInfo : INotifyPropertyChanged
    {
        public int Index { get; }                    // N of \\.\PhysicalDriveN
        public string Name { get; }                  // vendor + product model string
        public DiskKind Kind { get; }
        public string TabTitle => string.IsNullOrEmpty(_driveLetters)
            ? $"磁盘 {Index}"
            : $"磁盘 {Index} ({_driveLetters})";    // tab header, Task Manager style: 磁盘 0 (C: D:)

        internal DiskInfo(int index, string name, DiskKind kind)
        {
            Index = index;
            Name = name;
            Kind = kind;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private string _driveLetters = "";          // "C: D:" — letters whose volumes sit on this disk

        // Space-joined drive letters on this disk ("C: D:"); empty when it holds no
        // mounted volume. Refreshed at each re-enumeration (mounts change over time).
        public string DriveLetters
        {
            get => _driveLetters;
            internal set
            {
                if (_driveLetters != value)
                {
                    _driveLetters = value;
                    Notify(nameof(DriveLetters));
                    Notify(nameof(TabTitle));
                }
            }
        }

        private double _utilPercent;
        private long _readBytesPerSec;
        private long _writeBytesPerSec;
        private double _responseMs;

        public double UtilPercent
        {
            get => _utilPercent;
            internal set { if (_utilPercent != value) { _utilPercent = value; Notify(nameof(UtilPercent)); } }
        }

        public long ReadBytesPerSec
        {
            get => _readBytesPerSec;
            internal set { if (_readBytesPerSec != value) { _readBytesPerSec = value; Notify(nameof(ReadBytesPerSec)); } }
        }

        public long WriteBytesPerSec
        {
            get => _writeBytesPerSec;
            internal set { if (_writeBytesPerSec != value) { _writeBytesPerSec = value; Notify(nameof(WriteBytesPerSec)); } }
        }

        public double ResponseMs
        {
            get => _responseMs;
            internal set { if (_responseMs != value) { _responseMs = value; Notify(nameof(ResponseMs)); } }
        }

        private void Notify(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// A disk-only sample: the headline utilization (what the overlay column and the
    /// detail chart show — the mean / max across disks or one specific disk's, per the
    /// 显示方式 setting), its 60-tick history, and the per-disk live metrics (the stable
    /// <see cref="DiskInfo"/> objects, updated in place).
    /// </summary>
    internal struct DiskSample
    {
        public double HeadlinePercent;          // 0–100, per the 显示方式 setting (MetricDisplayMode)
        public double[] History;                // recent HeadlinePercent (oldest→newest), for the chart
        public List<DiskInfo> Disks;            // per-disk live metrics
    }

    /// <summary>
    /// Samples per-physical-disk utilization / read / write / average response time the
    /// way Task Manager's <c>WdcDiskMonitor::Query</c> does (reversed from Taskmgr.exe):
    /// one zero-access handle per <c>\\.\PhysicalDriveN</c> (no elevation needed), an
    /// <c>IOCTL_DISK_PERFORMANCE</c> read every tick, and delta math against the previous
    /// cumulative counters:
    ///
    ///   utilization % = (1 − IdleTimeΔ / QueryTimeΔ) × 100
    ///   read  B/s     = BytesReadΔ    / (QueryTimeΔ / 10⁷)
    ///   write B/s     = BytesWrittenΔ / (QueryTimeΔ / 10⁷)
    ///   response ms   = (ReadTimeΔ + WriteTimeΔ) / 10⁴, ÷ ReadCountΔ when non-zero
    ///
    /// (The response-time divisor is the READ count only — that is Task Manager's own
    /// formula, verified in its disassembly, quirk included. When a tick has no reads
    /// the undivided total is kept, exactly as Taskmgr does; negative → 0.)
    ///
    /// Disk type is classified once at enumeration via IOCTL_STORAGE_QUERY_PROPERTY:
    /// BusType (SCM/USB/SD special-cased, FileBackedVirtual excluded like Taskmgr), then
    /// StorageDeviceSeekPenaltyProperty — IncursSeekPenalty=false → SSD, else HDD (NVMe
    /// lands here too, same as Task Manager).
    ///
    /// The disk set is re-probed every 30 ticks (cheap CreateFile probe) so hot-plugged
    /// disks appear/disappear; baselines survive re-enumeration (keyed by disk number).
    /// Single-threaded (taskbar STA thread), sampled every 1s.
    ///
    /// Handles are NEVER held open between ticks — each query opens the disk fresh and
    /// closes it immediately. A held-open handle on a PhysicalDrive vetoes the PnP
    /// query-remove (IRP_MN_QUERY_REMOVE_DEVICE), so "safely eject" of a USB drive
    /// reports 设备被占用 while this app runs. Taskmgr avoids that by not retaining the
    /// handle either; a fresh zero-access CreateFile per tick is negligible.
    /// </summary>
    internal sealed class DiskSampler
    {
        private const int MaxHistory = 60;
        private const int ReenumerateEveryTicks = 30;
        private const int MaxConsecutiveFailures = 3;
        private const int MaxProbeIndex = 15;    // probe PhysicalDrive0..15

        // Per-disk runtime state keyed by the PhysicalDrive number. The DiskInfo inside
        // is the long-lived object the UI binds to. No handle is stored here — see the
        // class doc: handles are opened per query and closed immediately.
        private sealed class DiskEntry
        {
            public DiskInfo Info;
            public DiskInterop.DISK_PERFORMANCE Prev;
            public bool HasBaseline;
            public int Failures;
        }

        private readonly Dictionary<int, DiskEntry> _disks = new Dictionary<int, DiskEntry>();
        private readonly Queue<double> _history = new Queue<double>(MaxHistory);
        private int _ticksSinceEnumerate = ReenumerateEveryTicks; // enumerate on the first Sample()

        // The 显示方式 last used to build the headline (taskbar STA thread only). A change
        // clears the history so the chart never mixes values computed under two semantics.
        private MetricDisplayMode _lastMode = MetricDisplayMode.Average;
        private int _lastSpecificIndex = -1;

        /// <param name="mode">Headline source (设置 → 采样项目 → 磁盘 → 显示方式): the mean
        /// or max of per-disk utilization, or one specific disk's.</param>
        /// <param name="specificIndex">The PhysicalDrive index for
        /// <see cref="MetricDisplayMode.Specific"/>; ignored otherwise. A specific disk that
        /// isn't currently present falls back to the mean of the remaining disks (the
        /// setting survives — its own values resume when it returns).</param>
        public DiskSample Sample(MetricDisplayMode mode, int specificIndex)
        {
            if (mode != _lastMode || specificIndex != _lastSpecificIndex)
            {
                _history.Clear();
                _lastMode = mode;
                _lastSpecificIndex = specificIndex;
            }

            if (++_ticksSinceEnumerate >= ReenumerateEveryTicks)
            {
                Reenumerate();
                _ticksSinceEnumerate = 0;
            }

            // Every disk is queried every tick regardless of the mode — the detail view's
            // per-disk tabs need all of them live; the mode only picks the headline.
            double utilSum = 0;
            double utilMax = 0;
            double specificUtil = 0;
            bool specificFound = false;
            int utilCount = 0;
            foreach (var entry in _disks.Values)
            {
                if (QueryDisk(entry))
                {
                    utilSum += entry.Info.UtilPercent;
                    if (entry.Info.UtilPercent > utilMax) utilMax = entry.Info.UtilPercent;
                    if (entry.Info.Index == specificIndex)
                    {
                        specificUtil = entry.Info.UtilPercent;
                        specificFound = true;
                    }
                    utilCount++;
                }
            }

            // Failed disks are removed AFTER the loop (removing mid-iteration would
            // invalidate the dictionary enumerator).
            var dead = new List<DiskEntry>();
            foreach (var entry in _disks.Values)
                if (entry.Failures >= MaxConsecutiveFailures) dead.Add(entry);
            foreach (var entry in dead) RemoveDisk(entry);

            double headline;
            switch (mode)
            {
                case MetricDisplayMode.Max:
                    headline = utilMax;
                    break;
                case MetricDisplayMode.Specific:
                    // The chosen disk is absent (unplugged): fall back to the mean of the
                    // remaining disks. The setting is kept untouched, so the specific
                    // disk's own values resume automatically when it comes back.
                    headline = specificFound ? specificUtil
                                             : utilCount > 0 ? utilSum / utilCount : 0;
                    break;
                default: // Average
                    headline = utilCount > 0 ? utilSum / utilCount : 0;
                    break;
            }
            _history.Enqueue(headline);
            while (_history.Count > MaxHistory) _history.Dequeue();

            // Publish a FRESH list each tick (the snapshot contract is never-mutated
            // objects; the DiskInfo items inside are the long-lived INPC ones).
            var disks = new List<DiskInfo>(_disks.Count);
            var sorted = new List<int>(_disks.Keys);
            sorted.Sort();
            foreach (var key in sorted) disks.Add(_disks[key].Info);

            return new DiskSample
            {
                HeadlinePercent = headline,
                History = _history.ToArray(),
                Disks = disks,
            };
        }

        // ---------- per-tick: one IOCTL_DISK_PERFORMANCE per disk + Taskmgr's delta math ----------
        private bool QueryDisk(DiskEntry entry)
        {
            // Open fresh, query, close — never retain the handle (it would veto USB
            // safe-eject; see the class doc).
            using (var handle = OpenDisk(entry.Info.Index))
            {
                if (handle.IsInvalid)
                {
                    // A vanished disk: zero its metrics; Sample() drops it after a few
                    // strikes (the periodic re-enumeration picks it back up if it returns).
                    entry.Failures++;
                    Logger.WarnOnce($"disk-open-{entry.Info.Index}",
                        $"PhysicalDrive{entry.Info.Index}（{entry.Info.Name}）打开失败 err={Marshal.GetLastWin32Error()}（设备已移除？）——指标归零");
                    ZeroMetrics(entry.Info);
                    return false;
                }

                var perf = new DiskInterop.DISK_PERFORMANCE();
                int size = Marshal.SizeOf<DiskInterop.DISK_PERFORMANCE>();
                bool ok = DiskInterop.DeviceIoControl(
                    handle, DiskInterop.IOCTL_DISK_PERFORMANCE,
                    null, 0, ref perf, (uint)size, out _, IntPtr.Zero);

                if (!ok)
                {
                    entry.Failures++;
                    Logger.WarnOnce($"disk-ioctl-{entry.Info.Index}",
                        $"IOCTL_DISK_PERFORMANCE 查询失败 err={Marshal.GetLastWin32Error()}（PhysicalDrive{entry.Info.Index}）——指标归零");
                    ZeroMetrics(entry.Info);
                    return false;
                }
                entry.Failures = 0;
                return ApplyPerf(entry, perf);
            }
        }

        // The Taskmgr delta math against the previous cumulative counters.
        private static bool ApplyPerf(DiskEntry entry, DiskInterop.DISK_PERFORMANCE perf)
        {
            if (!entry.HasBaseline)
            {
                // First sighting (or post-change resync): establish the baseline only —
                // a delta against nothing would spike (Taskmgr's resync branch does the same).
                entry.Prev = perf;
                entry.HasBaseline = true;
                ZeroMetrics(entry.Info);
                return true;
            }

            long dt = perf.QueryTime - entry.Prev.QueryTime;   // 100ns units
            if (dt <= 0)
            {
                ZeroMetrics(entry.Info);
            }
            else
            {
                long idleDelta = perf.IdleTime - entry.Prev.IdleTime;
                double util = idleDelta < dt ? (1.0 - (double)idleDelta / dt) * 100.0 : 0.0;
                if (util < 0) util = 0;
                if (util > 100) util = 100;

                double seconds = dt / 10_000_000.0;
                long read = (long)((perf.BytesRead - entry.Prev.BytesRead) / seconds);
                long write = (long)((perf.BytesWritten - entry.Prev.BytesWritten) / seconds);
                if (read < 0) read = 0;
                if (write < 0) write = 0;

                double resp = ((perf.WriteTime - entry.Prev.WriteTime) +
                               (perf.ReadTime - entry.Prev.ReadTime)) / 10_000.0; // → ms
                int readOps = perf.ReadCount - entry.Prev.ReadCount;
                if (readOps != 0) resp /= readOps;
                if (resp < 0) resp = 0;

                entry.Info.UtilPercent = util;
                entry.Info.ReadBytesPerSec = read;
                entry.Info.WriteBytesPerSec = write;
                entry.Info.ResponseMs = resp;
            }

            entry.Prev = perf;
            return true;
        }

        private static SafeFileHandle OpenDisk(int index)
            => DiskInterop.CreateFileW(
                $@"\\.\PhysicalDrive{index}", 0,
                DiskInterop.FILE_SHARE_READ_WRITE, IntPtr.Zero,
                DiskInterop.OPEN_EXISTING, 0, IntPtr.Zero);

        private static void ZeroMetrics(DiskInfo info)
        {
            info.UtilPercent = 0;
            info.ReadBytesPerSec = 0;
            info.WriteBytesPerSec = 0;
            info.ResponseMs = 0;
        }

        // ---------- enumeration: probe \\.\PhysicalDriveN, classify, keep stable DiskInfos ----------
        private void Reenumerate()
        {
            var seen = new HashSet<int>();
            for (int i = 0; i <= MaxProbeIndex; i++)
            {
                var handle = OpenDisk(i);
                if (handle.IsInvalid) continue;

                if (_disks.TryGetValue(i, out var existing))
                {
                    // Already tracked — keep its baseline, drop the probe handle.
                    handle.Dispose();
                    seen.Add(i);
                    continue;
                }

                if (!TryClassify(handle, i, out string name, out var kind))
                {
                    // FileBackedVirtual (VHD) or an unqueryable disk — excluded, as Taskmgr excludes it.
                    handle.Dispose();
                    continue;
                }

                // Never retain the probe handle (see the class doc) — each tick reopens.
                handle.Dispose();
                _disks[i] = new DiskEntry
                {
                    Info = new DiskInfo(i, name, kind),
                    HasBaseline = false,
                };
                seen.Add(i);
                Logger.Info($"发现磁盘 PhysicalDrive{i}（{name}，{kind}）——纳入每 tick IOCTL_DISK_PERFORMANCE 采样");
            }

            // Drop disks that no longer probe open (unplugged since the last enumeration).
            var gone = new List<int>();
            foreach (var key in _disks.Keys)
                if (!seen.Contains(key)) gone.Add(key);
            foreach (var key in gone)
                RemoveDisk(_disks[key]);

            UpdateDriveLetters();
        }

        // ---------- volume → disk mapping (the "(C: D:)" in the tab titles) ----------
        // For every mounted letter, open the volume (\\.\C:) zero-access and ask which
        // physical disk(s) its extent(s) live on — the same IOCTL_VOLUME_GET_VOLUME_DISK_
        // EXTENTS mapping Taskmgr's GetDiskExtents uses. Runs only at re-enumeration
        // (every 30 ticks); a volume spanning several disks shows its letter on each.
        private void UpdateDriveLetters()
        {
            var byDisk = new Dictionary<int, List<string>>();
            foreach (string drive in Environment.GetLogicalDrives())   // "C:\"
            {
                string path = @"\\.\" + drive.TrimEnd('\\');            // "\\.\C:"
                var handle = DiskInterop.CreateFileW(
                    path, 0, DiskInterop.FILE_SHARE_READ_WRITE, IntPtr.Zero,
                    DiskInterop.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                try
                {
                    // DISK_EXTENTS: uint NumberOfDiskExtents at 0x00, then 4 bytes ALIGNMENT
                    // PADDING, then DISK_EXTENT[NumberOfDiskExtents] at 0x08 — each 24 bytes
                    // (DWORD DiskNumber + pad + LARGE_INTEGER StartingOffset + ExtentLength).
                    // The first extent is NOT at 0x04 (that reads the zero padding and maps
                    // every letter to disk 0).
                    var buf = new byte[8 + 8 * 24];
                    if (!DiskInterop.DeviceIoControl(
                            handle, DiskInterop.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                            null, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
                        continue;

                    int count = BitConverter.ToInt32(buf, 0);
                    string letter = drive.Substring(0, 2);              // "C:"
                    for (int e = 0; e < count && e < 8; e++)
                    {
                        int diskNumber = BitConverter.ToInt32(buf, 8 + e * 24);
                        if (!byDisk.TryGetValue(diskNumber, out var letters))
                            byDisk[diskNumber] = letters = new List<string>();
                        if (!letters.Contains(letter)) letters.Add(letter);
                    }
                }
                finally { handle.Dispose(); }
            }

            foreach (var kv in _disks)
            {
                var letters = byDisk.TryGetValue(kv.Key, out var l) ? l : null;
                letters?.Sort(StringComparer.Ordinal);
                kv.Value.Info.DriveLetters = letters == null ? "" : string.Join(" ", letters);
            }
        }

        private void RemoveDisk(DiskEntry entry)
        {
            _disks.Remove(entry.Info.Index);
            Logger.Info($"磁盘 PhysicalDrive{entry.Info.Index}（{entry.Info.Name}）已移除（拔出或连续 {MaxConsecutiveFailures} 次查询失败）");
        }

        // Query BusType + seek penalty + the vendor/product name. Returns false when the
        // disk should be excluded (FileBackedVirtual) — mirrors Taskmgr's ShouldIncludeDisk.
        private static bool TryClassify(SafeFileHandle handle, int index, out string name, out DiskKind kind)
        {
            name = null;
            kind = DiskKind.Unknown;

            int busType = QueryBusType(handle, out string vendor, out string product);
            if (busType == DiskInterop.BusTypeFileBackedVirtual) return false;

            name = BuildName(vendor, product, index);

            switch (busType)
            {
                case DiskInterop.BusTypeScm: kind = DiskKind.Scm; return true;
                case DiskInterop.BusTypeUsb: kind = DiskKind.Usb; return true;
                case DiskInterop.BusTypeSd: kind = DiskKind.Sd; return true;
            }

            // Default HDD, upgraded to SSD when the device reports no seek penalty
            // (NVMe included — Taskmgr has no NVMe special case either).
            kind = QueryIncursSeekPenalty(handle) == false ? DiskKind.Ssd : DiskKind.Hdd;
            return true;
        }

        private static string BuildName(string vendor, string product, int index)
        {
            string name = ((vendor ?? "") + " " + (product ?? "")).Trim();
            // A space-padded vendor string alone ("ATA") reads worse than the model alone.
            if (name.Length == 0) name = $"PhysicalDrive{index}";
            return name;
        }

        // StorageDeviceProperty → STORAGE_DEVICE_DESCRIPTOR. Returns the BusType int (-1 on
        // failure) and extracts the ASCII vendor/product strings at their descriptor offsets.
        private static int QueryBusType(SafeFileHandle handle, out string vendor, out string product)
        {
            vendor = null;
            product = null;
            var query = DiskInterop.MakePropertyQuery(DiskInterop.StorageDeviceProperty);
            var buf = new byte[512];
            if (!DiskInterop.DeviceIoControl(handle, DiskInterop.IOCTL_STORAGE_QUERY_PROPERTY,
                    query, (uint)query.Length, buf, (uint)buf.Length, out _, IntPtr.Zero))
                return -1;

            // Offsets into STORAGE_DEVICE_DESCRIPTOR (x86/x64 identical — all fixed fields).
            int busType = BitConverter.ToInt32(buf, 0x1C);
            vendor = ReadDescriptorAscii(buf, BitConverter.ToInt32(buf, 0x0C));
            product = ReadDescriptorAscii(buf, BitConverter.ToInt32(buf, 0x10));
            return busType;
        }

        // Device descriptor strings are ASCII, space-padded, not necessarily terminated.
        private static string ReadDescriptorAscii(byte[] buf, int offset)
        {
            if (offset <= 0 || offset >= buf.Length) return null;
            int end = offset;
            while (end < buf.Length && buf[end] != 0) end++;
            return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
        }

        // StorageDeviceSeekPenaltyProperty → DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size,
        // IncursSeekPenalty (1-byte BOOLEAN) }. Returns null when the query fails (caller
        // then keeps the HDD default, like Taskmgr's HarmlessError path).
        private static bool? QueryIncursSeekPenalty(SafeFileHandle handle)
        {
            var query = DiskInterop.MakePropertyQuery(DiskInterop.StorageDeviceSeekPenaltyProperty);
            var buf = new byte[0x0C];
            if (!DiskInterop.DeviceIoControl(handle, DiskInterop.IOCTL_STORAGE_QUERY_PROPERTY,
                    query, (uint)query.Length, buf, (uint)buf.Length, out _, IntPtr.Zero))
                return null;
            return buf[0x08] != 0;
        }
    }
}
