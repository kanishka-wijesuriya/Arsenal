using Arsenal.Peripherals.Keyboard;
using Arsenal.USB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using MediaColor = System.Windows.Media.Color;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// The laptop's own keyboard, drawn to scale, with every key showing the colour
    /// the selected Aura effect is putting behind it.
    ///
    /// What it can and cannot claim:
    ///
    /// The firmware effects - Static through Comet - are played by the keyboard's own
    /// controller, which reports nothing about where in the animation it is. Those are
    /// reproduced here from the effect's shape, colour and speed. They match what the
    /// keyboard is doing; they are not in step with it, and cannot be.
    ///
    /// The modes Arsenal computes itself - Heatmap, Ambient, Battery, GPU Mode,
    /// Gradient and the audio modes - are shown from the colours actually sent, which
    /// arrive through <see cref="Aura.ColorsApplied"/>.
    ///
    /// The preview also respects what the hardware can do rather than what the effect
    /// could look like: a single-zone backlight is drawn as one colour across every
    /// key, and a four-zone one as four bands, because that is what those machines
    /// are able to show.
    /// </summary>
    public sealed class KeyboardPreview : FrameworkElement, IDisposable
    {
        private const double CornerRadius = 3.2;

        /// <summary>The unlit keycap. Everything else is this mixed towards the glow.</summary>
        private static readonly MediaColor CapColor = MediaColor.FromRgb(0x14, 0x16, 0x19);
        private static readonly MediaColor BoardColor = MediaColor.FromRgb(0x0B, 0x0C, 0x0E);

        private LaptopKeyboardModel? _model;
        private FormattedText?[] _labels = Array.Empty<FormattedText?>();
        private readonly SolidColorBrush _boardBrush = new(BoardColor);

        private DrawingColor[]? _liveColors;
        private long _liveColorsAt;
        private bool _hooked;
        private bool _rendering;
        private readonly DateTime _started = DateTime.UtcNow;
        private double _unit = 1;

        /// <summary>Device pixels per WPF unit, for snapping edges onto the pixel grid.</summary>
        private double _dpiScale = 1;
        private double _pixel = 1;

        public KeyboardPreview()
        {
            _boardBrush.Freeze();

            // Keys are small, and every one of them has a one-pixel rim and a legend of
            // a dozen pixels. Both need to land on the pixel grid to look drawn rather
            // than photographed: layout rounding for the element, display-mode text for
            // the legends, which hints glyphs to whole pixels instead of laying them out
            // for an ideal resolution the screen does not have.
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            IsVisibleChanged += OnIsVisibleChanged;
            Unloaded += (_, _) => Dispose();
        }

        // ---------------------------------------------------------------------
        // Properties
        // ---------------------------------------------------------------------

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(int), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnModeChanged));

        /// <summary>The <see cref="AuraMode"/> currently selected.</summary>
        public int Mode { get => (int)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

        public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
            nameof(Speed), typeof(int), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

        public int Speed { get => (int)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

        public static readonly DependencyProperty PrimaryColorProperty = DependencyProperty.Register(
            nameof(PrimaryColor), typeof(MediaColor), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(MediaColor.FromRgb(255, 0, 128), FrameworkPropertyMetadataOptions.AffectsRender));

        public MediaColor PrimaryColor { get => (MediaColor)GetValue(PrimaryColorProperty); set => SetValue(PrimaryColorProperty, value); }

        public static readonly DependencyProperty SecondaryColorProperty = DependencyProperty.Register(
            nameof(SecondaryColor), typeof(MediaColor), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(MediaColor.FromRgb(0, 0, 0), FrameworkPropertyMetadataOptions.AffectsRender));

        public MediaColor SecondaryColor { get => (MediaColor)GetValue(SecondaryColorProperty); set => SetValue(SecondaryColorProperty, value); }

        public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
            nameof(Brightness), typeof(int), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimationRelevantChanged));

        /// <summary>0 to 3, the same scale the backlight keys use. 0 draws a dark keyboard.</summary>
        public int Brightness { get => (int)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

        public static readonly DependencyProperty BacklightTypeProperty = DependencyProperty.Register(
            nameof(BacklightType), typeof(int), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata((int)AuraBacklightType.PerKey, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The detected <see cref="AuraBacklightType"/>, which limits what is drawn.</summary>
        public int BacklightType { get => (int)GetValue(BacklightTypeProperty); set => SetValue(BacklightTypeProperty, value); }

        public static readonly DependencyProperty ShowKeyEdgeLightingProperty = DependencyProperty.Register(
            nameof(ShowKeyEdgeLighting), typeof(bool), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>
        /// Whether the preview shows coloured light escaping around each keycap. Some
        /// keyboards, including the GU605, illuminate only the printed legends.
        /// </summary>
        public bool ShowKeyEdgeLighting
        {
            get => (bool)GetValue(ShowKeyEdgeLightingProperty);
            set => SetValue(ShowKeyEdgeLightingProperty, value);
        }

        public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
            nameof(Layout), typeof(LaptopKeyboardOptions), typeof(KeyboardPreview),
            new FrameworkPropertyMetadata(default(LaptopKeyboardOptions), OnLayoutChanged));

        /// <summary>
        /// The whole shape of the keyboard, as one value.
        ///
        /// One property rather than one per feature: the view model already has to
        /// combine the chassis table with the user's overrides to decide these, so
        /// splitting them across bindings would only give the two a chance to disagree
        /// mid-update and rebuild the layout twice.
        /// </summary>
        public LaptopKeyboardOptions Layout
        {
            get => (LaptopKeyboardOptions)GetValue(LayoutProperty);
            set => SetValue(LayoutProperty, value);
        }

        // ---------------------------------------------------------------------
        // Layout
        // ---------------------------------------------------------------------

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var preview = (KeyboardPreview)d;
            preview._model = null;
            preview.InvalidateMeasure();
            preview.InvalidateVisual();
        }

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var preview = (KeyboardPreview)d;

            // Leaving an application-driven mode must drop the colours it left behind,
            // or a firmware effect would start from the last heatmap frame.
            preview._liveColors = null;
            preview.UpdateHook();
            preview.UpdateRendering();
        }

        private static void OnAnimationRelevantChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((KeyboardPreview)d).UpdateRendering();

        private LaptopKeyboardModel Model()
        {
            if (_model is not null) return _model;

            _model = LaptopKeyboardLayout.Build(Layout);

            // Labels never change once the layout is built, and laying out text is by
            // far the most expensive thing in a frame, so they are made once here.
            _labels = new FormattedText?[_model.Keys.Count];
            var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int i = 0; i < _model.Keys.Count; i++)
            {
                LaptopKey key = _model.Keys[i];
                if (key.Kind != LaptopKeyKind.Key || key.Label.Length == 0) continue;

                // Display formatting, again: a FormattedText built for the ideal
                // resolution puts glyphs on fractional pixels no matter what the element
                // asks for.
                _labels[i] = new FormattedText(
                    key.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 1, Brushes.White, null, TextFormattingMode.Display, pixelsPerDip);
            }

            return _model;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            LaptopKeyboardModel model = Model();
            double width = double.IsInfinity(availableSize.Width) ? 720 : availableSize.Width;
            return new Size(width, width * model.Height / model.Width);
        }

        // ---------------------------------------------------------------------
        // Animation
        // ---------------------------------------------------------------------

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRendering();

        /// <summary>
        /// The loop runs only while the control is on screen and there is something
        /// moving. A static effect, or a keyboard with the backlight off, draws once
        /// and then costs nothing at all - this page is often left open.
        /// </summary>
        private void UpdateRendering()
        {
            bool animated = IsVisible && Brightness > 0 && IsAnimatedMode((AuraMode)Mode);

            if (animated == _rendering) return;
            _rendering = animated;

            if (animated) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;

            InvalidateVisual();
        }

        private static bool IsAnimatedMode(AuraMode mode)
        {
            // Application-driven modes redraw when their colours arrive, not on a clock.
            if (AuraEffectSimulator.IsDrivenByApplication(mode)) return false;
            return mode != AuraMode.AuraStatic;
        }

        private TimeSpan _lastFrame;

        private void OnFrame(object? sender, EventArgs e)
        {
            // WPF offers frames at the display's rate. Half of them is plenty for a
            // backlight preview and leaves the rest of the app alone.
            if (e is RenderingEventArgs args)
            {
                if (args.RenderingTime - _lastFrame < TimeSpan.FromMilliseconds(30)) return;
                _lastFrame = args.RenderingTime;
            }

            InvalidateVisual();
        }

        private void UpdateHook()
        {
            bool wanted = AuraEffectSimulator.IsDrivenByApplication((AuraMode)Mode);
            if (wanted == _hooked) return;

            _hooked = wanted;
            if (wanted) Aura.ColorsApplied += OnColorsApplied;
            else Aura.ColorsApplied -= OnColorsApplied;
        }

        private void OnColorsApplied(DrawingColor[] colors)
        {
            // Raised on the lighting timer's thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _liveColors = colors;
                _liveColorsAt = Environment.TickCount64;
                InvalidateVisual();
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        public void Dispose()
        {
            if (_rendering)
            {
                CompositionTarget.Rendering -= OnFrame;
                _rendering = false;
            }
            if (_hooked)
            {
                Aura.ColorsApplied -= OnColorsApplied;
                _hooked = false;
            }
        }

        // ---------------------------------------------------------------------
        // Drawing
        // ---------------------------------------------------------------------

        protected override void OnRender(DrawingContext drawingContext)
        {
            LaptopKeyboardModel model = Model();
            if (ActualWidth <= 0 || model.Keys.Count == 0) return;

            UpdateHook();
            _unit = ActualWidth / model.Width;
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_dpiScale <= 0) _dpiScale = 1;
            _pixel = 1 / _dpiScale;

            drawingContext.DrawRoundedRectangle(_boardBrush, null,
                new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);

            double time = (DateTime.UtcNow - _started).TotalSeconds;
            var mode = (AuraMode)Mode;
            var speed = (AuraSpeed)Math.Clamp(Speed, 0, 2);
            DrawingColor primary = ToDrawing(PrimaryColor);
            DrawingColor secondary = ToDrawing(SecondaryColor);

            // The brightness key dims the real backlight; the preview follows it, and
            // an off backlight is drawn as an off keyboard rather than a lit one.
            double dim = Brightness switch { 0 => 0, 1 => 0.35, 2 => 0.65, _ => 1.0 };
            var backlight = (AuraBacklightType)BacklightType;
            bool live = _liveColors is { Length: > 0 } && AuraEffectSimulator.IsDrivenByApplication(mode);

            for (int i = 0; i < model.Keys.Count; i++)
            {
                LaptopKey key = model.Keys[i];
                MediaColor glow = dim <= 0
                    ? MediaColor.FromRgb(0, 0, 0)
                    : Scale(GlowFor(key, i, mode, speed, time, primary, secondary, backlight, live), dim);

                DrawKey(drawingContext, key, glow, _labels[i]);
            }
        }

        /// <summary>
        /// The colour behind one key, taking the hardware's own limits into account:
        /// a single-zone backlight shows one colour everywhere, a four-zone one shows
        /// the colour at the centre of that key's zone, and only per-key hardware
        /// samples the effect at the key itself.
        /// </summary>
        private DrawingColor GlowFor(
            LaptopKey key, int index, AuraMode mode, AuraSpeed speed, double time,
            DrawingColor primary, DrawingColor secondary, AuraBacklightType backlight, bool live)
        {
            if (live)
            {
                DrawingColor[] colors = _liveColors!;
                int zone = Math.Clamp(key.Zone, 0, colors.Length - 1);
                return colors[zone];
            }

            switch (backlight)
            {
                case AuraBacklightType.SingleZone:
                    return AuraEffectSimulator.Sample(mode, time, 0.5, 0.5, primary, secondary, speed, 0);

                case AuraBacklightType.MultiZone:
                {
                    // Four bands: sample once at the middle of the band this key is in.
                    double centre = (Math.Clamp(key.Zone, 0, 3) + 0.5) / 4.0;
                    if (key.Kind == LaptopKeyKind.Lightbar) centre = key.CentreX;
                    return AuraEffectSimulator.Sample(mode, time, centre, 0.5, primary, secondary, speed, key.Zone);
                }

                default:
                    return AuraEffectSimulator.Sample(mode, time, key.CentreX, key.CentreY, primary, secondary, speed, index);
            }
        }

        /// <summary>
        /// Rounds a coordinate onto the device pixel grid.
        ///
        /// Without this every keycap lands on a fractional pixel and its one-pixel rim
        /// is spread across two, which is what makes a grid of small keys look soft
        /// rather than drawn. Snapping the edges rather than the position also keeps
        /// the gaps between keys equal, since both sides land on the same grid.
        /// </summary>
        private double Snap(double value) => Math.Round(value * _dpiScale) / _dpiScale;

        private void DrawKey(DrawingContext context, LaptopKey key, MediaColor glow, FormattedText? label)
        {
            // The gap is taken out of the key rather than added around it, so a row of
            // keys still spans exactly the width the layout gave it.
            double gap = _unit * 0.085;
            double left = Snap(key.X * _unit);
            double top = Snap(key.Y * _unit);
            double right = Snap(key.X * _unit + key.Width * _unit - gap);
            double bottom = Snap(key.Y * _unit + key.Height * _unit - gap);
            var rect = new Rect(left, top, Math.Max(_pixel, right - left), Math.Max(_pixel, bottom - top));

            double strength = Luminance(glow);

            if (key.Kind == LaptopKeyKind.Lightbar)
            {
                // The bar is the light itself rather than a lit keycap, so it is drawn
                // at full strength with a tight bleed under it.
                if (strength > 0.004)
                {
                    context.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(glow, 0.22)), null,
                        Inflate(rect, Snap(_unit * 0.07)), 3, 3);
                }
                context.DrawRoundedRectangle(new SolidColorBrush(glow), null, rect, 2, 2);
                return;
            }

            double radius = Math.Min(CornerRadius, _unit * 0.16);

            // A tight bleed, not a halo. Legend-only keyboards leave this out entirely:
            // their keycaps stay neutral while the printed character carries the light.
            if (ShowKeyEdgeLighting && strength > 0.01)
            {
                context.DrawRoundedRectangle(
                    new SolidColorBrush(WithAlpha(glow, 0.26 * Math.Min(1, strength * 2.2))), null,
                    Inflate(rect, Snap(_unit * 0.055)), radius + 1, radius + 1);
            }

            // The cap stays completely neutral on legend-only keyboards. Edge-lit
            // models retain the subtle colour reflected into the keycap surface.
            var fill = new SolidColorBrush(ShowKeyEdgeLighting
                ? Mix(CapColor, glow, 0.20)
                : CapColor);

            // One whole device pixel, placed on the half-pixel so the stroke covers a
            // pixel exactly instead of straddling two.
            double thickness = Math.Max(_pixel, Math.Round(_unit * 0.045 * _dpiScale) / _dpiScale);
            MediaColor edgeColor = ShowKeyEdgeLighting
                ? Mix(CapColor, glow, 0.78)
                : MediaColor.FromRgb(0x2B, 0x2E, 0x33);
            var edge = new Pen(new SolidColorBrush(edgeColor), thickness);
            var stroked = Deflate(rect, thickness / 2);

            context.DrawRoundedRectangle(fill, edge, stroked, radius, radius);

            if (label is null) return;

            // A whole number of device pixels: a fractional em size is the other half
            // of why small legends look smudged.
            double fontSize = Math.Max(1, Math.Round(_unit * 0.30 * _dpiScale) / _dpiScale);
            label.SetFontSize(fontSize);

            // The legend is the lit part of a real key. Unlit, it falls back to the
            // grey a keycap is actually printed in.
            // The legend is the brightest thing on a lit key: the light comes through
            // the printed character, so it reads brighter than the edge it escapes past.
            label.SetForegroundBrush(new SolidColorBrush(strength > 0.02
                ? Mix(glow, MediaColor.FromRgb(0xFF, 0xFF, 0xFF), 0.46)
                : MediaColor.FromRgb(0x60, 0x66, 0x6B)));

            // A long legend on a narrow key is shrunk to fit rather than dropped: a
            // blank keycap reads as a key with no marking on it, which is a different
            // keyboard.
            double available = rect.Width - _unit * 0.12;
            if (label.Width > available)
            {
                double shrunk = Math.Max(1, Math.Round(fontSize * available / label.Width * _dpiScale) / _dpiScale);
                if (shrunk < fontSize * 0.5) return;
                label.SetFontSize(shrunk);
                if (label.Width > available) return;
            }

            context.DrawText(label, new Point(
                Snap(rect.X + (rect.Width - label.Width) / 2),
                Snap(rect.Y + (rect.Height - label.Height) / 2)));
        }

        private static Rect Inflate(Rect rect, double amount)
            => new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);

        private static Rect Deflate(Rect rect, double amount) => new(
            rect.X + amount, rect.Y + amount,
            Math.Max(0.01, rect.Width - amount * 2), Math.Max(0.01, rect.Height - amount * 2));

        private static MediaColor WithAlpha(MediaColor color, double alpha)
            => MediaColor.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);

        // ---------------------------------------------------------------------
        // Colour helpers
        // ---------------------------------------------------------------------

        private static DrawingColor ToDrawing(MediaColor color) => DrawingColor.FromArgb(color.R, color.G, color.B);

        private static MediaColor Scale(DrawingColor color, double amount) => MediaColor.FromRgb(
            (byte)(color.R * amount), (byte)(color.G * amount), (byte)(color.B * amount));

        private static MediaColor Mix(MediaColor from, MediaColor to, double amount) => MediaColor.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));

        private static double Luminance(MediaColor color)
            => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
    }
}
