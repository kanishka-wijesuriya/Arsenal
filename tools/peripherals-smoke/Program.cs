using Arsenal.Application.Services.Implementations;
using Arsenal.Peripherals;
using Arsenal.Peripherals.Mouse;
using Arsenal.Simulation;
using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace PeripheralsSmoke;

/// <summary>
/// Opens the Peripherals page on the simulated Strix Scar 18, which has a ROG Chakram X,
/// a ROG Azoth X and a ROG Balteus plugged in, and checks that each device is its own
/// page: the list shows one card per device, opening one leaves only that device's
/// sections, and what is changed there reaches that device.
///
/// Built Debug, which is what carries the simulator:
///
///     PeripheralsSmoke.exe [folder for screenshots]
/// </summary>
internal static class Program
{
    private const string Profile = "rog-strix-scar-18-g834jyr";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            DeviceSimulation.Start([DeviceSimulation.Argument, Profile]);
            Assert(DeviceSimulation.Profile is not null, $"No simulator profile called {Profile}.");

            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            // Without the lifetime-smoke guard the first pump runs the real startup,
            // which hands off to an installed Arsenal and shuts this process down.
            typeof(App).GetProperty(nameof(App.Services), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
                .SetValue(null, new ServiceCollection().BuildServiceProvider());
            Environment.SetEnvironmentVariable("ARSENAL_UI_LIFETIME_SMOKE_HOST", "1");

            string folder = args.FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(folder);

            // Subpages off: a device is a place to go whatever that setting says.
            bool subpages = SettingsGroup.SubpagesEnabled;
            SettingsGroup.SetSubpagesEnabled(false);

            PeripheralsProvider.DetectAllAsusMice();
            PeripheralsProvider.DetectAllAsusKeyboards();
            var service = new PeripheralService();
            var viewModel = new PeripheralsViewModel(service);
            var page = new PeripheralsPage(viewModel);

            var host = new Window
            {
                Width = 1080, Height = 3200, Left = -20000, Top = 0,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Background = (Brush)System.Windows.Application.Current.FindResource("SurfaceBase"),
                Content = page
            };
            host.Show();
            Pump();

            // ---- The list ---------------------------------------------------------
            Assert(viewModel.Peripherals.Count == 3, $"Expected three peripherals, found {viewModel.Peripherals.Count}.");
            var chakram = viewModel.Peripherals.OfType<MousePeripheralViewModel>().Single(p => !p.IsPad);
            var balteus = viewModel.Peripherals.OfType<MousePeripheralViewModel>().Single(p => p.IsPad);
            var azoth = viewModel.Peripherals.OfType<KeyboardPeripheralViewModel>().Single();
            Console.WriteLine($"  list: {string.Join(", ", viewModel.Peripherals.Select(p => $"{p.Name} ({p.Summary})"))}");

            SettingsGroup chakramPage = GroupFor(page, chakram);
            SettingsGroup azothPage = GroupFor(page, azoth);
            SettingsGroup balteusPage = GroupFor(page, balteus);
            Assert(chakramPage.DrillIn && azothPage.DrillIn && balteusPage.DrillIn, "A device is not a destination with subpages off.");
            Assert(Sections(chakramPage).All(section => !section.DrillIn), "A section inside a device became a destination.");
            Render(page, Path.Combine(folder, "peripherals-list.png"));

            // ---- Opening a mouse --------------------------------------------------
            chakramPage.Open();
            WaitFor(() => !chakram.IsReading, "the Chakram X to be read");
            Assert(azothPage.Visibility == Visibility.Collapsed && balteusPage.Visibility == Visibility.Collapsed,
                "The other devices stayed on screen inside the Chakram X.");
            SettingsGroup performance = Sections(chakramPage).Single(s => s.Header == "Performance");
            Assert(performance.Visibility == Visibility.Visible && performance.IsVisible, "The Chakram X's own sections were stood aside.");

            AsusMouse probe = service.FindMouse(chakram.Id)!;
            Assert(chakram.HasPerformance && chakram.DpiStages.Count >= 2, $"The Chakram X shows {chakram.DpiStages.Count} DPI stages (mouse: count {probe.CurrentDPIProfileCount} of {probe.DPIProfileCount()}, active {probe.DpiProfile}, settings [{string.Join(", ", probe.DpiSettings.Select(d => d?.ToString() ?? "null"))}]).");
            Assert(chakram.DpiStages.Count(stage => stage.IsActive) == 1, "Not exactly one DPI stage is marked active.");
            Assert(chakram.HasLightingZones, "The Chakram X lost its lighting zones.");
            Console.WriteLine($"  chakram: {chakram.DpiStages.Count} stages, {chakram.PollingRateOptions.Count} rates, {chakram.LightingZones.Count} zones, bindings {chakram.HasBindings} ({chakram.BindingSlots.Count})");

            // Rainbow offers speed and direction but no colour; Comet with random colour
            // on hides the colour picker.
            chakram.SelectedZone = (int)LightingZone.All;
            Pump();
            if (chakram.LightingModes.Any(m => m.Value == (int)LightingMode.Rainbow))
            {
                chakram.SelectedMode = (int)LightingMode.Rainbow;
                Assert(chakram.ShowsSpeed && chakram.ShowsDirection && !chakram.ShowsColor, "Rainbow shows the wrong settings.");
            }

            // What changes on the page reaches the mouse.
            AsusMouse mouse = service.FindMouse(chakram.Id)!;
            int target = Math.Clamp(1600, chakram.MinDpi, chakram.MaxDpi);
            chakram.ActiveDpi = target;
            WaitFor(() => mouse.DpiSettings[Math.Max(1, mouse.DpiProfile) - 1]?.DPI == (uint)target, "the DPI to reach the mouse");
            Assert(chakram.DpiStages.Single(stage => stage.IsActive).Dpi == target, "The active stage tile kept its old DPI.");

            chakram.SelectedMode = (int)LightingMode.Static;
            chakram.Brightness = 40;
            WaitFor(() => mouse.LightingSettingForZone(LightingZone.All)?.Brightness == 40, "the lighting to reach the mouse");
            AssertNoEmptyBoxes(chakramPage);
            Render(page, Path.Combine(folder, "peripherals-mouse.png"));

            // ---- Back, then the pad -----------------------------------------------
            SettingsGroup.Close();
            Pump();
            Assert(azothPage.Visibility == Visibility.Visible && balteusPage.Visibility == Visibility.Visible,
                "Leaving the Chakram X did not bring the list back.");

            Assert(!balteus.HasPerformance, "The Balteus shows a DPI and polling section.");
            Assert(!balteus.HasLightingZones, "The single-zone Balteus shows a zone row.");
            Assert(balteus.BrightnessFormat == "{0} of 4", "The Balteus's brightness reads as a percentage.");

            // ---- The keyboard -----------------------------------------------------
            azothPage.Open();
            WaitFor(() => !azoth.IsReading, "the Azoth X to be read");
            Console.WriteLine($"  azoth: {azoth.LightingModes.Count} effects, {azoth.KeyTiles.Count} keys, per-key {azoth.HasPerKeyRgb}, bindings {azoth.HasKeyBindings}, screen {azoth.HasOled}");
            Assert(azoth.HasLighting && azoth.LightingModes.Count > 0, "The Azoth X shows no lighting effects.");
            Assert(azoth.HasOled && azoth.OledModes.Count >= 2, "The Azoth X's screen section is missing.");
            if (azoth.HasPerKeyRgb)
            {
                Assert(azoth.KeyTiles.Count > 60, $"The Azoth X drew {azoth.KeyTiles.Count} keys.");
                KeyTileViewModel escape = azoth.KeyTiles.First(tile => tile.Usage != 0);
                azoth.PressKeyCommand.Execute(escape);
                Assert(azoth.SelectedKey == escape && escape.IsSelected, "Pressing a key did not select it.");
                Assert(azoth.KeyBindingOptions.Groups?.Count >= 4, "The key binding list is not grouped.");
            }
            AssertNoEmptyBoxes(azothPage);
            Render(page, Path.Combine(folder, "peripherals-keyboard.png"));

            SettingsGroup.Close();
            Pump();

            // ---- Choosing what is plugged in ----------------------------------------
            IReadOnlyList<SimulatedPeripherals.Model> models = SimulatedPeripherals.Catalogue();
            Console.WriteLine($"  catalogue: {models.Count(m => !m.IsKeyboard)} mice and pads, {models.Count(m => m.IsKeyboard)} keyboards");
            Assert(models.Count(m => !m.IsKeyboard) > 40 && models.Count(m => m.IsKeyboard) >= 5, "The simulator offers too few models.");
            Assert(models.All(m => m.DisplayName != m.ClassName || m.ClassName.Length > 0), "A model has no name.");

            // Unplug the Balteus and plug in a TUF M3 Gen II, live.
            var swapped = new List<string> { "ChakramX", "AzothX", "TUFM3GenII" };
            var worker = Task.Run(() => SimulatedPeripherals.PlugIn(swapped));
            WaitFor(() => worker.IsCompleted
                && viewModel.Peripherals.Count == 3
                && viewModel.Peripherals.All(p => !p.Name.Contains("Balteus"))
                && viewModel.Peripherals.Any(p => p.Name.Contains("M3")), "the swap to reach the page", 10000);
            Assert(viewModel.Peripherals.Contains(chakram) && viewModel.Peripherals.Contains(azoth),
                "Plugging in a device rebuilt the entries for the ones already there.");
            Console.WriteLine($"  after swap: {string.Join(", ", viewModel.Peripherals.Select(p => p.Name))}");

            // A choice is kept per laptop, and dropping it goes back to the profile's list.
            SimulationProfile profile = DeviceSimulation.Profile!;
            bool hadChoice = SimulatedPeripherals.HasChoice(profile.Id);
            List<string>? before = hadChoice ? SimulatedPeripherals.ChosenFor(profile) : null;
            SimulatedPeripherals.Choose(profile.Id, swapped);
            Assert(SimulatedPeripherals.ChosenFor(profile).SequenceEqual(swapped), "A chosen set of peripherals did not read back.");
            SimulatedPeripherals.Choose(profile.Id, before);

            SettingsGroup.SetSubpagesEnabled(subpages);
            host.Close();
            app.Shutdown();
            Console.WriteLine("Peripherals smoke: OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static SettingsGroup GroupFor(DependencyObject page, PeripheralViewModel peripheral) =>
        Descendants(page).OfType<SettingsGroup>().First(group => ReferenceEquals(group.DataContext, peripheral) && group.IsDestination);

    private static IEnumerable<SettingsGroup> Sections(SettingsGroup device) =>
        device.Items.OfType<SettingsGroup>();

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new InvalidOperationException($"Timed out waiting for {what}.");
            Pump();
            Thread.Sleep(20);
        }
        Pump();
    }

