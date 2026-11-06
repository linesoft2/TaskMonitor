using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

namespace task_monitor
{
    internal sealed class TaskbarOverlayManager
    {
        private sealed class Entry
        {
            public string Id;
            public bool Floating;
            public TaskbarDisplayInfo Target;
            public TaskbarWindow Window;
            public Thread Thread;
            public volatile bool Desired = true;
        }

        private readonly SamplingController _sampling;
        private readonly Dispatcher _ui;
        private readonly object _sync = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly DispatcherTimer _catalogTimer;
        private readonly DispatcherTimer _reconcileDebounce;
        private TaskbarWindow[] _windows = Array.Empty<TaskbarWindow>();
        private List<TaskbarDisplaySelection> _selection;
        private string _catalogSignature;
        private bool _started;
        private volatile bool _stopping;

        private bool _floating;
        private int? _floatingX;
        private int? _floatingY;
        private bool _onLeft;
        private bool _snapToStart;
        private bool _compactLayout;
        private int _sampleIntervalMs = 1000;
        private int _samplingMask = SystemSampler.MaskAll;
        private int _clickDisabledMask;
        private GpuTaskbarContent _gpuContent = GpuTaskbarContent.Usage;
        private bool _gpuTempFahrenheit;
        private bool _floatingTopmost = true;
        private bool _floatingAllowOutOfBounds;
        private bool _floatingDark;
        private float _floatingOpacity = 1f;
        private bool _floatingEdgeHide;
        private bool _floatingFullscreenHide = true;
        private IntPtr[] _keepOut = Array.Empty<IntPtr>();

        public Action<TaskbarWindow, int> ToggleCallback;
        public Action RightClickRequested;
        public Action<int, int> FloatingPositionChanged;
        public Action<int, int> FloatingDragged;
        public Action<TaskbarWindow> AnchorRemoved;
        public Action<IReadOnlyList<TaskbarDisplayInfo>> CatalogChanged;

        internal TaskbarOverlayManager(SamplingController sampling)
        {
            _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
            _ui = Dispatcher.CurrentDispatcher;
            _catalogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _catalogTimer.Tick += (s, e) => Reconcile();
            _reconcileDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _reconcileDebounce.Tick += (s, e) =>
            {
                _reconcileDebounce.Stop();
                Reconcile();
            };
        }

        public TaskbarWindow DefaultWindow
        {
            get
            {
                var windows = _windows;
                return windows.Length == 0 ? null : windows[0];
            }
        }

        public void Start(bool floating, IEnumerable<TaskbarDisplaySelection> selection)
        {
            if (_started) return;
            _floating = floating;
            _selection = CopySelection(selection);
            _stopping = false;
            _started = true;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            _catalogTimer.Start();
            Reconcile();
        }

        public void StopAll()
        {
            if (!_ui.CheckAccess())
            {
                _ui.Invoke(new Action(StopAll));
                return;
            }
            _stopping = true;
            _started = false;
            _catalogTimer.Stop();
            _reconcileDebounce.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Entry[] entries;
            lock (_sync) entries = _entries.ToArray();
            foreach (var entry in entries)
            {
                entry.Desired = false;
                entry.Window.RequestStop();
            }
            RefreshWindows();
            foreach (var entry in entries)
            {
                if (entry.Thread != null && entry.Thread.IsAlive && !entry.Thread.Join(1000))
                    Logger.Warn($"覆盖层线程退出等待超时：{entry.Id}");
            }
        }

        public bool Contains(TaskbarWindow window) => window != null && Array.IndexOf(_windows, window) >= 0;

        public List<TaskbarWindow> GetWatchdogWindows()
        {
            var result = new List<TaskbarWindow>();
            lock (_sync)
            {
                foreach (var entry in _entries)
                {
                    if (!entry.Desired) continue;
                    bool waiting = !entry.Floating && entry.Target != null && !entry.Target.HasTaskbar
                        && entry.Window.OverlayHwnd == IntPtr.Zero;
                    if (!waiting) result.Add(entry.Window);
                }
            }
            return result;
        }

        public void PublishSnapshot(SystemSnapshot snapshot)
        {
            foreach (var window in _windows) window.PublishSnapshot(snapshot);
        }

        public void SetDisplaySelection(IEnumerable<TaskbarDisplaySelection> selection)
        {
            _selection = CopySelection(selection);
            Reconcile();
        }

        public void SetFloatingMode(bool on, int? x, int? y)
        {
            _floating = on;
            _floatingX = x;
            _floatingY = y;
            Reconcile();
        }

