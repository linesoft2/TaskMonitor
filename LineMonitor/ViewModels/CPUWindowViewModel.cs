
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System;
using LiveChartsCore.Defaults;

namespace LineMonitor.ViewModels 
{

    public partial class CPUWindowViewModel : ObservableObject
    {
        ObservableCollection<DateTimePoint> CPUValues = [];
        [ObservableProperty]
        public ISeries[] series;
        public Axis[] XAxes { get; set; } = { new Axis { IsVisible = false } };
        public Axis[] YAxes { get; set; } = { new Axis { IsVisible = false } };
        DispatcherQueue dispatcherQueue;
        App app = (App)Microsoft.UI.Xaml.Application.Current;
        LineSeries<DateTimePoint> lineSeries;

        public CPUWindowViewModel(DispatcherQueue mDispatcherQueue)
        {
            dispatcherQueue = mDispatcherQueue;
            Series = new ISeries[1];
            lineSeries = new LineSeries<DateTimePoint>
            {
                Values = CPUValues,
                Fill = new SolidColorPaint(SKColors.CornflowerBlue),
                Stroke = null,
                GeometryFill = null,
                GeometryStroke = null
            };
            Series[0] = lineSeries;
            WeakReferenceMessenger.Default.Register<string>(this, (sender, message) =>
            {
                if (message == "Update")
                {
                    Update();
                }
            });
        }
        void Update()
        {
            dispatcherQueue.TryEnqueue(() =>
            {
                foreach (var totalData in app.TotalDatas)
                {
                    if(totalData.title == "CPU")
                    {
                        if (CPUValues.Count > 30)
                        {
                            CPUValues.RemoveAt(0);
                        }
                        CPUValues.Add( new DateTimePoint(DateTime.Now,(int)totalData.value));
                        //lineSeries.Values = CPUValues;
                        
                    }
                    
                }
            });
        }
    }

}
