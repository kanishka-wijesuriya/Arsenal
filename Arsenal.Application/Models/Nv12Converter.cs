using System;
using System.Threading.Tasks;

namespace Arsenal.Application.Models
{
    /// <summary>
    /// Turns a captured BGRA surface into the NV12 a video encoder wants.
    /// </summary>
    /// <remarks>
    /// Every hardware H.264 and HEVC encoder on Windows takes NV12 and nothing useful
    /// else, so this conversion sits between the screen and the encoder on every frame.
    /// It is the hottest loop in a session, and it is also the one piece of the video
    /// path whose mistakes are quiet: a swapped coefficient or an off-by-one in the
    /// chroma plane produces a picture that arrives, decodes and looks slightly wrong,
    /// which is why it lives here where it can be tested rather than inside the
    /// encoder's COM plumbing.
    ///
    /// <para><b>Colour.</b> BT.709, limited range, because that is what a decoder
    /// assumes for anything above standard definition when the stream says nothing, and
    /// what this writes into the stream's own VUI so it does not have to assume.</para>
    ///
    /// <para><b>Chroma.</b> NV12 carries one U and one V for each 2x2 block of pixels.
    /// The four are averaged rather than sampled from the top-left corner: point
    /// sampling a desktop makes single-pixel coloured text fringe badly, because the
    /// corner it happened to take is as likely to be background as glyph.</para>
    ///
    /// <para><b>Speed.</b> Scalar, over row bands in parallel. A 4K frame is eight
    /// million pixels and a single thread spends about thirty milliseconds on it, which
    /// would cap a session at half the frame rate it asked for before the encoder had
    /// seen anything. Row bands are independent, which is what makes that safe: every
    /// band writes its own rows of both planes and reads nobody else's.</para>
    /// </remarks>
    public static class Nv12Converter
    {
        /// <summary>
        /// Rows converted by one work item.
        /// </summary>
        /// <remarks>
        /// Two at a time at minimum, since a chroma row is shared by two luma rows.
        /// Sixteen keeps the number of work items sensible at 4K (135 of them) without
        /// leaving a 720p frame as a single band that defeats the point.
        /// </remarks>
        private const int BandRows = 16;

        /// <summary>The NV12 buffer size for a frame, luma plane plus interleaved chroma.</summary>
        public static int BufferSize(int width, int height) => width * height * 3 / 2;

        /// <summary>
        /// Converts one frame.
        /// </summary>
        /// <param name="source">BGRA, top row first.</param>
        /// <param name="sourceStride">Bytes per source row, which may exceed width * 4.</param>
        /// <param name="destination">At least <see cref="BufferSize"/> bytes.</param>
        /// <remarks>
        /// Width and height must be even. Every caller scales to even dimensions
        /// already because the encoders require it, and handling the odd case here
        /// would mean inventing a half-block of chroma that no encoder would accept.
        /// </remarks>
        public static void Convert(ReadOnlySpan<byte> source, int sourceStride, int width, int height, Span<byte> destination)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs even dimensions.", nameof(width));
            if (sourceStride < width * 4) throw new ArgumentOutOfRangeException(nameof(sourceStride));
            if (source.Length < sourceStride * (height - 1) + width * 4) throw new ArgumentException("Source is short.", nameof(source));
            if (destination.Length < BufferSize(width, height)) throw new ArgumentException("Destination is short.", nameof(destination));

            // Parallel.For cannot close over a Span, so the bands address the buffers
            // through pointers taken once here. Each band touches only its own rows.
            unsafe
            {
                fixed (byte* sourceBase = source)
                fixed (byte* destinationBase = destination)
                {
                    byte* from = sourceBase;
                    byte* to = destinationBase;
                    int bands = (height + BandRows - 1) / BandRows;

                    if (bands == 1)
                    {
                        ConvertBand(from, sourceStride, width, height, to, 0, height);
                        return;
                    }

                    Parallel.For(0, bands, band =>
                    {
                        int top = band * BandRows;
                        int bottom = Math.Min(top + BandRows, height);
                        ConvertBand(from, sourceStride, width, height, to, top, bottom);
                    });
                }
            }
        }

        private static unsafe void ConvertBand(byte* source, int sourceStride, int width, int height, byte* destination, int top, int bottom)
        {
            byte* luma = destination;
            byte* chroma = destination + width * height;

            for (int y = top; y < bottom; y += 2)
            {
                byte* row0 = source + (long)y * sourceStride;
                byte* row1 = source + (long)(y + 1) * sourceStride;
                byte* luma0 = luma + (long)y * width;
                byte* luma1 = luma + (long)(y + 1) * width;
                byte* chromaRow = chroma + (long)(y / 2) * width;

                for (int x = 0; x < width; x += 2)
                {
                    int offset = x * 4;

                    // BGRA, so blue is first. Read all four pixels of the block once:
                    // the luma of each is needed anyway and the chroma is their mean.
                    int b00 = row0[offset], g00 = row0[offset + 1], r00 = row0[offset + 2];
                    int b01 = row0[offset + 4], g01 = row0[offset + 5], r01 = row0[offset + 6];
                    int b10 = row1[offset], g10 = row1[offset + 1], r10 = row1[offset + 2];
                    int b11 = row1[offset + 4], g11 = row1[offset + 5], r11 = row1[offset + 6];

                    luma0[x] = Luma(r00, g00, b00);
                    luma0[x + 1] = Luma(r01, g01, b01);
                    luma1[x] = Luma(r10, g10, b10);
                    luma1[x + 1] = Luma(r11, g11, b11);

                    int r = (r00 + r01 + r10 + r11 + 2) >> 2;
                    int g = (g00 + g01 + g10 + g11 + 2) >> 2;
                    int b = (b00 + b01 + b10 + b11 + 2) >> 2;

                    chromaRow[x] = ChromaU(r, g, b);
                    chromaRow[x + 1] = ChromaV(r, g, b);
                }
            }
        }

        // BT.709 limited range, in 16.16 fixed point. Integer arithmetic throughout:
        // the float form of this loop is measurably slower and cannot be more accurate
        // than the eight bits it is rounding into.
        //
        //   Y  =  16 + ( 0.2126 R + 0.7152 G + 0.0722 B) * 219/255
        //   Cb = 128 + (-0.1146 R - 0.3854 G + 0.5000 B) * 224/255
        //   Cr = 128 + ( 0.5000 R - 0.4542 G - 0.0458 B) * 224/255

        private const int YR = 11966;   // 0.2126 * 219/255 * 65536
        private const int YG = 40254;   // 0.7152 * 219/255 * 65536
        private const int YB = 4064;    // 0.0722 * 219/255 * 65536
        private const int UR = -6596;   // -0.1146 * 224/255 * 65536
        private const int UG = -22189;  // -0.3854 * 224/255 * 65536
        private const int UB = 28784;   //  0.5000 * 224/255 * 65536
        private const int VR = 28784;
        private const int VG = -26145;  // -0.4542 * 224/255 * 65536
        private const int VB = -2638;   // -0.0458 * 224/255 * 65536

        private const int Half = 1 << 15;

        private static byte Luma(int r, int g, int b) =>
            Clamp(16 + ((YR * r + YG * g + YB * b + Half) >> 16));

        private static byte ChromaU(int r, int g, int b) =>
            Clamp(128 + ((UR * r + UG * g + UB * b + Half) >> 16));

        private static byte ChromaV(int r, int g, int b) =>
            Clamp(128 + ((VR * r + VG * g + VB * b + Half) >> 16));

        private static byte Clamp(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
    }
}
