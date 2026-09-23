using Arsenal.Helpers;
using Xunit;

namespace Arsenal.Tests;

public class GpuPreferenceTests
{
    [Theory]
    [InlineData(AsusACPI.GPUModeEco, false)]
    [InlineData(AsusACPI.GPUModeStandard, false)]
    public void HybridModesPreferIntegratedGraphics(int mode, bool alwaysUltimate)
    {
        Assert.False(GpuPreference.ShouldPreferHighPerformance(mode, alwaysUltimate));
    }

    [Fact]
    public void UltimateModePrefersHighPerformanceGraphics()
    {
        Assert.True(GpuPreference.ShouldPreferHighPerformance(AsusACPI.GPUModeUltimate));
    }

    [Fact]
    public void AlwaysUltimateHardwarePrefersHighPerformanceGraphics()
    {
        Assert.True(GpuPreference.ShouldPreferHighPerformance(AsusACPI.GPUModeStandard, alwaysUltimate: true));
    }
}
