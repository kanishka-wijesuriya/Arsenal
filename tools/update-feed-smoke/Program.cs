using Arsenal.AutoUpdate;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: update-feed-smoke stable.json Arsenal-package.exe");
    return 2;
}

ReleaseUpdate release = ReleaseFeedClient.ParseSignedEnvelope(File.ReadAllBytes(args[0]));
FileInfo package = new(args[1]);
if (package.Length != release.PackageBytes) throw new InvalidDataException("Package byte count does not match the signed feed.");
using (FileStream stream = package.OpenRead())
{
    string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(release.PackageSha256)))
        throw new CryptographicException("Package hash does not match the signed feed.");
}
using (FileStream executable = package.OpenRead())
{
    if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z')
        throw new InvalidDataException("The release package is not a Windows executable.");
}
if (ReleaseVersion.Parse(release.Version).CompareTo(ReleaseVersion.Parse("0.9.9")) <= 0)
    throw new InvalidDataException("The stable release version is unexpectedly old.");

JsonObject tampered = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
string encodedPayload = tampered["payload"]!.GetValue<string>();
tampered["payload"] = (encodedPayload[0] == 'A' ? 'B' : 'A') + encodedPayload[1..];
try
{
    ReleaseFeedClient.ParseSignedEnvelope(System.Text.Encoding.UTF8.GetBytes(tampered.ToJsonString()));
    throw new InvalidDataException("A tampered release feed was accepted.");
}
catch (CryptographicException) { }

Console.WriteLine($"Signed feed smoke passed: {release.Title}, {package.Length} bytes, {release.Notes.Count} Windows changelog entries.");
return 0;
