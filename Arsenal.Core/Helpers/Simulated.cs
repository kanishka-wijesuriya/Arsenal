using Arsenal.AnimeMatrix.Communication.Platform;

namespace Arsenal.Helpers;

/// <summary>
/// The points where the device simulator reaches into the rest of Arsenal.
/// </summary>
/// <remarks>
/// Every hook the app calls is here, in one file that is always compiled, so the code at
/// each call site reads the same in every build: <c>if (Simulated.Skip("...")) return;</c>.
/// In a build without the simulator these are constant answers - never active, nothing
/// to substitute - and the simulator itself, which lives in the Simulation folders, is
/// not compiled at all. A stable release cannot be switched into a simulation because
/// the code that would do it is not in the executable.
///
/// <para>A hook belongs at the narrowest point that every path to the machine passes
/// through: the ACPI control call, the HID enumeration, the process launcher. Guarding
/// individual features instead would leave every feature added later unguarded.</para>
/// </remarks>
public static class Simulated
{
#if ARSENAL_SIMULATOR
    public static bool IsActive => Simulation.DeviceSimulation.IsActive;

    /// <summary>The model name the profile claims, or null to read the real one.</summary>
    public static string? Model => Simulation.DeviceSimulation.Profile?.Model;

    /// <summary>The raw SMBIOS BIOS version, for example <c>GU605MI.312</c>.</summary>
    public static string? Bios => Simulation.DeviceSimulation.Profile?.Bios;

    /// <summary>What ASUS software would have recorded in RLSinfo.</summary>
    public static (string Series, string KeyboardLayout)? MachineInfo =>
        Simulation.DeviceSimulation.Profile is { } profile ? (profile.Series, profile.KeyboardLayout) : null;

    public static bool? OledCare => Simulation.DeviceSimulation.Profile?.OledCare;

    /// <summary>Where a simulated session keeps its own settings and log.</summary>
    public static string? StateDirectory => Simulation.DeviceSimulation.StateDirectory;

    /// <summary>
    /// Stands in for an action that would change the real machine.
    /// </summary>
    /// <returns>True when simulating, in which case the caller must not do it.</returns>
    public static bool Skip(string action) => Simulation.DeviceSimulation.Skip(action);

    /// <summary>Answers one ATKACPI control call from the profile instead of the firmware.</summary>
    public static bool TryFirmware(uint controlCode, byte[] input, byte[] output) =>
        Simulation.DeviceSimulation.TryFirmware(controlCode, input, output);

    /// <summary>A USB channel that accepts everything and reaches nothing.</summary>
    public static UsbProvider? UsbProvider(ushort vendorId, ushort productId) =>
        IsActive ? new Simulation.SimulatedUsbProvider(vendorId, productId) : null;

    /// <summary>The mice, keyboards and headsets the profile says are plugged in.</summary>
    public static IEnumerable<Peripherals.Mouse.AsusMouse> Mice() => Simulation.SimulatedPeripherals.Mice();

    public static IEnumerable<Peripherals.Keyboard.AsusKeyboard> Keyboards() => Simulation.SimulatedPeripherals.Keyboards();

    public static IEnumerable<Peripherals.Headset.AsusHeadset> Headsets() => Simulation.SimulatedPeripherals.Headsets();

    /// <summary>What a mouse in test mode answers, in place of a USB round trip.</summary>
    public static byte[]? MouseReply(Peripherals.Mouse.AsusMouse mouse, byte[] request, int size) =>
        Simulation.SimulatedPeripherals.MouseReply(mouse, request, size);
#else
    public static bool IsActive => false;
    public static string? Model => null;
    public static string? Bios => null;
    public static (string Series, string KeyboardLayout)? MachineInfo => null;
    public static bool? OledCare => null;
    public static string? StateDirectory => null;
    public static bool Skip(string action) => false;
    public static bool TryFirmware(uint controlCode, byte[] input, byte[] output) => false;
    public static UsbProvider? UsbProvider(ushort vendorId, ushort productId) => null;
    public static IEnumerable<Peripherals.Mouse.AsusMouse> Mice() => [];
    public static IEnumerable<Peripherals.Keyboard.AsusKeyboard> Keyboards() => [];
    public static IEnumerable<Peripherals.Headset.AsusHeadset> Headsets() => [];
    public static byte[]? MouseReply(Peripherals.Mouse.AsusMouse mouse, byte[] request, int size) => null;
#endif
}
