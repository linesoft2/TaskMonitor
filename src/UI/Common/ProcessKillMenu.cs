using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Common.IconKeys;
using iNKORE.UI.WPF.Modern.Controls;

namespace task_monitor
{
    /// <summary>
    /// Wires the 结束进程 action onto an <see cref="ItemsControl"/> process list: right-click
    /// a row → a Fluent context menu (iNKORE styles by key, same as the taskbar menu) → a
    /// small confirm card (view-owned <see cref="Popup"/>) → every member PID terminated via
    /// OpenProcess + TerminateProcess (Task Manager's 结束任务 semantics — no close-message
    /// grace, the only thing that works on a hung process). One <c>Attach</c> per detail
    /// view; CPU/RAM/磁盘/GPU/网络 all share this implementation.
    /// </summary>
    /// <remarks>
    /// <b>Why the menu lives at list level, not as a row <see cref="ContextMenu"/>:</b> the
    /// lists rebind every tick (<c>ItemsSource =</c> a fresh list), which regenerates the row
    /// containers — the same constraint that drives ProcessListTip (gotchas §14). A menu in
    /// the row template would be torn down under an open popup, and its placement target is
    /// whatever container survived the last rebind. Driving it from the list's own
    /// <see cref="InputElement.MouseRightButtonUp"/> (re-hit-testing via
    /// <see cref="ItemsControl.ContainerFromElement"/>) keeps the opened menu + confirm card
    /// decoupled from the row containers' lifetime, and the captured
    /// <see cref="ProcessInfo"/> stays valid regardless of what the next tick's rebind does
    /// to the list.
    /// <para>Rows without a killable target never open the menu. That is rare now:
    /// Clash/Mihomo controller rows carry no PID (the core's API reports only a path), so a
    /// right-click RESOLVES the path back to live PIDs (name candidates → full image-path
    /// verification) and kills those — the menu only stays away when nothing resolves (the
    /// connection's process is already gone). Merged rows (合并相同程序) kill ALL member PIDs
    /// (<see cref="ProcessInfo.Pids"/>), not just the first member's.</para>
    /// </remarks>
    internal static class ProcessKillMenu
    {
        public static void Attach(ItemsControl list) => new Session(list);

        private sealed class Session
        {
            private readonly ItemsControl _list;
            private ContextMenu _menu;          // lazily built on the first right-click
            private MenuItem _killItem;
            private FontIcon _fallbackGlyph;    // the menu item's static RecordStop icon
            private Image _cardIcon;            // the confirm card's 20×20 process icon
            private System.Windows.Shapes.Rectangle _cardIconGap; // hidden together with the icon
            private TextBlock _killHeaderText;  // the menu item's header (trimmable, capped width)
            private Popup _confirm;             // lazily built with the menu
            private TextBlock _confirmTitle;
            private TextBlock _confirmDetail;
            private Button _killButton;
            private Button _cancelButton;
            private ProcessInfo _row;           // the right-clicked row, kept across ticks
            private List<int> _targets;         // its resolved kill targets (see TargetsOf)
            private Point _anchor;              // right-click point (list space) — the confirm card's position

            public Session(ItemsControl list)
            {
                _list = list;
                list.MouseRightButtonUp += List_MouseRightButtonUp;
                // The flyout is destroyed on focus loss while our popups are open — never
                // leave an orphan menu/card floating over a dead window.
                list.Unloaded += (s, e) => CloseAll();
            }

            private void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
            {
                var src = e.OriginalSource as DependencyObject;
                var container = src != null ? _list.ContainerFromElement(src) : null;
                if (!(container is ContentPresenter cp) || !(cp.DataContext is ProcessInfo p))
                    return;

                _targets = TargetsOf(p);
                if (_targets == null) return;   // nothing local to end (e.g. a Clash row whose process is already gone)

                EnsureMenu();
                _row = p;
                _anchor = e.GetPosition(_list);
                // Contextualize the item with the row's own name (the ×N spelling matches
                // the row's merge chip): capped + ellipsized so a long service display name
                // can't blow the menu out to absurd widths.
                _killHeaderText.Text = _targets.Count > 1 ? $"结束进程 {p.Name} ×{_targets.Count}" : $"结束进程 {p.Name}";

                _menu.PlacementTarget = _list;
                _menu.Placement = PlacementMode.MousePoint;
                _menu.IsOpen = true;
                e.Handled = true;
            }

