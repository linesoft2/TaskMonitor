using System.Windows;

namespace task_monitor
{
    /// <summary>
    /// One-time legacy-OS compatibility warning, shown on the FIRST launch on Windows 10
    /// or older (the widget is built and maintained for Windows 11 only; the classical
    /// taskbar path is best-effort and no longer maintained). Purely informational — the
    /// single 我知道了 button, ✕ and Esc all just close it; the caller persists the
    /// "shown" flag to settings.yaml so later launches stay quiet.
    /// </summary>
    public partial class LegacyOsWarningDialog : Window
    {
        public LegacyOsWarningDialog()
        {
            InitializeComponent();
        }
    }
}
