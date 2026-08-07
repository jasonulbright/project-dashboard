using ProjectDashboard.Views.Windows;

namespace ProjectDashboard.Tests;

/// <summary>
/// Pure-geometry checks for the saved-position clamp. The virtual screen is
/// passed explicitly, so no display or WPF machinery is involved.
/// </summary>
public class WindowPlacementTests
{
    // Primary 1920x1080 with a second monitor to its LEFT: virtual screen
    // starts at a negative X. Positions there are valid and must survive.
    private const double DualLeft = -1920, DualTop = 0, DualWidth = 3840, DualHeight = 1080;

    // Single primary monitor, e.g. after undocking the second one.
    private const double SingleLeft = 0, SingleTop = 0, SingleWidth = 1920, SingleHeight = 1080;

    [Fact]
    public void PositionOnMonitorLeftOfPrimary_IsPreserved()
    {
        var result = MainWindow.ClampToVirtualScreen(
            -1900, 10, 1200, 800, DualLeft, DualTop, DualWidth, DualHeight);

        Assert.Equal((-1900, 10), result);
    }

    [Fact]
    public void PositionFromUndockedMonitor_IsClampedBackIntoView()
    {
        // Saved on the left monitor, restored after undocking it: the rect no
        // longer intersects the remaining screen, so the left edge clamps until
        // the minimum 100 px is visible again.
        var result = MainWindow.ClampToVirtualScreen(
            -2400, 200, 1200, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Equal((-1100, 200), result);
    }

    [Fact]
    public void PositionFarBeyondBottomRight_IsClampedToVisibleMargin()
    {
        var result = MainWindow.ClampToVirtualScreen(
            99999, 99999, 1200, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Equal((1820, 1030), result);
    }

    [Fact]
    public void PositionAboveScreen_ClampsTopUntilMinimumVisible()
    {
        // Only 10 px of the window's bottom shows: below the 50 px floor.
        var result = MainWindow.ClampToVirtualScreen(
            100, -790, 1200, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Equal((100, -750), result);
    }

    [Fact]
    public void PositionExactlyAtMinimumVisibility_IsPreserved()
    {
        // Precisely 100 px of the window's right edge is on-screen.
        var result = MainWindow.ClampToVirtualScreen(
            -1100, 200, 1200, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Equal((-1100, 200), result);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.NegativeInfinity)]
    public void NonFinitePosition_IsRejected(double left, double top)
    {
        var result = MainWindow.ClampToVirtualScreen(
            left, top, 1200, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Null(result);
    }

    [Fact]
    public void GarbageSize_StillProducesInBoundsPosition()
    {
        // NaN width degrades to the minimum-visibility size, so the clamp keeps
        // the whole (assumed tiny) window inside the screen.
        var result = MainWindow.ClampToVirtualScreen(
            5000, 100, double.NaN, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Equal((1820, 100), result);
    }

    [Fact]
    public void NegativeOnePosition_IsGeometricallyValid()
    {
        // The caller filters the -1/-1 never-saved sentinel; the geometry itself
        // treats a 1 px overhang as an ordinary near-corner position.
        var result = MainWindow.ClampToVirtualScreen(
            -1, -1, 1200, 800, SingleLeft, SingleTop, SingleWidth, SingleHeight);

        Assert.Equal((-1, -1), result);
    }
}
