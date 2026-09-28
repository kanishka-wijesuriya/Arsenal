using Arsenal.Peripherals.Mouse.Models;
using Arsenal.Simulation;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The device simulator's profiles and its stand-in for the firmware.
/// </summary>
/// <remarks>
/// A simulator that answers wrongly does not fail; it shows the wrong pages and looks
/// convincing doing it. What is checked here is the part that decides what appears: that
/// every shipped profile loads and names peripherals that exist, and that the firmware
/// stand-in speaks the same byte layout as the ATKACPI driver, including the answer that
/// makes Arsenal hide a feature.
/// </remarks>
public class DeviceSimulatorTests
{
    private const uint ControlCode = 0x0022240C;
    private const uint DSTS = 0x53545344;
    private const uint DEVS = 0x53564544;

    private static byte[] Call(uint method, uint deviceId, uint argument)
    {
        byte[] input = new byte[16];
        BitConverter.GetBytes(method).CopyTo(input, 0);
        BitConverter.GetBytes(8u).CopyTo(input, 4);
        BitConverter.GetBytes(deviceId).CopyTo(input, 8);
        BitConverter.GetBytes(argument).CopyTo(input, 12);
        return input;
    }

    /// <summary>What <c>AsusACPI.DeviceGet</c> would return.</summary>
    private static int DeviceGet(SimulatedFirmware firmware, uint deviceId, uint argument = 0)
    {
        byte[] output = new byte[16];
        firmware.Answer(ControlCode, Call(DSTS, deviceId, argument), output);
        return BitConverter.ToInt32(output, 0) - 65536;
    }

    private static int DeviceSet(SimulatedFirmware firmware, uint deviceId, int value)
    {
        byte[] output = new byte[16];
        firmware.Answer(ControlCode, Call(DEVS, deviceId, unchecked((uint)value)), output);
        return BitConverter.ToInt32(output, 0);
    }

    private static SimulationProfile Sample() => SimulationProfile.Parse("sample", """
        {
          "name": "Sample",
          "model": "ROG Sample G000XX_G000XX",
          "firmware": {
            "00120075": 0,
            "00090016": "0x1",
            "00110024": "hex:1E28323C46505A6400050A1423374141",
            "00110024/2": "hex:1E28323C46505A64141E283241505A64"
          },
          "accepts": ["00110022"],
          "peripherals": ["ChakramX"]
        }
        """);

    [Fact]
    public void EveryBuiltInProfileLoadsAndPlugsInRealPeripherals()
    {
        List<SimulationProfile> builtIn = DeviceSimulation.Catalogue().Where(profile => profile.BuiltIn).ToList();

        Assert.True(builtIn.Count >= 6, $"Expected the shipped profiles, found {builtIn.Count}.");
        foreach (SimulationProfile profile in builtIn)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Model), profile.Id);
            Assert.False(string.IsNullOrWhiteSpace(profile.Source), profile.Id);
            Assert.NotEmpty(profile.Firmware);
            foreach (string peripheral in profile.Peripherals)
                Assert.True(SimulatedPeripherals.IsKnown(peripheral), $"{profile.Id} plugs in {peripheral}, which is not a model class.");
        }
    }

    [Fact]
    public void ShippedTextHasNoEmDashes()
    {
        foreach (SimulationProfile profile in DeviceSimulation.Catalogue().Where(profile => profile.BuiltIn))
        {
            Assert.DoesNotContain((char)0x2014, profile.Name);
            Assert.DoesNotContain((char)0x2014, profile.Source);
        }
    }

    [Fact]
    public void ListedIdReadsAsPresentAndUnlistedAsUnsupported()
    {
        var firmware = new SimulatedFirmware(Sample());

        Assert.Equal(0, DeviceGet(firmware, 0x00120075));
        Assert.Equal(1, DeviceGet(firmware, 0x00090016));

        // What real firmware answers for an id it does not implement: 0xFFFFFFFE.
        // DeviceGet reports it as negative, which is what hides a feature.
        Assert.True(DeviceGet(firmware, 0x00090020) < 0);
    }

    [Fact]
    public void AWriteIsReadBack()
    {
        var firmware = new SimulatedFirmware(Sample());

        Assert.Equal(1, DeviceSet(firmware, 0x00120075, 2));
        Assert.Equal(2, DeviceGet(firmware, 0x00120075));
    }

    [Fact]
    public void AWriteToAnUnknownIdIsRefusedAndNotRemembered()
    {
        var firmware = new SimulatedFirmware(Sample());
        var seen = new List<SimulatedAction>();
        firmware.Wrote += seen.Add;

        Assert.NotEqual(1, DeviceSet(firmware, 0x00050019, 1));
        Assert.True(DeviceGet(firmware, 0x00050019) < 0);
        Assert.False(Assert.Single(seen).Accepted);
    }

    [Fact]
    public void AcceptedWriteOnlyIdAnswersOk()
    {
        var firmware = new SimulatedFirmware(Sample());
        Assert.Equal(1, DeviceSet(firmware, 0x00110022, 0x0102));
    }

    [Fact]
    public void FanCurvesAnswerPerModeAndFallBackToTheFirst()
    {
        var firmware = new SimulatedFirmware(Sample());
        byte[] output = new byte[16];

        firmware.Answer(ControlCode, Call(DSTS, 0x00110024, 2), output);
        Assert.Equal(0x14, output[8]);

        // No silent curve in this profile: mode 1 falls back to the balanced one.
        firmware.Answer(ControlCode, Call(DSTS, 0x00110024, 1), output);
        Assert.Equal(0x00, output[8]);
        Assert.Equal(0x1E, output[0]);
    }

    [Fact]
    public void ProfileSurvivesASaveAndLoad()
    {
        SimulationProfile original = Sample();
        SimulationProfile copy = SimulationProfile.Parse(original.Id, original.ToJson());

        Assert.Equal(original.Model, copy.Model);
        Assert.Equal(original.Firmware.Count, copy.Firmware.Count);
        Assert.Equal(original.Firmware[(0x00110024, 2)].Raw, copy.Firmware[(0x00110024, 2)].Raw);
        Assert.Equal(1, copy.Firmware[(0x00090016, 0)].Value);
        Assert.Contains(0x00110022u, copy.Accepts);
        Assert.Equal(original.Peripherals, copy.Peripherals);
    }

    [Fact]
    public void APretendMouseReportsACharge()
    {
        var mouse = new ChakramX();
        byte[] reply = SimulatedPeripherals.MouseReply(mouse, [0x00, 0x12, 0x07], 65);

        Assert.Equal(0x12, reply[1]);
        Assert.Equal(0x07, reply[2]);
        Assert.True(reply[5] > 0, "A zero battery reads as a mouse that has gone to sleep.");
    }

    [Fact]
    public void APretendMouseReportsDpiInsideItsOwnRange()
    {
        var mouse = new ChakramX();
        byte[] reply = SimulatedPeripherals.MouseReply(mouse, [0x00, 0x12, 0x04, 0x00], 65);

        int increment = mouse.DPIIncrements();
        int first = (reply[5] | reply[6] << 8) * increment + increment;
        Assert.InRange(first, mouse.MinDPI(), mouse.MaxDPI());
    }
}