        public void SetPlacement(bool onLeft, bool snapToStart)
        {
            _onLeft = onLeft;
            _snapToStart = snapToStart;
            foreach (var window in _windows) window.SetPlacement(onLeft, snapToStart);
        }

        public void SetCompactLayout(bool on)
        {
            _compactLayout = on;
            foreach (var window in _windows) window.SetCompactLayout(on);
        }

        public void SetSampleInterval(int ms)
        {
            _sampleIntervalMs = ms;
            _sampling.SetSampleInterval(ms);
            foreach (var window in _windows) window.SetSampleInterval(ms);
        }

        public void SetMetricSamplingMask(int mask)
        {
            _samplingMask = mask;
            _sampling.SetEnabledMask(mask);
            foreach (var window in _windows) window.SetOverlayMetricSamplingMask(mask);
        }

        public void SetGpuTempDisplay(GpuTaskbarContent content, int tempIndex, bool fahrenheit)
        {
            _gpuContent = content;
            _gpuTempFahrenheit = fahrenheit;
            _sampling.SetGpuTempConfig(tempIndex, fahrenheit);
            foreach (var window in _windows) window.SetOverlayGpuTempDisplay(content, fahrenheit);
        }

        public void SetMergeSamePathProcesses(bool on) => _sampling.SetMergeByPath(on);
        public void SetDiskDisplay(int mode, int index) => _sampling.SetDiskDisplay(mode, index);
        public void SetGpuDisplay(int mode, int index) => _sampling.SetGpuDisplay(mode, index);
        public void SetNetAdapter(string id) => _sampling.SetNetAdapter(id);
        public void SetClashApi(bool enabled, string address, string secret) => _sampling.SetClashApi(enabled, address, secret);
        public void SetPublicIpLookup(bool enabled) => _sampling.SetPublicIpLookup(enabled);

        public void SetColumnClickEnabled(int slot, bool enabled)
        {
            if (slot < 0 || slot >= 5) return;
            int bit = 1 << slot;
            _clickDisabledMask = enabled ? _clickDisabledMask & ~bit : _clickDisabledMask | bit;
            foreach (var window in _windows) window.SetColumnClickEnabled(slot, enabled);
        }

        public void SetFloatingTopmost(bool on)
        {
            _floatingTopmost = on;
            FloatingWindow()?.SetFloatingTopmost(on);
        }

        public void SetFloatingAllowOutOfBounds(bool on)
        {
            _floatingAllowOutOfBounds = on;
            FloatingWindow()?.SetFloatingAllowOutOfBounds(on);
        }

        public void SetFloatingDark(bool dark)
        {
            _floatingDark = dark;
            FloatingWindow()?.SetFloatingDark(dark);
        }

        public void SetFloatingOpacity(double opacity)
        {
            _floatingOpacity = (float)opacity;
            FloatingWindow()?.SetFloatingOpacity(opacity);
        }

        public void SetFloatingEdgeHide(bool on)
        {
            _floatingEdgeHide = on;
            FloatingWindow()?.SetFloatingEdgeHide(on);
        }

        public void SetFloatingFullscreenHide(bool on)
        {
            _floatingFullscreenHide = on;
            FloatingWindow()?.SetFloatingFullscreenHide(on);
        }

        public void SetFloatingKeepOut(IntPtr[] hwnds)
        {
            _keepOut = hwnds ?? Array.Empty<IntPtr>();
            FloatingWindow()?.SetFloatingKeepOut(_keepOut);
        }

        private TaskbarWindow FloatingWindow()
        {
            foreach (var window in _windows)
            {
                foreach (var entry in _entries)
                {
                    if (entry.Floating && entry.Desired && ReferenceEquals(entry.Window, window)) return window;
                }
            }
            return null;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (!_ui.CheckAccess())
            {
                _ui.BeginInvoke(new Action(TriggerReconcile));
                return;
            }
            TriggerReconcile();
        }

        private void TriggerReconcile()
        {
            _reconcileDebounce.Stop();
            _reconcileDebounce.Start();
        }

