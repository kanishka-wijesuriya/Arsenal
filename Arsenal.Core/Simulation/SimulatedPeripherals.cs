using Arsenal.Peripherals.Headset;
using Arsenal.Peripherals.Keyboard;
using Arsenal.Peripherals.Mouse;

namespace Arsenal.Simulation;

/// <summary>
/// The mice, keyboards and headsets a profile plugs in, and what a pretend mouse says back.
/// </summary>
/// <remarks>
/// Built from the same model classes the real detection uses, found by class name, so a
/// simulated Chakram X has exactly the capabilities the real one is declared with: its
/// DPI range, its lighting zones, its polling rates. Only the conversation is invented.
/// Keyboards and headsets already have a test mode that answers with canned replies;
/// mice get the same, answered here.
/// </remarks>
public static class SimulatedPeripherals
{
    private static readonly Lazy<Dictionary<string, Type>> MouseTypes = new(() => FindTypes(typeof(AsusMouse)));
    private static readonly Lazy<Dictionary<string, Type>> KeyboardTypes = new(() => FindTypes(typeof(AsusKeyboard)));
    private static readonly Lazy<Dictionary<string, Type>> HeadsetTypes = new(() => FindTypes(typeof(AsusHeadset)));

    /// <summary>Every model class that can be plugged in, for the profile editor and the tests.</summary>
    public static IEnumerable<string> MouseNames => MouseTypes.Value.Keys.Order();
    public static IEnumerable<string> KeyboardNames => KeyboardTypes.Value.Keys.Order();
    public static IEnumerable<string> HeadsetNames => HeadsetTypes.Value.Keys.Order();

    public static bool IsKnown(string name) =>
        MouseTypes.Value.ContainsKey(name) || KeyboardTypes.Value.ContainsKey(name) || HeadsetTypes.Value.ContainsKey(name);

    /// <summary>One model that can be plugged in, by class and by the name it shows.</summary>
    public sealed record Model(string ClassName, string DisplayName, bool IsKeyboard, bool IsHeadset = false);

