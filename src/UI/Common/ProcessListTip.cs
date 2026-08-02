using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace task_monitor
{
    /// <summary>
    /// Wires a hover tooltip onto an <see cref="ItemsControl"/> process list, driven by the
    /// list's own <see cref="Mouse.MouseMove"/> hit-testing and a view-owned <see cref="Popup"/>
    /// — the same pattern the chart tooltips use. Content: an ordinary row shows its file
    /// description over the image path; a renamed svchost row (ServiceHostMap) shows the
    /// service's 服务名 over its 描述 (single service), or the group name over the full
    /// member list (service group).
    /// </summary>
    /// <remarks>
    /// <b>Why not a plain <see cref="ToolTip"/> on each row:</b> the lists rebind every tick
    /// (<c>ItemsSource =</c> a fresh list), which regenerates the row containers and cancels any
    /// pending/open row tooltip — so it would appear only sometimes, depending on tick phase.
    /// Driving the popup from the list's MouseMove (re-hit-testing each move via
    /// <see cref="ItemsControl.ContainerFromElement"/>) decouples it from the row containers'
    /// lifetime: it survives the per-tick rebuild and stays open/responsive while hovered.
    /// </remarks>
    internal static class ProcessListTip
    {
        public static void Attach(ItemsControl list, Popup tip, TextBlock descBlock, TextBlock pathBlock)
        {
            list.MouseMove += (_, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                var container = src != null ? list.ContainerFromElement(src) : null;

                if (container is ContentPresenter cp && cp.DataContext is ProcessInfo p)
                {
                    if (p.ServiceHost != null)
                        FillServiceTip(p, descBlock, pathBlock);
                    else
                        FillFileTip(p, descBlock, pathBlock);

                    // Follow the cursor (same offset scheme as the chart tooltips).
                    var pos = e.GetPosition(list);
                    tip.HorizontalOffset = pos.X + 14;
                    tip.VerticalOffset = pos.Y + 14;
                    tip.IsOpen = true;
                }
                // else: over a gap / empty area — leave the current tooltip as-is so moving
                // between adjacent rows doesn't flicker. Closing is driven by MouseLeave below.
            };

            list.MouseLeave += (_, __) => tip.IsOpen = false;
        }

        // Ordinary row: file description over its image path (the long-standing content).
        private static void FillFileTip(ProcessInfo p, TextBlock descBlock, TextBlock pathBlock)
        {
            string desc = FileDescriptionResolver.Resolve(p.ExePath);
            descBlock.Text = desc;
            descBlock.Visibility = string.IsNullOrEmpty(desc) ? Visibility.Collapsed : Visibility.Visible;
            pathBlock.Text = string.IsNullOrEmpty(p.ExePath) ? "无相关信息" : p.ExePath;
        }

        // Renamed svchost row: a single service shows "显示名称 (服务名)" over its services.msc
        // 描述 (resolved lazily + cached on first hover — ServiceControlManager.GetServiceDescription;
        // falls back to the exe path when the service has none); a -k group shows the group
        // name + member count over the full member list.
        private static void FillServiceTip(ProcessInfo p, TextBlock descBlock, TextBlock pathBlock)
        {
            var host = p.ServiceHost;
            descBlock.Visibility = Visibility.Visible;
            if (!host.IsGroup)
            {
                var s = host.Services[0];
                descBlock.Text = string.IsNullOrEmpty(s.DisplayName) || s.DisplayName == s.Name
                    ? s.Name : $"{s.DisplayName} ({s.Name})";
                string desc = ServiceControlManager.GetServiceDescription(s.Name);
                pathBlock.Text = !string.IsNullOrEmpty(desc) ? desc
                    : string.IsNullOrEmpty(p.ExePath) ? "无相关信息" : p.ExePath;
            }
            else
            {
                descBlock.Text = host.GroupName != null
                    ? $"服务组: {host.GroupName} · {host.Services.Count} 个服务"
                    : $"服务组 · {host.Services.Count} 个服务";
                var sb = new StringBuilder();
                foreach (var s in host.Services)
                {
                    if (sb.Length > 0) sb.Append('、');
                    sb.Append(string.IsNullOrEmpty(s.DisplayName) ? s.Name : s.DisplayName);
                }
                pathBlock.Text = sb.ToString();
            }
        }
    }
}
