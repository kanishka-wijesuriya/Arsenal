using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using Arsenal.UI.Views.Windows;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SliderRangeSmoke;

/// <summary>
/// Guards the rule that a slider's track has to be able to reach the value printed
/// beside it. The bug this exists for was a Turbo profile running at 150 W under a
/// track that stopped at 140 W: the readout claimed one number, the thumb sat on
/// another, and pressing Apply wrote back whatever the track could represent.
///
/// The page is driven by stub services standing in for four very different machines,
/// so the check is that the ranges reach the view at all - a mistyped binding path
/// silently leaves a WPF slider on its registered default, which is exactly the
/// failure this is meant to catch.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // AppConfig prefers a config beside the executable, which keeps this out of
            // the user's real settings the same way the other smoke tools do.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"), "{\"theme\":1,\"subpages\":0}");

            var application = new App();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            App.ApplyConfiguredTheme();
            var sentinel = new Window
            {
                Width = 1,
                Height = 1,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                AllowsTransparency = true,
                Opacity = 0,
                Left = -20000,
                Top = -20000
            };
            application.MainWindow = sentinel;
            sentinel.Show();

            // A 2024 G16: the machine the 150 W report came from.
            CheckMachine(
                "GU605MI",
                new StubPerformance
                {
                    Profiles = Enumerable.Range(0, 7)
                        .Select(index => new PerformancePlanInfo(index, index < 3 ? $"Built-in {index}" : $"Custom Plan {index - 2}", index < 3 ? index : 0, index > 2))
                        .ToList(),
                    PowerLimitRange = new ControlRange(5, 150),
                    CpuTempRange = new ControlRange(75, 96),
                    CpuUndervoltRange = new ControlRange(-40, 0),
                    IgpuUndervoltRange = new ControlRange(-30, 0),
                    Profile = new PerformanceProfile
                    {
                        Spl = 150, Sppt = 150, Fppt = 150, CpuTempLimit = 95,
                        GpuBoost = 25, GpuTempTarget = 87, GpuPowerTarget = 140, GpuClockLimit = 3000
                    }
                },
                new StubGpu
                {
                    GpuBoostRange = new ControlRange(5, 20),
                    GpuPowerOffsetRange = new ControlRange(0, 40),
                    GpuPowerBaseWatts = 55
                }, args.FirstOrDefault());

            // An Ally, whose whole budget is smaller than the G16's floor-to-ceiling gap.
            CheckMachine(
                "ROG Ally",
                new StubPerformance
                {
                    PowerLimitRange = new ControlRange(5, 50),
                    Profile = new PerformanceProfile { Spl = 30, Sppt = 30, Fppt = 30, GpuClockLimit = 3000 }
                },
                new StubGpu { HasDedicatedGpu = false, IsGpuPowerAdjustable = false });

            // An Advantage Edition, which goes far past the old hard-coded ceilings.
            CheckMachine(
                "Advantage Edition",
                new StubPerformance
                {
                    PowerLimitRange = new ControlRange(5, 250),
                    Profile = new PerformanceProfile { Spl = 250, Sppt = 250, Fppt = 250, GpuClockLimit = 3000 }
                },
                new StubGpu { IsGpuPowerAdjustable = false });

            // A stored profile from a machine the user no longer has: every value is
            // past this machine's ceilings and must be brought onto the track.
            CheckMachine(
                "profile carried over from wider hardware",
                new StubPerformance
                {
                    PowerLimitRange = new ControlRange(5, 90),
                    CpuTempRange = new ControlRange(75, 96),
                    Profile = new PerformanceProfile
                    {
                        Spl = 200, Sppt = 200, Fppt = 200, CpuTempLimit = 105,
                        GpuBoost = 35, GpuTempTarget = 95, GpuClockLimit = 3000
                    }
                },
                new StubGpu { GpuBoostRange = new ControlRange(5, 15), IsGpuPowerAdjustable = false });

            Console.WriteLine("Slider range smoke: OK");
            sentinel.Close();
            application.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Slider range smoke FAILED: " + ex);
            return 1;
        }
    }

    private static void CheckMachine(string label, StubPerformance performance, StubGpu gpu, string? renderOutput = null)
    {
        var viewModel = new PerformanceViewModel(performance, new StubCooling(), gpu);
        var page = new PerformancePage(viewModel);

        // Realised off-screen in a window that is never shown: the sliders only apply
        // their template and run their bindings once they are in a presentation source,
        // and coercion deliberately waits for Loaded.
        var host = new Window
        {
            Content = page,
            Width = 1100,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            AllowsTransparency = true,
            Opacity = 0,
            Left = -20000,
            Top = -20000
        };

        host.Show();
        host.UpdateLayout();

        var sliders = Descendants(page).OfType<ValueSlider>().ToList();
        Assert(sliders.Count > 0, $"{label}: performance page produced no sliders.");

        int checkedSliders = 0;
        foreach (var slider in sliders)
        {
            // A collapsed row is not drawing anything to disagree with.
            if (!slider.IsVisible) continue;
            checkedSliders++;

            Assert(slider.Maximum > slider.Minimum,
                $"{label}: a slider has an empty track ({slider.Minimum}..{slider.Maximum}).");

            Assert(slider.Value >= slider.Minimum && slider.Value <= slider.Maximum,
                $"{label}: readout {slider.Value} is outside its track {slider.Minimum}..{slider.Maximum}.");
        }

        Assert(checkedSliders > 0, $"{label}: no visible sliders were checked.");
        Assert(viewModel.VisibleProfiles.Count <= 5,
            $"{label}: the main performance row exposed more than five plans.");
        Assert(viewModel.Profiles.Count <= 5 || viewModel.HasMoreProfiles,
            $"{label}: an overflowing plan row did not offer View all.");

        // The specific mismatch that started this: the sustained-power track has to
        // reach the machine's own ceiling, not a number compiled into the view.
        var spl = sliders[0];
        Assert(spl.Maximum == performance.PowerLimitRange.Maximum,
            $"{label}: sustained power tops out at {spl.Maximum}, machine allows {performance.PowerLimitRange.Maximum}.");
        Assert(spl.Minimum == performance.PowerLimitRange.Minimum,
            $"{label}: sustained power starts at {spl.Minimum}, machine allows from {performance.PowerLimitRange.Minimum}.");

        // And the view model is what will be written, so it has to agree too.
        Assert(viewModel.Spl <= performance.PowerLimitRange.Maximum,
            $"{label}: view model kept {viewModel.Spl} W, above the machine's ceiling.");
        Assert(viewModel.GpuBoost <= gpu.GpuBoostRange.Maximum,
            $"{label}: view model kept {viewModel.GpuBoost} W of boost, above the part's ceiling.");

        if (!string.IsNullOrWhiteSpace(renderOutput))
        {
            SavePng(page, 1100, 900, renderOutput);

            var tuning = new PerformanceTuningWindow(viewModel);
            Assert(tuning.FindName("CloseButton") is Wpf.Ui.Controls.Button,
                $"{label}: custom performance window is missing its close button.");
            FrameworkElement tuningContent = (FrameworkElement)tuning.Content;
            tuningContent.Measure(new System.Windows.Size(940, 760));
            tuningContent.Arrange(new System.Windows.Rect(0, 0, 940, 760));
            tuningContent.UpdateLayout();
            SavePng(tuningContent, 940, 760, SiblingOutput(renderOutput, "-tuning"));

            var escape = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(System.Windows.Application.Current.MainWindow)!,
                Environment.TickCount,
                System.Windows.Input.Key.Escape)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            };
            tuning.RaiseEvent(escape);
            Assert(escape.Handled, $"{label}: Escape was not handled by the custom performance window.");

            viewModel.OpenProfileManager();
            host.UpdateLayout();
            ForceOverlayOpen((OverlayHost)page.FindName("ProfileManagerOverlay"));
            SavePng(page, 1100, 900, SiblingOutput(renderOutput, "-library"));
            viewModel.CloseProfileManager();
        }

        Console.WriteLine($"  {label}: {checkedSliders} sliders, sustained power {spl.Minimum}-{spl.Maximum} W, value {spl.Value} W");
        host.Close();
    }

    private static string SiblingOutput(string output, string suffix) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(output))!,
        Path.GetFileNameWithoutExtension(output) + suffix + Path.GetExtension(output));

    private static void SavePng(FrameworkElement element, int width, int height, string output)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using FileStream stream = File.Create(output);
        encoder.Save(stream);
    }

    private static void ForceOverlayOpen(OverlayHost overlay)
    {
        overlay.ApplyTemplate();
        if (overlay.Template.FindName("PART_Scrim", overlay) is FrameworkElement scrim) scrim.Opacity = 1;
        if (overlay.Template.FindName("PART_Card", overlay) is FrameworkElement card) card.Opacity = 1;
        if (overlay.Template.FindName("PART_Scale", overlay) is ScaleTransform scale)
            scale.ScaleX = scale.ScaleY = 1;
        if (overlay.Template.FindName("PART_Translate", overlay) is TranslateTransform translate)
            translate.Y = 0;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The stubs below implement the full service contracts but only the ranges and the
    // stored profile matter here; nothing raises their events.
#pragma warning disable CS0067

    private sealed class StubPerformance : IPerformanceService
    {
        public PerformanceProfile Profile { get; set; } = new();
        public IReadOnlyList<PerformancePlanInfo> Profiles { get; set; } = new[]
        {
            new PerformancePlanInfo(2, "Silent", 2, false),
            new PerformancePlanInfo(0, "Balanced", 0, false),
            new PerformancePlanInfo(1, "Turbo", 1, false)
        };
        public int CurrentMode => 1;
        public string CurrentModeName => "Turbo";
        public event Action<int>? ModeChanged;
        public event Action<string>? ModeLabelChanged;
        public event Action? ProfilesChanged;
        public void SetMode(int modeIndex, bool notify = true) { }
        public void CycleMode(bool backward = false) { }
        public PerformanceProfile GetCurrentProfile() => Profile;
        public IReadOnlyList<PerformancePlanInfo> GetProfiles() => Profiles;
        public int CreateProfile(string? name = null) => -1;
        public bool RenameProfile(int modeIndex, string name) => false;
        public bool DeleteProfile(int modeIndex) => false;
        public void SaveProfile(PerformanceProfile profile) { }
        public void ResetProfile(int modeIndex) { }
        public bool IsCpuBoostSupported => true;
        public bool IsRyzenSmuSupported => true;
        public bool IsIntelMsrSupported => false;
        public bool IsUndervoltSupported => true;
        public bool IsIgpuUndervoltSupported => true;
        public void ApplyPowerLimits(int spl, int sppt, int fppt) { }
        public void ApplyUndervolt(int cpuUvMv, int igpuUvMv) { }
        public ControlRange PowerLimitRange { get; set; } = new(5, 150);
        public ControlRange CpuTempRange { get; set; } = new(75, 96);
        public ControlRange CpuUndervoltRange { get; set; } = new(-40, 0);
        public ControlRange IgpuUndervoltRange { get; set; } = new(-30, 0);
        public ControlRange FanHysteresisRange { get; set; } = new(0, 20);
    }

    private sealed class StubGpu : IGpuService
    {
        public int CurrentGpuMode => 1;
        public bool IsEcoSupported => true;
        public bool IsMuxSupported => true;
        public bool HasDedicatedGpu { get; set; } = true;
        public bool IsXgmConnected => false;
        public event Action<int>? GpuModeChanged;
        public event Action<string?>? GpuLockStatusChanged;
        public event Action<bool, string?>? GpuBusyChanged;
        public void SetGpuMode(int mode, int auto = 0) { }
        public void SetGpuClocks(int coreOffsetMhz, int memoryOffsetMhz) { }
        public void SetGpuPower(int dynamicBoostW, int tempTargetC, int powerTargetW) { }
        public void ToggleXgm() { }
        public void KillGpuApps() { }
        public void RestartNvServices() { }
        public ControlRange GpuCoreOffsetRange { get; set; } = new(-250, 250);
        public ControlRange GpuMemoryOffsetRange { get; set; } = new(-500, 500);
        public ControlRange GpuClockLimitRange { get; set; } = new(400, 3000);
        public ControlRange GpuBoostRange { get; set; } = new(5, 25);
        public ControlRange GpuTempRange { get; set; } = new(75, 87);
        public ControlRange GpuPowerOffsetRange { get; set; } = new(0, 70);
        public int GpuPowerBaseWatts { get; set; }
        public bool IsGpuPowerAdjustable { get; set; } = true;
    }

    private sealed class StubCooling : ICoolingService
    {
        public bool CustomFansSupported => true;
        public event Action<string>? CalibrationStatusChanged;
        public event Action? CalibrationCompleted;
        public FanCurveModel GetFanCurve(int fanIndex, int modeIndex) => FanCurveModel.CreateDefault(fanIndex, "Fan");
        public void SaveFanCurve(int fanIndex, int modeIndex, FanCurveModel curve) { }
        public void ResetFanCurves(int modeIndex) { }
        public void ApplyFanCurves(int modeIndex) { }
        public void StartCalibration() { }
    }

#pragma warning restore CS0067
}
