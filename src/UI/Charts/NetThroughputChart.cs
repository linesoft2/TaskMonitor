using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Bidirectional network throughput chart: upload (↑) is drawn as a line + faint area
    /// in the UPPER half (growing up from the centerline), and download (↓) is mirrored in
    /// the LOWER half (growing down from the centerline), each in its own color. Both halves
    /// share one vertical scale — auto-sized to the larger of the two series' max in the
    /// visible window — so up and down stay directly comparable. 60-tick rolling history.
    ///
    /// Hand-drawn via DrawingContext with no charting library (same pattern as
    /// <see cref="UsageHistoryChart"/>). Used only by the Network detail panel.
    /// </summary>
    public sealed class NetThroughputChart : FrameworkElement
    {
        public static readonly DependencyProperty BackgroundProperty =
            DependencyProperty.Register(
                nameof(Background),
                typeof(Brush),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Background
        {
            get => (Brush)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public static readonly DependencyProperty UpValuesProperty =
            DependencyProperty.Register(
                nameof(UpValues),
                typeof(IReadOnlyList<double?>),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DownValuesProperty =
            DependencyProperty.Register(
                nameof(DownValues),
                typeof(IReadOnlyList<double?>),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double?> UpValues
        {
            get => (IReadOnlyList<double?>)GetValue(UpValuesProperty);
            set => SetValue(UpValuesProperty, value);
        }

        public IReadOnlyList<double?> DownValues
        {
            get => (IReadOnlyList<double?>)GetValue(DownValuesProperty);
            set => SetValue(DownValuesProperty, value);
        }

        public static readonly DependencyProperty UpColorProperty =
            DependencyProperty.Register(
                nameof(UpColor),
                typeof(Color),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x0A, 0x8C, 0x4B), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DownColorProperty =
            DependencyProperty.Register(
                nameof(DownColor),
                typeof(Color),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0x78, 0xD4), FrameworkPropertyMetadataOptions.AffectsRender));

        public Color UpColor
        {
            get => (Color)GetValue(UpColorProperty);
            set => SetValue(UpColorProperty, value);
        }

        public Color DownColor
        {
            get => (Color)GetValue(DownColorProperty);
            set => SetValue(DownColorProperty, value);
        }

        public static readonly DependencyProperty GridColorProperty =
            DependencyProperty.Register(
                nameof(GridColor),
                typeof(Color),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x1A, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public Color GridColor
        {
            get => (Color)GetValue(GridColorProperty);
            set => SetValue(GridColorProperty, value);
        }

        public static readonly DependencyProperty AxisColorProperty =
            DependencyProperty.Register(
                nameof(AxisColor),
                typeof(Color),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x40, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public Color AxisColor
        {
            get => (Color)GetValue(AxisColorProperty);
            set => SetValue(AxisColorProperty, value);
        }

        public static readonly DependencyProperty LabelColorProperty =
            DependencyProperty.Register(
                nameof(LabelColor),
                typeof(Color),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(Color.FromRgb(0x88, 0x88, 0x88), FrameworkPropertyMetadataOptions.AffectsRender));

        // Color of the small edge annotations (each half's scale ceiling). Neutral gray by
        // default; the view re-tints it to the theme's secondary text color.
        public Color LabelColor
        {
            get => (Color)GetValue(LabelColorProperty);
            set => SetValue(LabelColorProperty, value);
        }

        public static readonly DependencyProperty UpFillOpacityProperty =
            DependencyProperty.Register(
                nameof(UpFillOpacity),
                typeof(double),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(48.0 / 255.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DownFillOpacityProperty =
            DependencyProperty.Register(
                nameof(DownFillOpacity),
                typeof(double),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(48.0 / 255.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double UpFillOpacity
        {
            get => (double)GetValue(UpFillOpacityProperty);
            set => SetValue(UpFillOpacityProperty, value);
        }

        public double DownFillOpacity
        {
            get => (double)GetValue(DownFillOpacityProperty);
            set => SetValue(DownFillOpacityProperty, value);
        }

        public static readonly DependencyProperty LineThicknessProperty =
            DependencyProperty.Register(
                nameof(LineThickness),
                typeof(double),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

        public double LineThickness
        {
            get => (double)GetValue(LineThicknessProperty);
            set => SetValue(LineThicknessProperty, value);
        }

        public static readonly DependencyProperty FrameColorProperty =
            DependencyProperty.Register(
                nameof(FrameColor),
                typeof(Color),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(Color.FromArgb(0x66, 0x00, 0x00, 0x00), FrameworkPropertyMetadataOptions.AffectsRender));

        public Color FrameColor
        {
            get => (Color)GetValue(FrameColorProperty);
            set => SetValue(FrameColorProperty, value);
        }

        public static readonly DependencyProperty FrameCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(FrameCornerRadius),
                typeof(double),
                typeof(NetThroughputChart),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double FrameCornerRadius
        {
            get => (double)GetValue(FrameCornerRadiusProperty);
            set => SetValue(FrameCornerRadiusProperty, value);
        }

        /// <summary>
        /// Nearest data-point index for the given mouse position, or -1 if too far from any.
        /// Index space is shared by both series (they're sampled in lockstep, one per tick).
        /// </summary>
        public int HitTest(Point pt)
        {
            var values = UpValues ?? DownValues;
            if (values == null || values.Count < 2)
                return -1;

            double w = ActualWidth;
            if (w <= 0)
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

            // Clip to the rounded frame so the fills/lines never leak past the corners.
            double clipR = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), clipR, clipR));

            if (Background != null)
                dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

            DrawGrid(dc, w, h);

            // Each half scales INDEPENDENTLY to its own series' window-peak: upload fills the
            // upper half against the upload peak, download fills the lower half against the
            // download peak. Up and down usually differ by orders of magnitude, so a shared
            // scale would flatten the smaller one — each gets its own auto-scale instead.
            double upMax = MaxOf(UpValues);
            double downMax = MaxOf(DownValues);
            double upScale = upMax > 0 ? upMax : 1;     // fallback to 1 → flat line on centerline
            double downScale = downMax > 0 ? downMax : 1;

            DrawSeries(dc, w, h, UpValues, upScale, up: true, UpColor, UpFillOpacity);
            DrawSeries(dc, w, h, DownValues, downScale, up: false, DownColor, DownFillOpacity);

            DrawAxis(dc, w, h);

            dc.Pop();

            // Frame last (unclipped, on top) so the border stays crisp.
            DrawFrame(dc, w, h);

            // Edge annotations: each half's current scale ceiling (its own peak), small gray
            // text — drawn last so it sits on top of the frame/line and stays readable.
            // Upload's max rides the top edge, download's the bottom edge.
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            DrawScaleLabel(dc, h, upMax, up: true, pixelsPerDip);
            DrawScaleLabel(dc, h, downMax, up: false, pixelsPerDip);
        }

        // Small "↑/↓ 峰值 <peak>" label at the top edge (upload) or bottom edge (download),
        // spelling out that the value is that half's peak (its independent scale ceiling).
        private void DrawScaleLabel(DrawingContext dc, double h, double peak, bool up, double pixelsPerDip)
        {
            string text = (up ? "↑ 峰值 " : "↓ 峰值 ") + NetRateFormatter.Format((long)Math.Round(peak));
            const double size = 10;
            var typeface = new Typeface(SystemFonts.MessageFontFamily,
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var brush = new SolidColorBrush(LabelColor);
            if (brush.CanFreeze) brush.Freeze();
            var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
            double x = 6;
            double y = up ? 3 : h - ft.Height - 3;
            dc.DrawText(ft, new Point(x, y));
        }

        private static double MaxOf(IReadOnlyList<double?> values)
        {
            double m = 0;
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i].HasValue && values[i].Value > m)
                        m = values[i].Value;
                }
            }
            return m;
        }

        private void DrawGrid(DrawingContext dc, double w, double h)
        {
            var gridPen = new Pen(new SolidColorBrush(GridColor), 0.75);
            if (gridPen.CanFreeze)
                gridPen.Freeze();

            // One horizontal guide per half (at h*0.25 and h*0.75); the centerline itself is
            // drawn separately as a stronger axis in DrawAxis.
            dc.DrawLine(gridPen, new Point(0, h * 0.25), new Point(w, h * 0.25));
            dc.DrawLine(gridPen, new Point(0, h * 0.75), new Point(w, h * 0.75));

            // Vertical divides the 60-second history into 4 segments (interior only).
            for (int i = 1; i <= 3; i++)
            {
                double x = w * (i / 4.0);
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
        }

        // The center baseline — the shared zero that upload grows up from and download down.
        private void DrawAxis(DrawingContext dc, double w, double h)
        {
            var axisPen = new Pen(new SolidColorBrush(AxisColor), 1.0);
            if (axisPen.CanFreeze)
                axisPen.Freeze();
            dc.DrawLine(axisPen, new Point(0, h / 2.0), new Point(w, h / 2.0));
        }

        // Draws one direction: a faint area wedged between the centerline and the data line,
        // then the line stroke on top. `up` selects upper (upload) vs lower (download) half.
        private void DrawSeries(DrawingContext dc, double w, double h, IReadOnlyList<double?> values, double max, bool up, Color color, double fillOpacity)
        {
            if (values == null || values.Count < 2)
                return;

            int n = values.Count;
            double stepX = w / (n - 1);
            double midY = h / 2.0;

            double YFor(double v)
            {
                double frac = v / max;
                return up ? midY - frac * midY   // upload: 0 → midY, max → top (0)
                          : midY + frac * midY;  // download: 0 → midY, max → bottom (h)
            }

            // Area fill: walk the data line, bookended by dropping to the centerline at each end.
            var areaFigure = new PathFigure();
            bool hasStart = false;
            double firstX = 0, lastX = 0;

            for (int i = 0; i < n; i++)
            {
                if (!values[i].HasValue)
                    continue;

                double x = i * stepX;
                double y = YFor(values[i].Value);

                if (!hasStart)
                {
                    areaFigure.StartPoint = new Point(x, midY);
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

            areaFigure.Segments.Add(new LineSegment(new Point(lastX, midY), true));
            areaFigure.Segments.Add(new LineSegment(new Point(firstX, midY), true));

            var areaGeometry = new PathGeometry();
            areaGeometry.Figures.Add(areaFigure);

            byte fillAlpha = (byte)Math.Round(Math.Max(0, Math.Min(255, fillOpacity * 255)));
            var fillBrush = new SolidColorBrush(Color.FromArgb(fillAlpha, color.R, color.G, color.B));
            if (fillBrush.CanFreeze)
                fillBrush.Freeze();

            dc.DrawGeometry(fillBrush, null, areaGeometry);

            // Line stroke on top.
            var lineFigure = new PathFigure();
            bool lineStarted = false;

            for (int i = 0; i < n; i++)
            {
                if (!values[i].HasValue)
                    continue;

                double x = i * stepX;
                double y = YFor(values[i].Value);

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

            var linePen = new Pen(new SolidColorBrush(color), LineThickness);
            if (linePen.CanFreeze)
                linePen.Freeze();

            dc.DrawGeometry(null, linePen, lineGeometry);
        }

        // Outer frame: rounded corners + a more prominent stroke than the inner grid.
        private void DrawFrame(DrawingContext dc, double w, double h)
        {
            var framePen = new Pen(new SolidColorBrush(FrameColor), 1.0);
            if (framePen.CanFreeze)
                framePen.Freeze();

            // Inset by half the thickness so the full stroke is visible inside the bounds.
            double inset = framePen.Thickness / 2.0;
            double r = Math.Min(FrameCornerRadius, Math.Min(w, h) / 2.0);
            var rect = new Rect(inset, inset, w - inset * 2, h - inset * 2);
            dc.DrawRoundedRectangle(null, framePen, rect, r, r);
        }
    }
}
