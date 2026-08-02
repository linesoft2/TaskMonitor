using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Lightweight WPF-only rounded column chart for per-core CPU usage.
    /// Each core gets a full-height track with a value-proportional bar on top.
    /// </summary>
    public sealed class CpuCoresChart : FrameworkElement
    {
        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(
                nameof(Background),
                typeof(Brush),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(
                nameof(Values),
                typeof(IReadOnlyList<double>),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register(
                nameof(AccentColor),
                typeof(Color),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0x78, 0xD4), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillOpacityProperty =
            DependencyProperty.Register(
                nameof(FillOpacity),
                typeof(double),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(48.0 / 255.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackColorProperty =
            DependencyProperty.Register(
                nameof(TrackColor),
                typeof(Color),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x21, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius),
                typeof(double),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BarPaddingProperty =
            DependencyProperty.Register(
                nameof(BarPadding),
                typeof(double),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MaxBarWidthProperty =
            DependencyProperty.Register(
                nameof(MaxBarWidth),
                typeof(double),
                typeof(CpuCoresChart),
                new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double> Values
        {
            get => (IReadOnlyList<double>)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Color AccentColor
        {
            get => (Color)GetValue(AccentColorProperty);
            set => SetValue(AccentColorProperty, value);
        }

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        public Color TrackColor
        {
            get => (Color)GetValue(TrackColorProperty);
            set => SetValue(TrackColorProperty, value);
        }

        public double CornerRadius
        {
            get => (double)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public double BarPadding
        {
            get => (double)GetValue(BarPaddingProperty);
            set => SetValue(BarPaddingProperty, value);
        }

        public double MaxBarWidth
        {
            get => (double)GetValue(MaxBarWidthProperty);
            set => SetValue(MaxBarWidthProperty, value);
        }

        /// <summary>
        /// Returns the core index whose bar is actually under the given mouse position,
        /// or -1 if the cursor is outside any bar (e.g. in the gap between bars).
        /// </summary>
        public int HitTest(Point pt)
        {
            var values = Values;
            if (values == null || values.Count == 0)
                return -1;

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return -1;

            int n = values.Count;
            double slotWidth = w / n;
            double barWidth = Math.Min(slotWidth - BarPadding * 2, MaxBarWidth);

            for (int i = 0; i < n; i++)
            {
                double slotCenter = (i + 0.5) * slotWidth;
                double barLeft = slotCenter - barWidth / 2.0;
                double barRight = slotCenter + barWidth / 2.0;
                if (pt.X >= barLeft && pt.X <= barRight)
                    return i;
            }
            return -1;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            if (Background != null)
                dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

            var values = Values;
            if (values == null || values.Count == 0)
                return;

            int n = values.Count;
            double slotWidth = w / n;
            double barWidth = Math.Min(slotWidth - BarPadding * 2, MaxBarWidth);
            double rx = Math.Min(CornerRadius, barWidth / 2.0);
            double ry = Math.Min(CornerRadius, h / 2.0);

            var trackBrush = new SolidColorBrush(TrackColor);
            if (trackBrush.CanFreeze)
                trackBrush.Freeze();

            // Accent color at FillOpacity (set by the host): same hue as the line chart's
            // fill, softer than a solid bar.
            byte fillAlpha = (byte)Math.Round(Math.Max(0, Math.Min(255, FillOpacity * 255)));
            var barBrush = new SolidColorBrush(Color.FromArgb(fillAlpha, AccentColor.R, AccentColor.G, AccentColor.B));
            if (barBrush.CanFreeze)
                barBrush.Freeze();

            for (int i = 0; i < n; i++)
            {
                double slotCenter = (i + 0.5) * slotWidth;
                double barLeft = slotCenter - barWidth / 2.0;

                // Full-height track behind the bar.
                var trackRect = new Rect(barLeft, 0, barWidth, h);
                dc.DrawRoundedRectangle(trackBrush, null, trackRect, rx, ry);

                // Value-proportional bar.
                double barHeight = values[i] / 100.0 * h;
                double barTop = h - barHeight;
                var barRect = new Rect(barLeft, barTop, barWidth, barHeight);
                dc.DrawRoundedRectangle(barBrush, null, barRect, rx, ry);
            }
        }
    }
}
