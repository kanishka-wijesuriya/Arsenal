using Arsenal;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.Peripherals;
using Arsenal.Simulation;
using Arsenal.UI;
using Arsenal.UI.Simulation;
using Arsenal.USB;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Runs one simulated laptop inside this process and checks what Arsenal would see.
//
// Built Debug, which is what carries the simulator. One profile per run, because a
// simulation is fixed for the life of a process exactly as it is in the app:
//
//     SimulatorSmoke.exe rog-ally-rc71l [panel.png]
//
// Checks, in order: the model the app reads is the profile's; the firmware answers
// from the profile and remembers a write; peripherals are the profile's and sync; and
// every path that would change this machine was stopped rather than run. Then renders
// the Settings section, which is the one piece of new XAML and only fails at runtime.
// Exits non-zero on the first failure.

// SimulatorSmoke.exe --capture reads this machine into the profiles folder, exactly as
// the Capture button does, and checks the profile it wrote loads back. DSTS reads only.
if (args.FirstOrDefault() == "--capture")
{
    using var real = new AsusACPI();
    if (!real.IsConnected())
    {
        Console.WriteLine("FAILED: the ATKACPI driver could not be opened.");
        return 1;
    }

    var watch = System.Diagnostics.Stopwatch.StartNew();
    SimulationProfile captured = DeviceSimulation.Capture(real);
    string saved = DeviceSimulation.Save(captured);
    SimulationProfile reloaded = SimulationProfile.Parse(captured.Id, File.ReadAllText(saved));

    Console.WriteLine($"Captured {captured.Name}: {captured.Firmware.Count} firmware values in {watch.ElapsedMilliseconds} ms");
    Console.WriteLine($"  saved   {saved}");
    Console.WriteLine($"  model   {reloaded.Model}, BIOS {reloaded.Bios}, series {reloaded.Series}, keyboard {reloaded.KeyboardLayout}");
    bool same = reloaded.Firmware.Count == captured.Firmware.Count;
    Console.WriteLine(same ? "Simulator capture: OK" : "Simulator capture: the saved profile does not load back the same");
    return same ? 0 : 1;
}

string id = args.FirstOrDefault() ?? "rog-ally-rc71l";
int failures = 0;

void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "  ok    " : "  FAIL  ") + what);
    if (!condition) failures++;
}

DeviceSimulation.Start([DeviceSimulation.Argument, id]);
if (DeviceSimulation.Profile is not { } profile)
{
    Console.WriteLine($"FAILED: no profile called {id}.");
    return 1;
}

var (bios, biosModel) = AppConfig.GetBiosAndModel();
Console.WriteLine($"{profile.Name}\n  model  {AppConfig.GetModel()}\n  short  {AppConfig.GetModelShort()}\n  bios   {biosModel}.{bios}");
Check(AppConfig.GetModel() == profile.Model, "the app reads the profile's model");
Check(Logger.logFile.StartsWith(DeviceSimulation.StateDirectory!, StringComparison.OrdinalIgnoreCase), "the log is the simulation's own");

Console.WriteLine("\nModel checks");
foreach (var (name, value) in new (string, bool)[]
{
    ("Ally", AppConfig.IsAlly()), ("TUF", AppConfig.IsTUF()), ("Strix", AppConfig.IsStrix()),
    ("Slash", AppConfig.IsSlash()), ("AniMe Matrix", AppConfig.IsAnimeMatrix()), ("Z13", AppConfig.IsZ13()),
    ("Vivo/Zen", AppConfig.IsVivoZenPro()), ("Intel HX", AppConfig.IsIntelHX()), ("OLED", AppConfig.IsOLED()),
    ("mini-LED forced", AppConfig.IsForceMiniled()), ("AMD iGPU", AppConfig.IsAMDiGPU()),
})
    Console.WriteLine($"  {name,-18}{value}");

Console.WriteLine("\nFirmware");
var acpi = new AsusACPI();
Arsenal.Program.acpi = acpi;
Check(acpi.IsConnected(), "the ACPI stand-in reports connected without the driver");

