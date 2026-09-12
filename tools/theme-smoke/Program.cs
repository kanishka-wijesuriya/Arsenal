using Arsenal.UI;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using Arsenal.UI.Views.Windows;
using Arsenal.UI.Controls;
using Arsenal.Display;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace ThemeSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(value => value.Equals("--quick-test", StringComparison.OrdinalIgnoreCase)))
            return RunQuickPanelProbe();

        try
        {
            // AppConfig deliberately prefers a config beside the executable. That gives
            // this smoke test a completely isolated settings store and guarantees it
            // never reads or changes the user's real Arsenal preferences.
            string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            File.WriteAllText(configPath, "{\"theme\":1,\"accent_source\":1,\"accent_color\":\"#D83B01\"}");

            var application = new App();
            application.InitializeComponent();
            App.ApplyConfiguredTheme();

            AssertAccentResources(application, expectedSubtleAlpha: 0x2E);
            Color darkAccent = ResourceColor(application, "AccentPrimary");
            Assert(darkAccent.R > darkAccent.B, "Custom orange did not replace the blue-biased accent.");

            var viewModel = new SettingsViewModel();
            Assert(viewModel.SelectedAccentSource == 1, "Settings did not restore the custom accent source.");
            Assert(viewModel.CustomAccentHex == "#D83B01", "Settings did not restore the custom accent value.");
            var page = new SettingsPage(viewModel);
            Assert(ReferenceEquals(page.DataContext, viewModel), "Settings accent editor failed to load.");
            var devicesPage = new DevicesPage(null!);
            Assert(devicesPage is not null, "Devices page with keyboard controls failed to load.");
            var lightingPage = new LightingPage(null!);
            Assert(lightingPage is not null, "Lighting page with AniMe Matrix controls failed to load.");
            AssertQuickPanelSliderGeometry();
            AssertColorPipelineIsolation();
            AssertOpaqueByDefault(application);

            AppConfig.Set("theme", 2);
            App.ApplyConfiguredTheme();
            AssertAccentResources(application, expectedSubtleAlpha: 0x20);

            AppConfig.Set("accent_source", 0);
            App.ApplyConfiguredAccent();
            AssertAccentResources(application, expectedSubtleAlpha: 0x20);

            AppConfig.Flush();
            application.Shutdown();
            Console.WriteLine("Theme smoke passed: accent themes, Settings editor and Quick Panel slider geometry.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunQuickPanelProbe()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        File.WriteAllText(configPath, "{\"theme\":1,\"accent_source\":1,\"accent_color\":\"#D83B01\"}");

        var application = new App();
        application.InitializeComponent();
        App.ApplyConfiguredTheme();

        // Placement belongs to the window and does not depend on hardware state. A null
        // view model keeps this diagnostic isolated from every ASUS service while still
        // rendering the exact production chrome, shadow, DPI and work-area anchor.
        var panel = new QuickPanelWindow(null!);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            panel.Close();
            application.Shutdown();
        };
        timeout.Start();
        var frame = new DispatcherFrame();
        panel.Closed += (_, _) => frame.Continue = false;
        panel.ShowAnimated();
        panel.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (panel.FindName("PanelChrome") is not System.Windows.FrameworkElement chrome) return;
            System.Windows.Point cardBottomRight = chrome.PointToScreen(
                new System.Windows.Point(chrome.ActualWidth, chrome.ActualHeight));
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            Console.WriteLine(
                $"Quick Panel physical gaps: right={area.Right - cardBottomRight.X:0.###}px, " +
                $"bottom={area.Bottom - cardBottomRight.Y:0.###}px");
        }), DispatcherPriority.ContextIdle);
        // Pump only this diagnostic window instead of constructing the full hardware
        // application host. The timeout closes it and releases the nested frame.
        Dispatcher.PushFrame(frame);
        return 0;
    }

    /// <summary>
    /// The config written above deliberately has no opaque_window key, which is the
    /// state of a machine nobody has visited the setting on. Mica is an opt-in from
    /// there, so both grounds have to be painted rather than left transparent for a
    /// backdrop to show through.
    /// </summary>
    private static void AssertOpaqueByDefault(App application)
    {
        Assert(App.IsOpaqueWindow, "A config with no transparency preference did not default to opaque.");

        Assert(application.Resources["AppSidebarBackground"] is SolidColorBrush { Color.A: 0xFF },
            "The navigation ground was left transparent with no backdrop behind it.");

        var settings = new SettingsViewModel();
        Assert(settings.DisableTransparency,
            "Settings showed transparency as on while the window was painting opaque.");
    }

    private static void AssertQuickPanelSliderGeometry()
    {
        var panel = new QuickPanelWindow(null!);
        var sliders = Descendants(panel).OfType<ValueSlider>().ToArray();
        ValueSlider oled = sliders.Single(slider => slider.Header == "OLED dimming");

        Assert(oled.HeaderWidth == 100, "OLED dimming did not receive the expanded label column.");
        Assert(oled.ReadoutWidth == 38, "Quick Panel retained the oversized value column.");
        Assert(oled.ReadoutMargin.Left == 6, "Quick Panel retained the oversized track-to-value gap.");
        var enabledBinding = System.Windows.Data.BindingOperations.GetBinding(oled, UIElement.IsEnabledProperty);
        Assert(enabledBinding?.Path?.Path == "IsOledDimmingAvailable",
            "Quick Panel OLED dimming is not gated by the GameVisual profile state.");
        AssertQuickPanelWheelGate(panel);

        var typeface = new Typeface(
            new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        var text = new FormattedText(
            oled.Header,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            13,
            System.Windows.Media.Brushes.Black,
            1);
        Assert(text.WidthIncludingTrailingWhitespace < oled.HeaderWidth,
            "OLED dimming still cannot fit on one line without trimming.");

        panel.Close();
    }

    private static void AssertColorPipelineIsolation()
    {
        AppConfig.Set("visual", (int)SplendidCommand.Disabled);
        Assert(!VisualControl.IsColorPipelineEnabled(),
            "Disabled GameVisual was incorrectly treated as dimming-compatible.");
        if (AppConfig.IsOLED())
        {
            int savedDimming = VisualControl.GetBrightness();
            int ignoredDimming = VisualControl.SetBrightness(savedDimming == 100 ? 25 : 100);
            Assert(ignoredDimming == savedDimming && VisualControl.GetBrightness() == savedDimming,
                "OLED dimming changed persisted state while GameVisual was disabled.");
        }

        AppConfig.Set("visual", (int)VisualControl.GetDefaultVisualMode());
        Assert(VisualControl.IsColorPipelineEnabled(),
            "An active GameVisual profile was incorrectly treated as disabled.");

        var page = new DisplayPage(null!);
        ValueSlider dimming = Descendants(page)
            .OfType<ValueSlider>()
            .Single(slider => System.Windows.Data.BindingOperations
                .GetBinding(slider, ValueSlider.ValueProperty)?.Path?.Path == "Brightness");
        var enabledBinding = System.Windows.Data.BindingOperations.GetBinding(dimming, UIElement.IsEnabledProperty);
        Assert(enabledBinding?.Path?.Path == "IsOledDimmingAvailable",
            "Display-page OLED dimming is not gated by the GameVisual profile state.");
    }

    private static void AssertQuickPanelWheelGate(QuickPanelWindow panel)
    {
        MethodInfo beginWheel = typeof(QuickPanelWindow).GetMethod(
            "TryBeginTilePageWheel",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Quick Panel wheel gate was not found.");
        MethodInfo rearmWheel = typeof(QuickPanelWindow).GetMethod(
            "TilePageWheelIdleTimer_Tick",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Quick Panel wheel re-arm was not found.");

        bool Begin(int delta) => (bool)beginWheel.Invoke(panel, new object[] { delta })!;

        Assert(Begin(-120), "The first wheel packet was delayed.");
        Assert(!Begin(-120), "One gesture was allowed to change multiple pages.");
        Assert(Begin(120), "A real direction reversal was delayed.");
        rearmWheel.Invoke(panel, new object?[] { null, EventArgs.Empty });
        Assert(Begin(120), "The pager did not re-arm after the wheel stream went idle.");
        rearmWheel.Invoke(panel, new object?[] { null, EventArgs.Empty });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject) continue;
            yield return dependencyObject;
            foreach (DependencyObject descendant in Descendants(dependencyObject))
                yield return descendant;
        }
    }

    private static void AssertAccentResources(App application, byte expectedSubtleAlpha)
    {
        Color accent = ResourceColor(application, "AccentPrimary");
        Color wpfUiAccent = ApplicationAccentColorManager.SecondaryAccent;
        Assert(accent == Color.FromRgb(wpfUiAccent.R, wpfUiAccent.G, wpfUiAccent.B),
            "Arsenal and WPF UI resolved different accent colours.");

        Color foreground = ResourceColor(application, "AccentForeground");
        Assert(foreground == Colors.Black || foreground == Colors.White,
            "Accent foreground is not a deterministic high-contrast colour.");

        Color subtle = ResourceColor(application, "AccentSubtle");
        Assert(subtle.A == expectedSubtleAlpha && subtle.R == accent.R && subtle.G == accent.G && subtle.B == accent.B,
            "Subtle accent is not derived from the active accent.");

        _ = ResourceColor(application, "SurfaceSelected");
    }

    private static Color ResourceColor(App application, string key)
        => application.Resources[key] is SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"{key} is not a solid colour brush.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