    private static readonly Lazy<IReadOnlyList<Model>> Models = new(() =>
    {
        List<Model> models = MouseTypes.Value.Select(pair => new Model(pair.Key, DisplayNameOf(pair.Value, pair.Key), false))
            .Concat(KeyboardTypes.Value.Select(pair => new Model(pair.Key, DisplayNameOf(pair.Value, pair.Key), true)))
            .Concat(HeadsetTypes.Value.Select(pair => new Model(pair.Key, DisplayNameOf(pair.Value, pair.Key), false, true)))
            .ToList();

        // Some variants share a name with the model they differ from only in colour or
        // edition, so those carry their class to tell them apart.
        HashSet<string> shared = models.GroupBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return models
            .Select(model => shared.Contains(model.DisplayName) ? model with { DisplayName = $"{model.DisplayName} ({model.ClassName})" } : model)
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    /// <summary>Every mouse, pad, keyboard and headset the simulator can plug in, by display name.</summary>
    public static IReadOnlyList<Model> Catalogue() => Models.Value;

    /// <summary>
    /// The model's own name, as the Peripherals page will show it. Wired and wireless
    /// variants are separate classes and say so in their names.
    /// </summary>
    private static string DisplayNameOf(Type type, string fallback)
    {
        try
        {
            object? instance = Activator.CreateInstance(type);
            string name = (instance as Arsenal.Peripherals.IPeripheral)?.GetDisplayName() ?? fallback;
            (instance as IDisposable)?.Dispose();
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch
        {
            return fallback;
        }
    }

    // ---- Which peripherals a simulated laptop has ----------------------------------

    /// <summary>
    /// Where the user's own choice of peripherals for one laptop is kept, beside that
    /// laptop's simulated settings. The profile's list is only the starting point.
    /// </summary>
    private static string ChoicePath(string profileId) =>
        Path.Combine(DeviceSimulation.Root, "Sessions", profileId, "peripherals.json");

    /// <summary>Whether the peripherals for this laptop were chosen rather than taken from its profile.</summary>
    public static bool HasChoice(string profileId) => File.Exists(ChoicePath(profileId));

    /// <summary>The peripherals this laptop will have: the chosen ones, or else the profile's.</summary>
    public static List<string> ChosenFor(SimulationProfile profile)
    {
        try
        {
            string path = ChoicePath(profile.Id);
            if (File.Exists(path) &&
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) is { } chosen)
                return chosen.Where(IsKnown).Distinct().ToList();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Simulator peripherals: " + ex.Message);
        }
        return profile.Peripherals.ToList();
    }

    /// <summary>
    /// Keeps a choice of peripherals for a laptop; null goes back to the profile's own.
    /// </summary>
    public static void Choose(string profileId, IEnumerable<string>? classNames)
    {
        string path = ChoicePath(profileId);
        if (classNames is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(classNames.Where(IsKnown).Distinct().ToList()));
    }

    /// <summary>
    /// A copy of what is plugged in. Detection can run on a device-change thread while
    /// <see cref="PlugIn"/> is changing the list on another.
    /// </summary>
    private static List<string> Plugged(SimulationProfile profile)
    {
        lock (profile.Peripherals) return profile.Peripherals.ToList();
    }

    /// <summary>Puts the chosen peripherals in place as a simulation starts.</summary>
    internal static void ApplyChoice(SimulationProfile profile)
    {
        List<string> chosen = ChosenFor(profile);
        profile.Peripherals.Clear();
        profile.Peripherals.AddRange(chosen);
    }

    /// <summary>
    /// Plugs in and unplugs peripherals in the running simulation, as though they had
    /// been connected to the laptop or pulled out of it. Blocks while each one
    /// connects, so call it off the UI thread.
    /// </summary>
    public static void PlugIn(IReadOnlyCollection<string> classNames)
    {
        if (DeviceSimulation.Profile is not { } profile) return;

        lock (profile.Peripherals)
        {
            profile.Peripherals.Clear();
            profile.Peripherals.AddRange(classNames.Where(IsKnown).Distinct());
        }

        foreach (AsusMouse mouse in Arsenal.Peripherals.PeripheralsProvider.SnapshotMice())
            if (mouse.TestMode && !classNames.Contains(mouse.GetType().Name))
            {
                Logger.WriteLine($"Simulator: unplugged {mouse.GetDisplayName()}");
                Arsenal.Peripherals.PeripheralsProvider.Disconnect(mouse);
            }

        foreach (AsusKeyboard keyboard in Arsenal.Peripherals.PeripheralsProvider.SnapshotKeyboards())
            if (keyboard.TestMode && !classNames.Contains(keyboard.GetType().Name))
            {
                Logger.WriteLine($"Simulator: unplugged {keyboard.GetDisplayName()}");
                Arsenal.Peripherals.PeripheralsProvider.Disconnect(keyboard);
            }

        foreach (AsusHeadset headset in Arsenal.Peripherals.PeripheralsProvider.SnapshotHeadsets())
            if (headset.TestMode && !classNames.Contains(headset.GetType().Name))
            {
                Logger.WriteLine($"Simulator: unplugged {headset.GetDisplayName()}");
                Arsenal.Peripherals.PeripheralsProvider.Disconnect(headset);
            }

        Arsenal.Peripherals.PeripheralsProvider.DetectAllAsusMice();
        Arsenal.Peripherals.PeripheralsProvider.DetectAllAsusKeyboards();
        Arsenal.Peripherals.PeripheralsProvider.DetectAllAsusHeadsets();
    }

    public static IEnumerable<AsusMouse> Mice()
    {
        if (DeviceSimulation.Profile is not { } profile) yield break;

        foreach (string name in Plugged(profile))
        {
            if (!MouseTypes.Value.TryGetValue(name, out Type? type)) continue;
            if (Activator.CreateInstance(type) is AsusMouse mouse)
            {
                mouse.TestMode = true;
                yield return mouse;
            }
        }
    }

    public static IEnumerable<AsusKeyboard> Keyboards()
    {
        if (DeviceSimulation.Profile is not { } profile) yield break;

        foreach (string name in Plugged(profile))
        {
            if (!KeyboardTypes.Value.TryGetValue(name, out Type? type)) continue;
            if (Activator.CreateInstance(type) is AsusKeyboard keyboard)
            {
                keyboard.TestMode = true;
                yield return keyboard;
            }
        }
    }

    public static IEnumerable<AsusHeadset> Headsets()
    {
        if (DeviceSimulation.Profile is not { } profile) yield break;

        foreach (string name in Plugged(profile))
        {
            if (!HeadsetTypes.Value.TryGetValue(name, out Type? type)) continue;
            if (Activator.CreateInstance(type) is AsusHeadset headset)
            {
                headset.TestMode = true;
                yield return headset;
            }
        }
    }

    /// <summary>
    /// The reply a mouse in test mode gives to one request.
    /// </summary>
    /// <remarks>
    /// The request's first four bytes are echoed, because the reader matches replies to
    /// requests on them, and the data after is chosen so each reader lands on something
    /// ordinary: a charged battery, a spread of DPI stages that fit the model's own
    /// increments, 1000 Hz, a lit static colour. Anything this does not recognise gets
    /// the echo alone, which the readers take as "nothing set".
    /// </remarks>
    public static byte[] MouseReply(AsusMouse mouse, byte[] request, int size)
    {
        byte[] reply = new byte[size];
        Array.Copy(request, reply, Math.Min(4, request.Length));

        byte command = request.Length > 1 ? request[1] : (byte)0;
        byte group = request.Length > 2 ? request[2] : (byte)0;
        byte detail = request.Length > 3 ? request[3] : (byte)0;

        if (command != 0x12)
        {
            DeviceSimulation.Record($"{mouse.GetDisplayName()}: {Convert.ToHexString(request.AsSpan(0, Math.Min(request.Length, 12)))}");
            return reply;
        }

        switch (group, detail)
        {
            case (0x07, _):                          // battery and energy
                reply[5] = 76;
                reply[6] = 0x02;
                reply[7] = 20;
                break;

            case (0x04, 0x00):                       // DPI stages, polling, snapping, debounce
                WriteDpiStages(mouse, reply, xy: false);
                reply[13] = 0x03;
                reply[15] = 0x02;
                break;

            case (0x04, 0x02):
                WriteDpiStages(mouse, reply, xy: true);
                break;

            case (0x04, 0x03):                       // one colour per DPI stage
                byte[][] colours = [[0xFF, 0x30, 0x30], [0x30, 0xFF, 0x30], [0x30, 0x80, 0xFF], [0xFF, 0xC0, 0x00]];
                for (int i = 0; i < Math.Min(mouse.DPIProfileCount(), 4); i++)
                    colours[i].CopyTo(reply, 5 + i * 3);
                break;

            case (0x03, _):                          // lighting
                reply[5] = 0x00;
                reply[6] = (byte)Math.Max(1, mouse.MaxBrightness() * 3 / 4);
                reply[7] = 0xE0;
                reply[8] = 0x20;
                reply[9] = 0x40;
                break;
        }

        return reply;
    }

    private static void WriteDpiStages(AsusMouse mouse, byte[] reply, bool xy)
    {
        int increment = Math.Max(1, mouse.DPIIncrements());
        int[] wanted = [400, 800, 1600, 3200];

        for (int i = 0; i < Math.Min(mouse.DPIProfileCount(), wanted.Length); i++)
        {
            int dpi = Math.Clamp(wanted[i], mouse.MinDPI(), mouse.MaxDPI());
            ushort encoded = (ushort)Math.Max(0, (dpi - increment) / increment);
            int offset = xy ? 5 + i * 4 : 5 + i * 2;

            reply[offset] = (byte)(encoded & 0xFF);
            reply[offset + 1] = (byte)(encoded >> 8);
            if (xy)
            {
                reply[offset + 2] = reply[offset];
                reply[offset + 3] = reply[offset + 1];
            }
        }
    }

    private static Dictionary<string, Type> FindTypes(Type baseType) =>
        baseType.Assembly.GetTypes()
            .Where(type => !type.IsAbstract && baseType.IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToDictionary(type => type.Name, StringComparer.OrdinalIgnoreCase);
}
