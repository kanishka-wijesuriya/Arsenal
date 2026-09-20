using Arsenal.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace Arsenal.Battery
{
    /// <summary>One week of the battery's recorded history.</summary>
    /// <param name="Period">When the week ran, for the axis label.</param>
    /// <param name="FullChargeCapacity">What the battery held that week, in mWh.</param>
    /// <param name="DesignCapacity">What it was built to hold, in mWh.</param>
    public readonly record struct BatteryHistoryPoint(
        DateTime Period,
        int FullChargeCapacity,
        int DesignCapacity)
    {
        /// <summary>How much of its original capacity remained that week, 0 to 1.</summary>
        public double Retained => DesignCapacity > 0
            ? Math.Clamp((double)FullChargeCapacity / DesignCapacity, 0, 1)
            : 0;
    }

    /// <summary>
    /// The parts of the Windows battery report worth reading, taken from its XML form.
    /// </summary>
    /// <remarks>
    /// The report Windows writes is an HTML page meant for a browser. Everything here
    /// comes from <c>powercfg /batteryreport /xml</c> instead, which is the same scan
    /// emitted as data rather than as a document - the numbers are identical and there
    /// is no markup to scrape.
    /// </remarks>
    public sealed class BatteryReportData
    {
        public string Manufacturer { get; init; } = "";
        public string Chemistry { get; init; } = "";
        public string SerialNumber { get; init; } = "";
        public int DesignCapacity { get; init; }
        public int FullChargeCapacity { get; init; }
        public int CycleCount { get; init; }

        /// <summary>Estimated time on a full charge, at design and at current capacity.</summary>
        public TimeSpan? DesignRuntime { get; init; }
        public TimeSpan? CurrentRuntime { get; init; }

        public DateTime ScanTime { get; init; }
        public int ReportDays { get; init; }

        /// <summary>Oldest week first, so it reads and draws left to right.</summary>
        public IReadOnlyList<BatteryHistoryPoint> History { get; init; } = Array.Empty<BatteryHistoryPoint>();

        /// <summary>The Windows HTML report, kept beside the XML for anyone who wants it.</summary>
        public string HtmlPath { get; init; } = "";

        /// <summary>How much of its original capacity the battery still holds, 0 to 1.</summary>
        public double Health => DesignCapacity > 0
            ? Math.Clamp((double)FullChargeCapacity / DesignCapacity, 0, 1)
            : 0;

        /// <summary>How much it has lost, as a percentage.</summary>
        public double WearPercent => DesignCapacity > 0 ? (1 - Health) * 100 : 0;

        /// <summary>
        /// Capacity lost since the report's earliest week, in mWh. Negative means the
        /// battery is reporting more than it did, which a recalibration can do.
        /// </summary>
        public int CapacityLostOverReport => History.Count >= 2
            ? History[0].FullChargeCapacity - History[^1].FullChargeCapacity
            : 0;

        /// <summary>
        /// A cycle count of zero is not a reading, it is the firmware declining to
        /// answer, and several ASUS packs do exactly that.
        /// </summary>
        public bool HasCycleCount => CycleCount > 0;
    }

    public static class BatteryReportReader
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/battery/2012";

        private static string ReportDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arsenal");

        /// <summary>
        /// Runs the Windows battery scan and reads the result.
        /// </summary>
        /// <remarks>
        /// Both forms are written: the XML this parses, and the HTML page Windows has
        /// always produced, so the full report stays one click away for anything not
        /// summarised here.
        ///
        /// <para>Into Arsenal's own folder rather than the user profile, where the old
        /// call left a battery-report.html in the middle of the user's home directory
        /// every time it ran.</para>
        /// </remarks>
        public static async Task<BatteryReportData?> GenerateAsync(CancellationToken cancel = default)
        {
            try
            {
                Directory.CreateDirectory(ReportDirectory);
                string xmlPath = Path.Combine(ReportDirectory, "battery-report.xml");
                string htmlPath = Path.Combine(ReportDirectory, "battery-report.html");

                if (!await RunPowercfgAsync(xmlPath, "xml", cancel).ConfigureAwait(false)) return null;

                // Best effort: the summary does not depend on it, and a failure here
                // should not lose the report that did come back.
                await RunPowercfgAsync(htmlPath, "html", cancel).ConfigureAwait(false);

                if (!File.Exists(xmlPath)) return null;
                return Parse(await File.ReadAllTextAsync(xmlPath, cancel).ConfigureAwait(false),
                             File.Exists(htmlPath) ? htmlPath : "");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Battery report failed: " + ex.Message);
                return null;
            }
        }

        private static async Task<bool> RunPowercfgAsync(string path, string format, CancellationToken cancel)
        {
            // powercfg directly, not through a shell. The old call handed a command line
            // to PowerShell, which meant the report path went through another parser on
            // its way - and a user profile path with a space in it broke there.
            var start = new ProcessStartInfo
            {
                FileName = ProcessHelper.SystemPath("powercfg"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("/batteryreport");
            start.ArgumentList.Add("/" + format);
            start.ArgumentList.Add("/output");
            start.ArgumentList.Add(path);

            using var process = Process.Start(start);
            if (process is null) return false;

            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            if (process.ExitCode == 0) return true;

            Logger.WriteLine($"powercfg /batteryreport /{format} exited {process.ExitCode}: "
                + (await process.StandardError.ReadToEndAsync(cancel).ConfigureAwait(false)).Trim());
            return false;
        }

        /// <summary>Reads a report that has already been written.</summary>
        public static BatteryReportData? Parse(string xml, string htmlPath = "")
        {
            XElement root;
            try { root = XDocument.Parse(xml).Root!; }
            catch (Exception ex)
            {
                Logger.WriteLine("Battery report is not readable XML: " + ex.Message);
                return null;
            }
            if (root is null) return null;

            // The first pack. Every ASUS laptop this runs on has one; a machine with two
            // would need the summary to say which, and none of them do.
            XElement? battery = root.Element(Ns + "Batteries")?.Element(Ns + "Battery");
            if (battery is null) return null;

            XElement? info = root.Element(Ns + "ReportInformation");
            XElement? estimates = root.Element(Ns + "RuntimeEstimates");

            var history = new List<BatteryHistoryPoint>();
            foreach (XElement entry in root.Element(Ns + "History")?.Elements(Ns + "HistoryEntry")
                     ?? Enumerable.Empty<XElement>())
            {
                int full = Int(entry.Attribute("FullChargeCapacity")?.Value);
                int design = Int(entry.Attribute("DesignCapacity")?.Value);

                // A week the machine was off reports nothing rather than zero capacity,
                // and a zero would draw as a battery that had briefly died.
                if (full <= 0 || design <= 0) continue;
                if (!DateTime.TryParse(entry.Attribute("LocalEndDate")?.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime end)) continue;

                history.Add(new BatteryHistoryPoint(end, full, design));
            }
            history.Sort((left, right) => left.Period.CompareTo(right.Period));

            return new BatteryReportData
            {
                Manufacturer = Text(battery, "Manufacturer"),
                Chemistry = Text(battery, "Chemistry"),
                SerialNumber = Text(battery, "SerialNumber"),
                DesignCapacity = Int(Text(battery, "DesignCapacity")),
                FullChargeCapacity = Int(Text(battery, "FullChargeCapacity")),
                CycleCount = Int(Text(battery, "CycleCount")),
                DesignRuntime = Duration(estimates?.Element(Ns + "DesignCapacity")?.Element(Ns + "ActiveRuntime")?.Value),
                CurrentRuntime = Duration(estimates?.Element(Ns + "FullChargeCapacity")?.Element(Ns + "ActiveRuntime")?.Value),
                ScanTime = DateTime.TryParse(info?.Element(Ns + "LocalScanTime")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime scan) ? scan : DateTime.Now,
                ReportDays = Int(info?.Element(Ns + "ReportDuration")?.Value),
                History = history,
                HtmlPath = htmlPath,
            };
        }

        private static string Text(XElement parent, string name) => (parent.Element(Ns + name)?.Value ?? "").Trim();

        private static int Int(string? value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

        /// <summary>
        /// Reads the ISO 8601 durations the report uses, such as PT3H37M59S.
        /// </summary>
        /// <remarks>
        /// Written by hand rather than with XmlConvert, which throws on the day form
        /// (P1DT2H22M53S) that the connected-standby estimates use.
        /// </remarks>
        private static TimeSpan? Duration(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value[0] != 'P') return null;

            double days = 0, hours = 0, minutes = 0, seconds = 0;
            double number = 0;
            bool inTime = false, any = false;

            foreach (char c in value.AsSpan(1))
            {
                if (c == 'T') { inTime = true; continue; }
                if (char.IsDigit(c)) { number = number * 10 + (c - '0'); continue; }

                switch (c)
                {
                    case 'D': days = number; break;
                    case 'H': hours = number; break;
                    case 'M' when inTime: minutes = number; break;
                    case 'S': seconds = number; break;
                    default: return null;
                }
                number = 0;
                any = true;
            }

            return any ? TimeSpan.FromSeconds(days * 86400 + hours * 3600 + minutes * 60 + seconds) : null;
        }
    }
}