        private void Reconcile()
        {
            if (!_ui.CheckAccess())
            {
                _ui.BeginInvoke(new Action(Reconcile));
                return;
            }
            if (!_started || _stopping) return;

            var displays = TaskbarDisplayCatalog.GetDisplays();
            LogCatalogIfChanged(displays);
            var desired = BuildDesired(displays);
            Entry[] entries;
            lock (_sync) entries = _entries.ToArray();

            foreach (var entry in entries)
            {
                string key = Key(entry.Floating, entry.Id);
                if (!desired.ContainsKey(key))
                {
                    StopEntry(entry);
                    continue;
                }
                var current = desired[key];
                if (entry.Desired && entry.Target != null && current != null
                    && entry.Target.TaskbarHwnd != IntPtr.Zero && current.TaskbarHwnd != IntPtr.Zero
                    && (entry.Target.TaskbarHwnd != current.TaskbarHwnd
                        || entry.Target.MonitorHandle != current.MonitorHandle))
                {
                    Logger.Info($"显示器任务栏归属变化：{entry.Id}——重建覆盖层");
                    StopEntry(entry);
                }
            }

            foreach (var pair in desired)
            {
                if (FindEntry(pair.Key) != null) continue;
                StartEntry(pair.Key == FloatingKey, pair.Value);
            }
            RefreshWindows();
        }

        private void LogCatalogIfChanged(List<TaskbarDisplayInfo> displays)
        {
            var signature = new StringBuilder();
            foreach (var display in displays)
            {
                signature.Append(display.Id).Append('@').Append(display.MonitorHandle)
                    .Append('/').Append(display.TaskbarHwnd).Append('|');
            }
            string current = signature.ToString();
            if (string.Equals(current, _catalogSignature, StringComparison.Ordinal)) return;
            _catalogSignature = current;
            foreach (var display in displays)
            {
                Logger.Info($"显示器目录：{display.Name}，主屏={display.IsPrimary}，任务栏={display.HasTaskbar}，ID={display.Id}");
            }
            CatalogChanged?.Invoke(displays);
        }

        private Dictionary<string, TaskbarDisplayInfo> BuildDesired(List<TaskbarDisplayInfo> displays)
        {
            var result = new Dictionary<string, TaskbarDisplayInfo>(StringComparer.OrdinalIgnoreCase);
            if (_floating)
            {
                result[FloatingKey] = TaskbarDisplayCatalog.GetPrimaryDisplay();
                return result;
            }

            if (_selection != null)
            {
                foreach (var selected in _selection)
                {
                    if (selected == null || string.IsNullOrWhiteSpace(selected.Id)) continue;
                    var display = FindDisplay(displays, selected.Id);
                    if (display != null && display.HasTaskbar) result[selected.Id] = display;
                }
            }
            if (result.Count == 0)
            {
                var primary = TaskbarDisplayCatalog.GetPrimaryDisplay();
                if (primary != null) result[primary.Id] = primary;
            }
            return result;
        }

        private const string FloatingKey = "<floating>";

        private static string Key(bool floating, string id) => floating ? FloatingKey : id;

        private Entry FindEntry(string key)
        {
            lock (_sync)
            {
                foreach (var entry in _entries)
                {
                    if (string.Equals(Key(entry.Floating, entry.Id), key, StringComparison.OrdinalIgnoreCase))
                        return entry;
                }
            }
            return null;
        }

