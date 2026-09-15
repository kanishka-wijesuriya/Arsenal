using Arsenal.Display;
using Xunit;

namespace Arsenal.Tests;

public class DisplayStatusTests
{
    [Theory]
    [InlineData(240, 60, 240)]
    [InlineData(60, 240, 60)]
    [InlineData(-1, 240, 240)]
    [InlineData(0, 144, 144)]
    public void ResolveReportedRefreshRate_PrefersAValidReadingThenTheLastKnownValue(
        int detected,
        int lastKnown,
        int expected)
    {
        Assert.Equal(expected, ScreenControl.ResolveReportedRefreshRate(detected, lastKnown));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ResolveReportedRefreshRate_NeverReturnsAnInvalidSentinel(int detected)
    {
        int resolved = ScreenControl.ResolveReportedRefreshRate(detected, -1);

        Assert.True(resolved > 0);
    }

    /// <summary>
    /// A null device name is what FindLaptopScreen answers while the internal panel is
    /// not being driven - clamshell on a dock, "second screen only", or a topology
    /// still settling. The refresh-rate buttons render this value directly, so it must
    /// resolve to a rate rather than the -1 that used to reach them as "-1 Hz".
    /// </summary>
    [Fact]
    public void GetMaxRate_WithNoInternalPanel_StillAnswersARate()
    {
        int rate = ScreenControl.GetMaxRate(null);

        Assert.True(rate > 0, $"expected a usable rate, got {rate}");
        Assert.True(rate >= ScreenControl.MIN_RATE, $"expected at least the {ScreenControl.MIN_RATE} Hz floor, got {rate}");
    }

    /// <summary>
    /// Enumeration failure must not overwrite the remembered ceiling with a sentinel.
    /// </summary>
    [Fact]
    public void GetMaxRefreshRate_WithNoInternalPanel_DoesNotPersistASentinel()
    {
        ScreenNative.GetMaxRefreshRate(null);

        Assert.NotEqual(-1, AppConfig.Get("screen_max", 0));
    }
}
