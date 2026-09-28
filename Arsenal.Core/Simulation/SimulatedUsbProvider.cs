using Arsenal.AnimeMatrix.Communication.Platform;

namespace Arsenal.Simulation;

/// <summary>
/// A USB channel for the lid lighting devices that accepts every packet and sends none.
/// </summary>
/// <remarks>
/// The Slash and AniMe Matrix devices open their HID interface as soon as they are
/// constructed, and on a machine that has one the packets would reach it: simulating a
/// G14 on a G16 would draw on the G16's real lid. This keeps the device object alive, so
/// the pages built on it appear, while nothing leaves the process. Reads return zeros,
/// which every caller already treats as "nothing to report".
/// </remarks>
internal sealed class SimulatedUsbProvider(ushort vendorId, ushort productId) : UsbProvider(vendorId, productId)
{
    public override void Set(byte[] data) => DeviceSimulation.Record($"USB {ProductID:X4} feature {Describe(data)}");

    public override byte[] Get(byte[] data) => new byte[data.Length];

    public override void Read(byte[] data) => Array.Clear(data);

    public override void Write(byte[] data) => DeviceSimulation.Record($"USB {ProductID:X4} write {Describe(data)}");

    public override void Dispose() { }

    private static string Describe(byte[] data) =>
        Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 12)));
}
