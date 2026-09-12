using Arsenal.Application.Services.Implementations;
using System.Reflection;

// Proves the sampling loop is demand-driven: stopped while nothing is looking,
// running while something is, and still answering a direct pull either way.
var service = new DeviceStateService();
var field = typeof(DeviceStateService).GetField("_telemetryTimer",
    BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("The telemetry timer field moved.");

bool Running() => ((System.Timers.Timer)field.GetValue(service)!).Enabled;
double Interval() => ((System.Timers.Timer)field.GetValue(service)!).Interval;

Console.WriteLine($"after construction : running={Running()}");
if (Running()) throw new InvalidOperationException("The poll started before anything was looking.");

service.ResumePolling();
Console.WriteLine($"surface visible    : running={Running()} interval={Interval()}ms");
if (!Running()) throw new InvalidOperationException("A visible surface did not start the poll.");

service.PausePolling();
Console.WriteLine($"surface hidden     : running={Running()}");
if (Running()) throw new InvalidOperationException("The poll kept running with every surface hidden.");

// The phone companion pulls directly; that must not depend on the timer.
service.RefreshNow();
Console.WriteLine($"pull while hidden  : ok, still running={Running()}");
if (Running()) throw new InvalidOperationException("A direct pull restarted the background poll.");

service.ResumePolling();
service.ResumePolling();
Console.WriteLine($"resume twice       : running={Running()} interval={Interval()}ms");

service.Dispose();
Console.WriteLine("PASS");