    /// <summary>
    /// Renders the page one screenful at a time. Windows clamps the off-screen host to
    /// the height of the screen, so a single render stops wherever the screen does.
    /// </summary>
    private static void Render(FrameworkElement page, string output)
    {
        var viewer = Descendants(page).OfType<System.Windows.Controls.ScrollViewer>().First();
        int width = (int)Math.Ceiling(page.ActualWidth), height = (int)Math.Ceiling(page.ActualHeight);
        int part = 0;
        for (double offset = 0; ; offset += viewer.ViewportHeight)
        {
            viewer.ScrollToVerticalOffset(offset);
            page.UpdateLayout();
            Pump();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(page);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string file = Path.ChangeExtension(output, null) + $"-{++part}.png";
            using (FileStream stream = File.Create(file)) encoder.Save(stream);
            Console.WriteLine("  " + file);

            if (offset + viewer.ViewportHeight >= viewer.ExtentHeight || part >= 6) break;
        }
        viewer.ScrollToVerticalOffset(0);
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject grandchild in Descendants(child)) yield return grandchild;
        }
    }

    /// <summary>
    /// Every visible list on a device's page shows a choice and carries no validation
    /// error. Profile and Polling rate first rendered empty behind a red border: their
    /// options were refilled and the unchanged value never selected itself again.
    /// </summary>
    private static void AssertNoEmptyBoxes(SettingsGroup device)
    {
        foreach (var box in Descendants(device).OfType<System.Windows.Controls.ComboBox>().Where(box => box.IsVisible && box.HasItems))
        {
            string label = (box.DataContext as PeripheralViewModel)?.Name ?? box.DataContext?.GetType().Name ?? "?";
            string path = System.Windows.Data.BindingOperations.GetBinding(box, System.Windows.Controls.Primitives.Selector.SelectedValueProperty)?.Path.Path ?? System.Windows.Data.BindingOperations.GetBinding(box, System.Windows.Controls.Primitives.Selector.SelectedIndexProperty)?.Path.Path ?? "?";
            Assert(!System.Windows.Controls.Validation.GetHasError(box), $"The {path} list on {label} carries a validation error: {System.Windows.Controls.Validation.GetErrors(box).FirstOrDefault()?.ErrorContent}");
            Assert(box.SelectedItem is not null, $"The {path} list on {label} shows no choice.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
