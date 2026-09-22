using Arsenal.UI;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using Arsenal.UI.Views.Windows;
using Arsenal.UI.Controls;
using Arsenal.Display;
using Arsenal.Mode;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
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
            File.WriteAllText(configPath, "{\"theme\":1,\"accent_source\":1,\"accent_color\":\"#D83B01\",\"start_minimized\":1,\"subpages\":1}");

            var application = new App();
            application.InitializeComponent();
            // Several assertions open and close off-screen windows. Keep the dispatcher
            // alive between them rather than letting the first close shut the test app
            // down before later controls can ever receive Loaded.
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();
            AssertSubpageHasNoHeadingOfItsOwn();
            AssertCustomPerformancePlans();

            AssertAccentResources(application, expectedSubtleAlpha: 0x2E);
            Color darkAccent = ResourceColor(application, "AccentPrimary");
            Assert(darkAccent.R > darkAccent.B, "Custom orange did not replace the blue-biased accent.");

            var viewModel = new SettingsViewModel();
            Assert(viewModel.StartMinimized, "Settings did not restore the start-minimised preference.");
            Assert(viewModel.SelectedAccentSource == 1, "Settings did not restore the custom accent source.");
            Assert(viewModel.CustomAccentHex == "#D83B01", "Settings did not restore the custom accent value.");
            var page = new SettingsPage(viewModel);
            Assert(ReferenceEquals(page.DataContext, viewModel), "Settings accent editor failed to load.");
            var devicesPage = new DevicesPage(null!);
            Assert(devicesPage is not null, "Devices page with keyboard controls failed to load.");
            var lightingPage = new LightingPage(null!);
            Assert(lightingPage is not null, "Lighting page with AniMe Matrix controls failed to load.");
            AssertQuickPanelSliderGeometry();
            if (args.FirstOrDefault() is { Length: > 0 } output && !output.StartsWith("--", StringComparison.Ordinal))
                RenderQuickPanelPreview(output);
            AssertColorPipelineIsolation();
            AssertOpaqueByDefault(application);

            AppConfig.Set("theme", 2);
            App.ApplyConfiguredTheme();
            AssertAccentResources(application, expectedSubtleAlpha: 0x20);

            AppConfig.Set("accent_source", 0);
            App.ApplyConfiguredAccent();
            AssertAccentResources(application, expectedSubtleAlpha: 0x20);

            AppConfig.Set("theme", (int)Arsenal.UI.Theming.AppTheme.Arsenal);
            App.ApplyConfiguredTheme();
            AssertGroupHeaderContentSurvivesTheTheme();

            AppConfig.Flush();
            application.Shutdown();
            Console.WriteLine("Theme smoke passed: accent themes, Settings editor, Quick Panel slider geometry and themed group header content.");
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
        var panel = new QuickPanelWindow(null!, null!);
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

        // Reported synchronously, on the frame the panel was placed on. Anything queued
        // behind this is racing whatever else the host application decides to do, and a
        // diagnostic that sometimes prints nothing is worse than no diagnostic at all.
        ReportPanelPlacement(panel);

        // Pump only this diagnostic window instead of constructing the full hardware
        // application host. The timeout closes it and releases the nested frame.
        Dispatcher.PushFrame(frame);
        return 0;
    }

    /// <summary>
    /// Prints where the Quick Panel's card came to rest, and which way it travels to
    /// get there.
    /// </summary>
    /// <remarks>
    /// Two numbers decide whether the panel follows the taskbar. The gaps say which
    /// corner of the work area the card landed in - the two edges it is seated against
    /// read as the same small number, the other two as hundreds of pixels. The entrance
    /// offset says which axis carries the motion and which way it points, which a
    /// screenshot of the settled panel cannot show: a bar along the bottom must start
    /// the card below its resting place and nowhere to either side, a bar down the left
    /// must start it to the left and no lower.
    /// </remarks>
    private static void ReportPanelPlacement(QuickPanelWindow panel)
    {
        if (panel.FindName("PanelChrome") is not System.Windows.FrameworkElement chrome) return;
        var transform = panel.FindName("PanelTransform") as TranslateTransform;

        Console.WriteLine($"Quick Panel entrance offset: ({transform?.X ?? 0:0.#},{transform?.Y ?? 0:0.#})");

        // Measured at rest. The entrance offset is a live render transform and
        // PointToScreen carries it, so it is taken out and put back rather than
        // reported as part of the card's seated geometry.
        double offsetX = transform?.X ?? 0;
        double offsetY = transform?.Y ?? 0;
        if (transform is not null) { transform.X = 0; transform.Y = 0; }
        panel.UpdateLayout();

        System.Windows.Point cardTopLeft = chrome.PointToScreen(new System.Windows.Point(0, 0));
        System.Windows.Point cardBottomRight = chrome.PointToScreen(
            new System.Windows.Point(chrome.ActualWidth, chrome.ActualHeight));

        if (transform is not null) { transform.X = offsetX; transform.Y = offsetY; }

        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Console.WriteLine(
            $"Quick Panel physical gaps: left={cardTopLeft.X - area.Left:0.###}px, " +
            $"top={cardTopLeft.Y - area.Top:0.###}px, " +
            $"right={area.Right - cardBottomRight.X:0.###}px, " +
            $"bottom={area.Bottom - cardBottomRight.Y:0.###}px");
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
        var panel = new QuickPanelWindow(null!, null!);
        var sliders = Descendants(panel).OfType<ValueSlider>().ToArray();
        ValueSlider oled = sliders.Single(slider => slider.Header == "OLED dimming");

        Assert(oled.HeaderWidth == 100, "OLED dimming did not receive the expanded label column.");
        AssertQuickPanelSliderRow(oled, "0%", "100%");
        Assert(panel.FindName("PerformanceModeRow") is System.Windows.Controls.Primitives.UniformGrid { Children.Count: 4 },
            "Quick Panel performance row does not contain three shortcuts and Custom.");
        Assert(panel.FindName("GpuModeRow") is System.Windows.Controls.Primitives.UniformGrid { Children.Count: 4 },
            "Quick Panel GPU row does not expose all four GPU modes.");
        Assert(panel.FindName("CustomPerformanceButton") is System.Windows.Controls.Button,
            "Quick Panel is missing the Custom entry point.");
        Assert(panel.FindName("MainView") is ScrollViewer
            {
                VerticalScrollBarVisibility: ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility: ScrollBarVisibility.Disabled
            },
            "Quick Panel main view cannot scroll when a monitor has a short work area.");
        AssertQuickPanelShortWorkArea(panel);
        Assert(QuickPanelViewModel.TilesPerPage == 6,
            "Quick Panel quick settings are not limited to three two-column rows per page.");
        Assert(Descendants((DependencyObject)panel.FindName("PerformanceModeRow")).OfType<SymbolIcon>().Count() == 4,
            "Quick Panel performance choices do not all have icons.");
        Assert(Descendants((DependencyObject)panel.FindName("GpuModeRow")).OfType<SymbolIcon>().Count() == 4,
            "Quick Panel GPU choices do not all have icons.");
        Assert(new[] { "QuickPlanSelector1", "QuickPlanSelector2", "QuickPlanSelector3" }
                .All(name => panel.FindName(name) is System.Windows.Controls.ComboBox),
            "Quick Panel performance shortcut editor does not contain three plan selectors.");
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

    private static void AssertQuickPanelShortWorkArea(QuickPanelWindow panel)
    {
        const double workAreaHeight = 577;
        FrameworkElement root = (FrameworkElement)panel.Content;
        root.Measure(new System.Windows.Size(452, 940));
        root.Arrange(new System.Windows.Rect(0, 0, 452, Math.Max(1, root.DesiredSize.Height)));
        root.UpdateLayout();

        typeof(QuickPanelWindow).GetField("_anchorHeight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, workAreaHeight);
        typeof(QuickPanelWindow).GetMethod("LockNativeViewport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);

        var main = (ScrollViewer)panel.FindName("MainView");
        Assert(panel.Height < workAreaHeight,
            $"Quick Panel viewport remained {panel.Height:F0}px tall in a {workAreaHeight:F0}px work area.");
        Assert(!double.IsInfinity(main.MaxHeight) && main.MaxHeight < workAreaHeight,
            "Quick Panel main view was not capped for a short work area.");
        Assert(main.VerticalOffset == 0,
            "Quick Panel opened a short viewport below its header.");
    }

    /// <summary>
    /// The compact panel carries no current-value readout and no scale under the
    /// track: the row is the label, the lowest value, the track, and the highest
    /// value. Checking the applied template is the only way to see that, since the
    /// pieces that were removed were removed from the template itself.
    /// </summary>
    private static void AssertQuickPanelSliderRow(ValueSlider slider, string minimum, string maximum)
    {
        slider.ApplyTemplate();
        var row = (System.Windows.Controls.Grid)VisualTreeHelper.GetChild(slider, 0);
        var labels = row.Children.OfType<System.Windows.Controls.TextBlock>().ToArray();

        Assert(labels.Length == 3,
            "A Quick Panel slider row is not exactly its label, its lowest value and its highest value.");
        Assert(labels[0].Text == slider.Header, "The Quick Panel slider label is not first in its row.");
        Assert(labels[1].Text == minimum && slider.MinimumText == minimum,
            $"The Quick Panel slider does not open its track with {minimum}.");
        Assert(labels[2].Text == maximum && slider.MaximumText == maximum,
            $"The Quick Panel slider does not close its track with {maximum}.");
        Assert(row.Children.OfType<System.Windows.Controls.Slider>().Count() == 1,
            "The Quick Panel slider row lost its track.");
        Assert(!row.Children.OfType<SliderScale>().Any(),
            "The Quick Panel slider still draws the scale under its track.");
        Assert(labels[0].Margin.Right >= 12,
            "The Quick Panel slider label crowds the lowest value beside it.");
    }

    private static void AssertCustomPerformancePlans()
    {
        for (int mode = 3; mode < Modes.MaxModes; mode++) Modes.Remove(mode);
        Modes.SetCurrent(0);
        AppConfig.SetMode("limit_total", 47);

        int studio = Modes.Add("  Studio  ");
        Assert(studio == 3 && Modes.GetName(studio) == "Studio",
            "The first custom plan was not created and trimmed correctly.");
        Assert(Modes.GetBase(studio) == 0 && AppConfig.Get("limit_total_3") == 47,
            "A custom plan did not inherit the active plan's base mode and power settings.");

        int automatic = Modes.Add();
        Assert(automatic == 4 && Modes.GetName(automatic) == "Custom Plan 1",
            "Default custom plan naming did not start at Custom Plan 1.");
        Assert(Modes.Rename(automatic, "Travel") && Modes.GetName(automatic) == "Travel",
            "A custom plan name did not persist.");
        Modes.Remove(automatic);
        Assert(!Modes.Exists(automatic), "A deleted custom plan remained in the mode list.");

        Modes.Remove(studio);
        Modes.SetCurrent(0);
    }

    private static void RenderQuickPanelPreview(string output)
    {
        var panel = new QuickPanelWindow(null!, null!)
        {
            DataContext = new QuickPanelPreview()
        };
        FrameworkElement root = (FrameworkElement)panel.Content;
        root.Measure(new System.Windows.Size(452, 940));
        root.Arrange(new System.Windows.Rect(0, 0, 452, Math.Min(940, Math.Max(1, root.DesiredSize.Height))));
        root.UpdateLayout();
        typeof(QuickPanelWindow).GetMethod("FitTilePageViewport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        root.Measure(new System.Windows.Size(452, 940));
        root.Arrange(new System.Windows.Rect(0, 0, 452, Math.Min(940, Math.Max(1, root.DesiredSize.Height))));
        root.UpdateLayout();

        int width = 452;
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using FileStream stream = File.Create(output);
        encoder.Save(stream);
        panel.Close();
    }

    private sealed class QuickPanelPreview
    {
        public PerformancePlanItem QuickPlan1 { get; set; } = new(new Arsenal.Application.Models.PerformancePlanInfo(2, "Silent", 2, false));
        public PerformancePlanItem QuickPlan2 { get; set; } = new(new Arsenal.Application.Models.PerformancePlanInfo(0, "Balanced", 0, false));
        public PerformancePlanItem QuickPlan3 { get; set; } = new(new Arsenal.Application.Models.PerformancePlanInfo(3, "Creator", 0, true));
        public bool IsQuickPlan1Selected => false;
        public bool IsQuickPlan2Selected => true;
        public bool IsQuickPlan3Selected => false;
        public int SelectedGpuMode => 1;
        public bool IsEcoSupported => true;
        public bool IsMuxSupported => true;
        public int RefreshRate => 240;
        public int PanelBrightness { get; set; } = 68;
        public bool IsOledPanel => true;
        public bool IsOledDimmingAvailable => true;
        public int OledDimming { get; set; } = 82;
        public int ChargeLimitMinimum => 40;
        public int ChargeLimit { get; set; } = 80;
        public int KeyboardBrightness { get; set; } = 2;
        public IEnumerable<PerformancePlanItem> PerformancePlans => new[] { QuickPlan1, QuickPlan2, QuickPlan3 };
        public bool HasTilePages => false;
        public bool IsEditingTiles => false;
        public bool IsDetailOpen => false;
        public IEnumerable<QuickTileSlot> Tiles { get; } = CreateTiles();

        private static QuickTileSlot[] CreateTiles()
        {
            (string key, string state)[] choices =
            {
                ("full_charge", "Stops at 100%"),
                ("performance", "Balanced"),
                ("gpu", "Standard"),
                ("refresh", "240 Hz"),
                ("keyboard", "2"),
                ("overlay", "On")
            };

            return choices.Select(choice => new QuickTileSlot(QuickTileCatalog.Find(choice.key)!, null)
            {
                State = choice.state,
                IsChecked = true
            }).ToArray();
        }
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

    /// <summary>
    /// A group's header content is still on screen once a theme has replaced the
    /// template that draws it.
    /// </summary>
    /// <remarks>
    /// The Arsenal theme brings its own SettingsGroup template, and the first version
    /// of it had no presenter for HeaderContent at all. Nothing failed: the property
    /// was set, the binding resolved, and the buttons were simply never realised, so
    /// Home lost its Edit controls and Reset with no error anywhere. A template that
    /// drops a property is invisible to the compiler and to every test that only looks
    /// at colours, which is what this checks instead.
    ///
    /// <para>Measured and arranged rather than merely constructed. An unrealised
    /// template has no visual children, so a tree walk over a control that was never
    /// laid out passes whether the presenter exists or not.</para>
    /// </remarks>
    private static void AssertGroupHeaderContentSurvivesTheTheme()
    {
        var marker = new Wpf.Ui.Controls.Button { Content = "Edit controls", Width = 120, Height = 32 };
        var group = new SettingsGroup
        {
            Header = "Controls",
            Description = "Your selected controls.",
            AlwaysOpen = true,
            HeaderContent = marker,
            Width = 800,
        };

        var host = new Border { Child = group, Width = 800 };
        host.Measure(new System.Windows.Size(800, 2000));
        host.Arrange(new System.Windows.Rect(0, 0, 800, 2000));
        host.UpdateLayout();

        Assert(VisualDescendants(host).Contains(marker),
            "The Arsenal theme's group template does not present HeaderContent, so a page's header buttons never appear.");
        Assert(marker.ActualWidth > 0 && marker.ActualHeight > 0,
            "Group header content was realised but laid out with no size.");

        // The far commoner case: no header content at all. The presenter has to take
        // itself out of the layout rather than leave its margin behind, or every other
        // group in the application gains a gap it did not ask for.
        var plain = new SettingsGroup { Header = "Controls", AlwaysOpen = true, Width = 800 };
        var plainHost = new Border { Child = plain, Width = 800 };
        plainHost.Measure(new System.Windows.Size(800, 2000));
        plainHost.Arrange(new System.Windows.Rect(0, 0, 800, 2000));
        plainHost.UpdateLayout();

        ContentPresenter? empty = VisualDescendants(plainHost)
            .OfType<ContentPresenter>()
            .FirstOrDefault(presenter => presenter.Name == "HeaderContentHost");
        Assert(empty is null || empty.Visibility != Visibility.Visible,
            "A group with no header content still reserves room for it.");
    }

    /// <summary>
    /// A drilled-into group states nothing about itself, and says what it is through
    /// the event the title bar listens to.
    /// </summary>
    /// <remarks>
    /// The subpage used to carry its own name and its own back button. Both are gone:
    /// the name is in the title bar after the page's, and the way back is the bar's
    /// arrow. If the back bar ever returns, the window grows a second back control two
    /// inches below the first and the same words appear twice, which is exactly the
    /// state this is here to prevent.
    /// </remarks>
    private static void AssertSubpageHasNoHeadingOfItsOwn()
    {
        string? announced = "not raised";
        void Heard(SettingsGroup? group) => announced = group?.Header;
        SettingsGroup.OpenGroupChanged += Heard;

        try
        {
            var group = new SettingsGroup
            {
                Header = "CPU power & thermals",
                Description = "What the processor is allowed to draw.",
                DrillIn = true,
                Width = 800,
            };
            group.Items.Add(new SettingsRow { Header = "Sustained power" });

            var host = new Border { Child = group, Width = 800 };
            host.Measure(new System.Windows.Size(800, 2000));
            host.Arrange(new System.Windows.Rect(0, 0, 800, 2000));
            host.UpdateLayout();

            // The harness does not enter Application.Run, so drive the control's real
            // routed lifetime explicitly after its production template is realised.
            group.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, group));

            group.Open();
            host.UpdateLayout();

            Assert(group.IsOpen,
                $"The group did not open, so nothing below is being tested. " +
                $"Loaded={group.IsLoaded}, DrillIn={group.DrillIn}, Subpages={SettingsGroup.SubpagesEnabled}, " +
                $"OpenGroupMatches={ReferenceEquals(SettingsGroup.OpenGroup, group)}.");

            Assert(announced == "CPU power & thermals",
                "Opening a subpage did not announce itself, so the title bar cannot name it.");

            // The heading text must not also be on the page. A back bar or a repeated
            // title would both show up as the header string inside the group.
            //
            // Effective visibility, not just presence: the closed card is still in the
            // tree while the group is open, collapsed, carrying the same header. IsVisible
            // cannot answer this because nothing here is connected to a window, so the
            // chain of Visibility up to the host is walked instead.
            bool repeatsItsName = VisualDescendants(host)
                .OfType<TextBlock>()
                .Where(text => string.Equals(text.Text, "CPU power & thermals", StringComparison.Ordinal))
                .Any(text => IsShown(text, host));
            Assert(!repeatsItsName, "An open subpage is still printing its own name on the page.");

            Assert(group.Template.FindName("PART_Back", group) is null,
                "The subpage back button is back; the title bar arrow is the only way out now.");

            SettingsGroup.Close();
            Assert(announced is null, "Closing a subpage did not announce it, so the title bar keeps the old path.");

            group.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent, group));
        }
        finally
        {
            SettingsGroup.OpenGroupChanged -= Heard;
            SettingsGroup.Close();
        }
    }

    /// <summary>Lets queued layout and Loaded work run before the next assertion.</summary>
    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

    /// <summary>Whether an element and every ancestor up to <paramref name="root"/> are visible.</summary>
    private static bool IsShown(DependencyObject element, DependencyObject root)
    {
        DependencyObject? node = element;
        while (node is not null)
        {
            if (node is UIElement visual && visual.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(node, root)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return true;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child))
                yield return descendant;
        }
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
