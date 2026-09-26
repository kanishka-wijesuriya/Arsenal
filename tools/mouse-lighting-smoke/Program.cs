using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
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

namespace MouseLightingSmoke;

/// <summary>
/// Renders the Devices page for a zoned mouse and for a Balteus pad from a fake
/// peripheral service, since no harness can plug a real one in. It checks the sections a
/// pad should and should not show, that each mode only offers the settings it reads, that
/// Apply sends what the page shows, and that a refresh after a write keeps the selection.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"),
                "{\"theme\":1,\"accent_source\":1,\"accent_color\":\"#8B5CF6\",\"subpages\":0}");

            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            // The Application constructor queues App.OnStartup, and this harness pumps the
            // dispatcher. Without the lifetime-smoke guard the first pump runs the real
            // startup, which hands off to an installed Arsenal and shuts this process down.
            typeof(App).GetProperty(nameof(App.Services), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
                .SetValue(null, new ServiceCollection().BuildServiceProvider());
            Environment.SetEnvironmentVariable("ARSENAL_UI_LIFETIME_SMOKE_HOST", "1");

            string folder = args.FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(folder);

            var service = new FakePeripheralService();
            var viewModel = new DevicesViewModel(service);
            var page = new DevicesPage(viewModel);

            // SettingsGroup builds its rows on Loaded, which a detached page never gets.
            var host = new Window
            {
                Width = 1080, Height = 2400, Left = -20000, Top = 0,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("SurfaceBase"),
                Content = page
            };
            host.Show();
            Pump();

            // Zoned mouse first: the zone row shows, Rainbow offers speed and direction but
            // no colour, and Comet with random colour on hides the colour picker.
            viewModel.SelectedDevice = viewModel.Devices[0];
            Assert(viewModel.SelectedDevice.HasMouseLightingZones, "Zoned mouse did not report zones.");
            Assert(viewModel.IsMousePerformanceSelected, "A mouse lost its performance panel.");
            Assert(viewModel.SelectedMouseZone?.Zone == 3, "The first zone shown is not All zones.");
            viewModel.MouseLightingMode = 3;
            Assert(viewModel.MouseShowsSpeed && viewModel.MouseShowsDirection && !viewModel.MouseShowsColor, "Rainbow shows the wrong settings.");
            viewModel.MouseLightingMode = 5;
            viewModel.MouseRandomColor = true;
            Assert(viewModel.MouseShowsRandomColor && !viewModel.MouseShowsColor, "Comet with random colour still shows a colour.");
            viewModel.MouseRandomColor = false;
            Assert(viewModel.MouseShowsColor, "Comet without random colour hides its colour.");

            viewModel.SelectedMouseZone = viewModel.SelectedDevice.MouseLightingZones.First(z => z.Zone == 1);
            Assert(viewModel.MouseLightingMode == 1, "Switching zone did not load that zone's own mode.");
            Assert(viewModel.SelectedMouseZone.Modes.All(m => m.Value is 0 or 1 or 2 or 4), "A single zone offered a whole-mouse effect.");
            Render(page, Path.Combine(folder, "mouse-lighting-zoned.png"));

            viewModel.MouseLightingBrightness = 60;
            Task apply = viewModel.ApplyMouseLightingCommand.ExecuteAsync(null);
            while (!apply.IsCompleted) Pump();
            Pump();
            Assert(service.LastWrite == ("mouse", 1, 1, 60), $"Apply sent {service.LastWrite}.");
            Assert(viewModel.SelectedDevice?.Id == "mouse", "The refresh after a write moved the selection off the mouse.");
            Assert(viewModel.SelectedMouseZone?.Zone == 1, "The refresh after a write moved the zone back to All.");

            // The pad: no performance panel, no zone row, four brightness steps.
            viewModel.SelectedDevice = viewModel.Devices[1];
            Assert(!viewModel.IsMousePerformanceSelected, "A pad shows the DPI and polling panel.");
            Assert(!viewModel.SelectedDevice.HasMouseLightingZones, "A single-zone pad shows a zone row.");
            Assert(viewModel.SelectedDevice.MouseLightingBrightnessFormat == "{0} of 4", "Pad brightness reads as a percentage.");
            Render(page, Path.Combine(folder, "mouse-lighting-pad.png"));

            // Both devices open on Breathing. When the mode list was bound to the zone's own
            // list, the same number on either side meant nothing re-selected it, and the
            // box sat empty behind a validation border.
            var modeBox = Descendants(page).OfType<System.Windows.Controls.ComboBox>()
                .Single(box => ReferenceEquals(box.ItemsSource, viewModel.MouseLightingModes));
            Assert(modeBox.SelectedItem is MouseLightingModeOption { Value: 1 }, "The pad's mode box does not show Breathing.");
            Assert(!System.Windows.Controls.Validation.GetHasError(modeBox), "The pad's mode box carries a validation error.");

            host.Close();
            app.Shutdown();
            Console.WriteLine("Mouse lighting smoke passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Render(FrameworkElement page, string output)
    {
        const int width = 1080, height = 2400;
        page.Width = width;
        page.Height = height;
        page.Measure(new System.Windows.Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        Pump();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(output);
        encoder.Save(stream);
        Console.WriteLine(output);
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakePeripheralService : IPeripheralService
    {
        public (string, int, int, int)? LastWrite { get; private set; }
        public IReadOnlyList<PeripheralDeviceModel> Devices => new[] { ZonedMouse(), Pad() };
        public event Action? DevicesChanged;

        public void RefreshDevices() => DevicesChanged?.Invoke();

        public void SetMouseLighting(string deviceId, int zone, int mode, int colorArgb, int brightness, bool randomColor, int speed, int direction)
        {
            LastWrite = (deviceId, zone, mode, brightness);
            RefreshDevices();
        }

        public void SetDpi(string deviceId, int dpi) { }
        public void SetPollingRate(string deviceId, int rateHz) { }
        public void SetSleepTimeout(string deviceId, int minutes) { }
        public void SetKeyboardLighting(string deviceId, int mode, int primaryArgb, int secondaryArgb, int speed, int brightness) { }
        public void SetKeyboardProfile(string deviceId, int profile) { }
        public void SetKeyboardEnergy(string deviceId, int sleepMinutes, int lowBatteryWarningPercent) { }
        public void SetKeyboardOled(string deviceId, bool enabled, int brightness, int mode) { }

        // Mirrors AsusMouse's defaults: random colour on Comet, direction on Rainbow and
        // Comet, speed on Rainbow, colour on Static, Breathing, React and Comet.
        private static MouseLightingModeOption Mode(int value, string label) => new()
        {
            Value = value,
            Label = label,
            HasColor = value is 0 or 1 or 4 or 5,
            HasRandomColor = value == 5,
            HasSpeed = value == 3,
            HasDirection = value is 3 or 5,
        };

        private static PeripheralDeviceModel ZonedMouse()
        {
            var all = new[] { Mode(0, "Static"), Mode(1, "Breathing"), Mode(2, "Color cycle"), Mode(3, "Rainbow"), Mode(4, "React"), Mode(5, "Comet") }.ToList();
            var single = all.Where(m => m.Value is 0 or 1 or 2 or 4).ToList();
            return new PeripheralDeviceModel
            {
                Id = "mouse", Name = "ROG Gladius III Wireless", DeviceType = "Mouse", IsConnected = true,
                HasMousePerformance = true, PollingRate = 1000, PollingRates = new() { 125, 250, 500, 1000 },
                MinDpi = 100, MaxDpi = 26000, CurrentDpi = 1600, DpiProfiles = new() { 1600 },
                HasMouseLighting = true, MaxMouseLightingBrightness = 100,
                MouseLightingZones = new()
                {
                    new() { Zone = 3, Label = "All zones", Modes = all, Mode = 0, ColorArgb = unchecked((int)0xFF8B5CF6), Brightness = 80 },
                    new() { Zone = 0, Label = "Logo", Modes = single, Mode = 0, ColorArgb = unchecked((int)0xFF8B5CF6), Brightness = 80 },
                    new() { Zone = 1, Label = "Scroll wheel", Modes = single, Mode = 1, ColorArgb = unchecked((int)0xFF22C55E), Brightness = 40 },
                    new() { Zone = 2, Label = "Underglow", Modes = single, Mode = 2, Brightness = 100 },
                },
            };
        }

        private static PeripheralDeviceModel Pad()
        {
            var modes = new List<MouseLightingModeOption>
            {
                new() { Value = 0, Label = "Static", HasColor = true },
                new() { Value = 1, Label = "Breathing", HasColor = true, HasRandomColor = true },
                new() { Value = 2, Label = "Color cycle" },
                new() { Value = 3, Label = "Rainbow" },
                new() { Value = 5, Label = "Comet", HasColor = true },
            };
            return new PeripheralDeviceModel
            {
                Id = "pad", Name = "ROG Balteus Qi", DeviceType = "Mouse", IsConnected = true,
                HasMousePerformance = false, HasMouseLighting = true, MaxMouseLightingBrightness = 4,
                MouseLightingZones = new() { new() { Zone = 3, Label = "All zones", Modes = modes, Mode = 1, ColorArgb = unchecked((int)0xFFEF4444), Brightness = 3 } },
            };
        }
    }
}
