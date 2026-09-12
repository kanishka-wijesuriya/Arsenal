using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Arsenal.UI.Controls;

/// <summary>
/// Compact HSV spectrum picker designed for the lighting page. It deliberately
/// stays dependency-free so the selected color can be bound two-way in XAML.
/// </summary>
public sealed class ColorSpectrumPicker : FrameworkElement
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor),
        typeof(Color),
        typeof(ColorSpectrumPicker),
        new FrameworkPropertyMetadata(
            Color.FromRgb(255, 0, 128),
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedColorChanged));

    private const double HueStripHeight = 18;
    private const double StripGap = 14;
    private double _hue = 330;
    private double _saturation = 1;
    private double _value = 1;
    private bool _updatingFromPointer;
    private PickerRegion _activeRegion;

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public ColorSpectrumPicker()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        MinWidth = 240;
        MinHeight = 170;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        Rect spectrum = SpectrumRect;
        Rect hueStrip = HueRect;
        var subtlePen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);

        drawingContext.DrawRoundedRectangle(new SolidColorBrush(HsvToRgb(_hue, 1, 1)), subtlePen, spectrum, 8, 8);
        drawingContext.DrawRoundedRectangle(
            new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
            null,
            spectrum,
            8,
            8);
        drawingContext.DrawRoundedRectangle(
            new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
            subtlePen,
            spectrum,
            8,
            8);

        var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        hueBrush.GradientStops.Add(new GradientStop(Colors.Red, 0));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Yellow, 1d / 6));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Lime, 2d / 6));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Cyan, 3d / 6));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Blue, 4d / 6));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Magenta, 5d / 6));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Red, 1));
        drawingContext.DrawRoundedRectangle(hueBrush, subtlePen, hueStrip, 9, 9);

        Point marker = new(
            spectrum.Left + (_saturation * spectrum.Width),
            spectrum.Top + ((1 - _value) * spectrum.Height));
        drawingContext.DrawEllipse(new SolidColorBrush(SelectedColor), new Pen(Brushes.White, 2), marker, 7, 7);
        drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 1), marker, 9, 9);

        double hueX = hueStrip.Left + ((_hue / 360d) * hueStrip.Width);
        var hueMarker = new Rect(hueX - 3, hueStrip.Top - 3, 6, hueStrip.Height + 6);
        drawingContext.DrawRoundedRectangle(Brushes.Transparent, new Pen(Brushes.White, 2), hueMarker, 3, 3);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point point = e.GetPosition(this);
        _activeRegion = HueRect.Contains(point) ? PickerRegion.Hue : PickerRegion.Spectrum;
        CaptureMouse();
        UpdateFromPointer(point);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
            UpdateFromPointer(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            UpdateFromPointer(e.GetPosition(this));
            ReleaseMouseCapture();
            _activeRegion = PickerRegion.None;
            e.Handled = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private Rect SpectrumRect => new(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - HueStripHeight - StripGap - 2));
    private Rect HueRect => new(1, Math.Max(1, ActualHeight - HueStripHeight - 1), Math.Max(0, ActualWidth - 2), HueStripHeight);

    private void UpdateFromPointer(Point point)
    {
        Rect rect = _activeRegion == PickerRegion.Hue ? HueRect : SpectrumRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (_activeRegion == PickerRegion.Hue)
            _hue = Math.Clamp((point.X - rect.Left) / rect.Width, 0, 1) * 360;
        else
        {
            _saturation = Math.Clamp((point.X - rect.Left) / rect.Width, 0, 1);
            _value = 1 - Math.Clamp((point.Y - rect.Top) / rect.Height, 0, 1);
        }

        _updatingFromPointer = true;
        SetCurrentValue(SelectedColorProperty, HsvToRgb(_hue, _saturation, _value));
        _updatingFromPointer = false;
        InvalidateVisual();
    }

    private static void OnSelectedColorChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var picker = (ColorSpectrumPicker)dependencyObject;
        if (!picker._updatingFromPointer && args.NewValue is Color color)
            RgbToHsv(color, out picker._hue, out picker._saturation, out picker._value);
        picker.InvalidateVisual();
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double match = value - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb(
            (byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255),
            (byte)Math.Round((b + match) * 255));
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        hue = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }

    private enum PickerRegion
    {
        None,
        Spectrum,
        Hue
    }
}
