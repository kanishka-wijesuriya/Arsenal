using System.Windows;

namespace Arsenal.UI.Services;

/// <summary>
/// Returns memory once the application has gone quiet again.
/// </summary>
/// <remarks>
/// This is a tray application: almost all of its life is spent with nothing on screen,
/// and the bursts of work in between - opening a page, dismissing the quick panel,
/// answering the phone - leave behind visuals, render caches and garbage that nothing
/// would otherwise collect. A process that then sits idle allocates so little that the
/// GC has no reason to run, so whatever the last burst touched simply stayed resident.
///
/// One policy rather than a trim at each call site. Every burst calls <see cref="Schedule"/>
/// when it finishes; the release happens once, a few seconds after the last of them, and
/// only if nothing is on screen by then. That is what keeps a phone polling every second
/// from inducing a collection every second, and what keeps a collection from landing while
/// the user is still looking at a window.
/// </remarks>
internal static class BackgroundMemoryRelease
{
    /// <summary>
    /// How long the application has to stay quiet before anything is given back. Long
    /// enough to cover a burst of related work - a phone stepping through several
    /// commands, a panel opened and closed and opened again - so the cost is paid once
    /// at the end rather than between each step.
    /// </summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Debounce token. Each call takes the next number; only the newest still matches by
    /// the time the wait is over, so earlier waits retire without doing anything. Cheaper
    /// and less fragile than cancelling and disposing a token source per call.
    /// </summary>
    private static int _generation;

    /// <summary>Called by anything that has just finished a burst of work.</summary>
    public static void Schedule()
    {
        int mine = Interlocked.Increment(ref _generation);
        _ = ReleaseWhenQuietAsync(mine);
    }

    private static async Task ReleaseWhenQuietAsync(int generation)
    {
        try
        {
            await Task.Delay(QuietPeriod).ConfigureAwait(false);

            // Superseded by a later burst, which will do this itself when it settles.
            if (Volatile.Read(ref _generation) != generation) return;

            if (await IsAnythingOnScreenAsync().ConfigureAwait(false)) return;

            // One line per idle period, not per burst. It is the only way to confirm from
            // outside that the release is running, and it costs a log entry an hour on a
            // machine nobody is touching.
            Logger.WriteLine("Releasing background memory");
            Arsenal.Helpers.MemoryHelper.TrimAfter();
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Background release: " + ex.Message);
        }
    }

    /// <summary>
    /// Whether any window is still up. Asked on the dispatcher: the window collection is
    /// thread-affine and throws when read from anywhere else.
    /// </summary>
    private static async Task<bool> IsAnythingOnScreenAsync()
    {
        System.Windows.Application? application = System.Windows.Application.Current;
        if (application is null) return false;

        return await application.Dispatcher.InvokeAsync(
            () => application.Windows.OfType<Window>().Any(window => window.IsVisible));
    }
}
