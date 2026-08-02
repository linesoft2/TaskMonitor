using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Controls;

namespace task_monitor
{
    /// <summary>
    /// The settings window — a WinUI-look shell (iNKORE modern window style + Mica)
    /// holding ONE scrollable page (Win11 Settings style, cards grouped by 类别
    /// TextBlock headers): 通用 (开机自启动 / 采样间隔 / 检查更新 / 更新源), 外观 (主题 / 靠左显示 — an
    /// expander whose item is 靠左位置), 采样 (one expander per metric, the toggle in
    /// its content area enabling/disabling that metric's sampling; 磁盘's and GPU's
    /// expanders also hold 显示方式 + the specific-device picker, 网络's the 适配器
    /// picker, the 公网 IP lookup switch and the Clash/Mihomo integration (switch +
    /// endpoint + 测试连接); plus the section-wide 合并相同程序 card) and 关于. Opened
    /// from the taskbar overlay's right-click menu (App keeps at most one instance).
    /// Every card is live: initial values + change callbacks arrive via the ctor (App
    /// owns settings.yaml, the scheduled task, the app theme and the taskbar overlay).
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Action<bool, bool> _placementChanged;
        private readonly Action<bool> _autoStartChanged;
        private readonly Action<int> _themeChanged;           // 0=跟随系统 1=浅色 2=深色
        private readonly Action<int> _intervalChanged;        // ms: 500 / 1000 / 2000
        private readonly Action<int, bool> _samplingChanged;  // (overlay hit slot 0–4, enabled)
        private readonly Action<bool> _mergeSamePathChanged;  // 合并相同程序
        private readonly Action<int, int> _diskDisplayChanged; // (mode 0=平均 1=最高 2=特定, PhysicalDrive index)
        private readonly Action<int, int> _gpuDisplayChanged;  // (mode 0=平均 1=最高 2=特定, "GPU N" number)
        private readonly Action<string, string> _netAdapterChanged; // (adapter Id or "" = 自动, display name)
        private readonly Action<bool> _publicIpLookupChanged;       // 公网 IP lookup
        private readonly Action<bool, string, string> _clashApiChanged; // (enabled, controller address or "" = default, API secret)
        private readonly Action<bool> _updateCheckChanged;  // 检查更新
        private readonly Action<int> _updateSourceChanged;  // 0=CNB 1=GitHub
        // 500ms single-shot debounce for the two Clash text boxes — a yaml write +
        // sampler retarget must not fire on every keystroke.
        private DispatcherTimer _clashDebounce;
        // Guards the event handlers while the ctor pushes the initial state into the
        // controls (setting IsOn / SelectedIndex fires Toggled / SelectionChanged).
        private bool _loaded;

