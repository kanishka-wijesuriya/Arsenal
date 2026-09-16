using Arsenal.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace Arsenal.UI.Services.Remote;

/// <summary>
/// The Windows battery report, parsed into something a phone can draw.
///
/// <c>battery.report</c> runs <c>powercfg /batteryreport</c> and opens the resulting HTML
/// on the desktop, which is useless from a phone - you press the button and nothing
/// appears to happen, because everything happened on a screen you are not looking at.
///
/// This runs the same tool with <c>/xml</c> and reads the numbers out, so the companion
/// can show battery health, cycle count and how the capacity has faded over time.
/// </summary>
internal static class CompanionBatteryReport
{
    /// <summary>
    /// powercfg takes several seconds and reads a log that only moves hourly, so a
    /// freshly generated report stays good for a while. Without this, opening the page
    /// twice would spawn the tool twice.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static object? _cached;
    private static DateTime _generatedUtc = DateTime.MinValue;

    internal static async Task<object> GetAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTime.UtcNow - _generatedUtc < CacheFor) return _cached;
            object report = await Task.Run(Generate, token).ConfigureAwait(false);
            _cached = report;
            _generatedUtc = DateTime.UtcNow;
            return report;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static object Generate()
    {
        // Its own file per run, in the temp folder rather than the user's profile: the
        // desktop's own report writes battery-report.html to the profile root and this
        // should not overwrite or race it.
        string path = Path.Combine(Path.GetTempPath(), "arsenal-battery-report.xml");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ProcessHelper.SystemPath("powercfg"),
                Arguments = "/batteryreport /xml /output \"" + path + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return Failed("Windows would not start powercfg.");

            // A machine with no battery makes powercfg exit without writing anything, so
            // the wait has a ceiling rather than trusting it to always finish.
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch { /* already gone */ }
                return Failed("The battery report timed out.");
            }

            if (!File.Exists(path)) return Failed("Windows did not produce a battery report. This PC may have no battery.");

            return Parse(XDocument.Load(path));
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Companion battery report: " + ex.Message);
            return Failed(ex.Message);
        }
    }

    private static object Failed(string message) => new { available = false, error = message };

    private static object Parse(XDocument document)
    {
        XNamespace ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;

        XElement? battery = document.Root?.Element(ns + "Batteries")?.Element(ns + "Battery");
        if (battery is null) return Failed("The battery report contained no battery.");

        long design = Long(battery.Element(ns + "DesignCapacity"));
        long full = Long(battery.Element(ns + "FullChargeCapacity"));

        // Capacity as it was measured at the end of each period Windows recorded. The
        // report holds these weekly, so a year of history is about fifty points - enough
        // to draw the fade without sending the whole document to the phone.
        var history = (document.Root?.Element(ns + "History")?.Elements(ns + "HistoryEntry") ?? Enumerable.Empty<XElement>())
            .Select(entry => new
            {
                date = (string?)entry.Element(ns + "LocalEndDate") ?? (string?)entry.Element(ns + "EndDate") ?? string.Empty,
                designCapacity = Long(entry.Element(ns + "DesignCapacity")),
                fullChargeCapacity = Long(entry.Element(ns + "FullChargeCapacity")),
                activeDcTime = Seconds(entry.Element(ns + "ActiveDcTime")),
                activeAcTime = Seconds(entry.Element(ns + "ActiveAcTime")),
            })
            .Where(entry => entry.designCapacity > 0 && entry.fullChargeCapacity > 0)
            .ToArray();

        // The most recent period Windows could estimate runtime for. Entries near the end
        // of the report can be empty, so this walks back to the last one with a number.
        XElement? latestRuntime = (document.Root?.Element(ns + "History")?.Elements(ns + "HistoryEntry") ?? Enumerable.Empty<XElement>())
            .LastOrDefault(entry => Seconds(entry.Element(ns + "EstimatedFullChargeActiveTime")) > 0);

        XElement? information = document.Root?.Element(ns + "ReportInformation");
        XElement? system = document.Root?.Element(ns + "SystemInformation");

        return new
        {
            available = true,
            generated = (string?)information?.Element(ns + "LocalReportTime") ?? (string?)information?.Element(ns + "ReportGuid") ?? string.Empty,
            computer = (string?)system?.Element(ns + "ComputerName") ?? string.Empty,

            id = Text(battery.Element(ns + "Id")),
            manufacturer = Text(battery.Element(ns + "Manufacturer")),
            chemistry = Text(battery.Element(ns + "Chemistry")),
            serialNumber = Text(battery.Element(ns + "SerialNumber")),
            manufactureDate = Text(battery.Element(ns + "ManufactureDate")),

            designCapacity = design,
            fullChargeCapacity = full,
            cycleCount = (int)Long(battery.Element(ns + "CycleCount")),

            // Health as a whole percentage. Zero design capacity means the firmware did
            // not report one, in which case there is no ratio to show rather than a
            // divide by zero or a meaningless 100%.
            healthPercent = design > 0 ? (int)Math.Round(full * 100.0 / design) : -1,

            estimatedRuntimeSeconds = Seconds(latestRuntime?.Element(ns + "EstimatedFullChargeActiveTime")),
            designRuntimeSeconds = Seconds(latestRuntime?.Element(ns + "EstimatedDesignActiveTime")),

            history,
        };
    }

    private static string Text(XElement? element) => ((string?)element ?? string.Empty).Trim();

    private static long Long(XElement? element) =>
        long.TryParse((string?)element, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    /// <summary>
    /// The report writes durations as ISO-8601 periods - "PT4H40M18S", "P2DT16H15M1S".
    /// <see cref="XmlConvert"/> parses those, but throws on the empty strings the report
    /// is full of, so this returns zero for anything it cannot read.
    /// </summary>
    private static long Seconds(XElement? element)
    {
        string text = Text(element);
        if (string.IsNullOrEmpty(text)) return 0;
        try { return (long)System.Xml.XmlConvert.ToTimeSpan(text).TotalSeconds; }
        catch { return 0; }
    }
}
