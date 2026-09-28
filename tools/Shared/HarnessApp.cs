using Arsenal.UI;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Arsenal.Tools;

/// <summary>
/// Keeps the application's own startup out of a harness.
/// </summary>
/// <remarks>
/// Constructing <see cref="App"/> queues its startup, and the first time a harness pumps
/// the dispatcher that startup runs for real: the single-instance handoff finds the
/// Arsenal already running on this PC and shuts the harness's application down, leaving
/// <c>Application.Current</c> null for whatever the harness does next. Whether that lands
/// before the harness finishes is a matter of timing, which is why update-card-smoke and
/// setup-smoke passed on their own and failed in a run of harnesses.
///
/// <para>App skips its startup when it already has services and this variable is set.
/// Linked into each harness that constructs App, so there is one copy of the trick.</para>
/// </remarks>
internal static class HarnessApp
{
    public static void KeepStartupOut()
    {
        if (App.Services is null)
        {
            typeof(App).GetProperty(nameof(App.Services), BindingFlags.Static | BindingFlags.Public)!
                .SetValue(null, new ServiceCollection().BuildServiceProvider());
        }
        Environment.SetEnvironmentVariable("ARSENAL_UI_LIFETIME_SMOKE_HOST", "1");
    }
}
