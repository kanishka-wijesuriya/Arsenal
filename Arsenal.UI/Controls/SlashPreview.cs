using Arsenal.AnimeMatrix;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// The lid of the laptop with its Slash bar, drawn to scale, showing what the
    /// selected effect is putting on it.
    ///
    /// Drawn rather than photographed. Armoury Crate composes the same view from a per
    /// chassis PNG that ROG Live Service downloads, which means no picture on a machine
    /// that never had it installed and a bitmap that softens on a high density display.
    /// The lid here is a rounded panel, a hatch and a sheared bar, so it is sharp at any
    /// scale and there is nothing to ship or fetch.
    ///
    /// The geometry is measured, not styled. Every number in the design box below was
    /// taken off the render ASUS ships for the GU605: the housing is a parallelogram
    /// twenty five units wide sheared along its length, which is why its end caps and
    /// every slit across it are level rather than square to the bar.
    ///
    /// <para>Two things keep it cheap enough to sit on a page that scrolls. The lid,
    /// its hatch, the housing and the unlit slits never change, so they are drawn once
    /// into their own visual and are not touched again until the control is resized;
    /// scrolling then moves a finished visual instead of re-stroking a hundred hatch
    /// lines every frame. And the bar steps between whole frames, so the lit slits are
    /// redrawn on a timer at that rate and only when the frame actually differs,
    /// rather than on the composition clock at the refresh rate of the display.</para>
    /// </summary>
    public sealed class SlashPreview : FrameworkElement, IDisposable
    {
        private const double DesignWidth = 750;
        private const double DesignHeight = 531;
        private const double LidRadius = 18;

        /// <summary>The bar's upper edge, hinge end to far end.</summary>
        private static readonly Point UpperNear = new(91.7, 75);
        private static readonly Point UpperFar = new(632.7, 453);

        /// <summary>The lower edge, twenty five units across and on the same slope.</summary>
        private static readonly Point LowerNear = new(116.7, 75);
        private static readonly Point LowerFar = new(657.7, 453);

        /// <summary>
        /// Slits cut into the light guide. There are far more of them than there are
        /// LEDs: the bar is a continuous diffuser, and a seven segment machine drives
        /// ten slits at a time. Seventy divides by both 7 and 35, so neither bar ends
        /// up with a segment a slit wider than its neighbours.
        /// </summary>
        private const int SlitCount = 70;

        /// <summary>Share of a slit's pitch that is lit, the rest being the gap.</summary>
        private const double SlitDuty = 0.42;

        /// <summary>How far across the bar a slit runs, as a share of its width.</summary>
        private const double SlitFrom = 0.17;
        private const double SlitTo = 0.79;

        private const int LevelSteps = 32;

        private static readonly Geometry[] _slits = BuildSlits(SlitDuty, SlitFrom, SlitTo);
        private static readonly Geometry[] _halos = BuildSlits(0.86, 0.04, 0.94);
        private static readonly Geometry _housing = Quad(UpperNear, LowerNear, LowerFar, UpperFar);
        private static readonly Geometry _lid = BuildLid();

        private static readonly Brush _lidBrush = Frozen(new LinearGradientBrush(
            MediaColor.FromRgb(0x0F, 0x10, 0x13), MediaColor.FromRgb(0x0A, 0x0B, 0x0D), 90));
        private static readonly Brush _housingBrush = Frozen(new SolidColorBrush(MediaColor.FromRgb(0x03, 0x03, 0x04)));
        private static readonly Brush _restBrush = Frozen(new SolidColorBrush(MediaColor.FromRgb(0x7C, 0x83, 0x8D)));
        private static readonly Pen _housingPen = Frozen(new Pen(
            Frozen(new SolidColorBrush(MediaColor.FromArgb(0xD8, 0xE6, 0xEA, 0xF2))), 1.1));
        private static readonly Pen _rimPen = Frozen(new Pen(
            Frozen(new SolidColorBrush(MediaColor.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))), 1));
        private static readonly Pen _hatchPen = Frozen(new Pen(
            Frozen(new SolidColorBrush(MediaColor.FromArgb(0x0C, 0xFF, 0xFF, 0xFF))), 1));

        // After the pen, and it has to be: a static initialiser runs in declaration
        // order, so building this any earlier would hand it a null pen.
        private static readonly Brush _hatchBrush = BuildHatchBrush();

        private static readonly Brush[] _litBrushes = BuildLitBrushes();
        private static readonly Brush[] _haloBrushes = BuildHaloBrushes();

        private readonly DrawingVisual _backdrop = new();
        private readonly DrawingVisual _leds = new();
        private readonly DispatcherTimer _timer;

        private byte[] _frame = new byte[7];
        private readonly byte[] _drawn = new byte[SlitCount];
        private double _unit;
        private double _dpi = 1;
        private readonly DateTime _started = DateTime.UtcNow;

        public SlashPreview()
        {
            AddVisualChild(_backdrop);
            AddVisualChild(_leds);

            // A timer at the bar's own rate, not CompositionTarget.Rendering. The
            // display offers frames five times faster than the bar changes, and the
            // only thing this control would do with them is discard them.
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(SlashEffectSimulator.StepMilliseconds),
            };
            _timer.Tick += (_, _) => DrawLeds();

            UseLayoutRounding = true;
            IsVisibleChanged += (_, _) => UpdateRunning();
            Unloaded += (_, _) => Dispose();
        }

        // ---------------------------------------------------------------------
        // Properties
        // ---------------------------------------------------------------------

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(int), typeof(SlashPreview), new FrameworkPropertyMetadata(0, OnEffectChanged));

        /// <summary>The <see cref="SlashMode"/> currently selected.</summary>
        public int Mode { get => (int)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

        public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
            nameof(Brightness), typeof(int), typeof(SlashPreview), new FrameworkPropertyMetadata(3, OnEffectChanged));

        /// <summary>0 to 3, the same scale the Slash brightness keys use. 0 draws a dark bar.</summary>
        public int Brightness { get => (int)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

        public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
            nameof(Segments), typeof(int), typeof(SlashPreview), new FrameworkPropertyMetadata(7, OnEffectChanged));

        /// <summary>
        /// Individually addressable LEDs behind the bar: 7 on most chassis, 35 on the
        /// long bar. The same number <see cref="SlashDevice"/> writes.
        /// </summary>
        public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

        private static void OnEffectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var preview = (SlashPreview)d;
            preview.UpdateRunning();
            preview.DrawLeds();
        }

        // ---------------------------------------------------------------------
        // Layout
        // ---------------------------------------------------------------------

        protected override int VisualChildrenCount => 2;

        protected override Visual GetVisualChild(int index) => index == 0 ? _backdrop : _leds;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? DesignWidth : availableSize.Width;
            return new Size(width, width * DesignHeight / DesignWidth);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double unit = finalSize.Width / DesignWidth;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (dpi <= 0) dpi = 1;

            if (Math.Abs(unit - _unit) > 0.0001 || Math.Abs(dpi - _dpi) > 0.0001)
            {
                _unit = unit;
                _dpi = dpi;

                _leds.Transform = Frozen(new ScaleTransform(unit, unit));
                BakeBackdrop(finalSize);
                DrawLeds(force: true);
            }

            return finalSize;
        }

        // ---------------------------------------------------------------------
        // Animation
        // ---------------------------------------------------------------------

        /// <summary>
        /// The timer runs only while the control is on screen and there is something
        /// moving. A static bar, or one with the lighting off, is drawn once and then
        /// costs nothing at all: this page is often left open.
        /// </summary>
        private void UpdateRunning()
        {
            bool animated = IsVisible
                && Brightness > 0
                && SlashEffectSimulator.IsAnimated((SlashMode)Mode);

            if (animated == _timer.IsEnabled) return;
            if (animated) _timer.Start();
            else _timer.Stop();
        }

        public void Dispose() => _timer.Stop();

        /// <summary>
        /// Draws the frame for right now, whether or not it differs from the last one.
        /// The control is otherwise driven by its own timer, which needs a running
        /// dispatcher; the preview harness renders off-screen without one.
        /// </summary>
        public void Refresh() => DrawLeds(force: true);

        // ---------------------------------------------------------------------
        // Drawing
        // ---------------------------------------------------------------------

        /// <summary>
        /// Rasterises the unchanging part of the lid into a bitmap, and leaves the
        /// backdrop visual holding nothing but that one image.
        ///
        /// This is the difference between a page that scrolls and one that stutters,
        /// and it is not the same thing as drawing it once. A DrawingVisual retains
        /// drawing instructions, not pixels, so filling it once still leaves every
        /// instruction in it to be rasterised again on every frame the window
        /// composites. Measured on this content, that was over a second a frame: four
        /// hundred clipped antialiased hatch lines, redrawn for every repaint of the
        /// page, whether or not anything on it had moved.
        ///
        /// Baked at the real device scale so it lands on the pixel grid one for one and
        /// stays as sharp as it was when it was vector.
        /// </summary>
        private void BakeBackdrop(Size finalSize)
        {
            // Exactly the pixels this control covers on the screen, so the image lands
            // one for one and is blitted rather than resampled. Resampling a bitmap of
            // this size on every frame measured at six milliseconds by itself, which is
            // most of a frame budget for a picture that never changes.
            int width = (int)Math.Round(finalSize.Width * _dpi);
            int height = (int)Math.Round(finalSize.Height * _dpi);

            if (width <= 0 || height <= 0) return;

            var source = new DrawingVisual
            {
                Transform = Frozen(new ScaleTransform(width / DesignWidth, height / DesignHeight)),
            };

            using (DrawingContext context = source.RenderOpen())
            {
                context.DrawGeometry(_lidBrush, _rimPen, _lid);

                context.PushClip(_lid);
                context.DrawRectangle(_hatchBrush, null, new Rect(0, 0, DesignWidth, DesignHeight));
                context.Pop();

                context.DrawGeometry(_housingBrush, _housingPen, _housing);

                // The unlit slits belong here rather than with the lit ones. They are
                // the diffuser catching room light, they are there whatever the bar is
                // doing, and baking them means a dark effect costs nothing to show.
                foreach (Geometry slit in _slits) context.DrawGeometry(_restBrush, null, slit);
            }

            // Plain 96, because the scaling is already in the transform above. The dpi
            // arguments here are not a label: RenderTargetBitmap scales what it renders
            // by dpi/96, so passing the screen's dpi applies it a second time and the
            // lid is drawn half again too large for the bitmap holding it.
            var baked = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            baked.Render(source);

            // Snapshotted out of the render target. Painting a RenderTargetBitmap
            // directly measured at twenty milliseconds a frame for this size: it is a
            // live render surface, and WPF does not treat it as a plain image.
            var snapshot = new CachedBitmap(baked, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            snapshot.Freeze();

            using DrawingContext target = _backdrop.RenderOpen();
            target.DrawImage(snapshot, new Rect(0, 0, finalSize.Width, finalSize.Height));
        }

        private void DrawLeds(bool force = false)
        {
            int segments = Math.Max(1, Segments);
            if (_frame.Length != segments) _frame = new byte[segments];

            double dim = Brightness switch { <= 0 => 0, 1 => 0.35, 2 => 0.65, _ => 1.0 };
            int step = (int)((DateTime.UtcNow - _started).TotalMilliseconds / SlashEffectSimulator.StepMilliseconds);
            SlashEffectSimulator.Sample((SlashMode)Mode, step, _frame);

            bool changed = force;

            for (int slit = 0; slit < SlitCount; slit++)
            {
                int segment = Math.Min(segments - 1, slit * segments / SlitCount);
                byte level = (byte)Math.Clamp((int)(_frame[segment] * dim / 255.0 * LevelSteps + 0.5), 0, LevelSteps);
                if (level != _drawn[slit]) changed = true;
                _drawn[slit] = level;
            }

            // Nothing moved this tick. The effects hold a frame for several steps and
            // some hold for a second at a time, so this is the common case.
            if (!changed) return;

            using DrawingContext context = _leds.RenderOpen();

            for (int slit = 0; slit < SlitCount; slit++)
            {
                byte level = _drawn[slit];
                if (level == 0) continue;

                // The bloom first, wider and much weaker, then the slit over it. Two
                // passes rather than a BlurEffect, which would cost a render target on
                // every redraw.
                context.DrawGeometry(_haloBrushes[level], null, _halos[slit]);
                context.DrawGeometry(_litBrushes[level], null, _slits[slit]);
            }
        }

        // ---------------------------------------------------------------------
        // Fixed geometry, built once for the whole application
        // ---------------------------------------------------------------------

        private static Geometry BuildLid()
            => Frozen(new RectangleGeometry(new Rect(0.5, 0.5, DesignWidth - 1, DesignHeight - 1), LidRadius, LidRadius));

        /// <summary>Spacing between hatch lines, across the lid.</summary>
        private const double HatchPitch = 5.5;

        /// <summary>
        /// The brushed diagonal on the lid, running parallel to the bar, as one tile
        /// that repeats rather than four hundred separate lines.
        /// </summary>
        /// <remarks>
        /// This was a <see cref="StreamGeometry"/> of about four hundred and twelve
        /// figures, stroked in one go into the backdrop. It looked right and it was
        /// ruinous: measured at the width the Lighting page gives it, baking the backdrop
        /// took <b>2.7 seconds on the UI thread</b>, which is the page not appearing.
        /// Cutting the line count tenfold cut the time fortyfold, which is what named the
        /// culprit; removing the clip around it changed nothing, so it was never the
        /// clip. It is simply that <see cref="RenderTargetBitmap"/> rasterises in
        /// software - it does not go near the graphics card - and a thin antialiased
        /// diagonal is the worst thing to ask a software rasteriser for, at about four
        /// milliseconds each.
        ///
        /// <para>The hatch is a repeating pattern, so only one period of it has to be
        /// drawn. Going down by <c>pitch / slope</c> moves a line across by exactly one
        /// pitch, so a tile that tall and one pitch wide contains the whole pattern and
        /// meets itself on every edge. The line is drawn three times in the tile, once
        /// either side, so the half of a stroke that falls outside an edge is supplied by
        /// the copy that belongs to the neighbour rather than being clipped away and
        /// leaving a seam.</para>
        ///
        /// <para>The result is identical to look at and is rasterised once, at whatever
        /// resolution it is asked for, instead of four hundred times.</para>
        /// </remarks>
        private static Brush BuildHatchBrush()
        {
            double slope = (UpperFar.X - UpperNear.X) / (UpperFar.Y - UpperNear.Y);
            double height = HatchPitch / slope;

            var geometry = new StreamGeometry();

            using (StreamGeometryContext context = geometry.Open())
            {
                for (int copy = -1; copy <= 1; copy++)
                {
                    double offset = copy * HatchPitch;
                    context.BeginFigure(new Point(offset, 0), false, false);
                    context.LineTo(new Point(offset + HatchPitch, height), true, false);
                }
            }

            geometry.Freeze();

            var brush = new DrawingBrush(new GeometryDrawing(null, _hatchPen, geometry))
            {
                Viewport = new Rect(0, 0, HatchPitch, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, HatchPitch, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
            };

            return Frozen(brush);
        }

        /// <summary>
        /// One quad per slit, at <paramref name="duty"/> of its pitch along the bar and
        /// running from <paramref name="from"/> to <paramref name="to"/> across it.
        /// Negative and over-one values widen a shape past the housing, which is what
        /// the bloom wants.
        /// </summary>
        private static Geometry[] BuildSlits(double duty, double from, double to)
        {
            var shapes = new Geometry[SlitCount];
            double half = duty / (2.0 * SlitCount);

            for (int i = 0; i < SlitCount; i++)
            {
                double centre = (i + 0.5) / SlitCount;
                double start = centre - half;
                double end = centre + half;

                shapes[i] = Quad(
                    Across(start, from), Across(start, to),
                    Across(end, to), Across(end, from));
            }

            return shapes;
        }

        /// <summary>
        /// A point on the bar: <paramref name="t"/> runs from the hinge end to the far
        /// end, <paramref name="u"/> from the upper edge to the lower one.
        /// </summary>
        private static Point Across(double t, double u)
        {
            double upperX = UpperNear.X + (UpperFar.X - UpperNear.X) * t;
            double lowerX = LowerNear.X + (LowerFar.X - LowerNear.X) * t;
            double y = UpperNear.Y + (UpperFar.Y - UpperNear.Y) * t;
            return new Point(upperX + (lowerX - upperX) * u, y);
        }

        private static Geometry Quad(Point a, Point b, Point c, Point d)
        {
            var geometry = new StreamGeometry();

            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(a, true, true);
                context.LineTo(b, true, false);
                context.LineTo(c, true, false);
                context.LineTo(d, true, false);
            }

            return Frozen(geometry);
        }

        /// <summary>
        /// The Slash LEDs are white and carry one brightness each, so there is no
        /// colour to track. A lit slit ramps from the unlit diffuser to white rather
        /// than appearing out of nothing, which is what a real one does as it comes up.
        /// </summary>
        private static Brush[] BuildLitBrushes()
        {
            var brushes = new Brush[LevelSteps + 1];

            for (int i = 0; i <= LevelSteps; i++)
            {
                double level = (double)i / LevelSteps;
                byte value = (byte)Math.Clamp(0x7C + (0xFF - 0x7C) * level, 0, 255);
                brushes[i] = Frozen(new SolidColorBrush(
                    MediaColor.FromRgb(value, value, (byte)Math.Min(255, value + 6))));
            }

            return brushes;
        }

        private static Brush[] BuildHaloBrushes()
        {
            var brushes = new Brush[LevelSteps + 1];

            for (int i = 0; i <= LevelSteps; i++)
            {
                double level = (double)i / LevelSteps;
                brushes[i] = Frozen(new SolidColorBrush(
                    MediaColor.FromArgb((byte)(level * level * 96), 0xDC, 0xE6, 0xFF)));
            }

            return brushes;
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
