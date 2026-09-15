using Arsenal.Gpu;
using Xunit;

namespace Arsenal.Tests;

public class GpuModeChangeConfirmationTests
{
    [Fact]
    public void Evaluate_ManualEcoWithExternalDisplay_WarnsAboutDisconnect()
    {
        var result = GpuModeChangeConfirmation.Evaluate(
            AsusACPI.GPUModeStandard,
            AsusACPI.GPUModeEco,
            auto: 0,
            externalDisplayConnected: true);

        Assert.False(result.RequiresRestart);
        Assert.True(result.WarnsExternalDisplays);
        Assert.True(result.IsRequired);
    }

    [Fact]
    public void Evaluate_ManualEcoWithoutExternalDisplay_DoesNotPrompt()
    {
        var result = GpuModeChangeConfirmation.Evaluate(
            AsusACPI.GPUModeStandard,
            AsusACPI.GPUModeEco,
            auto: 0,
            externalDisplayConnected: false);

        Assert.False(result.IsRequired);
    }

    [Fact]
    public void Evaluate_UltimateToEcoWithExternalDisplay_CombinesBothWarnings()
    {
        var result = GpuModeChangeConfirmation.Evaluate(
            AsusACPI.GPUModeUltimate,
            AsusACPI.GPUModeEco,
            auto: 0,
            externalDisplayConnected: true);

        Assert.True(result.RequiresRestart);
        Assert.True(result.WarnsExternalDisplays);
        Assert.True(result.IsRequired);
    }

    [Fact]
    public void Evaluate_AutomaticEcoTransition_RemainsNonBlocking()
    {
        var result = GpuModeChangeConfirmation.Evaluate(
            AsusACPI.GPUModeStandard,
            AsusACPI.GPUModeEco,
            auto: 1,
            externalDisplayConnected: true);

        Assert.False(result.IsRequired);
    }

    [Fact]
    public void Evaluate_ManualUltimateChange_StillRequiresRestartConfirmation()
    {
        var result = GpuModeChangeConfirmation.Evaluate(
            AsusACPI.GPUModeStandard,
            AsusACPI.GPUModeUltimate,
            auto: 0,
            externalDisplayConnected: false);

        Assert.True(result.RequiresRestart);
        Assert.False(result.WarnsExternalDisplays);
        Assert.True(result.IsRequired);
    }
}
