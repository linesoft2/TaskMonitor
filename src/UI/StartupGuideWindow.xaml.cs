using System.Windows;

namespace task_monitor
{
    public partial class StartupGuideWindow : Window
    {
        public bool UseFloatingMode => FloatingOption.IsChecked == true;
        public bool OverlayOnLeft => TrayOption.IsChecked != true;
        public bool OverlaySnapToStart => StartOption.IsChecked == true;

        public StartupGuideWindow(bool snapToStartSupported)
        {
            InitializeComponent();
            StartOption.IsEnabled = snapToStartSupported;
            FloatingOption.Checked += ModeOption_Checked;
            TaskbarOption.Checked += ModeOption_Checked;
        }

        private void ModeOption_Checked(object sender, RoutedEventArgs e)
        {
            PrimaryButton.Content = UseFloatingMode ? "完成" : "下一步";
            TaskbarWarning.Visibility = UseFloatingMode ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (UseFloatingMode || PlacementPage.Visibility == Visibility.Visible)
            {
                DialogResult = true;
                return;
            }

            ModePage.Visibility = Visibility.Collapsed;
            PlacementPage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            PrimaryButton.Content = "完成";
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            PlacementPage.Visibility = Visibility.Collapsed;
            ModePage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = UseFloatingMode ? "完成" : "下一步";
        }
    }
}
