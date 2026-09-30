using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace task_monitor
{
    /// <summary>
    /// 开机自启动 via a Task Scheduler logon task (NOT the Run registry key). The whole
    /// point is avoiding a UAC prompt at every boot: the app always runs elevated, and an
    /// elevated Run-key/console launch would prompt — a scheduled task registered with
    /// <c>RunLevel=HighestAvailable</c> from an ALREADY-elevated process starts elevated
    /// at logon silently. The app is always elevated by design (App.RunElevationGate), so
    /// registration always has the rights it needs.
    ///
    /// The task is registered from a generated XML (<c>schtasks /Create /XML</c>) rather
    /// than command-line switches because the schtasks defaults are wrong for an always-on
    /// GUI app, and the switches can't change them:
    ///   - <c>ExecutionTimeLimit</c> defaults to 72h — Task Scheduler would KILL the
    ///     overlay after three days of uptime; the XML sets PT0S (never).
    ///   - <c>DisallowStartIfOnBatteries</c>/<c>StopIfGoingOnBatteries</c> default to
    ///     true — a laptop on battery would silently not auto-start; both false here.
    /// The principal is the current user by SID with <c>InteractiveToken</c> (a GUI
    /// overlay can't run S4U), and the logon trigger is pinned to that same user.
    ///
    /// State is NOT duplicated in settings.yaml — the task itself is the source of
    /// truth: <see cref="IsEnabled"/> queries it, <see cref="SetEnabled"/> creates
    /// (overwrite, so a moved exe is re-pointed on every re-enable) or deletes it.
    /// </summary>
    internal static class StartupTask
    {
        private const string TaskName = "TaskMonitor";

        /// <summary>True when the logon task exists (any state — disabled tasks still count
        /// as "on": we never create one disabled).</summary>
        public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

        /// <summary>Create (overwrite) or delete the logon task. Returns false when
        /// schtasks failed, so the caller can snap the settings toggle back to reality.
        /// Never throws: the caller is a UI toggle handler, where an escaping exception
        /// means a crash dialog (App's DispatcherUnhandledException hook) for a
        /// best-effort feature.</summary>
        public static bool SetEnabled(bool on)
        {
            try
            {
                return SetEnabledCore(on);
            }
            catch (Exception ex)
            {
                Logger.Warn($"开机自启动：{(on ? "注册" : "删除")}计划任务失败（设置开关将回滚）", ex);
                return false;
            }
        }

        private static bool SetEnabledCore(bool on)
        {
            if (!on) return RunSchtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var userSid = WindowsIdentity.GetCurrent().User.Value;
            // XML-escape the exe path: an install path containing & or < would make the
            // document malformed, schtasks would fail, and the toggle would silently snap
            // back with no visible trace.
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userSid}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{userSid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{SecurityElement.Escape(exe)}</Command>
    </Exec>
  </Actions>
</Task>
";
            // schtasks /XML insists on a UTF-16 file. Temp file, deleted right after.
            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F") == 0;
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        // schtasks prints localized output we never parse — only the exit code matters, so
        // the pipes stay UNredirected: with RedirectStandardOutput/Error = true and no
        // reader, a chatty failure fills the ~4KB pipe buffer, schtasks blocks, and the
        // bounded wait below burns its whole 10s on the UI thread and reports a false
        // failure (the toggle then snaps back although the task was created).
        // CreateNoWindow keeps the console hidden; the wait stays bounded so a wedged
        // service can never hang the settings page.
        private static int RunSchtasks(string arguments)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }))
                {
                    if (p == null) return -1;
                    p.WaitForExit(10000);
                    if (!p.HasExited) { try { p.Kill(); } catch { /* best effort */ } return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                // Logger, not Debug: a Release build has no Debug listener, and a failed
                // 开机自启动 toggle is user-visible (§4/§5).
                Logger.Warn($"开机自启动：schtasks {arguments} 调用失败", ex);
                return -1;
            }
        }
    }
}
