using System.Reflection;

namespace Arsenal.Simulation;

/// <summary>One thing Arsenal tried to do to the machine while simulating.</summary>
/// <param name="Accepted">False when the firmware would have refused it, or it was not run at all.</param>
public sealed record SimulatedAction(DateTime Time, string Description, bool Accepted);

/// <summary>
/// Answers the ATKACPI driver's control calls from a profile.
/// </summary>
/// <remarks>
/// Every firmware conversation Arsenal has goes through one IOCTL, so this sits where
/// that call would be made and speaks the same byte layout: a method id, an argument
/// length, then the arguments. DSTS reads a device id, DEVS writes one.
///
/// <para>Writes are remembered. Setting a value and reading it back returns what was
/// set, which is what the firmware does for the plain registers - performance mode, GPU
/// mode, charge limit - and is what makes the UI settle on a choice instead of snapping
/// back to the profile's starting value. A write to a raw captured buffer is accepted
/// and logged but does not change what is read: those are curves and tables whose
/// read-back layout is not the layout that was written.</para>
///
/// <para>An id the profile does not list answers the way real firmware answers an id it
/// does not implement, with <c>0xFFFFFFFE</c>. That is the whole mechanism by which a
/// profile hides a feature: Arsenal asks, gets "unsupported", and does not show it.</para>
/// </remarks>
public sealed class SimulatedFirmware
{
    private const uint ControlCode = 0x0022240C;
    private const uint DSTS = 0x53545344;
    private const uint DEVS = 0x53564544;
    private const uint INIT = 0x54494E49;
    private const uint WDOG = 0x474F4457;
    private const uint Unsupported = 0xFFFFFFFE;

    private static readonly Lazy<Dictionary<uint, string>> Names = new(ReadNames);

    private readonly SimulationProfile _profile;
    private readonly object _lock = new();
    private readonly HashSet<uint> _known;

    public SimulatedFirmware(SimulationProfile profile)
    {
        _profile = profile;
        _known = profile.Firmware.Keys.Select(key => key.Id).Concat(profile.Accepts).ToHashSet();
    }

    /// <summary>Raised for every write, so the simulator can show what would have happened.</summary>
    public event Action<SimulatedAction>? Wrote;

    public bool Answer(uint controlCode, byte[] input, byte[] output)
    {
        Array.Clear(output);

        // The event listener codes (0x222400, 0x222408) wait on hardware notifications.
        // There are none to deliver, so they get an empty answer and nothing blocks.
        if (controlCode != ControlCode || input.Length < 12) return true;

        uint method = BitConverter.ToUInt32(input, 0);
        uint deviceId = BitConverter.ToUInt32(input, 8);
        uint argument = input.Length >= 16 ? BitConverter.ToUInt32(input, 12) : 0;

        lock (_lock)
        {
            switch (method)
            {
                case DSTS:
                    Read(deviceId, argument, output);
                    break;

                case DEVS:
                    Write(deviceId, input.AsSpan(12), output);
                    break;

                case INIT:
                case WDOG:
                    BitConverter.GetBytes(1).CopyTo(output, 0);
                    break;
            }
        }

        return true;
    }

    private void Read(uint deviceId, uint argument, byte[] output)
    {
        if (_profile.Firmware.TryGetValue((deviceId, argument), out FirmwareEntry? entry) ||
            _profile.Firmware.TryGetValue((deviceId, 0), out entry))
        {
            entry.WriteTo(output);
            return;
        }

        BitConverter.GetBytes(Unsupported).CopyTo(output, 0);
    }

    private void Write(uint deviceId, ReadOnlySpan<byte> payload, byte[] output)
    {
        bool accepted = _known.Contains(deviceId);

        if (accepted && payload.Length == 4 &&
            _profile.Firmware.TryGetValue((deviceId, 0), out FirmwareEntry? entry) && entry.Raw is null)
        {
            entry.Value = BitConverter.ToInt32(payload);
        }

        BitConverter.GetBytes(accepted ? 1u : Unsupported).CopyTo(output, 0);

        string value = payload.Length == 4
            ? "0x" + BitConverter.ToUInt32(payload).ToString("X")
            : Convert.ToHexString(payload.TrimEnd((byte)0));
        Wrote?.Invoke(new SimulatedAction(DateTime.Now,
            $"Firmware {NameOf(deviceId)} = {value}" + (accepted ? string.Empty : ", refused: not in the profile"),
            accepted));
    }

    /// <summary>The AsusACPI constant a device id is declared as, for the write log.</summary>
    public static string NameOf(uint deviceId) =>
        Names.Value.TryGetValue(deviceId, out string? name) ? $"{name} (0x{deviceId:X8})" : $"0x{deviceId:X8}";

    private static Dictionary<uint, string> ReadNames()
    {
        var names = new Dictionary<uint, string>();
        foreach (FieldInfo field in typeof(AsusACPI).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral) continue;

            uint value = field.GetRawConstantValue() switch
            {
                uint number => number,
                int number => unchecked((uint)number),
                _ => 0,
            };

            // Device ids carry a class in the high word; the small constants beside them
            // are key codes and mode numbers that would only collide.
            if (value > 0xFFFF) names.TryAdd(value, field.Name);
        }
        return names;
    }
}
