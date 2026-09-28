using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Arsenal.Simulation;

/// <summary>One firmware answer: what DSTS returns for a device id.</summary>
/// <remarks>
/// Either a value, which is what <c>DeviceGet</c> would report and so means the feature
/// exists, or the raw buffer a capture read off a real machine. Raw buffers are kept
/// byte for byte because several readers look past the first four bytes: fan curves,
/// the VRAM option list, battery discharge.
/// </remarks>
public sealed class FirmwareEntry
{
    public int? Value { get; set; }
    public byte[]? Raw { get; init; }

    /// <summary>The bytes DSTS hands back, sized by the caller's buffer.</summary>
    public void WriteTo(byte[] output)
    {
        Array.Clear(output);
        if (Raw is not null)
        {
            Array.Copy(Raw, output, Math.Min(Raw.Length, output.Length));
            return;
        }

        // DeviceGet subtracts 0x10000, the "present" bit, from the head.
        BitConverter.GetBytes(Value!.Value + 0x10000).CopyTo(output, 0);
    }

    public string ToJson() => Raw is not null
        ? "hex:" + Convert.ToHexString(Raw)
        : Value!.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A laptop Arsenal can pretend to be.
/// </summary>
/// <remarks>
/// Stored as JSON, one file per machine. The ones that ship are written by hand from
/// what Arsenal's own model checks expect, and say so in <see cref="Source"/>; the ones a
/// capture writes are read off real firmware and are the only ones that can be called
/// accurate. The firmware table is keyed by device id in hex, with an optional
/// <c>/argument</c> for the reads that take one - the fan curves are read once per mode.
/// </remarks>
public sealed class SimulationProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>The Win32_ComputerSystem model, which most of Arsenal's checks read.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The raw SMBIOS BIOS version, for example <c>G614JV.313</c>.</summary>
    public string Bios { get; set; } = string.Empty;

    /// <summary>The SKU ROG Live Service records in RLSinfo.</summary>
    public string Series { get; set; } = string.Empty;

    /// <summary>The backlight zoning RLSinfo records: SINGLE, 4ZONE or PERKEY.</summary>
    public string KeyboardLayout { get; set; } = string.Empty;

    public bool OledCare { get; set; }

    /// <summary>Where the answers came from, shown beside the profile.</summary>
    public string Source { get; set; } = string.Empty;

    public bool Captured { get; set; }

    /// <summary>Whether this is one of the profiles compiled into the build.</summary>
    public bool BuiltIn { get; set; }

    public Dictionary<(uint Id, uint Argument), FirmwareEntry> Firmware { get; } = new();

    /// <summary>Ids that accept a write without having anything to read back.</summary>
    public HashSet<uint> Accepts { get; } = new();

    /// <summary>Class names of the mice and keyboards to plug in, for example <c>ChakramX</c>.</summary>
    public List<string> Peripherals { get; } = new();

    public override string ToString() => Name;

    public static SimulationProfile Parse(string id, string json)
    {
        JsonNode root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        }) ?? throw new FormatException("The profile is empty.");

        var profile = new SimulationProfile
        {
            Id = id,
            Name = (string?)root["name"] ?? id,
            Model = (string?)root["model"] ?? throw new FormatException("A profile needs a model."),
            Bios = (string?)root["bios"] ?? string.Empty,
            Series = (string?)root["series"] ?? string.Empty,
            KeyboardLayout = (string?)root["keyboardLayout"] ?? string.Empty,
            OledCare = (bool?)root["oledCare"] ?? false,
            Source = (string?)root["source"] ?? string.Empty,
            Captured = (bool?)root["captured"] ?? false,
        };

        if (root["firmware"] is JsonObject firmware)
        {
            foreach (var (key, node) in firmware)
            {
                (uint deviceId, uint argument) = ParseKey(key);
                profile.Firmware[(deviceId, argument)] = ParseEntry(key, node);
            }
        }

        if (root["accepts"] is JsonArray accepts)
            foreach (JsonNode? node in accepts)
                profile.Accepts.Add(ParseId((string?)node ?? string.Empty));

        if (root["peripherals"] is JsonArray peripherals)
            foreach (JsonNode? node in peripherals)
                if ((string?)node is { Length: > 0 } name) profile.Peripherals.Add(name);

        return profile;
    }

    public string ToJson()
    {
        var firmware = new JsonObject();
        foreach (var ((deviceId, argument), entry) in Firmware.OrderBy(pair => pair.Key.Id).ThenBy(pair => pair.Key.Argument))
        {
            string key = deviceId.ToString("X8") + (argument != 0 ? "/" + argument : string.Empty);
            firmware[key] = entry.Raw is not null ? JsonValue.Create(entry.ToJson()) : JsonValue.Create(entry.Value!.Value);
        }

        var root = new JsonObject
        {
            ["name"] = Name,
            ["model"] = Model,
            ["bios"] = Bios,
            ["series"] = Series,
            ["keyboardLayout"] = KeyboardLayout,
            ["oledCare"] = OledCare,
            ["source"] = Source,
            ["captured"] = Captured,
            ["firmware"] = firmware,
            ["accepts"] = new JsonArray(Accepts.Order().Select(id => (JsonNode)JsonValue.Create(id.ToString("X8"))!).ToArray()),
            ["peripherals"] = new JsonArray(Peripherals.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static (uint, uint) ParseKey(string key)
    {
        int slash = key.IndexOf('/');
        if (slash < 0) return (ParseId(key), 0);
        return (ParseId(key[..slash]), uint.Parse(key[(slash + 1)..], CultureInfo.InvariantCulture));
    }

    internal static uint ParseId(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static FirmwareEntry ParseEntry(string key, JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int number)) return new FirmwareEntry { Value = number };

            if (value.TryGetValue(out string? text) && text is not null)
            {
                if (text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
                    return new FirmwareEntry { Raw = Convert.FromHexString(text[4..].Replace(" ", string.Empty).Replace("-", string.Empty)) };

                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return new FirmwareEntry { Value = int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) };
            }
        }

        throw new FormatException($"Firmware entry {key} is neither a number, a 0x value nor hex: bytes.");
    }
}
