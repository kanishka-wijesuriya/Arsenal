using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Arsenal.Application.Models
{
    /// <summary>
    /// A fingerprint of a captured frame, for deciding whether to encode it at all.
    /// </summary>
    /// <remarks>
    /// A video encoder asked to code an unchanged picture does not send nothing. It
    /// sends whatever its rate control thinks it owes, and on the hardware measured
    /// here that is the full configured bitrate: a frozen desktop cost twelve megabits
    /// a second, more than the same desktop with a window being dragged across it,
    /// because the rate controller was padding to hit its average. Every rate control
    /// mode the encoder offered did some version of this or broke outright.
    ///
    /// <para>So the encoder is not asked. A frame identical to the one before it is
    /// dropped before the colour conversion, which costs one pass over the pixels and
    /// saves both the conversion and the encode. A desktop nobody is touching then
    /// costs nothing at all, which is the correct answer and not one any encoder
    /// setting produced.</para>
    ///
    /// <para><b>Collisions.</b> 64-bit FNV-1a over every byte. A collision would hold
    /// one stale frame until the next change or the next keyframe, both of which are
    /// seconds away at worst; paying for a cryptographic hash to avoid something that
    /// rare and that cheap would be the wrong trade.</para>
    /// </remarks>
    public static class FrameDigest
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        /// <summary>Rows per work item, matching the conversion that follows it.</summary>
        private const int BandRows = 16;

        /// <summary>
        /// Hashes the visible pixels of a BGRA frame.
        /// </summary>
        /// <remarks>
        /// Row by row rather than over the whole buffer, because a capture surface is
        /// padded to a four byte boundary and the padding is not part of the picture:
        /// including it makes the digest depend on whatever the driver left there.
        /// </remarks>
        public static ulong Compute(ReadOnlySpan<byte> pixels, int stride, int width, int height)
        {
            if (width <= 0 || height <= 0) return 0;
            int rowBytes = width * 4;
            if (stride < rowBytes || pixels.Length < stride * (height - 1) + rowBytes) return 0;

            int bands = (height + BandRows - 1) / BandRows;
            var digests = new ulong[bands];

            unsafe
            {
                fixed (byte* origin = pixels)
                {
                    byte* start = origin;
                    if (bands == 1)
                    {
                        digests[0] = Band(start, stride, rowBytes, 0, height);
                    }
                    else
                    {
                        // Parallel.For cannot close over a Span, and each band reads
                        // only its own rows, so a pointer taken once is safe here.
                        var local = digests;
                        IntPtr baseAddress = (IntPtr)start;
                        Parallel.For(0, bands, band =>
                        {
                            int top = band * BandRows;
                            int bottom = Math.Min(top + BandRows, height);
                            local[band] = Band((byte*)baseAddress, stride, rowBytes, top, bottom);
                        });
                    }
                }
            }

            // The per-band digests are folded in order, so the result depends on where
            // a change was as well as what it was.
            ulong hash = Offset;
            foreach (ulong band in digests)
            {
                hash = (hash ^ band) * Prime;
            }
            return hash;
        }

        private static unsafe ulong Band(byte* origin, int stride, int rowBytes, int top, int bottom)
        {
            ulong hash = Offset;
            int aligned = rowBytes & ~7;

            for (int y = top; y < bottom; y++)
            {
                byte* row = origin + (long)y * stride;
                var wide = new ReadOnlySpan<ulong>(row, aligned / 8);
                foreach (ulong value in wide)
                {
                    hash = (hash ^ value) * Prime;
                }
                for (int i = aligned; i < rowBytes; i++)
                {
                    hash = (hash ^ row[i]) * Prime;
                }
            }
            return hash;
        }
    }
}
