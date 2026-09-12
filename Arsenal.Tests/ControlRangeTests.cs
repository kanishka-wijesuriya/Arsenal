using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// The clamp that decides what a slider is allowed to write to the hardware.
/// </summary>
/// <remarks>
/// Every writer below the UI silently drops an out-of-range value rather than
/// reporting it, so a clamp that is wrong at the boundary produces a control that
/// looks like it worked and did nothing. That failure is invisible in a screenshot,
/// which is what makes it worth pinning here.
/// </remarks>
public class ControlRangeTests
{
    [Theory]
    [InlineData(5, 50, 4, 5)]      // below the floor
    [InlineData(5, 50, 5, 5)]      // exactly the floor
    [InlineData(5, 50, 27, 27)]    // inside
    [InlineData(5, 50, 50, 50)]    // exactly the ceiling
    [InlineData(5, 50, 51, 50)]    // above the ceiling
    [InlineData(5, 50, int.MinValue, 5)]
    [InlineData(5, 50, int.MaxValue, 50)]
    public void ClampsOntoTheTrack(int minimum, int maximum, int value, int expected)
    {
        Assert.Equal(expected, new ControlRange(minimum, maximum).Clamp(value));
    }

    [Fact]
    public void NegativeBoundsClampNormally()
    {
        // Curve Optimizer offsets are negative on every supported Ryzen part.
        var undervolt = new ControlRange(-30, 0);
        Assert.Equal(-30, undervolt.Clamp(-50));
        Assert.Equal(0, undervolt.Clamp(10));
        Assert.Equal(-15, undervolt.Clamp(-15));
    }

    [Fact]
    public void ACollapsedRangeIsNotUsable()
    {
        // What an unsupported control reports. The view hides it rather than drawing a
        // slider with nowhere to go.
        Assert.False(new ControlRange(0, 0).IsUsable);
        Assert.False(new ControlRange(40, 40).IsUsable);
        Assert.False(new ControlRange(50, 10).IsUsable);
        Assert.True(new ControlRange(0, 1).IsUsable);
    }

    [Fact]
    public void ClampIsIdempotent()
    {
        // Applying a profile re-clamps values that were already clamped. A second pass
        // must not move them again.
        var range = new ControlRange(15, 80);
        foreach (int value in new[] { -100, 0, 15, 47, 80, 200 })
        {
            int once = range.Clamp(value);
            Assert.Equal(once, range.Clamp(once));
        }
    }

    [Fact]
    public void RangesWithEqualBoundsCompareEqual()
    {
        // It is a record struct, and the view models rely on equality to decide whether
        // a detected range actually changed and the control needs rebuilding.
        Assert.Equal(new ControlRange(5, 50), new ControlRange(5, 50));
        Assert.NotEqual(new ControlRange(5, 50), new ControlRange(5, 51));
    }
}
