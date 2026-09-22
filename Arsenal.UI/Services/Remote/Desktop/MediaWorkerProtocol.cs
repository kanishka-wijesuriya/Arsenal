using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace Arsenal.UI.Services.Remote.Desktop;

internal sealed record MediaWorkerConfiguration(
    int Monitor,
    int MaxWidth,
    int MaxHeight,
    int FrameRate,
    int BitrateKbps,
    int JpegQuality,
    bool Cursor,
    bool Audio,
    bool MonoAudio,
    string[] Codecs);

internal sealed record MediaWorkerReady(
    string Codec,
    string Encoder,
    int Width,
    int Height,
    int FrameRate,
    RemoteAudioFormat? Audio);

internal sealed record MediaWorkerStats(int Fps, int EncodeMs, int CaptureMs, string Encoder);

internal sealed record MediaWorkerSurface(bool Readable);

internal sealed record MediaWorkerError(string Message);

internal static class MediaWorkerProtocol
{
    internal const int HeaderSize = 6;
    internal const int MaxPayloadBytes = RemoteDesktopProtocol.MaxFrameBytes;

    internal enum Message : byte
    {
        Ready = 1,
        Video = 2,
        Audio = 3,
        Surface = 4,
        Stats = 5,
        Error = 6,
    }

    internal enum Command : byte
    {
        Stop = 1,
        KeyFrame = 2,
    }

    internal static string EncodeConfiguration(MediaWorkerConfiguration configuration)
    {
        string value = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(configuration, RemoteDesktopProtocol.Json));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static MediaWorkerConfiguration DecodeConfiguration(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
        return JsonSerializer.Deserialize<MediaWorkerConfiguration>(Convert.FromBase64String(base64), RemoteDesktopProtocol.Json)
            ?? throw new InvalidDataException("The media worker configuration was empty.");
    }

    internal static void WriteHeader(Span<byte> header, Message message, byte flags, int length)
    {
        header[0] = (byte)message;
        header[1] = flags;
        BinaryPrimitives.WriteInt32BigEndian(header[2..], length);
    }

    internal static (Message Message, byte Flags, int Length) ReadHeader(ReadOnlySpan<byte> header) =>
        ((Message)header[0], header[1], BinaryPrimitives.ReadInt32BigEndian(header[2..]));
}
