using Microsoft.UI.Xaml;
using LineMonitor.Views;
using WinUIEx;
using LineMonitor.Programs;
using CommunityToolkit.Mvvm.ComponentModel;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.ObjectModel;
using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using System.Data;
using CommunityToolkit.Mvvm.Messaging;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace LineMonitor
{
    public class TotalData
    {
        public string title;
        public double value;
        public Func<double> func;
        public Func<double, string> toFriendlyValue;

        public void SetValue()
        {
            this.value = func();
        }
        public string GetFriendlyValue()
        {
            return toFriendlyValue(value);
        }

    }
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();


        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            totalMonitor = new TotalMonitor();
            TotalDatas = new ObservableCollection<TotalData>
            {
                new TotalData { title = "CPU", func = ()=>totalMonitor.GetCpuUsage(), toFriendlyValue =(double value)=> value.ToString() + "%"},
                new TotalData { title = "RAM", func = ()=>totalMonitor.GetAvailableMemory(),toFriendlyValue =(double value)=> value.ToString() + "%"},
                new TotalData { title = "↑", func = () => totalMonitor.GetNetworkSentSpeed(),toFriendlyValue =(double value)=> Utils.ConvertBytesPerSecond(value)},
                new TotalData { title = "↓", func = () => totalMonitor.GetNetworkReceivedSpeed(),toFriendlyValue =(double value)=> Utils.ConvertBytesPerSecond(value)}
            };
            TimerCallback(null);
            timer = new Timer(TimerCallback, null, 0, 1000);
            //m_window = new MainWindow();
            //m_window.Activate();
            window1 = new BlankWindow1();
            window1.Activate();
            cPUWindow = new CPUWindow();
            cPUWindow.Activate();



        }

        void TimerCallback(object o)
        {
            foreach (TotalData data in TotalDatas)
            {
                data.SetValue();
            }
            WeakReferenceMessenger.Default.Send("Update");

        }
        public TotalMonitor totalMonitor { get; set; }
        public ObservableCollection<TotalData> TotalDatas = null;
        private WinUIEx.WindowEx window1;
        private Window m_window;
        private Window cPUWindow;
        Timer timer;
    }
}
