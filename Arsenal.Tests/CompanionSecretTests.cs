using Arsenal.Helpers;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// How companion credentials survive a round trip to disk.
/// </summary>
/// <remarks>
/// The bearer tokens these wrap are what authorize every command a paired phone can send.
/// They used to be stored as plain strings in config.json, which is copied to ProgramData
/// for the SYSTEM charge task, where every account on the machine can read it.
///
/// The interesting property is not that encryption works - that is DPAPI's job - but that
/// the migration does: a value written by an earlier build has no marker and must still
/// come back intact, or the upgrade silently unpairs every phone.
/// </remarks>
public class CompanionSecretTests
{
    [Fact]
    public void ProtectedValueSurvivesTheRoundTrip()
    {
        const string token = "Z0Zq3o0m2Jb4Xn5Kc6Rd7Se8Tf9Ug0Vh1Wi2Xj3Yk=";

        string stored = CompanionSecret.Protect(token);

        Assert.Equal(token, CompanionSecret.Unprotect(stored));
    }

    [Fact]
    public void ProtectedValueDoesNotContainThePlaintext()
    {
        // The whole point. If this fails the value is sitting in ProgramData in the clear
        // however well the rest of the wrapping is wired up.
        const string token = "a-very-recognisable-token-value";

        Assert.DoesNotContain(token, CompanionSecret.Protect(token));
    }

    [Fact]
    public void ProtectedValueCarriesTheMarker()
    {
        Assert.True(CompanionSecret.IsProtected(CompanionSecret.Protect("anything")));
    }

    [Fact]
    public void PlainValueWrittenByAnEarlierBuildIsReturnedAsItStands()
    {
        // No marker: this is what an upgraded installation finds in its config, and it
        // has to keep working or every paired phone is dropped on the first launch.
        const string legacy = "wZ8y5Q1r2S3t4U5v6W7x8Y9z0A1b2C3d4E5f6G7h8I0=";

        Assert.False(CompanionSecret.IsProtected(legacy));
        Assert.Equal(legacy, CompanionSecret.Unprotect(legacy));
    }

    [Fact]
    public void UnreadableValueIsReportedAsAbsentRatherThanThrowing()
    {
        // What another Windows account's value looks like from here: the marker says it
        // was wrapped, and unwrapping it fails. The caller mints a new credential, so
        // this has to answer null rather than throw out of the bridge's startup.
        string foreign = "ARSNLSEC1:" + Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Null(CompanionSecret.Unprotect(foreign));
    }

    [Fact]
    public void GarbageAfterTheMarkerIsReportedAsAbsent()
    {
        // Not valid base64 at all, which is a truncated or hand-edited config rather than
        // another account's. Same answer: no credential, no exception.
        Assert.Null(CompanionSecret.Unprotect("ARSNLSEC1:this is not base64!!"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingStoredIsNothingRead(string? stored)
    {
        Assert.Null(CompanionSecret.Unprotect(stored));
    }

    [Fact]
    public void EachProtectionIsDistinct()
    {
        // DPAPI salts each call, so two stored copies of one token do not compare equal.
        // Worth pinning: any future code that compares stored forms rather than unwrapped
        // ones would pass its own tests and fail on real machines.
        const string token = "the-same-token-twice";

        Assert.NotEqual(CompanionSecret.Protect(token), CompanionSecret.Protect(token));
    }
}