        /// <param name="overlayOnLeft">Effective 靠左显示 state (yaml null = the left default).</param>
        /// <param name="snapToStart">Current overlaySnapToStart value (left-side anchor).</param>
        /// <param name="placementChanged">(onLeft, snapToStart) reported on every user change.</param>
        /// <param name="autoStartOn">Whether the logon scheduled task currently exists.</param>
        /// <param name="autoStartChanged">Reported with the requested on/off; on a schtasks
        /// failure App calls <see cref="SyncAutoStart"/> to snap the toggle back.</param>
        /// <param name="themeIndex">0=跟随系统 (yaml null) 1=浅色 2=深色.</param>
        /// <param name="themeChanged">Reported with the new combo index.</param>
        /// <param name="intervalMs">Current sampling interval (500/1000/2000).</param>
        /// <param name="intervalChanged">Reported with the new interval in ms.</param>
        /// <param name="samplingMask">Current per-metric sampling mask (SystemSampler.Mask*
        /// bits, in overlay hit-slot order).</param>
        /// <param name="samplingChanged">Reported with (slot, enabled) on every 采样 toggle.</param>
        /// <param name="mergeSamePath">Current 合并相同程序 state (yaml null = the on default).</param>
        /// <param name="mergeSamePathChanged">Reported with the requested on/off.</param>
        /// <param name="diskDisplayIndex">Current 磁盘显示方式 combo index: 0=所有磁盘平均
        /// (yaml null) 1=最高利用率 2=特定磁盘.</param>
        /// <param name="pickedDiskIndex">The PhysicalDrive index the 特定磁盘 picker shows
        /// selected (yaml null = disk 0).</param>
        /// <param name="disks">The currently present physical disks (from the latest
        /// snapshot; null/empty when disk sampling is off) — the picker's items.</param>
        /// <param name="diskDisplayChanged">Reported with (modeIndex, PhysicalDrive index)
        /// on every 显示方式 / 特定磁盘 change.</param>
        /// <param name="gpuDisplayIndex">Current GPU 显示方式 combo index: 0=所有 GPU 平均
        /// 1=最高利用率 (yaml null, the default) 2=特定 GPU.</param>
        /// <param name="pickedGpuIndex">The "GPU N" number the 特定 GPU picker shows selected
        /// (yaml null = GPU 0).</param>
        /// <param name="gpus">The currently present GPU adapters (from the latest snapshot;
        /// null/empty when GPU sampling is off) — the picker's items.</param>
        /// <param name="gpuDisplayChanged">Reported with (modeIndex, "GPU N" number) on
        /// every 显示方式 / 特定 GPU change.</param>
        /// <param name="netAdapterId">The pinned adapter's NetworkInterface.Id (GUID);
        /// null/empty = 自动.</param>
        /// <param name="netAdapterName">The pinned adapter's display name, for the
        /// （未连接） placeholder when it's absent.</param>
        /// <param name="adapters">Every non-loopback adapter (Id + "Description (Name)"
        /// label), enumerated by App on settings open — the combo's items.</param>
        /// <param name="netAdapterChanged">Reported with (adapter Id or "" = 自动, its
        /// display name) on every 适配器 change.</param>
        /// <param name="publicIpLookupOn">The 公网 IP lookup switch state (yaml null = the
        /// on default).</param>
        /// <param name="publicIpLookupChanged">Reported with the requested on/off — takes
        /// effect live (the poll thread drops its cached address and stops BOTH the HTTP
        /// lookups and the 公网延迟 ICMP probe).</param>
        /// <param name="clashEnabled">The Clash/Mihomo integration switch state (yaml
        /// null = the on default).</param>
        /// <param name="clashApiAddress">The Clash/Mihomo controller address
        /// (host:port); null/empty = the 127.0.0.1:9090 default.</param>
        /// <param name="clashApiSecret">The controller's API secret; null/empty = none.</param>
        /// <param name="clashApiChanged">Reported with (enabled, address or "" = default,
        /// secret) — the switch reports immediately, text edits debounced 500ms.</param>
        /// <param name="updateCheckOn">Current 检查更新 state (yaml null = the on default).</param>
        /// <param name="updateCheckChanged">Reported with the requested on/off — takes
        /// effect on the next startup (the check runs once per launch).</param>
        /// <param name="updateSourceIndex">0=CNB (yaml null, the default) 1=GitHub.</param>
        /// <param name="updateSourceChanged">Reported with the new combo index.</param>
        // internal: the signature mentions internal sampler types — App (same assembly) is
        // the only caller; the window is never instantiated from XAML.
        internal SettingsWindow(bool overlayOnLeft, bool snapToStart, Action<bool, bool> placementChanged,
            bool autoStartOn, Action<bool> autoStartChanged,
            int themeIndex, Action<int> themeChanged,
            int intervalMs, Action<int> intervalChanged,
            int samplingMask, Action<int, bool> samplingChanged,
            bool mergeSamePath, Action<bool> mergeSamePathChanged,
            int diskDisplayIndex, int pickedDiskIndex, IReadOnlyList<DiskInfo> disks,
            Action<int, int> diskDisplayChanged,
            int gpuDisplayIndex, int pickedGpuIndex, IReadOnlyList<GpuInfo> gpus,
            Action<int, int> gpuDisplayChanged,
            string netAdapterId, string netAdapterName,
            IReadOnlyList<(string Id, string Label)> adapters,
            Action<string, string> netAdapterChanged,
            bool publicIpLookupOn, Action<bool> publicIpLookupChanged,
            bool clashEnabled, string clashApiAddress, string clashApiSecret,
            Action<bool, string, string> clashApiChanged,
            bool updateCheckOn, Action<bool> updateCheckChanged,
            int updateSourceIndex, Action<int> updateSourceChanged)
        {
            InitializeComponent();
            _placementChanged = placementChanged;
            _autoStartChanged = autoStartChanged;
            _themeChanged = themeChanged;
            _intervalChanged = intervalChanged;
            _samplingChanged = samplingChanged;
            _mergeSamePathChanged = mergeSamePathChanged;
            _diskDisplayChanged = diskDisplayChanged;
            _gpuDisplayChanged = gpuDisplayChanged;
            _netAdapterChanged = netAdapterChanged;
            _publicIpLookupChanged = publicIpLookupChanged;
            _clashApiChanged = clashApiChanged;
            _updateCheckChanged = updateCheckChanged;
            _updateSourceChanged = updateSourceChanged;

            OnLeftSwitch.IsOn = overlayOnLeft;
            AnchorCombo.SelectedIndex = snapToStart ? 1 : 0;
            AnchorCombo.IsEnabled = overlayOnLeft;   // the anchor only matters while 靠左 is on
            // Classical (Win10) taskbar: 靠左 has a single spot (the task-buttons band's
            // end next to Start) — the anchor combo is Win11-only (far-left corner vs
            // snapped to Start), and the Win11 fallback note in the description doesn't
            // apply either.
            if (!TaskbarWindow.IsWindows11Taskbar())
            {
                AnchorCard.Visibility = Visibility.Collapsed;
                OnLeftExpander.Description = "显示在任务栏左侧（任务按钮区靠开始按钮的一端）";
            }
            AutoStartSwitch.IsOn = autoStartOn;
            ThemeCombo.SelectedIndex = themeIndex < 0 || themeIndex > 2 ? 0 : themeIndex;
            IntervalCombo.SelectedIndex = IntervalToIndex(intervalMs);
            CpuSwitch.IsOn = (samplingMask & SystemSampler.MaskCpu) != 0;
            RamSwitch.IsOn = (samplingMask & SystemSampler.MaskRam) != 0;
            DiskSwitch.IsOn = (samplingMask & SystemSampler.MaskDisk) != 0;
            GpuSwitch.IsOn = (samplingMask & SystemSampler.MaskGpu) != 0;
            NetSwitch.IsOn = (samplingMask & SystemSampler.MaskNet) != 0;
            MergeSwitch.IsOn = mergeSamePath;
            DiskDisplayCombo.SelectedIndex = diskDisplayIndex < 0 || diskDisplayIndex > 2 ? 0 : diskDisplayIndex;
            PopulateDiskPicker(disks, pickedDiskIndex);
            DiskPickCombo.IsEnabled = DiskDisplayCombo.SelectedIndex == 2;
            GpuDisplayCombo.SelectedIndex = gpuDisplayIndex < 0 || gpuDisplayIndex > 2 ? 1 : gpuDisplayIndex;
            PopulateGpuPicker(gpus, pickedGpuIndex);
            GpuPickCombo.IsEnabled = GpuDisplayCombo.SelectedIndex == 2;
            PopulateNetAdapterPicker(adapters, netAdapterId, netAdapterName);
            PublicIpSwitch.IsOn = publicIpLookupOn;
            ClashSwitch.IsOn = clashEnabled;
            ClashAddressBox.Text = clashApiAddress ?? "";
            ClashSecretBox.Text = clashApiSecret ?? "";
            SetClashInputsEnabled(clashEnabled);
            UpdateCheckSwitch.IsOn = updateCheckOn;
            UpdateSourceCombo.SelectedIndex = updateSourceIndex == 1 ? 1 : 0;
            UpdateSourceCombo.IsEnabled = updateCheckOn;   // the source only matters while 检查更新 is on
            VersionText.Text = $"版本 {VersionInfo.Current}";   // AssemblyInformationalVersion — 与发版 tag 一致
            _loaded = true;
        }

