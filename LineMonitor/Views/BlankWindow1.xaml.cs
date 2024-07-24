using Microsoft.UI.Xaml;
using WinUIEx;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Foundation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace LineMonitor.Views
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class BlankWindow1 : WinUIEx.WindowEx
    {

        nint this_hwnd;
        int taskbar_height;
        public BlankWindow1()
        {
            
            this.InitializeComponent();
            RootPanel.DataContext = new ViewModels.MyViewModel(this.DispatcherQueue);
            this.IsTitleBarVisible = false;
            this.IsResizable = false;
            SetToTaskBar();

        }
        void SetToTaskBar()
        {
            this_hwnd = this.GetWindowHandle();
            var taskbar_hwnd = PInvoke.FindWindow("Shell_TrayWnd", null);
            PInvoke.SetParent((HWND)this_hwnd, taskbar_hwnd);
            Rect rect = new();
            RECT rcNotify;
            rect.X = 2;
            rect.Y = 2;
            rect.Width = 100;
            rect.Height = 100;
            PInvoke.MoveWindow((HWND)this_hwnd, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,true);
            //this.Height = Grid.ActualHeight;
            //PInvoke.GetWindowRect(PInvoke.FindWindow("TrayNotifyWnd", null),out rcNotify);
            //PInvoke.FindWindowEx(PInvoke.FindWindow("TrayNotifyWnd", null), nullptr, L"Start", NULL);
            
        }

        private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if(e.NewSize.Width == e.PreviousSize.Width)
            {
                return;
            }
            PInvoke.MoveWindow((HWND)this_hwnd, 2, 2, (int)(e.NewSize.Width * RootPanel.XamlRoot.RasterizationScale), (int)(e.NewSize.Height * RootPanel.XamlRoot.RasterizationScale), true);
        }
    }
}
