namespace Arsenal.Application.Models
{
    /// <summary>
    /// Lightweight identity for a built-in or user-created performance plan. The
    /// tuning values remain in the existing per-mode AppConfig keys; this type keeps
    /// presentation code from reaching into the legacy static mode store.
    /// </summary>
    public sealed record PerformancePlanInfo(int ModeIndex, string Name, int BaseMode, bool IsCustom);
}