        // The 特定磁盘 picker: one item per present physical disk ("磁盘 0 (C: D:) · model"),
        // Tag = the PhysicalDrive index. A stored pick whose disk is currently absent (or
        // a list that came back empty — disk sampling off) still gets a （未连接） item so
        // the user's choice stays visible instead of silently snapping to another disk.
        private void PopulateDiskPicker(IReadOnlyList<DiskInfo> disks, int selectedIndex)
        {
            int sel = -1;
            if (disks != null)
            {
                foreach (var d in disks)
                {
                    var item = new ComboBoxItem { Content = $"{d.TabTitle} · {d.Name}", Tag = d.Index };
                    DiskPickCombo.Items.Add(item);
                    if (d.Index == selectedIndex) sel = DiskPickCombo.Items.Count - 1;
                }
            }
            if (sel < 0)
            {
                DiskPickCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"磁盘 {selectedIndex}（未连接）",
                    Tag = selectedIndex,
                });
                sel = DiskPickCombo.Items.Count - 1;
            }
            DiskPickCombo.SelectedIndex = sel;
        }

        private int PickedDiskIndex()
            => (DiskPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : 0;

        // The 特定 GPU picker — same shape as the disk one (a missing stored pick gets a
        // （未连接） placeholder), items are "GPU 0 · NVIDIA …", Tag = the "GPU N" number.
        private void PopulateGpuPicker(IReadOnlyList<GpuInfo> gpus, int selectedIndex)
        {
            int sel = -1;
            if (gpus != null)
            {
                foreach (var g in gpus)
                {
                    var item = new ComboBoxItem { Content = $"{g.TabTitle} · {g.Name}", Tag = g.Index };
                    GpuPickCombo.Items.Add(item);
                    if (g.Index == selectedIndex) sel = GpuPickCombo.Items.Count - 1;
                }
            }
            if (sel < 0)
            {
                GpuPickCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"GPU {selectedIndex}（未连接）",
                    Tag = selectedIndex,
                });
                sel = GpuPickCombo.Items.Count - 1;
            }
            GpuPickCombo.SelectedIndex = sel;
        }

        private int PickedGpuIndex()
            => (GpuPickCombo.SelectedItem as ComboBoxItem)?.Tag is int i ? i : 0;

        // The 网络 适配器 combo: "自动（默认）" first (Tag = ""), then one item per
        // enumerated non-loopback adapter (Tag = its Id). A stored pick whose adapter is
        // absent keeps a （未连接） placeholder — same contract as the disk/GPU pickers.
        private void PopulateNetAdapterPicker(IReadOnlyList<(string Id, string Label)> adapters,
            string selectedId, string selectedName)
        {
            NetAdapterCombo.Items.Add(new ComboBoxItem { Content = "自动（默认）", Tag = "" });
            int sel = 0;
            if (adapters != null)
            {
                foreach (var (id, label) in adapters)
                {
                    NetAdapterCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                    if (!string.IsNullOrEmpty(selectedId) && id == selectedId)
                        sel = NetAdapterCombo.Items.Count - 1;
                }
            }
            if (sel == 0 && !string.IsNullOrEmpty(selectedId))
            {
                NetAdapterCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{(string.IsNullOrEmpty(selectedName) ? "未知适配器" : selectedName)}（未连接）",
                    Tag = selectedId,
                });
                sel = NetAdapterCombo.Items.Count - 1;
            }
            NetAdapterCombo.SelectedIndex = sel;
        }

        private static int IntervalToIndex(int ms) => ms <= 500 ? 0 : ms >= 2000 ? 2 : 1;
        private static int IndexToInterval(int index) => index == 0 ? 500 : index == 2 ? 2000 : 1000;

        /// <summary>App pushes the real task state back after a failed schtasks call,
        /// without re-firing the change callback.</summary>
        public void SyncAutoStart(bool on)
        {
            _loaded = false;
            AutoStartSwitch.IsOn = on;
            _loaded = true;
        }

        private void OnLeftSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            AnchorCombo.IsEnabled = OnLeftSwitch.IsOn;
            _placementChanged?.Invoke(OnLeftSwitch.IsOn, AnchorCombo.SelectedIndex == 1);
        }

        private void AnchorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _placementChanged?.Invoke(OnLeftSwitch.IsOn, AnchorCombo.SelectedIndex == 1);
        }

        private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _autoStartChanged?.Invoke(AutoStartSwitch.IsOn);
        }

        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _themeChanged?.Invoke(ThemeCombo.SelectedIndex);
        }

        private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _intervalChanged?.Invoke(IndexToInterval(IntervalCombo.SelectedIndex));
        }

        // 检查更新 toggle: reports immediately (persisted for the NEXT startup's check)
        // and gates the 更新源 picker (same pattern as the Clash switch).
        private void UpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            UpdateSourceCombo.IsEnabled = UpdateCheckSwitch.IsOn;
            _updateCheckChanged?.Invoke(UpdateCheckSwitch.IsOn);
        }

        private void UpdateSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            _updateSourceChanged?.Invoke(UpdateSourceCombo.SelectedIndex);
        }

        // One handler for all five 采样 toggles — the switch's Tag carries the overlay
        // hit slot (0=CPU 1=内存 2=磁盘 3=GPU 4=网络).
        private void MetricSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            if (sender is ToggleSwitch sw && int.TryParse(sw.Tag as string, out int slot))
                _samplingChanged?.Invoke(slot, sw.IsOn);
        }

        private void MergeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _mergeSamePathChanged?.Invoke(MergeSwitch.IsOn);
        }

        // 磁盘 → 显示方式 combo: 0=所有磁盘平均 1=最高利用率 2=特定磁盘 (the picker below
        // is enabled only in 特定磁盘 mode — the AnchorCombo pattern).
        private void DiskDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            DiskPickCombo.IsEnabled = DiskDisplayCombo.SelectedIndex == 2;
            _diskDisplayChanged?.Invoke(DiskDisplayCombo.SelectedIndex, PickedDiskIndex());
        }

        private void DiskPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            // Re-picking only matters while 特定磁盘 is the active mode.
            if (DiskDisplayCombo.SelectedIndex != 2) return;
            _diskDisplayChanged?.Invoke(2, PickedDiskIndex());
        }

        // GPU → 显示方式 combo: 0=所有 GPU 平均 1=最高利用率 2=特定 GPU (the GPU default is
        // 最高, unlike the disk's 平均; the picker below is enabled only in 特定 GPU mode).
        private void GpuDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            GpuPickCombo.IsEnabled = GpuDisplayCombo.SelectedIndex == 2;
            _gpuDisplayChanged?.Invoke(GpuDisplayCombo.SelectedIndex, PickedGpuIndex());
        }

        private void GpuPickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            // Re-picking only matters while 特定 GPU is the active mode.
            if (GpuDisplayCombo.SelectedIndex != 2) return;
            _gpuDisplayChanged?.Invoke(2, PickedGpuIndex());
        }

        // 网络 → 适配器 combo: the ""-tagged first item is 自动; every other item's Tag is
        // the adapter's NetworkInterface.Id. The display name rides along so the picker can
        // show a （未连接） placeholder for a missing pick later.
        private void NetAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            var item = NetAdapterCombo.SelectedItem as ComboBoxItem;
            string id = item?.Tag as string ?? "";
            _netAdapterChanged?.Invoke(id, id.Length == 0 ? null : item.Content as string);
        }

        // The 公网 IP switch: a discrete action, so it reports immediately (same as the
        // Clash switch) — NetInfoSampler's poll thread stops BOTH the HTTP lookups and
        // the 公网延迟 ICMP probe, and drops the cached address on off.
        private void PublicIpSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _publicIpLookupChanged?.Invoke(PublicIpSwitch.IsOn);
        }

        // The Clash/Mihomo switch: a discrete action, so it reports immediately (the text
        // boxes debounce instead), and the endpoint inputs follow its state.
        private void ClashSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            SetClashInputsEnabled(ClashSwitch.IsOn);
            _clashApiChanged?.Invoke(ClashSwitch.IsOn, ClashAddressBox.Text.Trim(), ClashSecretBox.Text.Trim());
        }

        private void SetClashInputsEnabled(bool on)
        {
            ClashAddressBox.IsEnabled = on;
            ClashSecretBox.IsEnabled = on;
            ClashTestButton.IsEnabled = on;
            if (!on) ClashTestResult.Text = "";
        }

        // Both Clash/Mihomo boxes funnel here per keystroke; the actual report is
        // debounced 500ms so a yaml write + sampler retarget doesn't fire per character.
        private void ClashApiBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded) return;
            if (_clashDebounce == null)
            {
                _clashDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _clashDebounce.Tick += (s2, e2) =>
                {
                    _clashDebounce.Stop();
                    _clashApiChanged?.Invoke(ClashSwitch.IsOn, ClashAddressBox.Text.Trim(), ClashSecretBox.Text.Trim());
                };
            }
            _clashDebounce.Stop();
            _clashDebounce.Start();
        }

        // 测试连接: probes the CURRENT box contents (empty address = the 127.0.0.1:9090
        // default — the same value the poller would use) via GET /version, off the UI
        // thread. The result line is colored per outcome (iNKORE scheme brushes).
        private async void ClashTestButton_Click(object sender, RoutedEventArgs e)
        {
            string address = ClashAddressBox.Text.Trim();
            string secret = ClashSecretBox.Text.Trim();
            ClashTestButton.IsEnabled = false;
            ClashTestResult.Text = "测试中…";
            ClashTestResult.Foreground = (Brush)FindResource("TextFillColorSecondaryBrush");
            var (ok, detail) = await Task.Run(() => ClashSampler.TestConnection(address, secret));
            ClashTestResult.Text = detail;
            ClashTestResult.Foreground = (Brush)FindResource(ok
                ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            ClashTestButton.IsEnabled = true;
        }

        // The two 关于 external-link cards.
        private const string GitHubUrl = "https://github.com/linesoft2/TaskMonitor";
        private const string AcknowledgementsUrl = "https://github.com/linesoft2/TaskMonitor#参考--致谢--开源许可";

        private void GitHubCard_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

        private void AcknowledgementsCard_Click(object sender, RoutedEventArgs e) => OpenUrl(AcknowledgementsUrl);

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { System.Diagnostics.Process.Start(url); }
            catch { /* no browser handler / malformed url — ignore */ }
        }
    }
}
