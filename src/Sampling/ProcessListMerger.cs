using System;
using System.Collections.Generic;

namespace task_monitor
{
    /// <summary>
    /// 设置 → 采样项目 → 合并相同程序: merges the per-process list rows that share an exe
    /// path into one row — every numeric value is summed and the member count rides on
    /// <see cref="ProcessInfo.Count"/> (shown as a "×N" row tag — ProcessInfo.TagText). Rows without a path (protected/system processes the sampler can't
    /// OpenProcess) group by name instead. 服务宿主 (svchost.exe) is exempt: its instances
    /// host unrelated services, so each keeps its own row. Clash/Mihomo controller rows
    /// (<see cref="ProcessInfo.ViaClash"/>) are exempt too — a separate data source shown
    /// unmerged by design. The per-process samplers apply this BEFORE
    /// their top-N cut so a group's total competes fairly with single processes for the
    /// visible rows (merging after the cut would under-count groups whose members rank
    /// below it). One-off per tick, on the taskbar STA thread like the samplers.
    /// </summary>
    internal static class ProcessListMerger
    {
        /// <summary>Group <paramref name="rows"/> by exe path, sum each group into its
        /// first member's row, re-rank by the summed <paramref name="rankBy"/> key (same
        /// desc + name tie-break the samplers use) and keep the top <paramref name="topN"/>.
        /// </summary>
        public static List<ProcessInfo> MergeByPath(List<ProcessInfo> rows, Func<ProcessInfo, double> rankBy, int topN)
        {
            var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            List<ProcessInfo> solo = null; // 服务宿主 + Clash rows — created lazily, most ticks have none in the list
            foreach (var row in rows)
            {
                // Clash/Mihomo controller rows (ViaClash): a separate data source the user
                // asked to see UNMERGED (no dedup/overlay against the same-path SRUM row),
                // and ClashSampler already aggregates per path, so they can't merge with
                // each other either — keep each solo, same as svchost below.
                if (row.ViaClash)
                {
                    if (solo == null) solo = new List<ProcessInfo>();
                    solo.Add(row);
                    continue;
                }

                // 服务宿主 (svchost.exe): every instance shares the one exe path but hosts
                // unrelated services — collapsing them into a single row would hide exactly
                // what the list is for, so each instance keeps its own row and competes
                // individually (Task Manager also lists them separately).
                if (row.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (solo == null) solo = new List<ProcessInfo>();
                    solo.Add(row);
                    continue;
                }

                // A path never starts with '\0', so the name fallback can't collide with one.
                string key = string.IsNullOrEmpty(row.ExePath) ? "\0" + row.Name : row.ExePath;
                if (!groups.TryGetValue(key, out var g))
                {
                    // The first member becomes the group row (keeps its Name/Pid/ExePath).
                    groups[key] = new Group { Row = row, TopMemberGpu = row.GpuPercent };
                    continue;
                }

                g.Row.Count++;
                g.Row.CpuPercent += row.CpuPercent;
                g.Row.WorkingSetBytes += row.WorkingSetBytes;
                g.Row.NetUpBytesPerSec += row.NetUpBytesPerSec;
                g.Row.NetDownBytesPerSec += row.NetDownBytesPerSec;
                g.Row.DiskReadBytesPerSec += row.DiskReadBytesPerSec;
                g.Row.DiskWriteBytesPerSec += row.DiskWriteBytesPerSec;
                g.Row.GpuPercent += row.GpuPercent;
                // The merged row's 引擎 column follows the biggest single member.
                if (row.GpuPercent > g.TopMemberGpu)
                {
                    g.TopMemberGpu = row.GpuPercent;
                    g.Row.GpuEngineName = row.GpuEngineName;
                }
            }

            var merged = new List<ProcessInfo>(groups.Count + (solo == null ? 0 : solo.Count));
            foreach (var g in groups.Values) merged.Add(g.Row);
            if (solo != null) merged.AddRange(solo);
            merged.Sort((a, b) =>
            {
                int c = rankBy(b).CompareTo(rankBy(a)); // desc by the summed key
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            if (merged.Count > topN) merged.RemoveRange(topN, merged.Count - topN);
            return merged;
        }

        private sealed class Group
        {
            public ProcessInfo Row;
            public double TopMemberGpu; // max single-member GpuPercent — picks the merged row's GpuEngineName
        }
    }
}
