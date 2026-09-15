namespace Arsenal.Gpu;

/// <summary>
/// Describes the warnings that must be accepted before a manual GPU mode change.
/// Kept separate from the dialog so the safety decision can be tested without hardware
/// or a running WPF dispatcher.
/// </summary>
public readonly record struct GpuModeChangeConfirmation(
    bool RequiresRestart,
    bool WarnsExternalDisplays)
{
    public bool IsRequired => RequiresRestart || WarnsExternalDisplays;

    public static GpuModeChangeConfirmation Evaluate(
        int currentMode,
        int targetMode,
        int auto,
        bool externalDisplayConnected)
    {
        if (auto != 0 || currentMode == targetMode)
            return default;

        bool requiresRestart =
            (currentMode == AsusACPI.GPUModeUltimate) !=
            (targetMode == AsusACPI.GPUModeUltimate);
        bool warnsExternalDisplays =
            targetMode == AsusACPI.GPUModeEco && externalDisplayConnected;

        return new GpuModeChangeConfirmation(requiresRestart, warnsExternalDisplays);
    }
}
