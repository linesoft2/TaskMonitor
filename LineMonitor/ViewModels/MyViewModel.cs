using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LineMonitor.Models;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Threading;

namespace LineMonitor.ViewModels
{
    partial class MyViewModel : ObservableObject
    {
        [ObservableProperty]
        public ObservableCollection<MyTotalData> myTotalDatas = new();
        MyModel myModel = null;
        DispatcherQueue diespatcherQueue;
        App app = (App)Microsoft.UI.Xaml.Application.Current;

        public MyViewModel(DispatcherQueue mDispatcherQueue) 
        {
            diespatcherQueue = mDispatcherQueue;
            myModel = new MyModel();
            foreach (var totalData in app.TotalDatas)
            {
                MyTotalDatas.Add(new MyTotalData(totalData.title, totalData.GetFriendlyValue()));
            }
            WeakReferenceMessenger.Default.Register<string>(this, (sender, message) =>
            {
                if(message == "Update")
                {
                    Update();
                }
            });
        }
        public void Update()
        {
            diespatcherQueue.TryEnqueue(() =>
            {
                foreach (var totalData in app.TotalDatas)
                {
                    MyTotalDatas[app.TotalDatas.IndexOf(totalData)].Value = totalData.GetFriendlyValue();
                }
            });
            
        }

        
    }
    public partial class MyTotalData : ObservableObject
    {
        [ObservableProperty]
        public string title;
        [ObservableProperty]
        public string value;
        public MyTotalData(string title, string value)
        {
            Title = title;
            Value = value;
        }
    }
}
