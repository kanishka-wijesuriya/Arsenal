using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

public class ApplicationLaunchTests
{
    [Fact]
    public void PreferenceKeepsOrdinaryLaunchesInTray()
    {
        Assert.True(ApplicationLaunch.ShouldStartMinimized([], preference: true));
        Assert.True(ApplicationLaunch.ShouldStartMinimized(["--elevated"], preference: true));
        Assert.False(ApplicationLaunch.ShouldStartMinimized([], preference: false));
    }

    [Theory]
    [InlineData("--minimized")]
    [InlineData("--STARTUP")]
    [InlineData("-M")]
    public void CommandLineSwitchAlwaysKeepsLaunchInTray(string argument)
        => Assert.True(ApplicationLaunch.ShouldStartMinimized([argument], preference: false));

    [Theory]
    [InlineData("--settings")]
    [InlineData("--setup")]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("uv")]
    [InlineData("services")]
    [InlineData("colors")]
    public void ExplicitDestinationsRemainVisible(string action)
        => Assert.False(ApplicationLaunch.ShouldStartMinimized([action], preference: true));
}