            // Kill targets, resolved ONCE per right-click and kept for the confirm card and
            // the kill itself: a merged row's full member list, a Clash row's path→PID
            // lookup, else the single Pid. Null when there is nothing local to terminate.
            private static List<int> TargetsOf(ProcessInfo p)
            {
                if (p.ViaClash) return ResolvePidsByPath(p.ExePath);
                if (p.Pids != null && p.Pids.Count > 0)
                {
                    var live = p.Pids.FindAll(pid => pid > 0);
                    return live.Count > 0 ? live : null;
                }
                return p.Pid > 0 ? new List<int> { p.Pid } : null;
            }

            // A Clash/Mihomo row carries no PID (the core's API reports only a path) —
            // resolve the path back to live PIDs: name candidates via GetProcessesByName,
            // then verify each candidate's FULL image path (Win32 form, the same source the
            // samplers' ExePath uses) so a same-named exe elsewhere can't be hit by mistake.
            // A one-off, shell-free walk on the UI thread, only for a right-clicked Clash
            // row; null when the path matches nothing running.
            private static List<int> ResolvePidsByPath(string exePath)
            {
                if (string.IsNullOrEmpty(exePath)) return null;
                try
                {
                    var result = new List<int>();
                    foreach (var proc in Process.GetProcessesByName(
                        Path.GetFileNameWithoutExtension(exePath)))
                    {
                        try
                        {
                            int pid = proc.Id;
                            if (string.Equals(SystemInfo.QueryProcessImageFileName(pid), exePath,
                                StringComparison.OrdinalIgnoreCase))
                                result.Add(pid);
                        }
                        finally { proc.Dispose(); }
                    }
                    return result.Count > 0 ? result : null;
                }
                catch { return null; }
            }

            private void EnsureMenu()
            {
                if (_menu != null) return;

                // Same style wiring as the taskbar menu (App.EnsureTaskbarMenu): iNKORE's
                // Fluent 2 looks for the standard ContextMenu/MenuItem, applied BY KEY
                // because the FluentWpfCore dictionary merged after XamlControlsResources
                // would otherwise win the keyless defaults.
                //
                // The header names the target row (the ×N spelling matches the row's merge
                // chip); the icon stays the static RecordStop glyph — the row's own icon
                // lives on the confirm card, not here (user's call). No explicit Foreground
                // on the header TextBlock: it must inherit the item's hover/selected
                // highlight from the MenuItem template.
                _fallbackGlyph = new FontIcon { Icon = FluentSystemIcons.RecordStop_16_Regular, FontSize = 16 };
                _killHeaderText = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 260,
                };
                _killItem = new MenuItem
                {
                    Icon = _fallbackGlyph,
                    Header = _killHeaderText,
                };
                _killItem.SetResourceReference(FrameworkElement.StyleProperty, "DefaultMenuItemStyle");
                _killItem.Click += (s, e) => ShowConfirm();

                _menu = new ContextMenu();
                _menu.SetResourceReference(FrameworkElement.StyleProperty, "DefaultContextMenuStyle");
                _menu.Items.Add(_killItem);

                // The confirm card: a view-owned popup in the tooltip idiom (AllowsTransparency
                // for the rounded corners), themed entirely via DynamicResource so it follows
                // the app theme like the window's other surfaces.
                _killButton = new Button { Content = "结束", MinWidth = 88 };
                _killButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
                _killButton.Click += (s, e) => Kill();

                _cancelButton = new Button { Content = "取消", MinWidth = 88 };
                _cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "DefaultButtonStyle");
                _cancelButton.Click += (s, e) => _confirm.IsOpen = false;

                _cardIcon = new Image
                {
                    Width = 20, Height = 20,
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true,
                };
                RenderOptions.SetBitmapScalingMode(_cardIcon, BitmapScalingMode.HighQuality);
                _confirmTitle = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _confirmTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                // Title row: [row icon] 结束 xxx？ — the icon makes the target unmistakable
                // even when the name is a truncated/renamed service display name.
                var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                titleRow.Children.Add(_cardIcon);
                _cardIconGap = new System.Windows.Shapes.Rectangle { Width = 8 };
                titleRow.Children.Add(_cardIconGap);
                titleRow.Children.Add(_confirmTitle);
                _confirmDetail = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                _confirmDetail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Margin = new Thickness(0, 14, 0, 0);
                buttons.HorizontalAlignment = HorizontalAlignment.Right;
                var gap = new System.Windows.Shapes.Rectangle { Width = 8 };
                buttons.Children.Add(_killButton);
                buttons.Children.Add(gap);
                buttons.Children.Add(_cancelButton);

