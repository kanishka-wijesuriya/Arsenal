using Arsenal.AnimeMatrix;
using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.USB;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SlashPreviewSmoke;

/// <summary>
/// Renders the Slash lid preview across the effects it can show, and checks the
/// things a screenshot cannot: that every mode lights something, that the animated
/// ones actually move, and that both bar lengths are driven.
///
/// The point of the images is to be able to judge the drawing without a laptop with
/// a Slash bar in front of you, and without waiting for an animation to reach an
/// interesting frame.
/// </summary>
internal static class Program
{
    /// <summary>Frames in a filmstrip, and the gap between them.</summary>
    private const int StripFrames = 7;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Subpages off. With them on a SettingsGroup is a card you open, and it
            // generates no containers for its rows until you do, so the preview would
            // not be in the page's visual tree to check. Written before the App is
            // built because SettingsGroup reads the setting in a static initialiser.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"), "{\"theme\":1,\"subpages\":0}");

            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();

            string directory = args.FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);

            AssertBarSitsOnTheLid();
            MeasureRasterCost();
            ReportAuthoredFrames();
            AssertEveryModeLights();
            AssertHeldModes();
            AssertAnimates();
            AssertBothLengths();
            AssertControlRedraws();
            AssertPageWiring(directory);

            Render(directory, "01-static", SlashMode.Static);
            Render(directory, "02-dark", SlashMode.Dark);
            Render(directory, "03-brightness-off", SlashMode.Spectrum, brightness: 0);
            Render(directory, "04-battery", SlashMode.BatteryLevel);
            Render(directory, "05-spectrum", SlashMode.Spectrum);
            Render(directory, "06-bitstream", SlashMode.BitStream);
            Render(directory, "07-transmission", SlashMode.Transmission);
            Render(directory, "08-ramp", SlashMode.Ramp);
            Render(directory, "09-long-bar-flux", SlashMode.Flux, segments: 35);
            Render(directory, "10-actual-size", SlashMode.Flow, scale: 1.0);

            Strip(directory, "11-strip-bounce", SlashMode.Bounce);
            Strip(directory, "12-strip-flow", SlashMode.Flow);
            Strip(directory, "13-strip-interfacing", SlashMode.Interfacing);
            Strip(directory, "14-strip-buzzer", SlashMode.Buzzer);

            Console.WriteLine("slash preview frames written to " + Path.GetFullPath(directory));
            app.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    // ---------------------------------------------------------------------
    // Checks
    // ---------------------------------------------------------------------

    /// <summary>
    /// The bar has to land where it belongs on the lid.
    ///
    /// The lid is rasterised into a bitmap and that bitmap is painted back at the
    /// control's size, which is two scales that have to agree. When they did not, the
    /// lid was drawn half again too large and the bar ran off the right edge, and every
    /// other check here still passed: the effects were right, the cost was right, and
    /// the picture was wrong.
    /// </summary>
    private static void AssertBarSitsOnTheLid()
    {
        var preview = new SlashPreview { Mode = (int)SlashMode.Static, Brightness = 3, Segments = 7 };
        RenderTargetBitmap bitmap = Capture(preview, 600, 0, 1.0);
        preview.Dispose();

        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1;

        for (int y = 0; y < bitmap.PixelHeight; y++)
        {
            for (int x = 0; x < bitmap.PixelWidth; x++)
            {
                int offset = y * stride + x * 4;
                if ((pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3 <= 90) continue;

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        // Where the housing sits in the 750 by 531 design box, as fractions.
        Check("left", left / (double)bitmap.PixelWidth, 91.7 / 750);
        Check("right", right / (double)bitmap.PixelWidth, 657.7 / 750);
        Check("top", top / (double)bitmap.PixelHeight, 75.0 / 531);
        Check("bottom", bottom / (double)bitmap.PixelHeight, 453.0 / 531);

        Console.WriteLine($"geometry: bar occupies x {left}..{right} y {top}..{bottom} of {bitmap.PixelWidth}x{bitmap.PixelHeight}");

        static void Check(string edge, double actual, double expected)
        {
            if (Math.Abs(actual - expected) <= 0.02) return;
            throw new InvalidOperationException(
                $"The bar's {edge} edge is at {actual:P1} of the lid, expected {expected:P1}.");
        }
    }

    /// <summary>
    /// What it costs to rasterise the control, which is not the same as what it costs
    /// to build its drawings.
    ///
    /// A DrawingVisual retains instructions, not pixels: everything in it is rasterised
    /// again every time the window composites, so a visual that is cheap to fill can
    /// still be expensive to show. That is the difference between a page that scrolls
    /// and one that stutters, and it is invisible to every other check here.
    /// </summary>
    private static void MeasureRasterCost()
    {
        // The floor: what this harness costs to rasterise a plain filled rectangle of
        // the same size. RenderTargetBitmap is the software rasteriser and has its own
        // per-call overhead, so the preview's own cost is what it adds to this.
        double floor = MeasureRasterCost(null);
        double backdrop = MeasureRasterCost(SlashMode.Dark);
        double lit = MeasureRasterCost(SlashMode.Static);

        Console.WriteLine($"raster: floor {floor:N2} ms, backdrop {backdrop:N2} ms, fully lit {lit:N2} ms");
        Console.WriteLine($"        the preview adds {lit - floor:N2} ms over a plain rectangle");

        lit -= floor;

        // A 60Hz frame is 16.7ms and the page has a great deal else in it. Anything
        // near that budget on its own is what the user feels as stutter, and not only
        // while scrolling: every repaint of the page pays it.
        if (lit > 4.0)
            throw new InvalidOperationException($"The preview costs {lit:N2} ms a frame to rasterise.");
    }

    /// <summary>
    /// The fair floor: one opaque bitmap of the same size, drawn the same way the
    /// preview's baked lid is. A filled rectangle is not the comparison, because
    /// painting an image is the thing being measured.
    /// </summary>
    private static FrameworkElement PlainImage()
    {
        double height = 560 * 531 / 750.0;
        var visual = new DrawingVisual();

        using (DrawingContext context = visual.RenderOpen())
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x14)), null, new Rect(0, 0, 560, height));

        var bitmap = new RenderTargetBitmap((int)(560 * 1.5), (int)(height * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return new System.Windows.Controls.Image { Source = bitmap, Width = 560, Height = height };
    }

    private static double MeasureRasterCost(SlashMode? mode)
    {
        FrameworkElement preview = mode is null ? PlainImage() : new SlashPreview
        {
            Mode = (int)mode.Value,
            Brightness = 3,
            Segments = 7,
        };

        preview.Measure(new Size(560, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, 560, preview.DesiredSize.Height));
        preview.UpdateLayout();
        (preview as SlashPreview)?.Refresh();

        // At the control's own DPI. The preview bakes its lid for the screen it is on,
        // so rasterising here at a different scale would measure a resample that the
        // real window never performs.
        double dpi = VisualTreeHelper.GetDpi(preview).DpiScaleX;

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(preview.ActualWidth * dpi), (int)Math.Ceiling(preview.ActualHeight * dpi),
            96 * dpi, 96 * dpi, PixelFormats.Pbgra32);

        bitmap.Render(preview);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        const int passes = 40;
        for (int i = 0; i < passes; i++) bitmap.Render(preview);
        clock.Stop();

        (preview as SlashPreview)?.Dispose();
        return clock.Elapsed.TotalMilliseconds / passes;
    }

    /// <summary>
    /// Whether this machine carries ASUS's own frame tables, and whether the simulator
    /// is really playing them. Without this the preview would quietly fall back to its
    /// drawn shapes and still pass every other check here, which is exactly the failure
    /// worth catching: the shapes resemble the bar, the tables match it.
    /// </summary>
    private static void ReportAuthoredFrames()
    {
        if (SlashContentLibrary.Count == 0)
        {
            Console.WriteLine("authored frames: none on this machine, drawn shapes in use");
            return;
        }

        Console.WriteLine($"authored frames: {SlashContentLibrary.Count} effects from {SlashContentLibrary.Origin}");

        foreach ((SlashMode mode, string name) in SlashDevice.Modes)
        {
            if (!SlashContentLibrary.TryGetFrames(mode, out byte[][] authored)) continue;

            byte[] frame = new byte[authored[0].Length];

            for (int step = 0; step < authored.Length; step++)
            {
                SlashEffectSimulator.Sample(mode, step, frame);
                if (frame.SequenceEqual(authored[step])) continue;

                throw new InvalidOperationException(
                    $"{name} frame {step} is not the authored one: "
                    + $"{string.Join(',', frame)} vs {string.Join(',', authored[step])}.");
            }
        }
    }

    /// <summary>
    /// A mode that never lights a segment would still render a perfectly plausible
    /// picture of an unlit bar, and nothing else here would notice.
    /// </summary>
    private static void AssertEveryModeLights()
    {
        foreach ((SlashMode mode, string name) in SlashDevice.Modes)
        {
            if (mode == SlashMode.Dark) continue;

            foreach (int length in new[] { 7, 35 })
            {
                byte[] frame = new byte[length];
                bool lit = false;

                for (int step = 0; step < 120 && !lit; step++)
                {
                    SlashEffectSimulator.Sample(mode, step, frame);
                    lit = frame.Any(value => value > 0);
                }

                if (!lit) throw new InvalidOperationException($"{name} never lights a segment at {length}.");
            }
        }

        Console.WriteLine($"modes: {SlashDevice.Modes.Count} checked at 7 and 35 segments");
    }

    /// <summary>The two held states are exactly that, at every step.</summary>
    private static void AssertHeldModes()
    {
        byte[] frame = new byte[7];

        for (int step = 0; step < 40; step++)
        {
            SlashEffectSimulator.Sample(SlashMode.Static, step, frame);
            if (frame.Any(value => value != 255))
                throw new InvalidOperationException("Static is not fully lit.");

            SlashEffectSimulator.Sample(SlashMode.Dark, step, frame);
            if (frame.Any(value => value != 0))
                throw new InvalidOperationException("Dark is not dark.");
        }

        if (SlashEffectSimulator.IsAnimated(SlashMode.Static) || SlashEffectSimulator.IsAnimated(SlashMode.Dark))
            throw new InvalidOperationException("A held mode is reported as animated, so the preview would redraw forever.");
    }

    /// <summary>
    /// Every mode the simulator calls animated has to differ from one step to the
    /// next within a cycle. One that quietly froze would keep the render loop running
    /// and show a still.
    /// </summary>
    private static void AssertAnimates()
    {
        foreach ((SlashMode mode, string name) in SlashDevice.Modes)
        {
            if (!SlashEffectSimulator.IsAnimated(mode)) continue;

            byte[] first = new byte[7];
            byte[] later = new byte[7];
            SlashEffectSimulator.Sample(mode, 0, first);

            bool moved = false;
            for (int step = 1; step < 120 && !moved; step++)
            {
                SlashEffectSimulator.Sample(mode, step, later);
                moved = !first.SequenceEqual(later);
            }

            if (!moved) throw new InvalidOperationException($"{name} is animated but never changes.");
        }
    }

    /// <summary>
    /// The long bar has to be driven along its whole length, not lit in the first
    /// seven segments with the rest left dark, which is what a shape written against
    /// a fixed index rather than a normalised position would do.
    /// </summary>
    private static void AssertBothLengths()
    {
        byte[] frame = new byte[35];
        bool farEndLit = false;

        for (int step = 0; step < 120 && !farEndLit; step++)
        {
            SlashEffectSimulator.Sample(SlashMode.Bounce, step, frame);
            farEndLit = frame[^1] > 0;
        }

        if (!farEndLit) throw new InvalidOperationException("Bounce never reaches the far end of a 35 segment bar.");
    }

    /// <summary>
    /// The control itself, not just the simulator: two captures a few steps apart
    /// must differ. This is what catches a preview that samples once and caches.
    /// </summary>
    private static void AssertControlRedraws()
    {
        var preview = new SlashPreview { Mode = (int)SlashMode.Bounce, Brightness = 3, Segments = 7 };

        byte[] before = Pixels(Capture(preview, 600, 0));
        bool changed = false;

        // Authored ASUS frames are allowed to hold for several simulator steps. Sample
        // a full visible beat rather than assuming the fourth step must be different.
        for (int step = 1; step <= 24 && !changed; step++)
        {
            byte[] after = Pixels(Capture(preview, 600, SlashEffectSimulator.StepMilliseconds));
            changed = !before.SequenceEqual(after);
        }

        if (!changed)
            throw new InvalidOperationException("The animated preview did not redraw within 24 steps.");

        (preview as SlashPreview)?.Dispose();
        Console.WriteLine("control: redraws across steps");
    }

    /// <summary>
    /// The row on the real page, with a real view model behind it. A binding path that
    /// does not resolve fails silently in WPF, so the control could sit there drawing a
    /// dark bar forever and every other check here would still pass.
    /// </summary>
    private static void AssertPageWiring(string directory)
    {
        var viewModel = new Arsenal.UI.ViewModels.LightingViewModel(new StubSlashLightingService());
        var page = new Arsenal.UI.Views.Pages.LightingPage(viewModel);

        page.Measure(new Size(1080, 3000));
        page.Arrange(new Rect(0, 0, 1080, 3000));
        page.UpdateLayout();

        var preview = Find(page)
            ?? throw new InvalidOperationException("The Lighting page does not contain a Slash preview.");

        if (preview.Mode != viewModel.MatrixMode)
            throw new InvalidOperationException($"Mode binding did not arrive: {preview.Mode} vs {viewModel.MatrixMode}.");
        if (preview.Brightness != viewModel.MatrixBrightness)
            throw new InvalidOperationException($"Brightness binding did not arrive: {preview.Brightness} vs {viewModel.MatrixBrightness}.");
        if (preview.Segments != viewModel.SlashSegments)
            throw new InvalidOperationException($"Segment binding did not arrive: {preview.Segments} vs {viewModel.SlashSegments}.");
        if (viewModel.SlashSegments is not (7 or 35))
            throw new InvalidOperationException($"The chassis reported {viewModel.SlashSegments} segments.");

        // Choosing a visual has to move the preview, which is the whole point of the
        // row sitting above the effect list.
        viewModel.MatrixMode = (int)SlashMode.Spectrum;
        page.UpdateLayout();
        if (preview.Mode != (int)SlashMode.Spectrum)
            throw new InvalidOperationException("Choosing a visual did not reach the preview.");

        var bitmap = new RenderTargetBitmap(1080, 1500, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        Save(bitmap, directory, "00-lighting-page");

        Console.WriteLine($"page wiring: every binding arrived, bar is {viewModel.SlashSegments} segments");
    }

    private static SlashPreview? Find(DependencyObject root)
    {
        if (root is SlashPreview found) return found;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            SlashPreview? child = Find(VisualTreeHelper.GetChild(root, i));
            if (child is not null) return child;
        }

        return null;
    }

    /// <summary>A machine with a Slash bar and no AniMe Matrix.</summary>
    private sealed class StubSlashLightingService : Arsenal.Application.Services.Contracts.ILightingService
    {
        public int Brightness => 3;
        public int CurrentMode => (int)AuraMode.AuraStatic;
        public bool HasAnimeMatrix => false;
        public bool HasSlash => true;
        public bool HasKeyboardColor => true;
        public bool HasAuraEffects => true;
        public int BacklightZoneType => (int)AuraBacklightType.PerKey;
        public bool HasLightbar => false;
        public int MatrixBrightness => 3;
        public int MatrixMode => (int)SlashMode.Bounce;

        public event Action<int>? BrightnessChanged { add { } remove { } }
        public event Action<int>? ModeChanged { add { } remove { } }

        public void SetBrightness(int level) { }
        public void CycleBrightness(int delta = 1) { }
        public void SetMode(int mode) { }
        public void SetColor(byte r, byte g, byte b) { }
        public void SetSpeed(int speed) { }
        public void SetAwake(bool enabled) { }
        public void SetBoot(bool enabled) { }
        public void SetSleep(bool enabled) { }
        public void SetShutdown(bool enabled) { }
        public void SetMatrixBrightness(int level) { }
        public void SetMatrixMode(int mode) { }
        public void SetMatrixPowerPolicy(bool disableOnBattery, bool disableWithLidClosed) { }
    }

    // ---------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------

    /// <summary>
    /// Lays the control out and captures one frame.
    ///
    /// No window is involved. The preview works out its frame from the time elapsed
    /// since it was constructed, and <see cref="RenderTargetBitmap.Render"/> drives
    /// OnRender itself, so waiting before the capture is all it takes to photograph a
    /// travelling effect part way through.
    /// </summary>
    private static RenderTargetBitmap Capture(SlashPreview preview, double width, int settleMilliseconds, double scale = 2.0)
    {
        preview.Measure(new Size(width, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, width, preview.DesiredSize.Height));
        preview.UpdateLayout();

        if (settleMilliseconds > 0) Thread.Sleep(settleMilliseconds);

        // The control animates on its own dispatcher timer, which never ticks here:
        // nothing is pumping a message loop. Refresh draws the frame for the moment
        // the sleep landed on.
        preview.Refresh();

        int pixelWidth = (int)Math.Ceiling(preview.ActualWidth * scale);
        int pixelHeight = (int)Math.Ceiling(preview.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // The lid is drawn on the page's sunken surface in the application; paint the
        // same ground here rather than judging it against transparency.
        var ground = new System.Windows.Shapes.Rectangle
        {
            Width = preview.ActualWidth,
            Height = preview.ActualHeight,
            Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x14)),
        };
        ground.Measure(new Size(preview.ActualWidth, preview.ActualHeight));
        ground.Arrange(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight));

        bitmap.Render(ground);
        bitmap.Render(preview);
        return bitmap;
    }

    private static void Render(
        string directory, string name, SlashMode mode,
        int brightness = 3, int segments = 7, double scale = 2.0)
    {
        var preview = new SlashPreview { Mode = (int)mode, Brightness = brightness, Segments = segments };
        Save(Capture(preview, 560, 400, scale), directory, name);
        (preview as SlashPreview)?.Dispose();
    }

    /// <summary>
    /// Consecutive frames stacked, which is the only way to judge whether an effect
    /// reads as motion. A single capture of a travelling head says nothing about
    /// which way it is going.
    /// </summary>
    private static void Strip(string directory, string name, SlashMode mode)
    {
        var preview = new SlashPreview { Mode = (int)mode, Brightness = 3, Segments = 7 };
        var frames = new List<RenderTargetBitmap>();

        for (int i = 0; i < StripFrames; i++)
            frames.Add(Capture(preview, 420, i == 0 ? 0 : SlashEffectSimulator.StepMilliseconds, 1.5));

        double width = frames[0].Width;
        double height = frames[0].Height;
        var visual = new DrawingVisual();

        using (DrawingContext context = visual.RenderOpen())
        {
            for (int i = 0; i < frames.Count; i++)
                context.DrawImage(frames[i], new Rect(0, i * height, width, height));
        }

        var sheet = new RenderTargetBitmap(
            frames[0].PixelWidth, frames[0].PixelHeight * frames.Count,
            frames[0].DpiX, frames[0].DpiY, PixelFormats.Pbgra32);
        sheet.Render(visual);

        Save(sheet, directory, name);
        (preview as SlashPreview)?.Dispose();
    }

    private static void Save(RenderTargetBitmap bitmap, string directory, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static byte[] Pixels(RenderTargetBitmap bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
