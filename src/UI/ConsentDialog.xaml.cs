using System.Windows;

namespace task_monitor
{
    /// <summary>
    /// First-run elevation consent prompt, shown UNelevated before any UAC request.
    /// Only "允许" sets <see cref="Window.DialogResult"/> to true; everything else
    /// (不允许 / ✕ / Esc — the latter two handled by <see cref="Button.IsCancel"/> and
    /// the window chrome) leaves it false/null, which the caller treats as refusal:
    /// exit without persisting anything, so the next launch asks again.
    /// </summary>
    public partial class ConsentDialog : Window
    {
        public ConsentDialog()
        {
            InitializeComponent();
        }

        private void Allow_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
