using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Global crash funnel: every unhandled managed exception — Dispatcher (UI thread),
    /// AppDomain (any thread, process-dying), unobserved Task, plus the overlay WndProc
    /// guard — goes through <see cref="Report"/>, which (1) writes the full stack to the
    /// file log (<see cref="Logger"/>, every report, duplicates included) and (2) pops a
    /// <see cref="CrashDialog"/> showing it. The dialog is built in code (no XAML/BAML) so
    /// a crash caused by a XAML/resource failure can't take the reporter down with it.
    /// At most one dialog exists at a time; further reports while it is open are counted,
    /// logged, and dropped.
    /// </summary>
    internal static class CrashReporter
    {
        private static int _open;      // 1 = a dialog is up (doubles as the re-entrancy guard)
        private static int _reported;  // total reports this run — the dialog's "第 N 个" line

        // AppDomain-level hooks, installed from App's static ctor — that runs before
        // Main(), so even an App.xaml BAML failure inside InitializeComponent lands here.
        // The Dispatcher hook needs Application.Current, so App adds it in OnStartup.
        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                // IsTerminating: the process dies when this handler returns, so Report
                // must BLOCK (a modal dialog on the UI thread) until the user dismisses
                // it — otherwise nobody ever sees why the process vanished.
                var ex = e.ExceptionObject as Exception
                         ?? new Exception(e.ExceptionObject?.ToString());
                Report("未处理异常（进程即将退出）", ex, fatal: true, block: true);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved();   // non-fatal by default on net48; keep it that way
                Report("后台任务异常", e.Exception, fatal: false, block: false);
            };
        }

        /// <summary>
        /// Show the crash dialog for <paramref name="ex"/>. Returns false only when the
        /// user chose 退出 on a non-fatal report; fatal reports and dropped duplicates
        /// always return true. Never throws — it runs inside exception handlers.
        /// </summary>
        public static bool Report(string source, Exception ex, bool fatal, bool block)
        {
            int n = Interlocked.Increment(ref _reported);
            // The file log gets EVERY report first — including duplicates the dialog below
            // would drop — so the stack survives even when nobody reads the dialog.
            Logger.Error($"崩溃报告 #{n}（{source}，fatal={fatal}）", ex);
            if (Interlocked.CompareExchange(ref _open, 1, 0) != 0) return true;  // one at a time
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return Fallback(source, ex);

                if (block && dispatcher.CheckAccess()) return ShowCore(source, ex, fatal, n);
                if (block)
                {
                    // A dying thread must wait for the dismissal; the timeout keeps a
                    // wedged dispatcher from hanging the exit instead.
                    object r = dispatcher.Invoke(
                        new Func<bool>(() => ShowCore(source, ex, fatal, n)),
                        TimeSpan.FromSeconds(30),
                        System.Windows.Threading.DispatcherPriority.Normal);
                    return r is bool b ? b : Fallback(source, ex);
                }

                // Fire-and-forget: callers like the overlay's WndProc guard sit inside a
                // message loop that must never block waiting on the user (a SendMessage
                // from explorer would hang the taskbar).
                dispatcher.BeginInvoke(new Action(() => ShowCore(source, ex, fatal, n)));
                return true;
            }
            catch
            {
                Volatile.Write(ref _open, 0);
                return true;
            }
        }

        // Runs on the UI thread. Returns the user's choice for non-fatal reports.
        private static bool ShowCore(string source, Exception ex, bool fatal, int n)
        {
            try
            {
                var dlg = new CrashDialog(source, ex, fatal, n);
                dlg.ShowDialog();
                return dlg.ContinueRun;
            }
            catch
            {
                return Fallback(source, ex);
            }
            finally
            {
                Volatile.Write(ref _open, 0);
            }
        }

        // Last resort when no dialog can be built/shown (no dispatcher, or the dialog
        // itself faulted): a plain Win32 MessageBox always works.
        private static bool Fallback(string source, Exception ex)
        {
            try
            {
                string text = $"{source}\r\n\r\n{ex}";
                if (text.Length > 4000) text = text.Substring(0, 4000) + "\r\n…";
                MessageBox.Show(text, "TaskMonitor 崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            finally { Volatile.Write(ref _open, 0); }
            return true;
        }
    }

    /// <summary>
    /// Code-only crash window — no XAML by design (see <see cref="CrashReporter"/>).
    /// Buttons still pick up the app-wide iNKORE Fluent styles from App.xaml.
    /// </summary>
    internal sealed class CrashDialog : Window
    {
        // The user's choice; meaningful only for non-fatal reports. Closing via ✕
        // counts as 继续 for non-fatal (dismissal) and as 退出 for fatal (the process
        // is dying either way once the handler returns).
        public bool ContinueRun { get; private set; }

        public CrashDialog(string source, Exception ex, bool fatal, int n)
        {
            ContinueRun = !fatal;

            Title = "TaskMonitor 崩溃";
            Width = 700; Height = 480;
            MinWidth = 520; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;               // must be findable even over fullscreen apps
            Background = Brushes.White;   // a crash dialog should not be fashionable
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

            var headline = new TextBlock
            {
                Text = fatal ? "程序遇到无法恢复的异常" : "程序遇到未处理的异常",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
            };
            var sub = new TextBlock
            {
                Text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {source}"
                     + (n > 1 ? $" · 本次运行第 {n} 个异常" : "")
                     + (fatal ? "\n关闭此窗口后进程将退出。" : ""),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 12),
            };
            var details = new TextBox
            {
                Text = ex.ToString(),
                IsReadOnly = true,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
            };

            var copy = new Button { Content = "复制详情", Width = 88, Height = 30, Margin = new Thickness(0, 12, 0, 0) };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {source}\r\n\r\n{ex}"); }
                catch { /* clipboard locked — non-essential */ }
            };
            var cont = new Button { Content = "继续运行", Width = 88, Height = 30, Margin = new Thickness(8, 12, 0, 0) };
            cont.Click += (s, e) => { ContinueRun = true; Close(); };
            var exit = new Button { Content = "退出程序", Width = 88, Height = 30, Margin = new Thickness(8, 12, 0, 0) };
            exit.Click += (s, e) => { ContinueRun = false; Close(); };
            if (fatal) { cont.Visibility = Visibility.Collapsed; exit.IsDefault = true; }
            else cont.IsDefault = true;

            // RightToLeft: first child is rightmost → primary action ends up at the
            // far right, 复制详情 at the far left.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.RightToLeft };
            buttons.Children.Add(cont);
            buttons.Children.Add(exit);
            buttons.Children.Add(copy);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(headline, 0);
            Grid.SetRow(sub, 1);
            Grid.SetRow(details, 2);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(headline);
            grid.Children.Add(sub);
            grid.Children.Add(details);
            grid.Children.Add(buttons);
            Content = grid;
        }
    }
}
