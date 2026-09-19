using Arsenal.Helpers;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// What this desktop will allow a paired phone to do to it.
/// </summary>
/// <remarks>
/// Pairing already proves the phone belongs to whoever set this machine up, and every
/// other companion endpoint treats that as enough. Taking the screen and the keyboard is
/// a different kind of permission: a phone left unlocked on a desk is now a person
/// sitting at the laptop. So the default is that a session asks, on screen, and a person
/// at the machine says yes.
///
/// <para>The switch to allow it unattended exists because the obvious use for this is
/// reaching a machine nobody is standing at. It is opt in, it is written down here
/// rather than inferred, and turning it on says plainly what it means.</para>
/// </remarks>
internal static class RemoteDesktopSettings
{
    internal const string EnabledKey = "remote_desktop";
    internal const string UnattendedKey = "remote_desktop_unattended";
    internal const string ConsentSecondsKey = "remote_desktop_consent_seconds";
    internal const string AllowInputKey = "remote_desktop_input";
    internal const string AllowAudioKey = "remote_desktop_audio";
    internal const string AllowClipboardKey = "remote_desktop_clipboard";
    internal const string AllowFilesKey = "remote_desktop_files";
    internal const string LockOnDisconnectKey = "remote_desktop_lock_on_disconnect";
    internal const string DefaultQualityKey = "remote_desktop_quality";

    /// <summary>Whether the session endpoint answers at all.</summary>
    internal static bool Enabled
    {
        get => AppConfig.Is(EnabledKey);
        set => AppConfig.Set(EnabledKey, value ? 1 : 0);
    }

    /// <summary>Start a session without asking anyone at the desktop first.</summary>
    internal static bool Unattended
    {
        get => AppConfig.Is(UnattendedKey);
        set => AppConfig.Set(UnattendedKey, value ? 1 : 0);
    }

    /// <summary>
    /// How long the prompt waits before refusing on its own.
    /// </summary>
    /// <remarks>
    /// It refuses rather than accepts when it runs out. A prompt that grants access to
    /// anyone patient enough to wait is not a prompt.
    /// </remarks>
    internal static int ConsentSeconds
    {
        get => Math.Clamp(AppConfig.Get(ConsentSecondsKey, 30), 10, 120);
        set => AppConfig.Set(ConsentSecondsKey, Math.Clamp(value, 10, 120));
    }

    /// <summary>View only when off: frames still flow, nothing reaches the desktop.</summary>
    internal static bool AllowInput
    {
        get => AppConfig.IsNotFalse(AllowInputKey);
        set => AppConfig.Set(AllowInputKey, value ? 1 : 0);
    }

    internal static bool AllowAudio
    {
        get => AppConfig.IsNotFalse(AllowAudioKey);
        set => AppConfig.Set(AllowAudioKey, value ? 1 : 0);
    }

    internal static bool AllowClipboard
    {
        get => AppConfig.IsNotFalse(AllowClipboardKey);
        set => AppConfig.Set(AllowClipboardKey, value ? 1 : 0);
    }

    internal static bool AllowFiles
    {
        get => AppConfig.IsNotFalse(AllowFilesKey);
        set => AppConfig.Set(AllowFilesKey, value ? 1 : 0);
    }

    /// <summary>
    /// Lock Windows when the last session ends.
    /// </summary>
    /// <remarks>
    /// Whoever was driving the machine has just walked away from it while it is signed
    /// in. On by default for the same reason a screen lock is.
    /// </remarks>
    internal static bool LockOnDisconnect
    {
        get => AppConfig.IsNotFalse(LockOnDisconnectKey);
        set => AppConfig.Set(LockOnDisconnectKey, value ? 1 : 0);
    }
}

/// <summary>
/// The three things a person actually adjusts when a session feels wrong, as presets.
/// </summary>
/// <remarks>
/// Resolution, frame rate and quality trade against each other and against whatever the
/// network is doing, and separate controls for each invite a combination that is worse
/// than any preset. The phone can still send explicit numbers; these are what its buttons
/// send.
/// </remarks>
internal readonly record struct RemoteQualityPreset(string Id, string Title, int MaxWidth, int MaxHeight, int FrameRate, int BitrateKbps, int JpegQuality)
{
    internal static readonly RemoteQualityPreset Sharp = new("sharp", "Sharp", 3840, 2160, 30, 24000, 82);
    internal static readonly RemoteQualityPreset Balanced = new("balanced", "Balanced", 1920, 1080, 30, 10000, 72);
    internal static readonly RemoteQualityPreset Smooth = new("smooth", "Smooth", 1600, 900, 60, 8000, 62);
    internal static readonly RemoteQualityPreset Economy = new("economy", "Data saver", 1280, 720, 20, 2500, 52);

    internal static readonly IReadOnlyList<RemoteQualityPreset> All = new[] { Sharp, Balanced, Smooth, Economy };

    internal static RemoteQualityPreset ById(string? id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase), Balanced);
}
