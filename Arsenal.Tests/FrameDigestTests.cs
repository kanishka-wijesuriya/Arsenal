using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The check that decides whether a captured frame is worth encoding.
/// </summary>
/// <remarks>
/// A digest that misses a change freezes the remote screen until the next keyframe,
/// which is up to ten seconds of a session showing something that is no longer there.
/// That is the failure worth guarding: reporting a change that did not happen only
/// costs one wasted frame.
/// </remarks>
public class FrameDigestTests
{
    private const int Width = 64;
    private const int Height = 48;

    [Fact]
    public void TheSameFrameDigestsTheSame()
    {
        byte[] frame = Noise(seed: 1);
        Assert.Equal(
            FrameDigest.Compute(frame, Width * 4, Width, Height),
            FrameDigest.Compute(frame, Width * 4, Width, Height));
    }

    /// <summary>
    /// One byte, anywhere, changes the answer.
    /// </summary>
    /// <remarks>
    /// The corners and the exact centre are the positions a banded or sampled
    /// implementation is most likely to skip, and a caret is one pixel wide.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(Width * 4 * Height / 2)]
    [InlineData(Width * 4 * Height - 4)]
    [InlineData(Width * 4 * 17 + 40)]
    public void OneChangedPixelChangesTheDigest(int at)
    {
        byte[] before = Noise(seed: 7);
        ulong original = FrameDigest.Compute(before, Width * 4, Width, Height);

        byte[] after = (byte[])before.Clone();
        after[at] ^= 0x01;

        Assert.NotEqual(original, FrameDigest.Compute(after, Width * 4, Width, Height));
    }

    /// <summary>
    /// The same pixels at two positions do not digest the same.
    /// </summary>
    /// <remarks>
    /// A digest that folded its bands together without regard to order would call a
    /// window on the left identical to the same window on the right, and dragging it
    /// would not redraw.
    /// </remarks>
    [Fact]
    public void MovingContentChangesTheDigest()
    {
        byte[] left = new byte[Width * 4 * Height];
        byte[] right = new byte[Width * 4 * Height];
        for (int i = 0; i < 64; i++)
        {
            left[i] = 0xFF;
            right[left.Length - 64 + i] = 0xFF;
        }

        Assert.NotEqual(
            FrameDigest.Compute(left, Width * 4, Width, Height),
            FrameDigest.Compute(right, Width * 4, Width, Height));
    }

    /// <summary>
    /// Row padding is not part of the picture.
    /// </summary>
    /// <remarks>
    /// A capture surface is padded to a four byte boundary and the driver leaves
    /// whatever it likes there. Hashing it would report a change on every frame and
    /// turn the whole optimisation off, silently and only on some resolutions.
    /// </remarks>
    [Fact]
    public void IgnoresRowPadding()
    {
        int stride = Width * 4 + 16;
        byte[] first = new byte[stride * Height];
        byte[] second = new byte[stride * Height];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width * 4; x++)
            {
                first[y * stride + x] = (byte)(x + y);
                second[y * stride + x] = (byte)(x + y);
            }
            // Different junk in the padding of each.
            for (int p = Width * 4; p < stride; p++)
            {
                first[y * stride + p] = 0x11;
                second[y * stride + p] = 0xEE;
            }
        }

        Assert.Equal(
            FrameDigest.Compute(first, stride, Width, Height),
            FrameDigest.Compute(second, stride, Width, Height));
    }

    /// <summary>
    /// A change in the last partial band of rows is still seen.
    /// </summary>
    /// <remarks>
    /// The digest runs in sixteen-row bands. 50 rows is three full bands and a partial
    /// one, and a band boundary computed with the wrong rounding would stop watching
    /// the bottom two rows of the screen.
    /// </remarks>
    [Fact]
    public void WatchesTheLastPartialBand()
    {
        const int height = 50;
        byte[] before = new byte[Width * 4 * height];
        ulong original = FrameDigest.Compute(before, Width * 4, Width, height);

        byte[] after = (byte[])before.Clone();
        after[(height - 1) * Width * 4] = 0xFF;

        Assert.NotEqual(original, FrameDigest.Compute(after, Width * 4, Width, height));
    }

    private static byte[] Noise(int seed)
    {
        var random = new Random(seed);
        byte[] frame = new byte[Width * 4 * Height];
        random.NextBytes(frame);
        return frame;
    }
}
