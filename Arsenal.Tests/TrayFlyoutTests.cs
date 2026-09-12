using System.Drawing;
using Arsenal.Application.Models;
using Xunit;

namespace Arsenal.Tests;

/// <summary>
/// Where the Quick Panel lands, for every edge Windows will dock the taskbar to.
/// </summary>
/// <remarks>
/// Checking this on the machine means restarting the shell four times, on a laptop
/// whose only screen keeps its taskbar on the bottom. The arithmetic does not need any
/// of that: it is a rectangle, a size and an edge, and getting it wrong puts the panel
/// at the opposite end of the screen from the icon the user pressed - which is the
/// failure this file exists to catch.
/// </remarks>
public class TrayFlyoutTests
{
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);

    /// <summary>A 1920x1080 screen with a 48px bar on the named edge.</summary>
    private static Rectangle WorkAreaWithBarOn(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Bottom => Rectangle.FromLTRB(0, 0, 1920, 1032),
        TaskbarEdge.Top => Rectangle.FromLTRB(0, 48, 1920, 1080),
        TaskbarEdge.Left => Rectangle.FromLTRB(48, 0, 1920, 1080),
        _ => Rectangle.FromLTRB(0, 0, 1872, 1080)
    };

    [Theory]
    [InlineData(TaskbarEdge.Bottom)]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void ReadsTheEdgeTheWorkAreaGaveUp(TaskbarEdge edge)
    {
        Assert.Equal(
            edge,
            TrayFlyout.EdgeFromReservedSpace(Monitor, WorkAreaWithBarOn(edge), fallback: TaskbarEdge.Top));
    }

    [Fact]
    public void FallsBackWhenNothingIsReserved()
    {
        // An auto-hidden taskbar gives the whole screen back, so there is nothing on any
        // side to read. The primary bar's edge is the answer, not bottom-by-default.
        Assert.Equal(
            TaskbarEdge.Left,
            TrayFlyout.EdgeFromReservedSpace(Monitor, Monitor, fallback: TaskbarEdge.Left));
    }

    [Fact]
    public void IgnoresASliverTooThinToBeABar()
    {
        var workArea = Rectangle.FromLTRB(0, 0, 1920, 1080 - (TrayFlyout.MinimumReservedPixels - 1));
        Assert.Equal(
            TaskbarEdge.Right,
            TrayFlyout.EdgeFromReservedSpace(Monitor, workArea, fallback: TaskbarEdge.Right));
    }

    [Fact]
    public void TheWidestBandWinsWhenTwoSidesAreReserved()
    {
        // A taskbar along the bottom with a narrower docked app bar down one side. The
        // panel belongs in the taskbar's corner, not the app bar's.
        var workArea = Rectangle.FromLTRB(30, 0, 1920, 1032);
        Assert.Equal(
            TaskbarEdge.Bottom,
            TrayFlyout.EdgeFromReservedSpace(Monitor, workArea, fallback: TaskbarEdge.Top));
    }

    [Fact]
    public void SurvivesADegenerateMonitor()
    {
        Assert.Equal(
            TaskbarEdge.Top,
            TrayFlyout.EdgeFromReservedSpace(Rectangle.Empty, Rectangle.Empty, fallback: TaskbarEdge.Top));
    }

    /// <summary>A secondary monitor sits at a non-zero origin; placement must follow it.</summary>
    private static readonly FlyoutBounds SecondScreen = new(-1920, 200, -320, 1100);

    private const double Gap = 11;
    private const double Width = 452;
    private const double Height = 700;
    private static readonly FlyoutGutter Gutter = new(10, 10, 10, 10);

    private static FlyoutPlacement PlaceOn(TaskbarEdge edge, FlyoutBounds workArea)
        => TrayFlyout.Place(edge, workArea, Width, Height, Gap, Gutter);

    [Fact]
    public void ABottomBarSeatsThePanelInTheBottomRightCorner()
    {
        var placement = PlaceOn(TaskbarEdge.Bottom, SecondScreen);

        Assert.Equal(SecondScreen.Right - Width - (Gap - Gutter.Right), placement.Left, 6);
        Assert.Equal(SecondScreen.Bottom - Height - (Gap - Gutter.Bottom), placement.Top, 6);
        Assert.False(placement.CardAtTop);
    }

    [Fact]
    public void ATopBarSeatsThePanelInTheTopRightCorner()
    {
        var placement = PlaceOn(TaskbarEdge.Top, SecondScreen);

        Assert.Equal(SecondScreen.Right - Width - (Gap - Gutter.Right), placement.Left, 6);
        Assert.Equal(SecondScreen.Top + (Gap - Gutter.Top), placement.Top, 6);

        // The window is taller than the card, so the card has to change which edge of it
        // it hangs from or the panel floats away from the bar it belongs to.
        Assert.True(placement.CardAtTop);
    }

    [Fact]
    public void ALeftBarSeatsThePanelInTheBottomLeftCorner()
    {
        var placement = PlaceOn(TaskbarEdge.Left, SecondScreen);

        Assert.Equal(SecondScreen.Left + (Gap - Gutter.Left), placement.Left, 6);
        Assert.Equal(SecondScreen.Bottom - Height - (Gap - Gutter.Bottom), placement.Top, 6);
        Assert.False(placement.CardAtTop);
    }

    [Fact]
    public void ARightBarKeepsTheBottomRightCorner()
    {
        var placement = PlaceOn(TaskbarEdge.Right, SecondScreen);

        Assert.Equal(SecondScreen.Right - Width - (Gap - Gutter.Right), placement.Left, 6);
        Assert.Equal(SecondScreen.Bottom - Height - (Gap - Gutter.Bottom), placement.Top, 6);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom, 0, 1)]
    [InlineData(TaskbarEdge.Top, 0, -1)]
    [InlineData(TaskbarEdge.Left, -1, 0)]
    [InlineData(TaskbarEdge.Right, 1, 0)]
    public void TheEntranceTravelsOutOfTheBar(TaskbarEdge edge, double enterX, double enterY)
    {
        var placement = PlaceOn(edge, SecondScreen);

        Assert.Equal(enterX, placement.EnterX);
        Assert.Equal(enterY, placement.EnterY);
    }

    [Fact]
    public void AGutterWiderThanTheGapDoesNotPushThePanelOffTheEdge()
    {
        // The shadow gutter already holds more room than the gap asks for, so no further
        // inset is applied - and in particular the window is not pulled outwards.
        var placement = TrayFlyout.Place(
            TaskbarEdge.Bottom, SecondScreen, Width, Height, gap: 4, gutter: Gutter);

        Assert.Equal(SecondScreen.Right - Width, placement.Left, 6);
        Assert.Equal(SecondScreen.Bottom - Height, placement.Top, 6);
    }

    [Fact]
    public void AWindowTallerThanTheWorkAreaKeepsItsOriginOnScreen()
    {
        var small = new FlyoutBounds(0, 0, 400, 500);
        var placement = TrayFlyout.Place(TaskbarEdge.Bottom, small, Width, Height, Gap, Gutter);

        Assert.Equal(small.Left, placement.Left, 6);
        Assert.Equal(small.Top, placement.Top, 6);
    }
}
