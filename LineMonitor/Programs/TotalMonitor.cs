using Hardware.Info;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace LineMonitor.Programs
{
    public class TotalMonitor
    {
        private PerformanceCounter cpuCounter;
        private PerformanceCounter memoryCounter;
        private List<PerformanceCounter> networkSentCounter = new();
        private List<PerformanceCounter> networkReceivedCounter = new();

        public TotalMonitor()
        {
            cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            //memoryCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
            InitializeNetworkCounters();
            Debug.WriteLine(Microsoft.UI.Xaml.Application.Current);
            
        }

        private void InitializeNetworkCounters()
        {
            PerformanceCounterCategory pcCategory = new PerformanceCounterCategory("Network Interface");
            string[] iNames = pcCategory.GetInstanceNames();
            if (iNames.Length != 0)
            {
                foreach (string instanceName in iNames)
                {
                    Debug.WriteLine("Network Interface: " + instanceName);
                    networkSentCounter.Add( new PerformanceCounter("Network Interface", "Bytes Sent/sec", instanceName));
                    networkReceivedCounter.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", instanceName));
                }
            }
            else
            {
                throw new InvalidOperationException("No suitable network interface found.");
            }
        }

        public int GetCpuUsage()
        {
            return (int)cpuCounter.NextValue();
        }

        public int GetAvailableMemory()
        {
            MEMORYSTATUSEX mEMORYSTATUSEX = new();
            mEMORYSTATUSEX.dwLength = (uint)Marshal.SizeOf(mEMORYSTATUSEX);
            PInvoke.GlobalMemoryStatusEx(ref mEMORYSTATUSEX);
            return (int)mEMORYSTATUSEX.dwMemoryLoad;
        }

        public double GetNetworkSentSpeed()
        {
            float total = 0;
                foreach (var i in networkSentCounter)
            {
                total += i?.NextValue() ?? 0;
            }
                return Math.Round(total, 2);
        }

        public double GetNetworkReceivedSpeed()
        {
            float total = 0;
            foreach (var i in networkReceivedCounter)
            {
                total += i?.NextValue() ?? 0;
            }
            return Math.Round(total, 2);
        }
    }

}
