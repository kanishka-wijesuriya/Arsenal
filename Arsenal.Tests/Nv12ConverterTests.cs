using System;
using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The colour conversion between the screen and the video encoder.
/// </summary>
/// <remarks>
/// This runs on every pixel of every frame of every session, and it fails quietly: a
/// swapped coefficient or a chroma plane written one row out still produces a picture
/// that arrives and decodes, just with the wrong colours or a soft magenta edge along
/// every line of text. Nobody finds that by looking at a screenshot on a phone, so the
/// values are pinned here instead.
/// </remarks>
public class Nv12ConverterTests
{
    private const int Width = 4;
    private const int Height = 4;

    /// <summary>
    /// The three cases where BT.709 limited range has an exact answer.
    /// </summary>
    /// <remarks>
    /// Black is 16 rather than 0 and white is 235 rather than 255, which is the whole
    /// point of limited range and the first thing a wrong conversion gets wrong. Mid
    /// grey is checked too, because a conversion that dropped the 219/255 scale
    /// entirely would still pass black if it only forgot the offset.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(128, 128, 128, 126, 128, 128)]
    public void ConvertsGreyToTheLimitedRange(byte r, byte g, byte b, byte expectedY, byte expectedU, byte expectedV)
    {
        byte[] nv12 = Convert(Fill(r, g, b));

        Assert.Equal(expectedY, nv12[0]);
        Assert.Equal(expectedY, nv12[Width * Height - 1]);
        Assert.Equal(expectedU, nv12[Width * Height]);
        Assert.Equal(expectedV, nv12[Width * Height + 1]);
    }

    /// <summary>
    /// Red, green and blue land where BT.709 says, and on the right axis.
    /// </summary>
    /// <remarks>
    /// The direction matters as much as the value. Blue has the largest positive Cb and
    /// the smallest luma; red has the largest positive Cr. Swapping the U and V writes,
    /// which is a single-character mistake in an interleaved plane, turns one into the
    /// other and is invisible in any test that only checks luma.
    /// </remarks>
    [Fact]
    public void PutsPrimariesOnTheRightChromaAxis()
    {
        byte[] red = Convert(Fill(255, 0, 0));
        byte[] green = Convert(Fill(0, 255, 0));
        byte[] blue = Convert(Fill(0, 0, 255));

        int plane = Width * Height;

        // Luma: green is much brighter than red, red brighter than blue.
        Assert.True(green[0] > red[0] && red[0] > blue[0]);

        // Red is the Cr extreme, blue the Cb extreme, and green negative on both.
        Assert.True(red[plane + 1] > 200 && red[plane] < 128);
        Assert.True(blue[plane] > 200 && blue[plane + 1] < 128);
        Assert.True(green[plane] < 128 && green[plane + 1] < 128);
    }

    /// <summary>
    /// A 2x2 block with one bright pixel averages rather than point samples.
    /// </summary>
    /// <remarks>
    /// Point sampling the top-left corner is the cheaper thing to write and produces a
    /// visible fringe on coloured text, where the corner it takes is as likely to be
    /// background as glyph. Checked by making the corner black and the rest red: a
    /// point sampler returns neutral chroma, an averaging one does not.
    /// </remarks>
    [Fact]
    public void AveragesChromaOverTheBlock()
    {
        byte[] source = new byte[Width * 4 * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int at = y * Width * 4 + x * 4;
                bool corner = x == 0 && y == 0;
                source[at] = 0;                        // blue
                source[at + 1] = 0;                    // green
                source[at + 2] = corner ? (byte)0 : (byte)255; // red
                source[at + 3] = 255;
            }
        }

        byte[] nv12 = Convert(source);
        int plane = Width * Height;

        // Three of the four pixels are red, so Cr must be well above neutral but short
        // of where a fully red block would put it.
        byte[] solidRed = Convert(Fill(255, 0, 0));
        Assert.True(nv12[plane + 1] > 150);
        Assert.True(nv12[plane + 1] < solidRed[plane + 1]);
    }

    /// <summary>A source row longer than the visible width is not read as pixels.</summary>
    /// <remarks>
    /// A capture surface is padded to a four byte boundary, so stride and width * 4
    /// differ on plenty of real resolutions. Reading the padding shifts every row by a
    /// few pixels and skews the whole image.
    /// </remarks>
    [Fact]
    public void HonoursASourceStrideWiderThanTheImage()
    {
        int stride = Width * 4 + 12;
        byte[] source = new byte[stride * Height];
        for (int i = 0; i < source.Length; i++) source[i] = 0xFF; // padding is white
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int at = y * stride + x * 4;
                source[at] = 0;
                source[at + 1] = 0;
                source[at + 2] = 0;
                source[at + 3] = 255;
            }
        }

        byte[] nv12 = new byte[Nv12Converter.BufferSize(Width, Height)];
        Nv12Converter.Convert(source, stride, Width, Height, nv12);

        // Every visible pixel was black, so every luma sample is the limited-range floor.
        for (int i = 0; i < Width * Height; i++) Assert.Equal(16, nv12[i]);
    }

    /// <summary>
    /// Every row is converted, including the last band of a frame that does not divide
    /// evenly into bands.
    /// </summary>
    /// <remarks>
    /// The conversion runs its rows in parallel bands. A band boundary computed with
    /// the wrong rounding leaves the bottom of the picture as whatever was in the
    /// buffer last frame, which on a still desktop looks like the frame simply working.
    /// 34 rows is two full 16-row bands and a partial one.
    /// </remarks>
    [Fact]
    public void ConvertsEveryRowOfAFrameThatDoesNotDivideIntoBands()
    {
        const int width = 8;
        const int height = 34;
        byte[] source = new byte[width * 4 * height];
        for (int i = 0; i < source.Length; i += 4)
        {
            source[i] = 255;      // blue
            source[i + 1] = 255;
            source[i + 2] = 255;
            source[i + 3] = 255;
        }

        byte[] nv12 = new byte[Nv12Converter.BufferSize(width, height)];
        Array.Fill(nv12, (byte)0xAA);
        Nv12Converter.Convert(source, width * 4, width, height, nv12);

        for (int i = 0; i < width * height; i++) Assert.Equal(235, nv12[i]);
        for (int i = width * height; i < nv12.Length; i++) Assert.Equal(128, nv12[i]);
    }

    [Fact]
    public void RefusesOddDimensions()
    {
        byte[] source = new byte[5 * 4 * 4];
        byte[] destination = new byte[Nv12Converter.BufferSize(4, 4)];
        Assert.Throws<ArgumentException>(() => Nv12Converter.Convert(source, 5 * 4, 5, 4, destination));
    }

    private static byte[] Fill(byte r, byte g, byte b)
    {
        byte[] source = new byte[Width * 4 * Height];
        for (int i = 0; i < source.Length; i += 4)
        {
            source[i] = b;
            source[i + 1] = g;
            source[i + 2] = r;
            source[i + 3] = 255;
        }
        return source;
    }

    private static byte[] Convert(byte[] source)
    {
        byte[] nv12 = new byte[Nv12Converter.BufferSize(Width, Height)];
        Nv12Converter.Convert(source, Width * 4, Width, Height, nv12);
        return nv12;
    }
}
