using System;
using System.Buffers.Binary;

namespace Arsenal.Application.Models
{
    /// <summary>
    /// The eight byte header every remote control frame carries.
    /// </summary>
    /// <remarks>
    /// Here rather than beside the session itself so it can be tested without a socket,
    /// a certificate or a phone, which is the same reason <see cref="PairingWindow"/>
    /// lives here. This is the one piece of the session that fails silently when it is
    /// wrong: a header written a byte out does not throw, it hands the reader a length
    /// taken from the wrong place, and the stream is unrecoverable from that point on
    /// with nothing in the log to say where it went.
    ///
    /// <para>Big endian, so a capture of the stream reads in the order the fields are
    /// written:</para>
    /// <code>
    /// 0      channel
    /// 1      flags     channel specific
    /// 2..3   stream    monitor index for video, transfer id for files, else zero
    /// 4..7   length    payload bytes following the header
    /// </code>
    /// </remarks>
    public static class RemoteSessionFraming
    {
        public const int HeaderSize = 8;

        /// <summary>
        /// The largest payload either side will read.
        /// </summary>
        /// <remarks>
        /// A keyframe of a 4K desktop at the highest quality is comfortably under four
        /// megabytes and a file chunk is capped far below that, so this is not a limit
        /// anything legitimate reaches. It exists so a corrupted or hostile length
        /// cannot make the reader allocate whatever it was told to.
        /// </remarks>
        public const int MaxFrameBytes = 16 * 1024 * 1024;

        public static void Write(Span<byte> header, byte channel, byte flags, ushort stream, int length)
        {
            if (header.Length < HeaderSize) throw new ArgumentException("A frame header needs " + HeaderSize + " bytes.", nameof(header));
            if (length < 0 || length > MaxFrameBytes) throw new ArgumentOutOfRangeException(nameof(length));

            header[0] = channel;
            header[1] = flags;
            BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2), stream);
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(4), length);
        }

        /// <summary>
        /// Reads a header, refusing a length that cannot be honest.
        /// </summary>
        /// <returns>
        /// False when the length is negative or past <see cref="MaxFrameBytes"/>. The
        /// caller ends the session rather than trusting the rest of the stream: after a
        /// bad length there is no way to find where the next frame starts.
        /// </returns>
        public static bool TryRead(ReadOnlySpan<byte> header, out byte channel, out byte flags, out ushort stream, out int length)
        {
            channel = 0;
            flags = 0;
            stream = 0;
            length = 0;
            if (header.Length < HeaderSize) return false;

            channel = header[0];
            flags = header[1];
            stream = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2));
            length = BinaryPrimitives.ReadInt32BigEndian(header.Slice(4));
            return length >= 0 && length <= MaxFrameBytes;
        }
    }
}
