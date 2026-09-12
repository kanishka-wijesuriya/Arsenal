using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arsenal.AutoUpdate;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The signature check that decides whether a downloaded executable gets run.
/// </summary>
/// <remarks>
/// This is the highest-consequence branch in the application. Everything downstream
/// (the size check, the SHA-256 and the installer script) trusts a payload that got
/// past here. These tests use a freshly generated key that the build does not trust, so a
/// payload signed with it must always be rejected; the accepting path is exercised
/// against the real embedded key using a vendored copy of the published feed.
/// </remarks>
public class ReleaseFeedTests
{
    [Fact]
    public void RejectsAnEnvelopeSignedByAnUntrustedKey()
    {
        using RSA attacker = RSA.Create(3072);
        byte[] envelope = BuildEnvelope(ValidPayload(), attacker, ReleaseFeedClient.KeyId);

        // The signature is real and internally consistent. It is simply not ours.
        Assert.Throws<CryptographicException>(() => ReleaseFeedClient.ParseSignedEnvelope(envelope));
    }

    [Fact]
    public void RejectsAnUnknownKeyId()
    {
        using RSA attacker = RSA.Create(3072);
        byte[] envelope = BuildEnvelope(ValidPayload(), attacker, "0000000000000000");

        Assert.Throws<CryptographicException>(() => ReleaseFeedClient.ParseSignedEnvelope(envelope));
    }

    [Fact]
    public void RejectsATamperedPayload()
    {
        // Sign one payload, then ship a different one under that signature; the shape
        // an attacker who can rewrite the response but not sign would produce.
        using RSA attacker = RSA.Create(3072);
        byte[] envelope = BuildEnvelope(ValidPayload(), attacker, ReleaseFeedClient.KeyId);

        using JsonDocument document = JsonDocument.Parse(envelope);
        string signature = document.RootElement.GetProperty("signature").GetString()!;
        string swapped = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            ValidPayload(version: "9.9.9", packageUrl: "https://get-arsenal.com/downloads/evil.exe")));

        byte[] tampered = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 1,
            algorithm = "RSA-SHA256-PKCS1",
            key_id = ReleaseFeedClient.KeyId,
            payload = swapped,
            signature,
        }));

        Assert.Throws<CryptographicException>(() => ReleaseFeedClient.ParseSignedEnvelope(tampered));
    }

    [Fact]
    public void RejectsAnUnknownAlgorithm()
    {
        byte[] envelope = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 1,
            algorithm = "none",
            key_id = ReleaseFeedClient.KeyId,
            payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(ValidPayload())),
            signature = Convert.ToBase64String(new byte[384]),
        }));

        Assert.Throws<CryptographicException>(() => ReleaseFeedClient.ParseSignedEnvelope(envelope));
    }

    [Fact]
    public void AcceptsTheRealPublishedFeed()
    {
        // A copy of the feed get-arsenal.com actually serves, signed by the real key.
        //
        // Kept here as a fixture rather than read out of the website checkout: this
        // repository holds only the Windows application, and a test that reaches for a
        // sibling directory is a test that fails in every clone. The feed is public and
        // anyone can fetch it, so vendoring it gives nothing away.
        //
        // If this stops parsing, the public key embedded in the application and the key
        // the server signs with have diverged, and every installed copy has quietly
        // stopped receiving updates.
        string feed = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stable-feed-1.0.0.json");
        Assert.True(File.Exists(feed), $"Missing test fixture: {feed}");

        ReleaseUpdate update = ReleaseFeedClient.ParseSignedEnvelope(File.ReadAllBytes(feed));

        Assert.Equal("1.0.0", update.Version);
        Assert.StartsWith("https://get-arsenal.com/", update.PackageUrl, StringComparison.Ordinal);
        Assert.Equal(64, update.PackageSha256.Length);
    }

    private static string ValidPayload(string version = "1.0.0", string? packageUrl = null) =>
        JsonSerializer.Serialize(new
        {
            schema = 1,
            product = "Arsenal",
            channel = "stable",
            version,
            published_at = "2026-09-10",
            title = "Arsenal " + version,
            notes = new[] { "Test payload." },
            mandatory = false,
            minimum_supported_version = "1.0.0",
            package = new
            {
                url = packageUrl ?? $"https://get-arsenal.com/downloads/Arsenal-{version}-win-x64.exe",
                bytes = 11_890_408,
                sha256 = new string('a', 64),
            },
        });

    private static byte[] BuildEnvelope(string payload, RSA key, string keyId)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schema = 1,
            algorithm = "RSA-SHA256-PKCS1",
            key_id = keyId,
            payload = Convert.ToBase64String(payloadBytes),
            signature = Convert.ToBase64String(signature),
        }));
    }

}
