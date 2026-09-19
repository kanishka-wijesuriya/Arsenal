using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arsenal.UI.Services.Remote.Desktop;

/// <summary>
/// The wire format for a remote control session.
/// </summary>
/// <remarks>
/// The companion's other endpoints are request and response over HTTPS, one request per
/// connection, and that shape cannot carry a screen. A session needs a socket that stays
/// open for as long as someone is watching, carries video, audio, input, clipboard and
/// file bytes at once, and lets either side speak first.
///
/// <para>So the session upgrades out of HTTP after the bearer token has been checked, on
/// the same TLS connection the phone already pinned, and everything after the upgrade is
/// this framing. There is no WebSocket here on purpose: both ends are ours, masking every
/// client frame buys nothing against a pinned certificate, and a fixed eight byte header
/// costs less per frame than a variable length one at sixty frames a second.</para>
///
/// <para>Header, big endian, in the order a reader needs them:</para>
/// <code>
/// 0      channel  which stream this belongs to
/// 1      flags    channel specific
/// 2..3   stream   monitor index for video, otherwise zero
/// 4..7   length   payload bytes following the header
/// </code>
/// </remarks>
internal static class RemoteDesktopProtocol
{
    /// <summary>The token both ends name in the HTTP upgrade.</summary>
    internal const string UpgradeToken = "arsenal-remote/1";

    internal const string SessionPath = "/v1/remote/session";

    internal const int HeaderSize = Arsenal.Application.Models.RemoteSessionFraming.HeaderSize;

    internal const int MaxFrameBytes = Arsenal.Application.Models.RemoteSessionFraming.MaxFrameBytes;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static void WriteHeader(Span<byte> header, Channel channel, byte flags, ushort stream, int length) =>
        Arsenal.Application.Models.RemoteSessionFraming.Write(header, (byte)channel, flags, stream, length);

    /// <summary>Reads a header, or returns a negative length for one that cannot be honest.</summary>
    internal static (Channel Channel, byte Flags, ushort Stream, int Length) ReadHeader(ReadOnlySpan<byte> header) =>
        Arsenal.Application.Models.RemoteSessionFraming.TryRead(header, out byte channel, out byte flags, out ushort stream, out int length)
            ? ((Channel)channel, flags, stream, length)
            : ((Channel)header[0], header[1], stream, -1);

    internal static byte[] Encode<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    internal static string Describe(ReadOnlySpan<byte> payload) => Encoding.UTF8.GetString(payload);

    internal enum Channel : byte
    {
        /// <summary>JSON messages that set up and steer the session.</summary>
        Control = 0,
        Video = 1,
        Audio = 2,
        /// <summary>Pointer and keyboard, phone to desktop, binary for latency.</summary>
        Input = 3,
        Clipboard = 4,
        Files = 5,
        /// <summary>Cursor shape and position, so the phone can draw it without a frame.</summary>
        Cursor = 6,
    }

    /// <summary>Flags on a <see cref="Channel.Video"/> frame.</summary>
    [Flags]
    internal enum VideoFlags : byte
    {
        None = 0,
        /// <summary>Decodable on its own. An H.264 IDR, or a full JPEG surface.</summary>
        KeyFrame = 1,
        /// <summary>Codec setup bytes rather than a picture. SPS and PPS, sent once.</summary>
        Configuration = 2,
        /// <summary>The payload is a tile list rather than a single coded picture.</summary>
        Tiles = 4,
    }

    /// <summary>
    /// The first byte of an <see cref="Channel.Input"/> payload.
    /// </summary>
    /// <remarks>
    /// Pointer positions travel as 0..65535 across the captured surface rather than as
    /// pixels. The phone does not know the desktop's pixel geometry, its own view is
    /// scaled and letterboxed, and a monitor can change resolution mid session; a
    /// normalised coordinate survives all three.
    /// </remarks>
    internal enum InputKind : byte
    {
        PointerMove = 1,
        PointerButton = 2,
        Wheel = 3,
        Key = 4,
        /// <summary>UTF-16 text, for anything a virtual keyboard cannot express as a key.</summary>
        Text = 5,
        /// <summary>A two finger scroll or pinch resolved by the phone into wheel units.</summary>
        HorizontalWheel = 6,
    }

    internal enum PointerButton : byte
    {
        Left = 0,
        Right = 1,
        Middle = 2,
        XButton1 = 3,
        XButton2 = 4,
    }
}

// ---- Control messages -------------------------------------------------------------
//
// One record per message, each carrying its own "type". They are small and there are
// enough of them that a switch over a string in one file is easier to follow than a
// polymorphic hierarchy spread over several.

internal sealed record RemoteMonitorInfo(int Index, string Name, int Width, int Height, int Left, int Top, bool Primary, int RefreshHz);

internal sealed record RemoteWelcome(
    string Type,
    int ProtocolVersion,
    string DeviceName,
    IReadOnlyList<RemoteMonitorInfo> Monitors,
    RemoteSessionCapabilities Capabilities);

internal sealed record RemoteSessionCapabilities(
    bool HardwareVideo,
    IReadOnlyList<string> Codecs,
    bool Audio,
    bool Clipboard,
    bool FileTransfer,
    bool PrivacyScreen,
    bool BlockLocalInput,
    bool Elevated);

/// <summary>Sent while a person at the desktop is being asked, and again with the answer.</summary>
internal sealed record RemoteConsentMessage(string Type, string State, int SecondsRemaining, string? Reason);

internal sealed record RemoteStartRequest(
    string Type,
    int Monitor,
    string? Codec,
    int MaxWidth,
    int MaxHeight,
    int FrameRate,
    int BitrateKbps,
    bool Audio,
    bool Cursor);

internal sealed record RemoteStarted(
    string Type,
    string Codec,
    int Width,
    int Height,
    int FrameRate,
    int Monitor,
    bool ViewOnly,
    RemoteAudioFormat? Audio);

internal sealed record RemoteAudioFormat(int SampleRate, int Channels, int BitsPerSample);

internal sealed record RemoteStats(
    string Type,
    int Fps,
    long BytesPerSecond,
    int EncodeMs,
    int CaptureMs,
    int QueuedFrames,
    string Encoder);

internal sealed record RemoteError(string Type, string Message, string? Code = null);
