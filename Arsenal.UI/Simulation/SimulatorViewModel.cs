using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Arsenal.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arsenal.UI.Simulation;

/// <summary>
/// The device simulator's section of Settings: pick a laptop, open Arsenal as it, capture
/// this one, and see what a simulated session tried to do.
/// </summary>
public partial class SimulatorViewModel : ObservableObject
{
    private readonly DispatcherTimer _refresh;
    private bool _actionsDirty = true;

    public SimulatorViewModel()
    {
        Profiles = new ObservableCollection<SimulationProfile>(DeviceSimulation.Catalogue());
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == DeviceSimulation.Profile?.Id)
            ?? Profiles.FirstOrDefault();

        // Writes arrive from any thread and in bursts - a lighting page streams them.
        // The list is redrawn at most twice a second, on the UI thread.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refresh.Tick += (_, _) => RefreshActions();
    }

    public ObservableCollection<SimulationProfile> Profiles { get; }

    public ObservableCollection<SimulatedAction> Actions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDescription))]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    private SimulationProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private bool _isCapturing;

    [ObservableProperty]
    private string _captureStatus = "Reads every firmware value this laptop answers, and the ASUS mice and keyboards plugged in, into a profile you can simulate later. It only reads. Takes a few seconds.";

    public bool IsSimulating => DeviceSimulation.IsActive;

    public string StatusLabel => DeviceSimulation.Profile is { } profile ? profile.Name : "This laptop";

    public string StatusDescription => DeviceSimulation.Profile is { } profile
        ? $"{profile.Model}. Firmware answers come from the profile, and nothing is sent to this PC's hardware or changed in Windows. Settings here are kept apart from your real ones."
        : "Arsenal is talking to the real hardware.";

    public string SelectedDescription => SelectedProfile is { } profile
        ? profile.Source
        : "No profiles found.";

    public string ProfilesFolder => DeviceSimulation.ProfilesDirectory;

    /// <summary>Starts the live list. Called while the section is on screen.</summary>
    public void Attach()
    {
        DeviceSimulation.ActionsChanged -= MarkDirty;
        DeviceSimulation.ActionsChanged += MarkDirty;
        _actionsDirty = true;
        RefreshActions();
        _refresh.Start();
    }

    public void Detach()
    {
        DeviceSimulation.ActionsChanged -= MarkDirty;
        _refresh.Stop();
    }

    private void MarkDirty() => _actionsDirty = true;

    private void RefreshActions()
    {
        if (!_actionsDirty) return;
        _actionsDirty = false;

        Actions.Clear();
        foreach (SimulatedAction action in DeviceSimulation.RecentActions()) Actions.Add(action);
    }

    private bool CanSimulate() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanSimulate))]
    private void Simulate()
    {
        if (SelectedProfile is null) return;
        Launch(SelectedProfile.Id);
    }

    [RelayCommand]
    private void Stop() => Launch(null);

    /// <summary>
    /// Opens another Arsenal. A simulated one closes itself behind it; the real one
    /// stays, so the simulation opens beside it.
    /// </summary>
    private static void Launch(string? profileId)
    {
        try
        {
            // Settings written in the last two seconds are still waiting on the debounce.
            AppConfig.Flush();
            DeviceSimulation.Launch(profileId);

            if (DeviceSimulation.IsActive && System.Windows.Application.Current is App app) app.ExitApplication();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Simulator launch: " + ex.Message);
        }
    }

    private bool CanCapture() => !IsCapturing && !DeviceSimulation.IsActive;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task Capture()
    {
        IsCapturing = true;
        CaptureStatus = "Reading the firmware…";
        try
        {
            SimulationProfile profile = await Task.Run(() => DeviceSimulation.Capture(Program.acpi));
            string path = await Task.Run(() => DeviceSimulation.Save(profile));

            ReloadProfiles(profile.Id);
            CaptureStatus = $"Saved {profile.Firmware.Count} firmware values to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Simulator capture: " + ex);
            CaptureStatus = "The capture failed: " + ex.Message;
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(DeviceSimulation.ProfilesDirectory);
            Process.Start(new ProcessStartInfo(DeviceSimulation.ProfilesDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Simulator folder: " + ex.Message);
        }
    }

    [RelayCommand]
    private void Reload() => ReloadProfiles(SelectedProfile?.Id);

    private void ReloadProfiles(string? select)
    {
        Profiles.Clear();
        foreach (SimulationProfile profile in DeviceSimulation.Catalogue()) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == select) ?? Profiles.FirstOrDefault();
    }

    // ---- Peripherals ---------------------------------------------------------------

    private bool _loadingPeripherals;
    private int _plugGeneration;

    /// <summary>Every mouse and mouse pad the simulator can plug in.</summary>
    public ObservableCollection<SimulatedPeripheralOption> MouseOptions { get; } = new();

    /// <summary>Every keyboard the simulator can plug in.</summary>
    public ObservableCollection<SimulatedPeripheralOption> KeyboardOptions { get; } = new();

    /// <summary>Every headset the simulator can plug in.</summary>
    public ObservableCollection<SimulatedPeripheralOption> HeadsetOptions { get; } = new();

    private IEnumerable<SimulatedPeripheralOption> AllOptions => MouseOptions.Concat(KeyboardOptions).Concat(HeadsetOptions);

    [ObservableProperty]
    private string _peripheralsSummary = string.Empty;

    /// <summary>The laptop being edited is the one this Arsenal is simulating, so changes connect at once.</summary>
    private bool IsLive => SelectedProfile is { } profile && DeviceSimulation.Profile?.Id == profile.Id;

    partial void OnSelectedProfileChanged(SimulationProfile? value) => LoadPeripherals();

    private void LoadPeripherals()
    {
        if (!AllOptions.Any())
        {
            foreach (SimulatedPeripherals.Model model in SimulatedPeripherals.Catalogue())
            {
                var option = new SimulatedPeripheralOption(this, model);
                (model.IsHeadset ? HeadsetOptions : model.IsKeyboard ? KeyboardOptions : MouseOptions).Add(option);
            }
        }

        _loadingPeripherals = true;
        try
        {
            HashSet<string> chosen = SelectedProfile is { } profile
                ? SimulatedPeripherals.ChosenFor(profile).ToHashSet()
                : new HashSet<string>();
            foreach (SimulatedPeripheralOption option in AllOptions)
                option.IsPluggedIn = chosen.Contains(option.ClassName);
        }
        finally
        {
            _loadingPeripherals = false;
        }
        Summarise();
    }

    private void Summarise()
    {
        var plugged = AllOptions.Where(option => option.IsPluggedIn).Select(option => option.DisplayName).ToList();
        string list = plugged.Count == 0 ? "Nothing plugged in." : "Plugged in: " + string.Join(", ", plugged) + ".";
        string when = IsLive
            ? " Changes connect and disconnect them at once."
            : " Used the next time you open Arsenal as this laptop.";
        string source = SelectedProfile is { } profile && !SimulatedPeripherals.HasChoice(profile.Id)
            ? " These are the ones its profile lists."
            : string.Empty;
        PeripheralsSummary = list + source + when;
    }

    internal void PeripheralToggled()
    {
        if (_loadingPeripherals || SelectedProfile is not { } profile) return;

        List<string> chosen = AllOptions
            .Where(option => option.IsPluggedIn).Select(option => option.ClassName).ToList();
        SimulatedPeripherals.Choose(profile.Id, chosen);
        Summarise();

        if (IsLive) PlugIn(chosen);
    }

    /// <summary>Puts the list back to the peripherals the laptop's profile names.</summary>
    [RelayCommand]
    private void ResetPeripherals()
    {
        if (SelectedProfile is not { } profile) return;
        SimulatedPeripherals.Choose(profile.Id, null);
        LoadPeripherals();
        if (IsLive) PlugIn(SimulatedPeripherals.ChosenFor(profile));
    }

    /// <summary>
    /// Connects the new set off the UI thread. A mouse takes a moment to connect, and a
    /// quick run of ticks should settle on the last one rather than queue every one.
    /// </summary>
    private void PlugIn(IReadOnlyCollection<string> chosen)
    {
        int generation = ++_plugGeneration;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _plugGeneration)) return;
            try
            {
                SimulatedPeripherals.PlugIn(chosen);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Simulator plug-in: " + ex.Message);
            }
        });
    }
}

/// <summary>One model in the simulator's list of peripherals.</summary>
public partial class SimulatedPeripheralOption : ObservableObject
{
    private readonly SimulatorViewModel _owner;

    public string ClassName { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isPluggedIn;

    public SimulatedPeripheralOption(SimulatorViewModel owner, SimulatedPeripherals.Model model)
    {
        _owner = owner;
        ClassName = model.ClassName;
        DisplayName = model.DisplayName;
    }

    partial void OnIsPluggedInChanged(bool value) => _owner.PeripheralToggled();
}
