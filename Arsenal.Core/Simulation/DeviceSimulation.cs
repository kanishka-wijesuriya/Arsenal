using Arsenal.Helpers;
using Arsenal.Peripherals;
using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Reflection;

namespace Arsenal.Simulation;

/// <summary>
/// Runs Arsenal as if it were on another ASUS laptop.
/// </summary>
/// <remarks>
/// A simulation is chosen at launch, with <c>--simulate &lt;profile&gt;</c>, and lasts for
/// the life of the process. It has to be: the model name is read once and cached, the
/// firmware's answers decide which pages exist, and half the app has already asked both
/// by the time a window is drawn. Switching profile is therefore a new process. It opens
/// beside the real Arsenal rather than replacing it, so the two can be compared and the
/// real laptop stays managed the whole time.
///
/// <para>While one runs, nothing reaches the hardware. The firmware calls are answered
/// from the profile, USB lighting devices get a channel that goes nowhere, and the
/// actions that would change Windows itself - power plans, services, scheduled tasks, a
/// restart after a GPU switch - are recorded instead of done. Each simulated machine
/// keeps its own settings and log under <see cref="Root"/>, so trying a profile cannot
/// disturb the real configuration.</para>
/// </remarks>
public static class DeviceSimulation
{
    public const string Argument = "--simulate";
    private const int ActionLimit = 300;
    private const string ResourcePrefix = "Arsenal.Simulation.Profiles.";

    private static readonly object Gate = new();
    private static readonly List<SimulatedAction> Actions = new();
    private static SimulatedFirmware? _firmware;
    private static string? _lastKey;

    public static SimulationProfile? Profile { get; private set; }

    public static bool IsActive => Profile is not null;

    /// <summary>This simulated machine's own settings and log.</summary>
    public static string? StateDirectory { get; private set; }

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arsenal", "Simulator");

    /// <summary>Where captured and hand-written profiles are kept, beside the built-in ones.</summary>
    public static string ProfilesDirectory => Path.Combine(Root, "Profiles");

    /// <summary>Raised whenever something is recorded, from whichever thread recorded it.</summary>
    public static event Action? ActionsChanged;

