using System;
using System.Collections.Generic;

namespace task_monitor
{
    /// <summary>
    /// What a renamed svchost.exe row carries (<see cref="ProcessInfo.ServiceHost"/>): the
    /// hosted services, and whether it's a single service (row name = its display name, tag
    /// 服务) or a -k group (row name = the group name, tag 服务组). The hover tooltip
    /// (ProcessListTip) reads the services from here — it never queries the SCM itself
    /// (except the single service's lazily-cached 描述).
    /// </summary>
    internal sealed class ServiceHostInfo
    {
        public bool IsGroup;
        public string GroupName;          // IsGroup only; null when the -k parse failed
        public List<ServiceRef> Services; // ≥ 1
    }

    /// <summary>
    /// svchost.exe → 服务命名 for the five per-process detail lists. <see cref="Refresh"/>
    /// re-enumerates the running Win32 services ONCE per tick (one
    /// <c>EnumServicesStatusExW(SC_ENUM_PROCESS_INFO)</c> RPC — tasklist /svc's and Task
    /// Manager's source) into a PID → services map; SystemSampler then applies it to every
    /// final list: an svchost.exe row with a known PID is renamed to the single service's
    /// display name, or to the -k group name when it hosts several.
    /// <para>Applied AFTER <see cref="ProcessListMerger"/> on purpose: the merger exempts
    /// svchost by its "svchost.exe" row name, and renaming inside a sampler would break that
    /// exemption — every instance shares the one exe path and they'd collapse into a single
    /// merged row.</para>
    /// </summary>
    internal sealed class ServiceHostMap
    {
        private Dictionary<int, List<ServiceRef>> _byPid = new Dictionary<int, List<ServiceRef>>();

        /// <summary>Once per tick, on the taskbar STA thread (same one-call-per-tick budget
        /// as the process walk). Keeps the last good map when the SCM query fails.</summary>
        public void Refresh()
        {
            var map = ServiceControlManager.EnumServicesByPid();
            if (map != null) _byPid = map;
        }

        /// <summary>Rename every svchost.exe row in a FINAL (post-merge) list in place.
        /// Rows without a service mapping keep "svchost.exe" (e.g. an svchost with no
        /// running services left, or an SCM failure this tick).</summary>
        public void ApplyTo(List<ProcessInfo> rows)
        {
            if (rows == null || rows.Count == 0 || _byPid.Count == 0) return;
            foreach (var row in rows)
            {
                if (!"svchost.exe".Equals(row.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_byPid.TryGetValue(row.Pid, out var svcs) || svcs.Count == 0) continue;

                if (svcs.Count == 1)
                {
                    row.Name = DisplayOf(svcs[0]);
                    row.ServiceHost = new ServiceHostInfo { IsGroup = false, Services = svcs };
                }
                else
                {
                    // Every service in one svchost PID shares its -k group by construction,
                    // so asking any one of them suffices (one QueryServiceConfig RPC — only
                    // multi-service rows pay it, a handful per tick at most). Fallback when
                    // unreadable: the first service's display name (the tag still says 服务组
                    // and the tooltip lists all members).
                    string group = ServiceControlManager.GetServiceGroupName(svcs[0].Name);
                    row.Name = group ?? DisplayOf(svcs[0]);
                    row.ServiceHost = new ServiceHostInfo { IsGroup = true, GroupName = group, Services = svcs };
                }
            }
        }

        private static string DisplayOf(ServiceRef s) =>
            string.IsNullOrEmpty(s.DisplayName) ? s.Name : s.DisplayName;
    }
}
