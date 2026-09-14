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
}
