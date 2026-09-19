using System;
using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The eight bytes in front of every remote control frame.
/// </summary>
/// <remarks>
/// This is the one part of a session that fails silently. A header written a byte out
/// still writes, and the reader on the other side takes a length from the wrong place
/// and then hunts for a frame boundary that is no longer where it thinks: the picture
/// stops, the log says nothing, and the only symptom is a session that dies a second
/// after it opens. Finding that on a phone costs an afternoon; finding it here costs a
/// millisecond.
/// </remarks>
public class RemoteSessionFramingTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 5, 0, 1)]
    [InlineData(5, 1, 65535, 192 * 1024)]
    [InlineData(255, 255, 40000, RemoteSessionFraming.MaxFrameBytes)]
    public void RoundTripsEveryField(byte channel, byte flags, ushort stream, int length)
    {
        Span<byte> header = stackalloc byte[RemoteSessionFraming.HeaderSize];
        RemoteSessionFraming.Write(header, channel, flags, stream, length);

        Assert.True(RemoteSessionFraming.TryRead(header, out byte readChannel, out byte readFlags, out ushort readStream, out int readLength));
        Assert.Equal(channel, readChannel);
        Assert.Equal(flags, readFlags);
        Assert.Equal(stream, readStream);
        Assert.Equal(length, readLength);
    }

    /// <summary>
    /// Big endian, in the documented order.
    /// </summary>
    /// <remarks>
    /// Pinned as literal bytes rather than through the reader, because a reader and a
    /// writer that agree with each other and disagree with the Android client would
    /// pass a round-trip test and fail on a phone.
    /// </remarks>
    [Fact]
    public void WritesTheDocumentedLayout()
    {
        Span<byte> header = stackalloc byte[RemoteSessionFraming.HeaderSize];
        RemoteSessionFraming.Write(header, channel: 1, flags: 4, stream: 0x0102, length: 0x00ABCDEF);

        Assert.Equal(new byte[] { 1, 4, 0x01, 0x02, 0x00, 0xAB, 0xCD, 0xEF }, header.ToArray());
    }

    [Fact]
    public void RefusesALengthPastTheCap()
    {
        Span<byte> header = stackalloc byte[RemoteSessionFraming.HeaderSize];
        header[4] = 0x7F;
        header[5] = 0xFF;
        header[6] = 0xFF;
        header[7] = 0xFF;

        Assert.False(RemoteSessionFraming.TryRead(header, out _, out _, out _, out int length));
        Assert.Equal(int.MaxValue, length);
    }

    /// <summary>
    /// A length whose top bit is set reads as negative, and must not be trusted.
    /// </summary>
    /// <remarks>
    /// A reader that took this at face value would either allocate nothing and treat
    /// the next frame's bytes as a header, or, on a signed comparison written the
    /// obvious way, read past its own buffer.
    /// </remarks>
    [Fact]
    public void RefusesANegativeLength()
    {
        Span<byte> header = stackalloc byte[RemoteSessionFraming.HeaderSize];
        header[4] = 0xFF;

        Assert.False(RemoteSessionFraming.TryRead(header, out _, out _, out _, out int length));
        Assert.True(length < 0);
    }

    [Fact]
    public void RefusesToWriteALengthItCouldNotRead()
    {
        byte[] header = new byte[RemoteSessionFraming.HeaderSize];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteSessionFraming.Write(header, 0, 0, 0, RemoteSessionFraming.MaxFrameBytes + 1));
    }

    [Fact]
    public void RefusesAShortBuffer()
    {
        byte[] header = new byte[RemoteSessionFraming.HeaderSize - 1];
        Assert.Throws<ArgumentException>(() => RemoteSessionFraming.Write(header, 0, 0, 0, 0));
        Assert.False(RemoteSessionFraming.TryRead(header, out _, out _, out _, out _));
    }
}
