using Arsenal.AutoUpdate;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// Version comparison decides whether an update is offered at all.
/// </summary>
/// <remarks>
/// Getting it wrong in one direction means users never hear about a release; in the
/// other it means offering them a downgrade. Neither shows up in a build, on a
/// screenshot, or in a smoke render, which is exactly why it is worth a test.
/// </remarks>
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("v1.2.3", 1, 2, 3)]      // the feed has carried a leading v before
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData(" 1.2.3 ", 1, 2, 3)]     // and stray whitespace
    [InlineData("1.2.3+build.7", 1, 2, 3)]
    public void ParsesWellFormedVersions(string input, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(input, out ReleaseVersion? version));
        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]          // a dash with no pre-release after it
    [InlineData("1.2.3-beta!")]     // a character not allowed in a pre-release part
    [InlineData("not a version")]
    public void RejectsMalformedVersions(string? input)
    {
        Assert.False(ReleaseVersion.TryParse(input, out ReleaseVersion? version));
        Assert.Null(version);
    }

    [Fact]
    public void OrdersByMajorThenMinorThenPatch()
    {
        Assert.True(Compare("2.0.0", "1.9.9") > 0);
        Assert.True(Compare("1.2.0", "1.1.9") > 0);
        Assert.True(Compare("1.1.2", "1.1.1") > 0);
        Assert.Equal(0, Compare("1.2.3", "1.2.3"));
    }

    [Fact]
    public void PatchTenSortsAbovePatchNine()
    {
        // The string comparison this replaced would put "1.0.10" below "1.0.9" and
        // quietly stop offering updates after the ninth patch of any minor version.
        Assert.True(Compare("1.0.10", "1.0.9") > 0);
        Assert.True(Compare("1.10.0", "1.9.0") > 0);
        Assert.True(Compare("10.0.0", "9.0.0") > 0);
    }

    [Fact]
    public void PreReleaseSortsBelowItsOwnRelease()
    {
        // 1.0.0-beta.1 must not be offered to somebody already running 1.0.0.
        Assert.True(Compare("1.0.0", "1.0.0-beta.1") > 0);
        Assert.True(Compare("1.0.0-beta.1", "1.0.0") < 0);
    }

    [Fact]
    public void OrdersPreReleaseIdentifiers()
    {
        Assert.True(Compare("1.0.0-beta.2", "1.0.0-beta.1") > 0);
        Assert.True(Compare("1.0.0-beta.10", "1.0.0-beta.9") > 0);   // numeric, not lexical
        Assert.True(Compare("1.0.0-beta", "1.0.0-alpha") > 0);
        Assert.True(Compare("1.0.0-beta.1", "1.0.0-beta") > 0);      // more parts wins
        Assert.True(Compare("1.0.0-alpha.1", "1.0.0-alpha.beta") < 0); // numeric below textual
    }

    [Fact]
    public void BuildMetadataDoesNotAffectOrder()
    {
        Assert.Equal(0, Compare("1.2.3+abc", "1.2.3+xyz"));
    }

    [Fact]
    public void ComparesGreaterThanNull()
    {
        Assert.True(ReleaseVersion.Parse("1.0.0").CompareTo(null) > 0);
    }

    [Fact]
    public void ParseThrowsOnMalformedInput()
    {
        Assert.Throws<FormatException>(() => ReleaseVersion.Parse("nope"));
    }

    [Fact]
    public void CurrentStringIsParseable()
    {
        // Whatever the assembly is stamped with has to survive a round trip, or the
        // installed version can never be compared against the feed.
        Assert.True(ReleaseVersion.TryParse(ReleaseVersion.CurrentString(), out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0")]
    [InlineData("2.4.0", "2.4")]
    [InlineData("1.0.1", "1.0.1")]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.1")]
    public void UsesShortLabelOnlyForStableZeroPatchVersions(string version, string expected)
    {
        Assert.Equal(expected, ReleaseVersion.DisplayString(version));
    }

    private static int Compare(string left, string right) =>
        ReleaseVersion.Parse(left).CompareTo(ReleaseVersion.Parse(right));
}
