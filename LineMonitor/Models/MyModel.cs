using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Collections.ObjectModel;
using LineMonitor.Programs;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LineMonitor.Models
{
    
    public class MyModel
    {
        TotalMonitor totalMonitor = ((App)Microsoft.UI.Xaml.Application.Current).totalMonitor;
        
        public MyModel()
        {
            //var etwSession = new TraceEventSession("MyKernelAndClrEventsSession");
            //etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.All);
            //etwSession.Source.Kernel.All += (evt) =>
            //{
            //    Debug.WriteLine("Kernel Event: " + evt.ToString());
            //};
            //etwSession.Source.Process();
            //foreach (Process process in processList)
            //{
            //    try
            //    {
            //        MyProcess myProcess = new MyProcess();
            //        myProcess.ProcessName = process.ProcessName;
            //        myProcess.CPU = process.TotalProcessorTime.ToString();
            //        myProcess.RAM = process.WorkingSet64.ToString();
            //        processes.Add(myProcess);
            //    }
            //    catch (Exception e)
            //    {
            //        Debug.WriteLine(e.Message);
            //    }
            //}
        }
    }
}
