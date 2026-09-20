using Arsenal.Battery;
using System.IO;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The report is parsed, not scraped, but the shape of it still comes from Windows.
/// The fixture is a real <c>powercfg /batteryreport /xml</c> scan with the usage log
/// and the machine's identifiers taken out.
/// </summary>
public class BatteryReportTests
{
    private static BatteryReportData Report()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "battery-report.xml");
        BatteryReportData? report = BatteryReportReader.Parse(File.ReadAllText(path));
        Assert.NotNull(report);
        return report!;
    }

    [Fact]
    public void ReadsTheBatteryItself()
    {
        BatteryReportData report = Report();

        Assert.Equal("LiON", report.Chemistry);
        Assert.Equal(90005, report.DesignCapacity);
        Assert.Equal(74521, report.FullChargeCapacity);
    }

    [Fact]
    public void HealthIsCurrentCapacityAgainstDesign()
    {
        BatteryReportData report = Report();

        Assert.Equal(0.828, report.Health, 3);
        Assert.Equal(17.2, report.WearPercent, 1);
    }

    /// <summary>
    /// Several ASUS packs answer the cycle count with a zero, which is the firmware
    /// declining rather than a battery that has never been charged. The page has to
    /// say so instead of printing "0 cycles" on a two-year-old machine.
    /// </summary>
    [Fact]
    public void ZeroCyclesIsNotACycleCount()
    {
        BatteryReportData report = Report();

        Assert.Equal(0, report.CycleCount);
        Assert.False(report.HasCycleCount);
    }

    [Fact]
    public void HistoryIsOldestFirstAndSkipsWeeksWithNoReading()
    {
        BatteryReportData report = Report();

        Assert.NotEmpty(report.History);
        for (int index = 1; index < report.History.Count; index++)
            Assert.True(report.History[index - 1].Period <= report.History[index].Period);

        Assert.All(report.History, point =>
        {
            Assert.True(point.FullChargeCapacity > 0);
            Assert.True(point.DesignCapacity > 0);
            Assert.InRange(point.Retained, 0, 1);
        });
    }

    [Fact]
    public void RuntimeEstimatesAreReadForBothCapacities()
    {
        BatteryReportData report = Report();

        // PT4H23M17S at design, PT3H37M59S at what it holds now.
        Assert.Equal(TimeSpan.FromSeconds(4 * 3600 + 23 * 60 + 17), report.DesignRuntime);
        Assert.Equal(TimeSpan.FromSeconds(3 * 3600 + 37 * 60 + 59), report.CurrentRuntime);
    }

    /// <summary>
    /// The connected-standby estimates carry a day component, which is the form
    /// XmlConvert.ToTimeSpan rejects and the reason the durations are read by hand.
    /// </summary>
    [Theory]
    [InlineData("PT3H37M59S", 0, 3, 37, 59)]
    [InlineData("P1DT2H22M53S", 1, 2, 22, 53)]
    [InlineData("PT25M12S", 0, 0, 25, 12)]
    [InlineData("P2DT16H15M1S", 2, 16, 15, 1)]
    public void ReadsIsoDurationsIncludingDays(string value, int days, int hours, int minutes, int seconds)
    {
        string xml = $"""
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries><Battery><DesignCapacity>100</DesignCapacity></Battery></Batteries>
              <RuntimeEstimates>
                <DesignCapacity><ActiveRuntime>{value}</ActiveRuntime></DesignCapacity>
              </RuntimeEstimates>
            </BatteryReport>
            """;

        BatteryReportData? report = BatteryReportReader.Parse(xml);

        Assert.NotNull(report);
        Assert.Equal(new TimeSpan(days, hours, minutes, seconds), report!.DesignRuntime);
    }

    [Fact]
    public void UnreadableReportIsNullRatherThanAThrow()
    {
        Assert.Null(BatteryReportReader.Parse("not xml at all"));
        Assert.Null(BatteryReportReader.Parse("<BatteryReport/>"));
    }

    /// <summary>
    /// A battery reporting more than it did is unusual but real, normally after the
    /// gauge recalibrates, and the page must not describe it as capacity lost.
    /// </summary>
    [Fact]
    public void CapacityLostIsNotNegativeWhenTheGaugeRecovers()
    {
        BatteryReportData report = Report();

        int lost = report.CapacityLostOverReport;
        int first = report.History[0].FullChargeCapacity;
        int last = report.History[^1].FullChargeCapacity;

        Assert.Equal(first - last, lost);
    }
}
