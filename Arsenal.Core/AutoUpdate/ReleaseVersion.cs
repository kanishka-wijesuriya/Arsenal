using System.Reflection;

namespace Arsenal.AutoUpdate;

public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly string[] _preRelease;

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Value { get; }
    public bool IsPreRelease => _preRelease.Length > 0;

    private ReleaseVersion(int major, int minor, int patch, string[] preRelease, string value)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
        Value = value;
    }

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string clean = value.Trim().TrimStart('v', 'V');
        int metadataAt = clean.IndexOf('+');
        if (metadataAt >= 0) clean = clean[..metadataAt];

        string core = clean;
        string[] preRelease = Array.Empty<string>();
        int preAt = clean.IndexOf('-');
        if (preAt >= 0)
        {
            core = clean[..preAt];
            string suffix = clean[(preAt + 1)..];
            if (suffix.Length == 0) return false;
            preRelease = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries);
        }

        string[] parts = core.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch)
            || major < 0 || minor < 0 || patch < 0) return false;

        if (preRelease.Any(part => part.Length == 0 || part.Any(character => !char.IsLetterOrDigit(character) && character != '-'))) return false;
        version = new ReleaseVersion(major, minor, patch, preRelease, clean);
        return true;
    }

    public static ReleaseVersion Parse(string value) =>
        TryParse(value, out ReleaseVersion? version) ? version : throw new FormatException($"Invalid release version: {value}");

    public static string CurrentString()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        Version? numeric = assembly.GetName().Version;
        return numeric is null ? "0.0.0" : $"{numeric.Major}.{numeric.Minor}.{Math.Max(0, numeric.Build)}";
    }

    /// <summary>
    /// Keeps the three-part value used for update comparisons while presenting an
    /// initial zero-patch release as the cleaner product label "1.0".
    /// </summary>
    public static string DisplayString(string value)
    {
        if (!TryParse(value, out ReleaseVersion? version) || version is null) return value;
        return !version.IsPreRelease && version.Patch == 0
            ? $"{version.Major}.{version.Minor}"
            : version.Value;
    }

    public static string CurrentDisplayString() => DisplayString(CurrentString());

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (!IsPreRelease && !other.IsPreRelease) return 0;
        if (!IsPreRelease) return 1;
        if (!other.IsPreRelease) return -1;

        int count = Math.Max(_preRelease.Length, other._preRelease.Length);
        for (int index = 0; index < count; index++)
        {
            if (index >= _preRelease.Length) return -1;
            if (index >= other._preRelease.Length) return 1;
            string left = _preRelease[index];
            string right = other._preRelease[index];
            bool leftNumber = int.TryParse(left, out int leftValue);
            bool rightNumber = int.TryParse(right, out int rightValue);
            int comparison = leftNumber && rightNumber
                ? leftValue.CompareTo(rightValue)
                : leftNumber ? -1
                : rightNumber ? 1
                : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    public override string ToString() => Value;
}