    /// <summary>
    /// Enters a simulation when the command line asks for one.
    /// </summary>
    /// <remarks>
    /// Called before anything else in the process, and in particular before the first
    /// read of AppConfig: its static constructor picks the settings file, and it has to
    /// find this simulation's directory already set when it does.
    /// </remarks>
    public static void Start(string[] args)
    {
        int index = Array.FindIndex(args, arg => string.Equals(arg, Argument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return;

        string id = args[index + 1];
        SimulationProfile? profile = Find(id);
        if (profile is null)
        {
            Logger.WriteLine($"Simulator: no profile called {id}; starting on the real machine.");
            return;
        }

        StateDirectory = Path.Combine(Root, "Sessions", profile.Id);
        Directory.CreateDirectory(StateDirectory);
        Logger.logFile = Path.Combine(StateDirectory, "log.txt");

        _firmware = new SimulatedFirmware(profile);
        _firmware.Wrote += Record;
        SimulatedPeripherals.ApplyChoice(profile);
        Profile = profile;

        Logger.WriteLine($"Simulator: running as {profile.Name} ({profile.Model}). Nothing is sent to the hardware.");
    }

    /// <summary>Every profile that can be simulated: the built-in ones, then the saved ones.</summary>
    public static IReadOnlyList<SimulationProfile> Catalogue()
    {
        var profiles = new Dictionary<string, SimulationProfile>(StringComparer.OrdinalIgnoreCase);

        Assembly assembly = typeof(DeviceSimulation).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            string id = Path.GetFileNameWithoutExtension(resource[ResourcePrefix.Length..]);
            try
            {
                using Stream stream = assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                SimulationProfile profile = SimulationProfile.Parse(id, reader.ReadToEnd());
                profile.BuiltIn = true;
                profiles[id] = profile;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Simulator: built-in profile {id} is broken: {ex.Message}");
            }
        }

        if (Directory.Exists(ProfilesDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                try
                {
                    profiles[id] = SimulationProfile.Parse(id, File.ReadAllText(file));
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"Simulator: skipped {file}: {ex.Message}");
                }
            }
        }

        return profiles.Values
            .OrderBy(profile => profile.Captured ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static SimulationProfile? Find(string id) =>
        Catalogue().FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool TryFirmware(uint controlCode, byte[] input, byte[] output) =>
        _firmware?.Answer(controlCode, input, output) ?? false;

    /// <summary>Records an action that would have changed the machine, and reports whether to skip it.</summary>
    public static bool Skip(string action)
    {
        if (!IsActive) return false;
        Record(new SimulatedAction(DateTime.Now, "Not run: " + action, false));
        return true;
    }

    public static void Record(string description) =>
        Record(new SimulatedAction(DateTime.Now, description, true));

    private static void Record(SimulatedAction action)
    {
        // Lighting streams a packet per frame. One line per device, updated in place,
        // says the same thing as a thousand and keeps the firmware writes visible.
        string key = action.Description.StartsWith("USB ", StringComparison.Ordinal)
            ? action.Description[..Math.Min(8, action.Description.Length)]
            : action.Description;

        bool repeated;
        lock (Gate)
        {
            repeated = key == _lastKey && Actions.Count > 0;
            if (repeated) Actions[^1] = action;
            else Actions.Add(action);

            _lastKey = key;
            if (Actions.Count > ActionLimit) Actions.RemoveRange(0, Actions.Count - ActionLimit);
        }

        if (!repeated) Logger.WriteLine("Simulator: " + action.Description);
        ActionsChanged?.Invoke();
    }

    /// <summary>The most recent actions, newest first.</summary>
    public static IReadOnlyList<SimulatedAction> RecentActions(int count = 60)
    {
        lock (Gate) return Actions.AsEnumerable().Reverse().Take(count).ToList();
    }

    /// <summary>
    /// Starts another Arsenal, as <paramref name="profileId"/> or as the real machine.
    /// </summary>
    /// <remarks>
    /// A simulated Arsenal runs beside the real one rather than replacing it, so the two
    /// can be compared and the real laptop stays managed throughout. A launch without a
    /// profile is an ordinary launch: it raises the real Arsenal if it is running and
    /// starts it if not.
    /// </remarks>
    public static void Launch(string? profileId)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Application.ExecutablePath,
            Arguments = profileId is null ? string.Empty : $"{Argument} \"{profileId}\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Reads this machine into a profile.
    /// </summary>
    /// <remarks>
    /// Every DSTS device id in the classes ASUS uses, the same sweep the firmware scan in
    /// Advanced makes, plus the three fan curves once per mode because those are the
    /// reads that take an argument. DSTS only reads; nothing here writes. Takes a few
    /// seconds, so it belongs on a background thread.
    /// </remarks>
    public static SimulationProfile Capture(AsusACPI acpi)
    {
        if (IsActive) throw new InvalidOperationException("A simulated machine cannot be captured.");

        (string series, string layout, bool oled) = ReadAsusRecord();
        string model = AppConfig.GetModel();

        var profile = new SimulationProfile
        {
            Name = AppConfig.GetModelDisplayName() + " (" + AppConfig.GetModelShort() + ", captured)",
            Model = model,
            Bios = ReadBios(),
            Series = series,
            KeyboardLayout = layout,
            OledCare = oled,
            Captured = true,
            Source = $"Captured from this machine on {DateTime.Now:d MMMM yyyy}.",
        };

        for (uint group = 0x00000000; group <= 0x00160000; group += 0x10000)
        {
            for (uint low = 0; low <= 0xFF; low++)
            {
                uint id = group + low;
                if (TryCapture(acpi, id, 0) is { } entry) profile.Firmware[(id, 0)] = entry;
            }
        }

        foreach (uint curve in new[] { AsusACPI.DevsCPUFanCurve, AsusACPI.DevsGPUFanCurve, AsusACPI.DevsMidFanCurve })
            for (uint mode = 1; mode <= 2; mode++)
                if (TryCapture(acpi, curve, mode) is { } entry) profile.Firmware[(curve, mode)] = entry;

        // The fan range is written beside every custom curve and never read, so no read
        // can find it. A machine with curves has it.
        if (profile.Firmware.ContainsKey((AsusACPI.DevsCPUFanCurve, 0)))
        {
            profile.Accepts.Add(AsusACPI.DevsCPUFan);
            profile.Accepts.Add(AsusACPI.DevsGPUFan);
        }

        foreach (IPeripheral peripheral in PeripheralsProvider.AllPeripherals())
            profile.Peripherals.Add(peripheral.GetType().Name);

        profile.Id = FileSafe(AppConfig.GetModelShort() is { Length: > 0 } shortName ? shortName : "machine") + "-captured";
        return profile;
    }

    /// <summary>Writes a profile to <see cref="ProfilesDirectory"/> and returns the file.</summary>
    public static string Save(SimulationProfile profile)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        string path = Path.Combine(ProfilesDirectory, profile.Id + ".json");
        File.WriteAllText(path, profile.ToJson());
        return path;
    }

    private static FirmwareEntry? TryCapture(AsusACPI acpi, uint id, uint argument)
    {
        byte[] raw = acpi.DeviceGetRaw(id, argument);
        int head = BitConverter.ToInt32(raw, 0);

        bool extra = raw.Skip(4).Any(value => value != 0);
        if (head - 0x10000 < 0 && !extra) return null;

        // A plain register is kept as the number DeviceGet would report, which reads
        // better and can be edited; anything with a payload keeps every byte.
        if (!extra) return new FirmwareEntry { Value = head - 0x10000 };

        int length = raw.Length;
        while (length > 4 && raw[length - 1] == 0) length--;
        return new FirmwareEntry { Raw = raw[..length] };
    }

    private static string ReadBios()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (ManagementBaseObject bios in searcher.Get())
                using (bios) return bios["SMBIOSBIOSVersion"]?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Simulator capture BIOS: " + ex.Message);
        }
        return string.Empty;
    }

    private static (string Series, string Layout, bool Oled) ReadAsusRecord()
    {
        try
        {
            using RegistryKey? record = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASUS\RLSinfo");
            using RegistryKey? oled = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ASUS\OLEDCare");
            return (
                record?.GetValue("Series")?.ToString() ?? string.Empty,
                record?.GetValue("KeyboardLayout")?.ToString() ?? string.Empty,
                oled is not null && Convert.ToInt32(oled.GetValue("EnablePixelRefresh", 0)) != 0);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Simulator capture registry: " + ex.Message);
            return (string.Empty, string.Empty, false);
        }
    }

    private static string FileSafe(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
