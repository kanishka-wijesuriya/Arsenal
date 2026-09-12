namespace Arsenal.AutoUpdate;

public sealed record ReleaseUpdate(
    string Version,
    string Title,
    IReadOnlyList<string> Notes,
    string PublishedAt,
    bool Mandatory,
    string MinimumSupportedVersion,
    string PackageUrl,
    long PackageBytes,
    string PackageSha256);