foreach (var (name, device) in new (string, uint)[]
{
    ("performance mode", AsusACPI.PerformanceMode), ("Vivobook mode", AsusACPI.VivoBookMode),
    ("GPU Eco", AsusACPI.GPUEcoROG), ("MUX", AsusACPI.GPUMuxROG), ("overdrive", AsusACPI.ScreenOverdrive),
    ("mini-LED", AsusACPI.ScreenMiniled1), ("mid fan", AsusACPI.Mid_Fan), ("XG Mobile", AsusACPI.GPUXGConnected),
    ("APU memory", (uint)AsusACPI.APU_MEM), ("core config", AsusACPI.CORES_CPU), ("GPU TGP", AsusACPI.GPU_BASE),
    ("NVIDIA boost", (uint)AsusACPI.PPT_GPUC0), ("TUF backlight", (uint)AsusACPI.TUF_KB_BRIGHTNESS),
})
    Console.WriteLine($"  {name,-18}{(acpi.IsSupported(device) ? "yes, " + acpi.DeviceGet(device) : "no")}");

Console.WriteLine($"  {"NVIDIA dGPU",-18}{acpi.IsNVidiaGPU()}");
Console.WriteLine($"  {"power ceiling",-18}{AsusACPI.MaxTotal} W");
Console.WriteLine($"  {"CPU fan",-18}{acpi.GetFan(AsusFan.CPU)}");

byte[] turbo = acpi.GetFanCurve(AsusFan.CPU, 1);
Check(!AsusACPI.IsInvalidCurve(turbo), "the Turbo fan curve reads back as a real curve");

bool hasMode = acpi.IsSupported(AsusACPI.PerformanceMode);
if (hasMode)
{
    Check(acpi.SetPerformanceMode(1) == 1, "the firmware accepts a mode change");
    Check(acpi.DeviceGet(AsusACPI.PerformanceMode) == 1, "and reads it back");
}

Console.WriteLine("\nPeripherals");
PeripheralsProvider.DetectAllAsusMice();
PeripheralsProvider.DetectAllAsusKeyboards();
List<IPeripheral> plugged = PeripheralsProvider.AllPeripherals();
foreach (IPeripheral peripheral in plugged)
    Console.WriteLine($"  {peripheral.GetDisplayName(),-32}ready {peripheral.IsDeviceReady}, battery {peripheral.Battery}");
Check(plugged.Count == profile.Peripherals.Count, $"the profile's {profile.Peripherals.Count} peripherals are plugged in");
Check(plugged.All(peripheral => peripheral.IsDeviceReady), "every one of them syncs");

Console.WriteLine("\nStopped instead of run");
Check(ProcessHelper.RunCMD("powercfg", "/list") == string.Empty, "a command is not started");
Check(!AsusHid.FindDevices(AsusHid.AURA_ID).Any(), "the laptop's own HID devices are not opened");
PowerNative.SetPowerPlan("381b4222-f694-41f0-9685-ff5bb260df2e");
Startup.Schedule();
ProcessHelper.KillByName("ASUSSmartDisplayControl");
HardwareControl.RecreateGpuControl();
Check(HardwareControl.GpuControl is null, "no graphics driver is opened");

IReadOnlyList<SimulatedAction> actions = DeviceSimulation.RecentActions(200);
Check(actions.Any(action => action.Description.Contains("power plan")), "the power plan change was recorded");
Check(actions.Any(action => action.Description.Contains("sign-in task")), "the scheduled task was recorded");
foreach (SimulatedAction action in actions.Take(8))
    Console.WriteLine($"    {action.Time:HH:mm:ss}  {action.Description}");

string output = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, $"simulator-{id}.png");
var thread = new Thread(() => failures += RenderPanel(output));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

Console.WriteLine(failures == 0 ? "\nSimulator smoke: OK" : $"\nSimulator smoke: {failures} FAILED");
return failures == 0 ? 0 : 1;

static int RenderPanel(string path)
{
    try
    {
        var application = new App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        App.ApplyConfiguredTheme();

        // Every section open, as one long page, so the rows are drawn and not only the card.
        Arsenal.UI.Controls.SettingsGroup.SetSubpagesEnabled(false);
        var panel = new SimulatorPanel { VerticalAlignment = VerticalAlignment.Top };
        var host = new Window
        {
            Content = new System.Windows.Controls.ScrollViewer { Content = panel, Padding = new Thickness(24) },
            Width = 1000,
            Height = 1100,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        host.SetResourceReference(Window.BackgroundProperty, "AppContentBackground");
        host.Show();
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Thread.Sleep(700);
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)host.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);

        Console.WriteLine($"\n  ok    the Settings section renders ({path})");
        host.Close();
        application.Shutdown();
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("\n  FAIL  the Settings section: " + ex);
        return 1;
    }
}