                var card = new Border
                {
                    Padding = new Thickness(16, 12, 16, 14),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    MaxWidth = 340,
                };
                card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                var panel = new StackPanel();
                panel.Children.Add(titleRow);
                panel.Children.Add(_confirmDetail);
                panel.Children.Add(buttons);
                card.Child = panel;

                _confirm = new Popup
                {
                    PlacementTarget = _list,
                    Placement = PlacementMode.Relative,
                    AllowsTransparency = true,
                    StaysOpen = false,
                    Child = card,
                };
                _confirm.Closed += (s, e) => ResetConfirm();
            }

            private void ShowConfirm()
            {
                if (_row == null) return;
                var p = _row;

                // The card's icon mirrors the menu item's (same row, same icon object — the
                // lists resolve Icon before binding so it's effectively always present).
                _cardIcon.Source = p.Icon;
                bool hasIcon = p.Icon != null;
                _cardIcon.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
                _cardIconGap.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;

                var targets = _targets ?? new List<int>();
                _confirmTitle.Text = targets.Count > 1
                    ? $"结束 {p.Name} 的全部 {targets.Count} 个进程？"
                    : $"结束 {p.Name}？";
                var pidText = $"PID {string.Join(", ", targets)}";
                _confirmDetail.Text = p.ServiceHost != null
                    ? $"{pidText} · svchost.exe 服务宿主——它承载的系统服务也会一并停止，相关功能可能中断。"
                    : $"{pidText} · 结束后该进程未保存的数据将丢失。";
                _killButton.Visibility = Visibility.Visible;
                _cancelButton.Content = "取消";

                // Same offset scheme as the row tooltip: below-right of the right-click point.
                _confirm.HorizontalOffset = _anchor.X + 14;
                _confirm.VerticalOffset = _anchor.Y + 14;
                _confirm.IsOpen = true;
            }

            private void Kill()
            {
                // Use the SAME resolution the confirm card showed (a re-resolve here could
                // diverge from what the user just approved — a vanished member shows up in
                // the per-PID error counts instead).
                if (_row == null || _targets == null || _targets.Count == 0)
                {
                    _confirm.IsOpen = false;
                    return;
                }
                var targets = _targets;
                int ok = 0, fail = 0, firstErr = 0;

                foreach (int pid in targets)
                {
                    IntPtr h = SystemInfo.OpenProcess(SystemInfo.PROCESS_TERMINATE, false, (uint)pid);
                    if (h == IntPtr.Zero)
                    {
                        fail++;
                        if (firstErr == 0) firstErr = Marshal.GetLastWin32Error();
                        continue;
                    }
                    try
                    {
                        if (SystemInfo.TerminateProcess(h, 1)) ok++;
                        else
                        {
                            fail++;
                            if (firstErr == 0) firstErr = Marshal.GetLastWin32Error();
                        }
                    }
                    finally { SystemInfo.CloseHandle(h); }
                }

                string id = $"{_row.Name} (PID {string.Join(",", targets)})";
                if (fail == 0)
                {
                    Logger.Info($"结束进程 {id}：成功（{ok} 个）");
                    _confirm.IsOpen = false;   // rows vanish on their own within a tick
                    return;
                }

                // Partial or total failure: swap the card into its error form (结束 hidden,
                // 取消 relabelled 关闭) so the reason is seen where the click happened.
                Logger.Warn($"结束进程 {id} 失败：成功 {ok}/{targets.Count}，err={firstErr}");
                _confirmTitle.Text = ok > 0 ? $"已结束 {ok}/{targets.Count} 个进程" : "结束进程失败";
                _confirmDetail.Text = ErrorText(firstErr);
                _killButton.Visibility = Visibility.Collapsed;
                _cancelButton.Content = "关闭";
            }

            private static string ErrorText(int err)
            {
                switch (err)
                {
                    case 5: return "拒绝访问——该进程可能受系统保护（PPL）。";
                    case 87: return "进程已不存在（可能在确认前已退出）。";
                    default: return $"系统错误 {err}。";
                }
            }

            private void ResetConfirm()
            {
                // The card is reused across rows — undo the error form's relabelling.
                _killButton.Visibility = Visibility.Visible;
                _cancelButton.Content = "取消";
            }

            private void CloseAll()
            {
                if (_menu != null) _menu.IsOpen = false;
                if (_confirm != null) _confirm.IsOpen = false;
            }
        }
    }
}
