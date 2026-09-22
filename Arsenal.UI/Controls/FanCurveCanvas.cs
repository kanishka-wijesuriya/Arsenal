using Arsenal.Application.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Arsenal.UI.Controls
{
    public class FanCurveCanvas : Canvas
    {
        public static readonly DependencyProperty CurveModelProperty =
            DependencyProperty.Register(
                nameof(CurveModel),
                typeof(FanCurveModel),
                typeof(FanCurveCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnCurveModelChanged));

        public FanCurveModel? CurveModel
        {
            get => (FanCurveModel?)GetValue(CurveModelProperty);
            set => SetValue(CurveModelProperty, value);
        }

        private static void OnCurveModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FanCurveCanvas canvas)
            {
                canvas.InvalidateVisual();
            }
        }

        private static readonly Typeface AxisTypeface = new("Segoe UI Variable Display, Segoe UI");
        private int _draggingIndex = -1;

        private readonly Pen _linePen = new(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 150, 255)), 2.5);
        private readonly Brush _areaBrush = new LinearGradientBrush(
            System.Windows.Media.Color.FromArgb(80, 0, 150, 255),
            System.Windows.Media.Color.FromArgb(10, 0, 150, 255),
            90.0);
        private readonly Brush _handleBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
        private readonly Brush _handleBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
        private readonly Pen _handleBorderPen;

        public FanCurveCanvas()
        {
            ClipToBounds = true;
            ThemeRepaint.Follow(this);
            _handleBorderPen = new Pen(_handleBorderBrush, 2.0);
            _linePen.Freeze();
            _areaBrush.Freeze();
            _handleBrush.Freeze();
            _handleBorderBrush.Freeze();
            _handleBorderPen.Freeze();
        }

        /// <summary>
        /// Grid lines and axis labels come from the shared theme brushes, so the chart
        /// stays legible when the app is switched to the light palette. Falls back to
        /// the dark-theme values if the resources are not reachable.
        /// </summary>
        private Pen ResolveGridPen()
        {
            if (TryFindResource("StrokeDivider") is Brush brush)
            {
                var pen = new Pen(brush, 1.0);
                pen.Freeze();
                return pen;
            }

            var fallback = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255)), 1.0);
            fallback.Freeze();
            return fallback;
        }

        private Brush ResolveAxisBrush()
            => TryFindResource("TextTertiary") as Brush ?? Brushes.Gray;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            const double padLeft = 35;
            const double padBottom = 25;
            const double padTop = 15;
            const double padRight = 15;

            double graphW = w - padLeft - padRight;
            double graphH = h - padTop - padBottom;

            Typeface typeFace = AxisTypeface;
            Pen gridPen = ResolveGridPen();
            Brush axisBrush = ResolveAxisBrush();
            Brush handleBrush = TryFindResource("TextPrimary") as Brush ?? _handleBrush;

            for (int t = 20; t <= 100; t += 20)
            {
                double x = padLeft + (t / 100.0) * graphW;
                dc.DrawLine(gridPen, new Point(x, padTop), new Point(x, h - padBottom));

                var text = new FormattedText($"{t}°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeFace, 11, axisBrush, 1.0);
                dc.DrawText(text, new Point(x - text.Width / 2, h - padBottom + 4));
            }

            for (int p = 0; p <= 100; p += 25)
            {
                double y = padTop + (1.0 - p / 100.0) * graphH;
                dc.DrawLine(gridPen, new Point(padLeft, y), new Point(w - padRight, y));

                var text = new FormattedText($"{p}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeFace, 11, axisBrush, 1.0);
                dc.DrawText(text, new Point(padLeft - text.Width - 6, y - text.Height / 2));
            }

            if (CurveModel == null || CurveModel.Points.Count == 0) return;

            var screenPoints = new List<Point>();
            foreach (var pt in CurveModel.Points)
            {
                double sx = padLeft + (Math.Clamp(pt.Temperature, 0, 100) / 100.0) * graphW;
                double sy = padTop + (1.0 - Math.Clamp(pt.Percentage, 0, 100) / 100.0) * graphH;
                screenPoints.Add(new Point(sx, sy));
            }

            var areaGeometry = new StreamGeometry();
            using (var ctx = areaGeometry.Open())
            {
                ctx.BeginFigure(new Point(padLeft, h - padBottom), true, true);
                ctx.LineTo(screenPoints[0], true, true);
                for (int i = 1; i < screenPoints.Count; i++)
                {
                    ctx.LineTo(screenPoints[i], true, true);
                }
                ctx.LineTo(new Point(w - padRight, screenPoints[^1].Y), true, true);
                ctx.LineTo(new Point(w - padRight, h - padBottom), true, true);
            }
            areaGeometry.Freeze();
            dc.DrawGeometry(_areaBrush, null, areaGeometry);

            for (int i = 0; i < screenPoints.Count - 1; i++)
            {
                dc.DrawLine(_linePen, screenPoints[i], screenPoints[i + 1]);
            }

            for (int i = 0; i < screenPoints.Count; i++)
            {
                var pt = screenPoints[i];
                dc.DrawEllipse(handleBrush, _handleBorderPen, pt, 5.5, 5.5);
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (CurveModel == null || e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(this);
            double w = ActualWidth;
            double h = ActualHeight;

            const double padLeft = 35;
            const double padBottom = 25;
            const double padTop = 15;
            const double padRight = 15;
            double graphW = w - padLeft - padRight;
            double graphH = h - padTop - padBottom;

            _draggingIndex = -1;
            for (int i = 0; i < CurveModel.Points.Count; i++)
            {
                var pt = CurveModel.Points[i];
                double sx = padLeft + (pt.Temperature / 100.0) * graphW;
                double sy = padTop + (1.0 - pt.Percentage / 100.0) * graphH;

                if (Math.Abs(pos.X - sx) < 12 && Math.Abs(pos.Y - sy) < 12)
                {
                    _draggingIndex = i;
                    CaptureMouse();
                    break;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingIndex < 0 || CurveModel == null || !IsMouseCaptured) return;

            Point pos = e.GetPosition(this);
            double w = ActualWidth;
            double h = ActualHeight;

            const double padLeft = 35;
            const double padBottom = 25;
            const double padTop = 15;
            const double padRight = 15;
            double graphW = w - padLeft - padRight;
            double graphH = h - padTop - padBottom;

            int newTemp = (int)Math.Round(((pos.X - padLeft) / graphW) * 100);
            int newPercent = (int)Math.Round((1.0 - (pos.Y - padTop) / graphH) * 100);

            newTemp = Math.Clamp(newTemp, 20, 100);
            newPercent = Math.Clamp(newPercent, 0, 100);

            if (_draggingIndex > 0)
                newTemp = Math.Max(newTemp, CurveModel.Points[_draggingIndex - 1].Temperature);
            if (_draggingIndex < CurveModel.Points.Count - 1)
                newTemp = Math.Min(newTemp, CurveModel.Points[_draggingIndex + 1].Temperature);

            CurveModel.Points[_draggingIndex].Temperature = newTemp;
            CurveModel.Points[_draggingIndex].Percentage = newPercent;

            InvalidateVisual();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
                _draggingIndex = -1;
            }
        }
    }
}