        private void StartEntry(bool floating, TaskbarDisplayInfo target)
        {
            string id = floating ? string.Empty : target.Id;
            var entry = new Entry { Id = id, Floating = floating, Target = target };
            var window = new TaskbarWindow(_sampling);
            entry.Window = window;
            ConfigureWindow(entry);
            lock (_sync) _entries.Add(entry);
            RefreshWindows();
            Logger.Info($"创建{(floating ? "悬浮" : "任务栏")}覆盖层：{(floating ? "单实例" : target.Name + "，ID=" + target.Id)}");

            var thread = new Thread(() => EntryLoop(entry))
            {
                IsBackground = true,
                Name = floating ? "TaskbarWindow-floating" : "TaskbarWindow-" + id
            };
            entry.Thread = thread;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void ConfigureWindow(Entry entry)
        {
            var window = entry.Window;
            window.ToggleCallback = column =>
            {
                if (entry.Desired && !_stopping) ToggleCallback?.Invoke(window, column);
            };
            window.RightClickRequested = () => RightClickRequested?.Invoke();
            window.SetPlacement(_onLeft, _snapToStart);
            window.SetCompactLayout(_compactLayout);
            window.SetSampleInterval(_sampleIntervalMs);
            window.SetOverlayMetricSamplingMask(_samplingMask);
            window.SetOverlayGpuTempDisplay(_gpuContent, _gpuTempFahrenheit);
            for (int slot = 0; slot < 5; slot++)
                window.SetColumnClickEnabled(slot, (_clickDisabledMask & (1 << slot)) == 0);
            window.SetFloatingMode(entry.Floating, _floatingX, _floatingY);
            window.SetFloatingTopmost(_floatingTopmost);
            window.SetFloatingAllowOutOfBounds(_floatingAllowOutOfBounds);
            window.SetFloatingDark(_floatingDark);
            window.SetFloatingOpacity(_floatingOpacity);
            window.SetFloatingEdgeHide(_floatingEdgeHide);
            window.SetFloatingFullscreenHide(_floatingFullscreenHide);
            window.SetFloatingKeepOut(_keepOut);
            window.PublishSnapshot(_sampling.LatestSnapshot);
            if (entry.Floating)
            {
                window.FloatingPositionChanged = (x, y) =>
                {
                    if (!entry.Desired || _stopping) return;
                    _floatingX = x;
                    _floatingY = y;
                    FloatingPositionChanged?.Invoke(x, y);
                };
                window.FloatingDragged = (dx, dy) =>
                {
                    if (entry.Desired && !_stopping) FloatingDragged?.Invoke(dx, dy);
                };
            }
        }

        private void EntryLoop(Entry entry)
        {
            try
            {
                while (entry.Desired && !_stopping)
                {
                    try
                    {
                        entry.Target = ResolveTarget(entry);
                        entry.Window.Start(entry.Target);
                        if (!entry.Desired || _stopping) break;
                        int delay = entry.Window.ConsumeQuickRestart() ? 150 : 2000;
                        Logger.Warn($"覆盖层 Start() 返回：{entry.Id}，{delay}ms 后按当前显示器目录重建");
                        if (!SleepBetweenRebuilds(entry, delay)) break;
                    }
                    catch (Exception ex)
                    {
                        try { entry.Window.AbortStart(); }
                        catch (Exception cleanupEx) { Logger.Warn($"失败启动清理异常：{entry.Id}", cleanupEx); }
                        Logger.Error($"覆盖层 Start() 异常：{entry.Id}，2s 后重试", ex);
                        if (!SleepBetweenRebuilds(entry, 2000)) break;
                    }
                }
            }
            finally
            {
                TaskbarWindow.ReleaseNativeCallChannel();
                CrashTrace.ClearThreadTrace(entry.Window.NativeThreadId);
                _ui.BeginInvoke(new Action(() => OnEntryExited(entry)));
            }
        }

        private TaskbarDisplayInfo ResolveTarget(Entry entry)
        {
            if (entry.Floating) return TaskbarDisplayCatalog.GetPrimaryDisplay();
            var current = TaskbarDisplayCatalog.FindDisplay(entry.Id);
            if (current != null) return current;
            return new TaskbarDisplayInfo
            {
                Id = entry.Id,
                Name = entry.Target?.Name ?? entry.Id
            };
        }

        private bool SleepBetweenRebuilds(Entry entry, int delayMs)
        {
            int elapsed = 0;
            while (entry.Desired && !_stopping && elapsed < delayMs)
            {
                entry.Window.LastTickTickCount = (long)SystemInfo.GetTickCount64();
                int chunk = Math.Min(250, delayMs - elapsed);
                Thread.Sleep(chunk);
                elapsed += chunk;
            }
            entry.Window.LastTickTickCount = (long)SystemInfo.GetTickCount64();
            return entry.Desired && !_stopping;
        }

        private void StopEntry(Entry entry)
        {
            if (!entry.Desired) return;
            entry.Desired = false;
            Logger.Info($"停止覆盖层：{entry.Id}");
            entry.Window.RequestStop();
            RefreshWindows();
        }

        private void OnEntryExited(Entry entry)
        {
            lock (_sync) _entries.Remove(entry);
            RefreshWindows();
            AnchorRemoved?.Invoke(entry.Window);
            if (!_stopping) Reconcile();
        }

        private void RefreshWindows()
        {
            lock (_sync)
            {
                var list = new List<TaskbarWindow>();
                foreach (var entry in _entries)
                {
                    if (entry.Desired) list.Add(entry.Window);
                }
                _windows = list.ToArray();
            }
        }

        private static TaskbarDisplayInfo FindDisplay(List<TaskbarDisplayInfo> displays, string id)
        {
            foreach (var display in displays)
            {
                if (string.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase)) return display;
            }
            return null;
        }

        private static List<TaskbarDisplaySelection> CopySelection(IEnumerable<TaskbarDisplaySelection> selection)
        {
            if (selection == null) return null;
            var copy = new List<TaskbarDisplaySelection>();
            foreach (var item in selection)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id)) continue;
                copy.Add(new TaskbarDisplaySelection { Id = item.Id, Name = item.Name });
            }
            return copy;
        }
    }
}
