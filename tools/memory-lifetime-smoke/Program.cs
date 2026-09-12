using Arsenal.Ally;
using Arsenal.Helpers;
using System.Diagnostics;
using System.Reflection;
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

Console.WriteLine($"Memory lifetime smoke passed. Handles {handlesBefore} -> {handlesAfter}; Ally={AppConfig.IsAlly()}.");
