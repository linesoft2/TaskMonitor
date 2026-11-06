using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace task_monitor
{
    internal sealed class TaskbarDisplayInfo
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string DeviceName { get; internal set; }
        public bool IsPrimary { get; internal set; }
        public IntPtr MonitorHandle { get; internal set; }
        public IntPtr TaskbarHwnd { get; internal set; }
        public WindowInterop.RECT Bounds { get; internal set; }
        public bool HasTaskbar => TaskbarHwnd != IntPtr.Zero;
    }

    internal static class TaskbarDisplayCatalog
    {
        private const string PrimaryTaskbarClass = "Shell_TrayWnd";
        private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

        public static List<TaskbarDisplayInfo> GetDisplays()
        {
            Dictionary<IntPtr, IntPtr> taskbars = FindTaskbars();
            var displays = new List<TaskbarDisplayInfo>();
            uint monitorInfoSize = (uint)Marshal.SizeOf(typeof(DisplayInterop.MONITORINFOEX));

            DisplayInterop.MonitorEnumProc enumMonitors = (hMonitor, hdcMonitor, lprcMonitor, dwData) =>
            {
                var info = new DisplayInterop.MONITORINFOEX { cbSize = monitorInfoSize };
                if (DisplayInterop.GetMonitorInfoW(hMonitor, ref info))
                {
                    string deviceName = info.szDevice ?? string.Empty;
                    ReadMonitorDevice(deviceName, out string deviceId, out string deviceString);
                    taskbars.TryGetValue(hMonitor, out IntPtr taskbarHwnd);
                    displays.Add(new TaskbarDisplayInfo
                    {
                        Id = deviceId ?? deviceName,
                        Name = ResolveDisplayName(deviceId, deviceString ?? GetFriendlyName(deviceName)),
                        DeviceName = deviceName,
                        IsPrimary = (info.dwFlags & DisplayInterop.MONITORINFOF_PRIMARY) != 0,
                        MonitorHandle = hMonitor,
                        TaskbarHwnd = taskbarHwnd,
                        Bounds = info.rcMonitor
                    });
                }
                return true;
            };

            try
            {
                DisplayInterop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, enumMonitors, IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(enumMonitors);
            }

            displays.Sort(CompareDisplays);
            DisambiguateNames(displays);
            return displays;
        }

        internal static string ResolveDisplayName(string deviceId, string fallback) =>
            ReadEdidFriendlyName(deviceId) ?? fallback;

        private static string ReadEdidFriendlyName(string deviceId)
        {
            // \\?\DISPLAY#model#instance#{guid} maps to Enum\DISPLAY\model\instance. In the
            // base EDID block, four 18-byte descriptors start at 54; tag FC holds a 13-byte name.
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            string[] parts = deviceId.Split('#');
            if (parts.Length < 3 || !string.Equals(parts[0], @"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                string keyPath = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
                using (RegistryKey parameters = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    byte[] edid = parameters?.GetValue("EDID") as byte[];
                    if (edid == null) return null;
                    for (int offset = 54; offset < 126 && offset + 17 < edid.Length; offset += 18)
                    {
                        if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0
                            || edid[offset + 3] != 0xFC) continue;
                        var name = new StringBuilder(13);
                        for (int i = offset + 5; i < offset + 18; i++)
                        {
                            byte value = edid[i];
                            if (value == 0 || value == '\r' || value == '\n') break;
                            name.Append((char)value);
                        }
                        string friendlyName = NonBlank(name.ToString());
                        if (friendlyName != null) return friendlyName;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("monitor-edid-" + deviceId, "读取显示器 EDID 失败——保留系统显示器名称", ex);
            }
            return null;
        }

        private static void DisambiguateNames(List<TaskbarDisplayInfo> displays)
        {
            var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            foreach (TaskbarDisplayInfo display in displays)
            {
                counts.TryGetValue(display.Name, out int count);
                counts[display.Name] = count + 1;
            }
            foreach (TaskbarDisplayInfo display in displays)
            {
                if (counts[display.Name] > 1)
                    display.Name += "（" + GetFriendlyName(display.DeviceName) + "）";
            }
        }

        public static TaskbarDisplayInfo FindDisplay(string id)
        {
            foreach (TaskbarDisplayInfo display in GetDisplays())
            {
                if (string.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase))
                    return display;
            }
            return null;
        }

        public static TaskbarDisplayInfo GetPrimaryDisplay()
        {
            List<TaskbarDisplayInfo> displays = GetDisplays();
            foreach (TaskbarDisplayInfo display in displays)
            {
                if (display.IsPrimary) return display;
            }
            return displays.Count == 0 ? null : displays[0];
        }

        private static Dictionary<IntPtr, IntPtr> FindTaskbars()
        {
            var taskbars = new Dictionary<IntPtr, IntPtr>();
            AddTaskbar(taskbars, WindowInterop.FindWindowW(PrimaryTaskbarClass, null));

            var secondaryTaskbars = new List<IntPtr>();
            DisplayInterop.EnumWindowsProc enumWindows = (hWnd, lParam) =>
            {
                var className = new StringBuilder(SecondaryTaskbarClass.Length + 1);
                if (WindowInterop.GetClassNameW(hWnd, className, className.Capacity) > 0
                    && string.Equals(className.ToString(), SecondaryTaskbarClass, StringComparison.Ordinal))
                {
                    secondaryTaskbars.Add(hWnd);
                }
                return true;
            };

            try
            {
                DisplayInterop.EnumWindows(enumWindows, IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(enumWindows);
            }

            foreach (IntPtr taskbar in secondaryTaskbars)
                AddTaskbar(taskbars, taskbar);
            return taskbars;
        }

        private static void AddTaskbar(Dictionary<IntPtr, IntPtr> taskbars, IntPtr taskbar)
        {
            if (taskbar == IntPtr.Zero) return;
            IntPtr monitor = WindowInterop.MonitorFromWindow(taskbar, WindowInterop.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && !taskbars.ContainsKey(monitor))
                taskbars.Add(monitor, taskbar);
        }

        private static void ReadMonitorDevice(string deviceName, out string deviceId, out string deviceString)
        {
            deviceId = null;
            deviceString = null;
            if (string.IsNullOrWhiteSpace(deviceName)) return;

            uint deviceSize = (uint)Marshal.SizeOf(typeof(DisplayInterop.DISPLAY_DEVICEW));
            for (uint index = 0; deviceId == null || deviceString == null; index++)
            {
                var device = new DisplayInterop.DISPLAY_DEVICEW { cb = deviceSize };
                if (!DisplayInterop.EnumDisplayDevicesW(
                    deviceName, index, ref device, DisplayInterop.EDD_GET_DEVICE_INTERFACE_NAME))
                    break;

                if (deviceId == null) deviceId = NonBlank(device.DeviceID);
                if (deviceString == null) deviceString = NonBlank(device.DeviceString);
            }
        }

        private static string NonBlank(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string GetFriendlyName(string deviceName)
        {
            const string devicePrefix = @"\\.\DISPLAY";
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                if (deviceName.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
                    && deviceName.Length > devicePrefix.Length)
                {
                    return "显示器 " + deviceName.Substring(devicePrefix.Length);
                }
                return deviceName;
            }
            return "未知显示器";
        }

        private static int CompareDisplays(TaskbarDisplayInfo left, TaskbarDisplayInfo right)
        {
            if (left.IsPrimary != right.IsPrimary) return left.IsPrimary ? -1 : 1;

            int result = StringComparer.OrdinalIgnoreCase.Compare(left.DeviceName, right.DeviceName);
            if (result != 0) return result;
            result = StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.DeviceName, right.DeviceName);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Id, right.Id);
            if (result != 0) return result;
            return left.MonitorHandle.ToInt64().CompareTo(right.MonitorHandle.ToInt64());
        }
    }
}
