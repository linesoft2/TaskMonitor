using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Task Manager "memory composition" bar: 使用中 | 已修改 | 备用 | 可用.
    /// <see cref="Values"/>[0..3] carry the four segment sizes in bytes; widths are drawn
    /// proportional to their sum. 备用 (Standby) is a near-blank tint, 可用 (Free) is left as
    /// the bar's empty track (blank). <see cref="HitTest"/> returns the segment index under
    /// the cursor (0=使用中, 1=已修改, 2=备用, 3=可用) for the hover tooltip. Hand-drawn via
    /// DrawingContext — no charting library.
    /// </summary>
    public sealed class MemoryCompositionBar : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(
                nameof(Values),
                typeof(IReadOnlyList<long>),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty InUseColorProperty =
            DependencyProperty.Register(
                nameof(InUseColor),
                typeof(Color),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0x78, 0xD4), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ModifiedColorProperty =
            DependencyProperty.Register(
                nameof(ModifiedColor),
                typeof(Color),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(Color.FromRgb(0xF7, 0xB5, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StandbyColorProperty =
            DependencyProperty.Register(
                nameof(StandbyColor),
                typeof(Color),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(Color.FromArgb(0x1A, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackColorProperty =
            DependencyProperty.Register(
                nameof(TrackColor),
                typeof(Color),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(Color.FromArgb(0x09, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameColorProperty =
            DependencyProperty.Register(
                nameof(FrameColor),
                typeof(Color),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(Color.FromArgb(0x33, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius),
                typeof(double),
                typeof(MemoryCompositionBar),
                new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<long> Values
        {
            get => (IReadOnlyList<long>)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Color InUseColor { get => (Color)GetValue(InUseColorProperty); set => SetValue(InUseColorProperty, value); }
        public Color ModifiedColor { get => (Color)GetValue(ModifiedColorProperty); set => SetValue(ModifiedColorProperty, value); }
        public Color StandbyColor { get => (Color)GetValue(StandbyColorProperty); set => SetValue(StandbyColorProperty, value); }
        public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }
        public Color FrameColor { get => (Color)GetValue(FrameColorProperty); set => SetValue(FrameColorProperty, value); }
        public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

        /// <summary>Segment index under the point (0..3), or -1 if outside the bar / no data.</summary>
        public int HitTest(Point pt)
        {
            var values = Values;
            if (values == null || values.Count < 4) return -1;

            double w = ActualWidth;
            if (w <= 0 || pt.X < 0 || pt.X > w) return -1;

            double total = values[0] + values[1] + values[2] + values[3];
            if (total <= 0) return -1;

            double x = pt.X;
            double cum = 0;
            for (int i = 0; i < 4; i++)
            {
                cum += values[i] / total * w;
                if (x <= cum) return i;
            }
            return 3; // last segment (rounding)
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double r = Math.Min(CornerRadius, Math.Min(w, h) / 2.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), r, r));

            var values = Values;
            double total = (values != null && values.Count >= 4)
                ? values[0] + values[1] + values[2] + values[3]
                : 0;

            if (total > 0)
            {
                // Track fills the whole bar so the 可用 (blank) portion still reads as part of the bar.
                var trackBrush = Freeze(new SolidColorBrush(TrackColor));
                dc.DrawRectangle(trackBrush, null, new Rect(0, 0, w, h));

                // Segments 0..2 (使用中, 已修改, 备用). 可用 (Free) is the track left bare.
                var fills = new[] { InUseColor, ModifiedColor, StandbyColor };
                double x = 0;
                for (int i = 0; i < 3; i++)
                {
                    double segW = values[i] / total * w;
                    if (segW > 0)
                        dc.DrawRectangle(Freeze(new SolidColorBrush(fills[i])), null, new Rect(x, 0, segW, h));
                    x += segW;
                }
            }

            dc.Pop();

            // Crisp rounded frame on top (unclipped).
            var framePen = new Pen(Freeze(new SolidColorBrush(FrameColor)), 1.0);
            if (framePen.CanFreeze) framePen.Freeze();
            double inset = framePen.Thickness / 2.0;
            double rr = Math.Min(CornerRadius, Math.Min(w, h) / 2.0);
            dc.DrawRoundedRectangle(null, framePen,
                new Rect(inset, inset, w - inset * 2, h - inset * 2), rr, rr);
        }

        private static T Freeze<T>(T brush) where T : Freezable
        {
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
}
