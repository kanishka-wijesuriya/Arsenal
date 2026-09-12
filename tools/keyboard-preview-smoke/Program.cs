using Arsenal.Peripherals.Keyboard;
using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.USB;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KeyboardPreviewSmoke;

/// <summary>
/// Renders the keyboard backlight preview across several effects, layouts and
/// backlight types, so the drawing can be looked at without an ASUS laptop in front
/// of you and without waiting for an animation to reach an interesting frame.
///
/// The preview animates on the composition clock, so each capture is taken through a
/// real off-screen window after letting frames run for a moment.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"), "{\"theme\":1}");

            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();

            string directory = args.FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);

            AssertCatalog();
            AssertLayout();
            AssertAnimates();
            AssertPageWiring(directory);
            AssertNoStaticLeak();

            var pink = Color.FromRgb(255, 0, 128);
            var cyan = Color.FromRgb(0, 200, 255);

            Render(directory, "01-static-perkey", AuraMode.AuraStatic, AuraBacklightType.PerKey, pink, numpad: false);
            Render(directory, "02-rainbow-perkey", AuraMode.AuraRainbow, AuraBacklightType.PerKey, pink, numpad: false);
            Render(directory, "03-rainbow-numpad", AuraMode.AuraRainbow, AuraBacklightType.PerKey, pink, numpad: true);
            Render(directory, "04-comet-perkey", AuraMode.Comet, AuraBacklightType.PerKey, cyan, numpad: false);
            Render(directory, "05-ripple-perkey", AuraMode.Ripple, AuraBacklightType.PerKey, cyan, numpad: false);
            Render(directory, "06-star-perkey", AuraMode.Star, AuraBacklightType.PerKey, cyan, numpad: false);
            Render(directory, "07-rainbow-fourzone", AuraMode.AuraRainbow, AuraBacklightType.MultiZone, pink, numpad: false);
            Render(directory, "08-breathe-singlezone", AuraMode.AuraBreathe, AuraBacklightType.SingleZone, pink, numpad: false);
            Render(directory, "09-static-iso-lightbar", AuraMode.AuraStatic, AuraBacklightType.PerKey, cyan, numpad: false, iso: true, lightbar: true);
            Render(directory, "10-backlight-off", AuraMode.AuraStatic, AuraBacklightType.PerKey, pink, numpad: false, brightness: 0);

            // One capture at the size the control is actually given on the page, so the
            // preview can be judged at the resolution it will really be seen at rather
            // than only at the flattering one.
            Render(directory, "11-rainbow-actual-size", AuraMode.AuraRainbow, AuraBacklightType.PerKey, pink, numpad: false, scale: 1.0);

            RenderThisMachine(directory);

            Console.WriteLine("keyboard preview frames written to " + Path.GetFullPath(directory));
            app.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    /// <summary>
    /// The parts of the layout a screenshot cannot check: that no key escapes the
    /// board, that the numpad adds what it should, that the ISO Enter really is two
    /// rows tall, and that zones climb left to right the way the driver's own zone
    /// tables do.
    /// </summary>
    private static void AssertLayout()
    {
        var plain = LaptopKeyboardLayout.Build(new LaptopKeyboardOptions(Numpad: false, Hotkeys: LaptopHotkeyStyle.MediaKeys));
        var numpad = LaptopKeyboardLayout.Build(new LaptopKeyboardOptions(Numpad: true, Hotkeys: LaptopHotkeyStyle.MediaKeys));

        if (numpad.Keys.Count <= plain.Keys.Count)
            throw new InvalidOperationException("The numpad layout did not add any keys.");
        if (numpad.Width <= plain.Width)
            throw new InvalidOperationException("The numpad layout is not wider than the plain one.");

        foreach (LaptopKey key in plain.Keys)
        {
            if (key.X < -0.001 || key.X + key.Width > plain.Width + 0.001)
                throw new InvalidOperationException($"Key {key.Label} at {key.X} runs outside the keyboard.");
            if (key.Zone is < 0 or > 7)
                throw new InvalidOperationException($"Key {key.Label} has zone {key.Zone}.");
        }

        int leftZone = plain.Keys.First(key => key.Label == "Esc").Zone;
        int rightZone = plain.Keys.First(key => key.Label == "Bksp").Zone;
        if (leftZone != 0 || rightZone != 3)
            throw new InvalidOperationException($"Zones do not span the keyboard: Esc={leftZone}, Bksp={rightZone}.");

        var iso = LaptopKeyboardLayout.Build(new LaptopKeyboardOptions(Iso: true, Hotkeys: LaptopHotkeyStyle.MediaKeys));
        if (iso.Keys.Count(key => key.Label == "Enter") != 1)
            throw new InvalidOperationException("The ISO layout should have exactly one Enter key.");
        if (!iso.Keys.Any(key => key.Label == "Enter" && key.Height > 1.5))
            throw new InvalidOperationException("The ISO Enter key is not two rows tall.");

        Console.WriteLine($"layout: {plain.Keys.Count} keys plain, {numpad.Keys.Count} with numpad, "
            + $"{plain.Width:0.##} x {plain.Height:0.##} units");
    }

    /// <summary>
    /// Lays the control out and captures one frame.
    ///
    /// No window is involved. The preview works out its colours from the time elapsed
    /// since it was constructed, and <see cref="RenderTargetBitmap.Render"/> drives
    /// OnRender itself, so waiting before the capture is all it takes to photograph a
    /// travelling effect part way through - the composition clock only decides how
    /// often the real control repaints, not what it paints.
    /// </summary>
    private static RenderTargetBitmap Capture(KeyboardPreview preview, double width, int settleMilliseconds, double scale = 2.0)
    {
        preview.Measure(new Size(width, double.PositiveInfinity));
        preview.Arrange(new Rect(0, 0, width, preview.DesiredSize.Height));
        preview.UpdateLayout();

        Thread.Sleep(settleMilliseconds);
        preview.InvalidateVisual();

        int pixelWidth = (int)Math.Ceiling(preview.ActualWidth * scale);
        int pixelHeight = (int)Math.Ceiling(preview.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // The board is drawn on the page's sunken surface in the application; paint the
        // same ground here rather than judging the keys against transparency.
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
        string directory, string name, AuraMode mode, AuraBacklightType backlight, Color color,
        bool numpad, bool iso = false, bool lightbar = false, int brightness = 3, double scale = 2.0)
    {
        var preview = new KeyboardPreview
        {
            Mode = (int)mode,
            Speed = (int)AuraSpeed.Normal,
            PrimaryColor = color,
            SecondaryColor = Color.FromRgb(0, 0, 0),
            Brightness = brightness,
            BacklightType = (int)backlight,
            Layout = new LaptopKeyboardOptions(Numpad: numpad, Iso: iso, Lightbar: lightbar,
                Hotkeys: LaptopHotkeyStyle.MediaKeys),
        };

        RenderTargetBitmap bitmap = Capture(preview, numpad ? 1000 : 800, 700, scale);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(stream);

        preview.Dispose();
    }

    /// <summary>
    /// An animated effect has to actually differ from one moment to the next. A
    /// preview that quietly froze at its first frame would still produce a perfectly
    /// plausible screenshot.
    /// </summary>
    private static void AssertAnimates()
    {
        var preview = new KeyboardPreview
        {
            Mode = (int)AuraMode.AuraRainbow,
            Speed = (int)AuraSpeed.Fast,
            PrimaryColor = Color.FromRgb(255, 0, 128),
            Brightness = 3,
            BacklightType = (int)AuraBacklightType.PerKey,
            Layout = new LaptopKeyboardOptions(Hotkeys: LaptopHotkeyStyle.MediaKeys),
        };

        byte[] first = PixelsOf(Capture(preview, 800, 0));
        byte[] second = PixelsOf(Capture(preview, 800, 600));
        preview.Dispose();

        if (first.AsSpan().SequenceEqual(second))
            throw new InvalidOperationException("The preview drew the same frame twice; the effect is not moving.");

        var stat = new KeyboardPreview
        {
            Mode = (int)AuraMode.AuraStatic,
            PrimaryColor = Color.FromRgb(255, 0, 128),
            Brightness = 3,
            BacklightType = (int)AuraBacklightType.PerKey,
            Layout = new LaptopKeyboardOptions(Hotkeys: LaptopHotkeyStyle.MediaKeys),
        };

        byte[] staticFirst = PixelsOf(Capture(stat, 800, 0));
        byte[] staticSecond = PixelsOf(Capture(stat, 800, 400));
        stat.Dispose();

        if (!staticFirst.AsSpan().SequenceEqual(staticSecond))
            throw new InvalidOperationException("A static effect changed between frames.");

        Console.WriteLine("animation: rainbow frames differ, static frames identical");
    }

    /// <summary>
    /// Builds the real Lighting page against a stub service and renders it.
    ///
    /// The preview is wired to the page through a dozen bindings, and a WPF binding
    /// that names a property wrongly fails silently - the control simply keeps its
    /// default and the page still looks plausible. This catches that: it renders the
    /// page and then asserts the preview actually received the view model's values.
    /// </summary>
    private static void AssertPageWiring(string directory)
    {
        var viewModel = new Arsenal.UI.ViewModels.LightingViewModel(new StubLightingService());
        var page = new Arsenal.UI.Views.Pages.LightingPage(viewModel);

        page.Measure(new Size(1080, 2400));
        page.Arrange(new Rect(0, 0, 1080, 2400));
        page.UpdateLayout();

        var preview = FindPreview(page)
            ?? throw new InvalidOperationException("The Lighting page does not contain a keyboard preview.");

        if (preview.Mode != viewModel.SelectedMode)
            throw new InvalidOperationException($"Mode binding did not arrive: {preview.Mode} vs {viewModel.SelectedMode}.");
        if (preview.Brightness != viewModel.Brightness)
            throw new InvalidOperationException($"Brightness binding did not arrive: {preview.Brightness} vs {viewModel.Brightness}.");
        if (preview.PrimaryColor != viewModel.SelectedColor)
            throw new InvalidOperationException($"Colour binding did not arrive: {preview.PrimaryColor} vs {viewModel.SelectedColor}.");
        if (preview.BacklightType != viewModel.BacklightZoneType)
            throw new InvalidOperationException($"Backlight type binding did not arrive: {preview.BacklightType} vs {viewModel.BacklightZoneType}.");
        if (preview.Layout != viewModel.PreviewLayout)
            throw new InvalidOperationException("The layout binding did not arrive.");

        // Changing the view model has to move the preview, which is the whole point of
        // the row sitting above the effect list.
        viewModel.SelectedMode = (int)AuraMode.AuraRainbow;
        page.UpdateLayout();
        if (preview.Mode != (int)AuraMode.AuraRainbow)
            throw new InvalidOperationException("Selecting an effect did not reach the preview.");

        var bitmap = new RenderTargetBitmap(1080, 1200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(directory, "00-lighting-page.png"))) encoder.Save(stream);

        Console.WriteLine("page wiring: every preview binding arrived, and selecting an effect moves it");
        Console.WriteLine("detected shape: " + viewModel.PreviewLayout + " chassis=" + viewModel.PreviewChassis);
    }

    private static KeyboardPreview? FindPreview(DependencyObject root)
    {
        if (root is KeyboardPreview found) return found;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            KeyboardPreview? child = FindPreview(VisualTreeHelper.GetChild(root, i));
            if (child is not null) return child;
        }
        return null;
    }

    /// <summary>Reports a per-key backlight with no hardware behind it.</summary>
    private sealed class StubLightingService : Arsenal.Application.Services.Contracts.ILightingService
    {
        public int Brightness => 3;
        public int CurrentMode => (int)AuraMode.AuraBreathe;
        public bool HasAnimeMatrix => false;
        public bool HasSlash => false;
        public bool HasKeyboardColor => true;
        public bool HasAuraEffects => true;
        public int BacklightZoneType => (int)AuraBacklightType.PerKey;
        public bool HasLightbar => true;
        public int MatrixBrightness => 0;
        public int MatrixMode => 0;

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

    /// <summary>
    /// The preview subscribes to a static event for the application-driven modes. A
    /// static event is the classic way to keep a control - and the page behind it -
    /// alive forever, so this opens one in that state, disposes it, and checks the
    /// subscription really went away.
    /// </summary>
    private static void AssertNoStaticLeak()
    {
        var field = typeof(Aura).GetField(nameof(Aura.ColorsApplied),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Aura.ColorsApplied is not a field-backed event any more.");

        int Handlers() => ((Delegate?)field.GetValue(null))?.GetInvocationList().Length ?? 0;

        int before = Handlers();

        var preview = new KeyboardPreview
        {
            Mode = (int)AuraMode.HEATMAP,
            Brightness = 3,
            BacklightType = (int)AuraBacklightType.PerKey,
            Layout = new LaptopKeyboardOptions(Hotkeys: LaptopHotkeyStyle.MediaKeys),
        };
        Capture(preview, 800, 0);

        if (Handlers() != before + 1)
            throw new InvalidOperationException("An application-driven mode did not subscribe for live colours.");

        preview.Dispose();

        if (Handlers() != before)
            throw new InvalidOperationException("Disposing the preview left it subscribed to Aura.ColorsApplied.");

        Console.WriteLine("lifetime: live-colour subscription is added on demand and released on dispose");
    }

    /// <summary>
    /// The chassis table is the part of this feature with no runtime signal behind it,
    /// so the lookup itself is worth pinning down: that each family resolves to the
    /// keyboard it actually has, that a longer model number cannot be swallowed by a
    /// shorter prefix, and that an unknown model falls back rather than guessing.
    /// </summary>
    /// <summary>
    /// Renders the layout exactly as the catalog resolves it for the machine this is
    /// running on, which is the only entry in the table that can actually be checked
    /// against a real keyboard.
    /// </summary>
    private static void RenderThisMachine(string directory)
    {
        LaptopKeyboardMatch match = LaptopKeyboardLayout.Detect();

        var preview = new KeyboardPreview
        {
            Mode = (int)AuraMode.AuraStatic,
            Speed = (int)AuraSpeed.Normal,
            PrimaryColor = Color.FromRgb(255, 255, 255),
            SecondaryColor = Color.FromRgb(0, 0, 0),
            Brightness = 3,
            BacklightType = (int)AuraBacklightType.PerKey,
            Layout = match.Options,
        };

        RenderTargetBitmap bitmap = Capture(preview, match.Options.Numpad ? 1000 : 800, 100);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(directory, "12-this-machine.png"))) encoder.Save(stream);
        preview.Dispose();

        Console.WriteLine($"this machine drawn as: {(match.ChassisName.Length > 0 ? match.ChassisName : "generic")}");
    }

    private static void AssertCatalog()
    {
        (string Model, bool Numpad, LaptopHotkeyStyle Hotkeys, LaptopArrowStyle Arrows, string Chassis)[] cases =
        {
            ("GU605MI", false, LaptopHotkeyStyle.MacroKeys, LaptopArrowStyle.HalfHeightCluster, "ROG Zephyrus G16"),
            ("GA403UV", false, LaptopHotkeyStyle.MacroKeys, LaptopArrowStyle.HalfHeightCluster, "ROG Zephyrus G14"),
            ("GX650PY", true,  LaptopHotkeyStyle.MacroKeys, LaptopArrowStyle.HalfHeightCluster, "ROG Zephyrus Duo"),
            ("G634JYR", false, LaptopHotkeyStyle.MediaKeys, LaptopArrowStyle.Tucked, "ROG Strix 15/16"),
            ("G713PV",  true,  LaptopHotkeyStyle.MediaKeys, LaptopArrowStyle.Tucked, "ROG Strix 17/18"),
            ("G834JZR", true,  LaptopHotkeyStyle.MediaKeys, LaptopArrowStyle.Tucked, "ROG Strix 17/18"),
            ("FA507NV", true,  LaptopHotkeyStyle.None, LaptopArrowStyle.Tucked, "TUF Gaming 15"),
            ("FX707VI", true,  LaptopHotkeyStyle.None, LaptopArrowStyle.Tucked, "TUF Gaming 17"),
            ("GV301QH", false, LaptopHotkeyStyle.None, LaptopArrowStyle.HalfHeightCluster, "ROG Flow X13"),
            ("GZ302EA", false, LaptopHotkeyStyle.None, LaptopArrowStyle.HalfHeightCluster, "ROG Flow Z13"),
        };

        foreach ((string model, bool numpad, LaptopHotkeyStyle hotkeys, LaptopArrowStyle arrows, string chassis) in cases)
        {
            LaptopKeyboardMatch match = LaptopKeyboardCatalog.Resolve(model, iso: false, lightbar: false);

            if (match.ChassisName != chassis)
                throw new InvalidOperationException($"{model} matched {match.ChassisName}, expected {chassis}.");
            if (match.Options.Numpad != numpad)
                throw new InvalidOperationException($"{model} number pad is {match.Options.Numpad}, expected {numpad}.");
            if (match.Options.Hotkeys != hotkeys)
                throw new InvalidOperationException($"{model} hotkeys are {match.Options.Hotkeys}, expected {hotkeys}.");
            if (match.Options.Arrows != arrows)
                throw new InvalidOperationException($"{model} arrows are {match.Options.Arrows}, expected {arrows}.");
            if (match.Confidence != LaptopKeyboardConfidence.Chassis)
                throw new InvalidOperationException($"{model} resolved with confidence {match.Confidence}.");
        }

        // Something ASUS has not made: it must fall back rather than pick a neighbour.
        LaptopKeyboardMatch unknown = LaptopKeyboardCatalog.Resolve("ZZ999XX", iso: false, lightbar: false);
        if (unknown.Confidence != LaptopKeyboardConfidence.Generic || unknown.ChassisName.Length != 0)
            throw new InvalidOperationException("An unknown model did not fall back to the generic shape.");

        // The hardware supplies these two, and the table must not overwrite them.
        LaptopKeyboardMatch hardware = LaptopKeyboardCatalog.Resolve("G713PV", iso: true, lightbar: true);
        if (!hardware.Options.Iso || !hardware.Options.Lightbar)
            throw new InvalidOperationException("The chassis table discarded what the hardware reported.");

        Console.WriteLine($"catalog: {cases.Length} chassis resolve correctly, unknown models fall back");
        Console.WriteLine($"this machine: model={LaptopKeyboardCatalog.ModelKey()} "
            + $"series={AsusMachineInfo.Series} recorded-backlight={AsusMachineInfo.RecordedBacklightType}");

        LaptopKeyboardMatch here = LaptopKeyboardLayout.Detect();
        Console.WriteLine($"resolved here: chassis={(here.ChassisName.Length > 0 ? here.ChassisName : "(none)")} "
            + $"numpad={here.Options.Numpad} hotkeys={here.Options.Hotkeys} arrows={here.Options.Arrows} "
            + $"iso={here.Options.Iso} lightbar={here.Options.Lightbar}");
    }

    private static byte[] PixelsOf(RenderTargetBitmap bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
