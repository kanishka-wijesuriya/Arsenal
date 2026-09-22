using Arsenal.Ally;
using Arsenal.Helpers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CoreProgram = Arsenal.Program;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Assert(CoreProgram.acpi is null, "Loading Program eagerly constructed an ACPI device.");
Assert(CoreProgram.toast is null, "Loading Program eagerly constructed the legacy toast host.");

var timerField = typeof(ToastForm).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Toast timer is not instance-owned.");
using (var firstToast = new ToastForm())
using (var secondToast = new ToastForm())
{
    Assert(!ReferenceEquals(timerField.GetValue(firstToast), timerField.GetValue(secondToast)),
        "Toast instances unexpectedly share a timer and can retain each other.");
}

var amdField = typeof(AllyControl).GetField("_amdControl", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Ally AMD controller field was not found.");
using (var ally = new AllyControl())
{
    if (!AppConfig.IsAlly())
        Assert(amdField.GetValue(null) is null, "A non-Ally system eagerly constructed AMD ADL control.");
}

using (var warmup = new AsusACPI())
{
    warmup.Dispose();
    warmup.Dispose();
}

int handlesBefore = Process.GetCurrentProcess().HandleCount;
for (int i = 0; i < 24; i++)
{
    using var acpi = new AsusACPI();
}
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
int handlesAfter = Process.GetCurrentProcess().HandleCount;
Assert(handlesAfter <= handlesBefore + 2,
    $"ACPI construct/dispose retained handles: before={handlesBefore}, after={handlesAfter}.");

// Page content that opts into standing aside inside a subpage must not outlive its
// page. It listens to a static event to hear about a group opening, and a subscription
// taken and not dropped roots the element, its parent chain and the whole page behind
// it - which silently undoes the tray release, one page per show-and-hide.
//
// On an STA thread of its own because WPF elements cannot be built anywhere else, and
// through raised Loaded and Unloaded events rather than a real window: the events are
// what the wiring hangs off, and a harness window never reliably renders.
string? uiFailure = null;
var ui = new Thread(() =>
{
    try
    {
        WeakReference probe = BuildAndDiscardPageContent();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (probe.IsAlive)
            uiFailure = "Page content marked HideInSubpage was still reachable after unloading; "
                + "something is holding it through the static open-group event.";
    }
    catch (Exception ex)
    {
        uiFailure = "UI lifetime check failed: " + ex;
    }
});
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();
Assert(uiFailure is null, uiFailure ?? "");

// In its own method so the element is not kept alive by a local still in scope.
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference BuildAndDiscardPageContent()
{
    var content = new System.Windows.Controls.Border();
    Arsenal.UI.Controls.SettingsGroup.SetHideInSubpage(content, true);

    content.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.LoadedEvent));
    content.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.FrameworkElement.UnloadedEvent));

    return new WeakReference(content);
}

Console.WriteLine($"Memory lifetime smoke passed. Handles {handlesBefore} -> {handlesAfter}; Ally={AppConfig.IsAlly()}.");
