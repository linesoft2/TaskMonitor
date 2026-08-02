using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace task_monitor
{
    /// <summary>One running Win32 service as <c>EnumServicesStatusExW</c> reports it.</summary>
    internal sealed class ServiceRef
    {
        public string Name;        // service name ("wuauserv")
        public string DisplayName; // localized display name ("Windows Update")
    }

    /// <summary>
    /// P/Invoke for the Service Control Manager APIs behind the process lists' svchost →
    /// service naming (ServiceHostMap / ProcessListTip):
    /// <list type="bullet">
    /// <item><c>EnumServicesStatusExW(SC_ENUM_PROCESS_INFO)</c> maps every RUNNING Win32
    /// service to its hosting PID in ONE RPC — the same source <c>tasklist /svc</c> and
    /// Task Manager's service tab use. Stopped services report PID 0 and are skipped.</item>
    /// <item><c>QueryServiceConfigW</c>'s lpBinaryPathName carries the service's full command
    /// line ("…\svchost.exe -k netsvcs -p") — the -k token is Task Manager's "服务主机: 组"
    /// name, and every service sharing one svchost PID shares its group by construction.</item>
    /// <item><c>QueryServiceConfig2W(SERVICE_CONFIG_DESCRIPTION)</c> is the services.msc 描述
    /// text. The raw value may be an "@dll,-id" indirect string — resolved via
    /// <c>SHLoadIndirectString</c> (shlwapi), the same loader services.msc uses.</item>
    /// </list>
    /// Every call opens/closes its own SCM handle, so the class is safe from any thread:
    /// the per-tick refresh + group lookup run on the taskbar STA thread, the 描述 lookup on
    /// the WPF UI thread (hover).
    /// </summary>
    internal static class ServiceControlManager
    {
        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        private const uint SERVICE_QUERY_CONFIG = 0x0001;
        private const int SC_ENUM_PROCESS_INFO = 0;
        private const uint SERVICE_WIN32 = 0x30;
        private const uint SERVICE_ACTIVE = 0x1;
        private const uint SERVICE_CONFIG_DESCRIPTION = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct ENUM_SERVICE_STATUS_PROCESSW
        {
            public IntPtr lpServiceName;
            public IntPtr lpDisplayName;
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QUERY_SERVICE_CONFIGW
        {
            public uint dwServiceType;
            public uint dwStartType;
            public uint dwErrorControl;
            public IntPtr lpBinaryPathName;
            public IntPtr lpLoadOrderGroup;
            public uint dwTagId;
            public IntPtr lpDependencies;
            public IntPtr lpServiceStartName;
            public IntPtr lpDisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_DESCRIPTIONW
        {
            public IntPtr lpDescription;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint dwAccess);

        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle", ExactSpelling = true, SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumServicesStatusExW(
            IntPtr hSCManager, int infoLevel, uint dwServiceType, uint dwServiceState,
            IntPtr lpServices, uint cbBufSize, out uint pcbBytesNeeded,
            out uint lpServicesReturned, ref uint lpResumeHandle, string pszGroupName);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryServiceConfigW(
            IntPtr hService, IntPtr lpServiceConfig, uint cbBufSize, out uint pcbBytesNeeded);

        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryServiceConfig2W(
            IntPtr hService, uint dwInfoLevel, IntPtr lpBuffer, uint cbBufSize, out uint pcbBytesNeeded);

        [DllImport("shlwapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

        /// <summary>Every running Win32 service, grouped by hosting PID. Null on SCM failure
        /// (the caller keeps its previous map). One RPC + one buffer; ~1 ms for ~200 services.</summary>
        public static Dictionary<int, List<ServiceRef>> EnumServicesByPid()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            if (scm == IntPtr.Zero)
            {
                Logger.WarnOnce("scm-open", $"OpenSCManagerW 失败 err={Marshal.GetLastWin32Error()}——svchost→服务命名本 tick 保留旧映射");
                return null;
            }
            try
            {
                // First call just sizes the buffer (always fails with ERROR_MORE_DATA when
                // any services exist).
                uint needed = 0, returned = 0, resume = 0;
                EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                    IntPtr.Zero, 0, out needed, out returned, ref resume, null);
                if (needed == 0) return null;

                IntPtr buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    resume = 0;
                    if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                        buf, needed, out _, out returned, ref resume, null))
                    {
                        Logger.WarnOnce("scm-enum", $"EnumServicesStatusExW 失败 err={Marshal.GetLastWin32Error()}——svchost→服务命名本 tick 保留旧映射");
                        return null;
                    }

                    var map = new Dictionary<int, List<ServiceRef>>();
                    int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
                    long p = buf.ToInt64();
                    for (int i = 0; i < returned; i++, p += stride)
                    {
                        var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>((IntPtr)p);
                        if (e.dwProcessId == 0) continue;
                        var svc = new ServiceRef
                        {
                            Name = Marshal.PtrToStringUni(e.lpServiceName) ?? "",
                            DisplayName = Marshal.PtrToStringUni(e.lpDisplayName) ?? "",
                        };
                        if (!map.TryGetValue((int)e.dwProcessId, out var list))
                            map[(int)e.dwProcessId] = list = new List<ServiceRef>();
                        list.Add(svc);
                    }
                    return map;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseServiceHandle(scm); }
        }

        /// <summary>The svchost -k group name behind a service (Task Manager's "服务主机: 组"),
        /// parsed off the service's own command line (QueryServiceConfigW lpBinaryPathName:
        /// "…\svchost.exe -k netsvcs -p"). Null when unreadable or not group-hosted. One RPC —
        /// ServiceHostMap only calls this for MULTI-service svchost rows (a handful per tick;
        /// Win10 1703+ splits most services into single-service hosts).</summary>
        public static string GetServiceGroupName(string serviceName)
        {
            string cmd = QueryConfigString(serviceName, description: false);
            if (string.IsNullOrEmpty(cmd)) return null;

            int i = cmd.IndexOf("-k ", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += 3;
            // Defensive: a quoted group name ("-k \"group name\"") doesn't occur for inbox
            // services but costs nothing to handle.
            if (i < cmd.Length && cmd[i] == '"')
            {
                int close = cmd.IndexOf('"', i + 1);
                return close > i + 1 ? cmd.Substring(i + 1, close - i - 1) : null;
            }
            int end = i;
            while (end < cmd.Length && !char.IsWhiteSpace(cmd[end])) end++;
            return end > i ? cmd.Substring(i, end - i) : null;
        }

        // 描述 lookup results, resolved once per service per process lifetime (descriptions
        // don't change while the system runs). UI thread only (ProcessListTip hover) — the
        // per-tick path never queries descriptions. A null result is cached too so a
        // description-less service isn't re-queried on every MouseMove.
        private static readonly Dictionary<string, string> _descCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The service's services.msc 描述 text ("@…" indirect strings resolved via
        /// SHLoadIndirectString). Null when the service has none / the query failed. Lazy +
        /// cached (<see cref="_descCache"/>); called from the UI thread on hover only.</summary>
        public static string GetServiceDescription(string serviceName)
        {
            if (_descCache.TryGetValue(serviceName, out string cached)) return cached;
            string desc = ResolveIndirect(QueryConfigString(serviceName, description: true));
            _descCache[serviceName] = desc;
            return desc;
        }

        // The raw config string: lpBinaryPathName (description:false) or lpDescription
        // (description:true). Two-call buffer sizing like the enum above. Null on failure.
        private static string QueryConfigString(string serviceName, bool description)
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return null;
            IntPtr svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_CONFIG);
            CloseServiceHandle(scm); // the service handle stands on its own once opened
            if (svc == IntPtr.Zero) return null;
            try
            {
                uint needed = 0;
                if (description) QueryServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, IntPtr.Zero, 0, out needed);
                else QueryServiceConfigW(svc, IntPtr.Zero, 0, out needed);
                if (needed == 0) return null;

                IntPtr buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (description)
                    {
                        if (!QueryServiceConfig2W(svc, SERVICE_CONFIG_DESCRIPTION, buf, needed, out _))
                            return null;
                        return Marshal.PtrToStringUni(
                            Marshal.PtrToStructure<SERVICE_DESCRIPTIONW>(buf).lpDescription);
                    }
                    if (!QueryServiceConfigW(svc, buf, needed, out _))
                        return null;
                    return Marshal.PtrToStringUni(
                        Marshal.PtrToStructure<QUERY_SERVICE_CONFIGW>(buf).lpBinaryPathName);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseServiceHandle(svc); }
        }

        // services.msc-style "@dll,-id" indirect string → the localized text. Plain strings
        // (most third-party descriptions) pass through untouched. Null when an indirect
        // string can't be resolved (the raw "@…" form is useless to show).
        private static string ResolveIndirect(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '@') return string.IsNullOrEmpty(s) ? null : s;
            var sb = new StringBuilder(2048);
            return SHLoadIndirectString(s, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0
                ? sb.ToString() : null;
        }
    }
}
