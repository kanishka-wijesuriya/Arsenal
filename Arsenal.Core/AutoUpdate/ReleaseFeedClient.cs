using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arsenal.AutoUpdate;

public sealed class ReleaseFeedClient
{
    public const string StableFeedUrl = "https://get-arsenal.com/api/v1/windows/stable.json";

    /// <summary>
    /// The signing key the server is currently expected to use.
    /// </summary>
    /// <remarks>
    /// Informational only. Trust is decided by <see cref="TrustedKeys"/>, not by this
    /// value, a feed signed by any key in that set is accepted regardless of which one
    /// is nominally current.
    /// </remarks>
    public const string KeyId = "8aea25a6f920d0d2";

    /// <summary>
    /// Every signing key this build accepts, by the key id the envelope carries.
    /// </summary>
    /// <remarks>
    /// A single embedded key cannot be rotated: the moment the server signs with a new
    /// one, every installed copy rejects the feed and stops receiving updates - including
    /// the update that would have taught it the new key. Rotation therefore has to run
    /// one release ahead of the server.
    ///
    /// <para>The order is: ship a build that trusts both keys, wait for it to reach the
    /// installed base, then switch the server to the new key, and only then drop the old
    /// one from this set in a later build. Removing a key here before the server has
    /// stopped using it strands everybody; adding one early costs nothing.</para>
    ///
    /// <para><c>8aea25a6f920d0d2</c> signed every release up to and including 1.0.0 and is
    /// still what the server uses. <c>0070164b938ed87f</c> replaces it because the first
    /// key was copied into a distributable deployment archive and must be treated as
    /// exposed.</para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> TrustedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["8aea25a6f920d0d2"] = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAtpoLM04XS++f4aXyNbYu
z1Rd29m/tX7CjFaVBA9CeQhJg+wV54PfOyAPX1yaV/eXof80V3yUgZi5ar6EBfP/
TjOQZWk8SVjzXr4O89d7pW7vUGvln4W8jvTZtrjO1hAlIBo5ru+u4wFDbr6nh2Ad
XXPrr5iTA+6jMyrUNImzKi7nTaDMwIVed30fCrV0npgnvoxkg8muB5xyaCIhbt2z
AbRvr/jE+hQWglel5zPSUelB6ouOVlITVQItZ+T9GB1Q3JPMRVYfAqaZ6yc4FJ6t
r0RvKTGvPYL00Cb+/vPd/yk7G1z+YuFniRq91UFyMNWmI25sBjovtdAZzWGRZPo5
2l8zR0xoRmyWpclTlv9HJpq5ZNb0Czg1JUI06bVcJ26vS8uMKzGqgy9fo0COldQe
5ONMA+k7Vn3Z8tllTyN56dlD3oD2Ag0SXH11jkJj7JFT/nfu8/7EQX42WJhJtXQR
iArBUCxjJh24510axaoXutw7ZoI7e2roPcItoNGjdTN1AgMBAAE=
-----END PUBLIC KEY-----
""",
        ["0070164b938ed87f"] = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEArtbWEWGq9Erpwwf+XaMG
FvITzF90QJQ1ZWT2aAAhmcurQy1ncHst6oHCaLa5zdCJxO3Ih+l5rBvwsXQWQRtG
AILPnXqqT0wpnlHYonWDgya8LdQv7X7bUxiZMfnYz4ITEjTy3slvrNVcJ0ifZVmU
959m+hABsLk5yWwamI9pApj3I1UimjtMYSMygBE1EQTVVzVKIqoKXW1zriuq0UST
XcnZrEYyLlh9GzYMFxjS0FmnqSxpJ1HTH0agrjQ4uTu5otSUH9iOj4HpnBvZS2VJ
U425w00ORDMB/Qz1mOjDzWLfbJ4/Ll3hnZ3r/on+OKlANXM+skpE/rYbw816+8m9
bhbP/nGdpi3EZDBJz24JLIKF3ARy5uSSV2zXR2ZBvQ8JHp43qyxZFeRgsn058Dxd
3SFh3gcY8Fl+DQc5CtQUFytOY+8MiW+g43TNq9O99HNmKr374qXcIOYIbwKUHoCy
VR1aRvHEAiPJd+LFIbrMqQfNTy5Np+lknh404pygfSChAgMBAAE=
-----END PUBLIC KEY-----
""",
    };

    private static readonly HttpClient Client = CreateClient();
    private readonly Uri _feedUri;

    public ReleaseFeedClient(string feedUrl = StableFeedUrl)
    {
        _feedUri = new Uri(feedUrl, UriKind.Absolute);
    }

    public async Task<ReleaseUpdate> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Client.GetAsync(_feedUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] envelopeBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (envelopeBytes.Length > 262144) throw new InvalidDataException("The Arsenal release feed is unexpectedly large.");

        return ParseSignedEnvelope(envelopeBytes);
    }

    public static ReleaseUpdate ParseSignedEnvelope(byte[] envelopeBytes)
    {
        using JsonDocument envelopeDocument = JsonDocument.Parse(envelopeBytes);
        JsonElement envelope = envelopeDocument.RootElement;
        if (envelope.GetProperty("schema").GetInt32() != 1
            || envelope.GetProperty("algorithm").GetString() != "RSA-SHA256-PKCS1")
            throw new CryptographicException("The Arsenal release feed uses an unknown signing format.");

        // The envelope names which key signed it, and that name only selects which of the
        // keys this build already trusts to verify against. An unrecognised id is refused
        // outright rather than falling back to trying them all, so a feed can never be
        // accepted on the strength of a key this build was not shipped with.
        string keyId = envelope.GetProperty("key_id").GetString() ?? "";
        if (!TrustedKeys.TryGetValue(keyId, out string? publicKeyPem))
            throw new CryptographicException("The Arsenal release feed is signed by an unrecognised key.");

        byte[] payload = Convert.FromBase64String(envelope.GetProperty("payload").GetString() ?? "");
        byte[] signature = Convert.FromBase64String(envelope.GetProperty("signature").GetString() ?? "");
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("The Arsenal release feed signature is invalid.");

        using JsonDocument payloadDocument = JsonDocument.Parse(payload);
        JsonElement root = payloadDocument.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("product").GetString() != "Arsenal")
            throw new InvalidDataException("The signed release payload is invalid.");

        string version = root.GetProperty("version").GetString() ?? "";
        string minimum = root.GetProperty("minimum_supported_version").GetString() ?? "";
        if (!ReleaseVersion.TryParse(version, out _) || !ReleaseVersion.TryParse(minimum, out _))
            throw new InvalidDataException("The signed release contains an invalid version.");

        JsonElement package = root.GetProperty("package");
        string packageUrl = package.GetProperty("url").GetString() ?? "";
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out Uri? packageUri)
            || packageUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(packageUri.Host, "get-arsenal.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The signed release package URL is not trusted.");

        long bytes = package.GetProperty("bytes").GetInt64();
        string sha256 = package.GetProperty("sha256").GetString() ?? "";
        if (bytes < 1 || bytes > 536870912 || sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The signed release package details are invalid.");

        List<string> notes = new();
        foreach (JsonElement note in root.GetProperty("notes").EnumerateArray())
        {
            string? text = note.GetString();
            if (!string.IsNullOrWhiteSpace(text)) notes.Add(text);
        }

        return new ReleaseUpdate(
            version,
            root.GetProperty("title").GetString() ?? $"Arsenal {version}",
            notes,
            root.GetProperty("published_at").GetString() ?? "",
            root.GetProperty("mandatory").GetBoolean(),
            minimum,
            packageUri.AbsoluteUri,
            bytes,
            sha256.ToLowerInvariant());
    }

    /// <summary>
    /// Downloads and verifies the signed package, reporting bytes received as it
    /// goes so a caller can show real progress rather than an indeterminate wait.
    /// </summary>
    public async Task<string> DownloadVerifiedPackageAsync(ReleaseUpdate release, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        string updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arsenal", "Updates", release.Version);
        Directory.CreateDirectory(updateDirectory);
        string destination = Path.Combine(updateDirectory, "Arsenal-update.exe");
        string temporary = destination + ".download";
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared && declared != release.PackageBytes)
                throw new InvalidDataException("The update package size does not match the signed release.");

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream target = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > release.PackageBytes) throw new InvalidDataException("The update package is larger than the signed release.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(total);
                }
                if (total != release.PackageBytes) throw new InvalidDataException("The update package is incomplete.");
            }

            // Scoped so the handle is closed before the move below: File.Move needs
            // delete access to the source, which File.OpenRead's share mode denies, so
            // a stream still open here fails every install with "being used by another
            // process" after a download that verified perfectly.
            await using (FileStream package = File.OpenRead(temporary))
            {
                if (package.ReadByte() != 'M' || package.ReadByte() != 'Z')
                    throw new InvalidDataException("The signed update is not a Windows executable.");
                package.Position = 0;
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(release.PackageSha256)))
                    throw new CryptographicException("The update package checksum does not match the signed release.");
            }

            File.Move(temporary, destination, true);
            return destination;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClientHandler handler = new() { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        HttpClient client = new(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Arsenal-Windows-Updater/1.0");
        client.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
        return client;
    }
}
