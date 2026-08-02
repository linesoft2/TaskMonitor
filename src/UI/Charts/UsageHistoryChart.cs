using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Lightweight WPF-only area/line chart for a 0–100 usage history (CPU% or RAM%).
    /// Renders directly via DrawingContext with no external charting library.
    /// Used by both the CPU and RAM detail panels.
    /// </summary>
    public sealed class UsageHistoryChart : FrameworkElement
    {
        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(
                nameof(Background),
                typeof(Brush),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register(
                nameof(Values),
                typeof(IReadOnlyList<double?>),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register(
                nameof(AccentColor),
                typeof(Color),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0x78, 0xD4), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridColorProperty =
            DependencyProperty.Register(
                nameof(GridColor),
                typeof(Color),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x33, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillOpacityProperty =
            DependencyProperty.Register(
                nameof(FillOpacity),
                typeof(double),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(48.0 / 255.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameColorProperty =
            DependencyProperty.Register(
                nameof(FrameColor),
                typeof(Color),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x66, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FrameCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(FrameCornerRadius),
                typeof(double),
                typeof(UsageHistoryChart),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double?> Values
        {
            get => (IReadOnlyList<double?>)GetValue(ValuesProperty);
            set => SetValue(ValuesProperty, value);
        }

        public Color AccentColor
        {
            get => (Color)GetValue(AccentColorProperty);
            set => SetValue(AccentColorProperty, value);
        }

        public Color GridColor
        {
            get => (Color)GetValue(GridColorProperty);
            set => SetValue(GridColorProperty, value);
        }

        public double FillOpacity
        {
            get => (double)GetValue(FillOpacityProperty);
            set => SetValue(FillOpacityProperty, value);
        }

        public Color FrameColor
        {
            get => (Color)GetValue(FrameColorProperty);
            set => SetValue(FrameColorProperty, value);
        }

        public double FrameCornerRadius
        {
            get => (double)GetValue(FrameCornerRadiusProperty);
            set => SetValue(FrameCornerRadiusProperty, value);
        }

        /// <summary>
        /// Returns the nearest data point index for the given mouse position,
        /// or -1 if the position is too far from any point.
        /// </summary>
        public int HitTest(Point pt)
        {
            var values = Values;
            if (values == null || values.Count < 2)
                return -1;

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return -1;

            int n = values.Count;
            double stepX = w / (n - 1);
            double tolerance = Math.Max(stepX * 0.5, 8.0);

            int nearest = (int)Math.Round(pt.X / stepX);
            nearest = Math.Max(0, Math.Min(n - 1, nearest));

            double pointX = nearest * stepX;
            return Math.Abs(pt.X - pointX) <= tolerance ? nearest : -1;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            // Clip everything to the rounded shape so the area fill / line never leak
            // past the frame's rounded corners.
            double clipR = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), clipR, clipR));

            if (Background != null)
                dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

            var values = Values;
            if (values != null && values.Count >= 2)
            {
                DrawGrid(dc, w, h);
                DrawAreaAndLine(dc, w, h, values);
            }

            dc.Pop();

            // Frame drawn last (unclipped, on top) so the border stays crisp.
            DrawFrame(dc, w, h);
        }

        private void DrawGrid(DrawingContext dc, double w, double h)
        {
            var gridPen = new Pen(new SolidColorBrush(GridColor), 0.75);
            if (gridPen.CanFreeze)
                gridPen.Freeze();

            // Inner grid lines only — the 0%/100% boundary is drawn as a rounded
            // frame in DrawFrame so the outer edge reads as a single crisp border.
            // Horizontal at 25%, 50%, 75%.
            for (int i = 1; i <= 3; i++)
            {
                double y = h * (1.0 - i / 4.0);
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }

            // Vertical divides the 60-second history into 4 segments (interior only).
            for (int i = 1; i <= 3; i++)
            {
                double x = w * (i / 4.0);
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
        }

        // Outer frame: rounded corners + a more prominent stroke than the inner grid.
        private void DrawFrame(DrawingContext dc, double w, double h)
        {
            var framePen = new Pen(new SolidColorBrush(FrameColor), 1.0);
            if (framePen.CanFreeze)
                framePen.Freeze();

            // Inset by half the thickness so the full stroke is visible inside the
            // element bounds (otherwise the outer half is clipped at the edges).
            double inset = framePen.Thickness / 2.0;
            double r = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            var rect = new Rect(inset, inset, w - inset * 2, h - inset * 2);
            dc.DrawRoundedRectangle(null, framePen, rect, r, r);
        }

        private void DrawAreaAndLine(DrawingContext dc, double w, double h, IReadOnlyList<double?> values)
        {
            int n = values.Count;
            double stepX = w / (n - 1);

            // Build the area fill path. The figure starts at the first valid point's
            // bottom projection, walks up along the data line, then back down to the
            // bottom to close the area.
            var areaFigure = new PathFigure();
            bool hasStart = false;
            double firstX = 0, lastX = 0;

            for (int i = 0; i < n; i++)
            {
                if (!values[i].HasValue)
                    continue;

                double x = i * stepX;
                double y = h * (1.0 - values[i].Value / 100.0);

                if (!hasStart)
                {
                    areaFigure.StartPoint = new Point(x, h);
                    areaFigure.Segments.Add(new LineSegment(new Point(x, y), true));
                    firstX = x;
                    hasStart = true;
                }
                else
                {
                    areaFigure.Segments.Add(new LineSegment(new Point(x, y), true));
                }
                lastX = x;
            }

            if (!hasStart)
                return;

            areaFigure.Segments.Add(new LineSegment(new Point(lastX, h), true));
            areaFigure.Segments.Add(new LineSegment(new Point(firstX, h), true));

            var areaGeometry = new PathGeometry();
            areaGeometry.Figures.Add(areaFigure);

            byte fillAlpha = (byte)Math.Round(Math.Max(0, Math.Min(255, FillOpacity * 255)));
            var fillBrush = new SolidColorBrush(Color.FromArgb(fillAlpha, AccentColor.R, AccentColor.G, AccentColor.B));
            if (fillBrush.CanFreeze)
                fillBrush.Freeze();

            dc.DrawGeometry(fillBrush, null, areaGeometry);

            // Stroke on top of the area.
            var lineFigure = new PathFigure();
            bool lineStarted = false;

            for (int i = 0; i < n; i++)
            {
                if (!values[i].HasValue)
                    continue;

                double x = i * stepX;
                double y = h * (1.0 - values[i].Value / 100.0);

                if (!lineStarted)
                {
                    lineFigure.StartPoint = new Point(x, y);
                    lineStarted = true;
                }
                else
                {
                    lineFigure.Segments.Add(new LineSegment(new Point(x, y), true));
                }
            }

            if (!lineStarted)
                return;

            var lineGeometry = new PathGeometry();
            lineGeometry.Figures.Add(lineFigure);

            var linePen = new Pen(new SolidColorBrush(AccentColor), 1.5);
            if (linePen.CanFreeze)
                linePen.Freeze();

            dc.DrawGeometry(null, linePen, lineGeometry);
        }
    }
}
