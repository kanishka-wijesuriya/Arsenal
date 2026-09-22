using System.Windows;
using Arsenal.UI.Views.Windows;

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
    /// A short tray toggle reuses the existing native Main Window. A genuinely idle
    /// tray session does not need to retain its HWND and composition target indefinitely.
    /// </summary>
    private static readonly TimeSpan DeepQuietPeriod = TimeSpan.FromSeconds(30);

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
        _ = ReleaseWindowWhenDeepQuietAsync(mine);
    }

    /// <summary>
    /// Retires a pending idle release when a window becomes visible again.
    /// </summary>
    public static void NotifyActivity()
    {
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Nothing is trimmed while a window is on screen, deliberately.
    /// </summary>
    /// <remarks>
    /// There was a timer here that handed the working set back after the machine had
    /// been untouched for half a minute, on the reasoning that somebody who has walked
    /// away will not notice the pages being faulted in when they return. They do. The
    /// first thing touched after a trim pays for all of it at once, and the first thing
    /// touched is usually an animation - a page arriving, the quick panel opening - so
    /// the cost landed precisely on the frames where it is most visible.
    ///
    /// <para>The release below already gives back far more, at the moment it costs
    /// nothing: the last window has gone and nobody is waiting for a frame. A window on
    /// screen is a window somebody is about to use.</para>
    /// </remarks>
    public static void WatchOpenWindows()
    {
        // Kept so callers do not have to know this decision was reversed.
    }

    private static async Task ReleaseWhenQuietAsync(int generation)
    {
        try
        {
            await Task.Delay(QuietPeriod).ConfigureAwait(false);

            // Superseded by a later burst, which will do this itself when it settles.
            if (Volatile.Read(ref _generation) != generation) return;

            if (!await PrepareHiddenWindowsAsync(generation).ConfigureAwait(false)) return;

            // One line per idle period, not per burst. It is the only way to confirm from
            // outside that the release is running, and it costs a log entry an hour on a
            // machine nobody is touching.
            Logger.WriteLine("Releasing background memory");
            bool report = Arsenal.Helpers.MemoryHelper.DetailedReportingEnabled;
            if (report) Arsenal.Helpers.MemoryHelper.LogReport("idle, before release");

            // Before the trim, so the libraries it lets go of are out of the working set
            // by the time the working set is handed back. Does nothing unless a remote
            // session has run, and refuses while one is still using it.
            Remote.Desktop.MediaFoundationVideoEncoder.ReleasePlatformIfIdle();

            // A window can be reopened while the native provider is shutting down.
            // Recheck the debounce token immediately before the expensive collection
            // so an arriving animation is not made to pay for background cleanup.
            Arsenal.Helpers.MemoryHelper.TrimAfter(
                shouldRun: () => Volatile.Read(ref _generation) == generation);

            // After the trim has had time to run - it is scheduled, not immediate - so
            // the pair of lines says what the release actually returned.
            if (report) _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                Arsenal.Helpers.MemoryHelper.LogReport("idle, after release");
            });
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Background release: " + ex.Message);
        }
    }

    private static async Task ReleaseWindowWhenDeepQuietAsync(int generation)
    {
        try
        {
            await Task.Delay(DeepQuietPeriod).ConfigureAwait(false);
            if (Volatile.Read(ref _generation) != generation) return;

            System.Windows.Application? application = System.Windows.Application.Current;
            if (application is null) return;

            bool released = await application.Dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _generation) != generation) return false;
                if (application.Windows.OfType<Window>().Any(window => window.IsVisible)) return false;
                if (application is App app && app.HasVisibleOwnedWindow) return false;
                return application is App arsenal && arsenal.ReleaseHiddenMainWindow();
            });

            if (!released) return;
            Logger.WriteLine("Released hidden Main Window after extended idle");
            Arsenal.Helpers.MemoryHelper.TrimAfter(
                shouldRun: () => Volatile.Read(ref _generation) == generation);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Hidden Main Window release: " + ex.Message);
        }
    }

    /// <summary>
    /// Whether any window is still up. Asked on the dispatcher: the window collection is
    /// thread-affine and throws when read from anywhere else.
    /// </summary>
    private static async Task<bool> PrepareHiddenWindowsAsync(int generation)
    {
        System.Windows.Application? application = System.Windows.Application.Current;
        if (application is null) return true;

        return await application.Dispatcher.InvokeAsync(
            () =>
            {
                if (Volatile.Read(ref _generation) != generation) return false;
                if (application.Windows.OfType<Window>().Any(window => window.IsVisible)) return false;
                if (application is App app && app.HasVisibleOwnedWindow) return false;

                // The active page is kept briefly so a quick tray toggle remains
                // allocation-free. Once the whole app has stayed hidden for the quiet
                // period, the page can be rebuilt from its existing view model on the
                // next open, using the same XAML and arrival animation.
                if (application is App arsenal)
                    arsenal.ReleaseHiddenMainWindowPageContent();
                else
                    foreach (MainWindow window in application.Windows.OfType<MainWindow>())
                        window.ReleaseHiddenPageContent();

                return true;
            });
    }
}
