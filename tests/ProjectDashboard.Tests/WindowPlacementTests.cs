using ProjectDashboard.Models;
using ProjectDashboard.Views.Windows;
using Wpf.Ui.Controls;

namespace ProjectDashboard.Tests;

/// <summary>
/// Precedence of the project status glyph. A repo can be dirty AND remoteless, and
/// ordering the two the wrong way hides the state the reader can act on.
/// </summary>
public class ProjectStatusGlyphTests
{
    [Fact]
    public void DirtyWithNoRemote_ShowsTheDirtyGlyph()
    {
        var glyph = MainWindow.StatusGlyph(new GitStatus { IsDirty = true, RemoteUrl = "" });

        Assert.Equal(SymbolRegular.Edit24, glyph);
    }

    [Fact]
    public void CleanWithNoRemote_StillReportsTheMissingRemote()
    {
        var glyph = MainWindow.StatusGlyph(new GitStatus { IsDirty = false, RemoteUrl = "" });

        Assert.Equal(SymbolRegular.CloudOff24, glyph);
    }

    [Fact]
    public void DirtyWithARemote_ShowsTheDirtyGlyph()
    {
        var glyph = MainWindow.StatusGlyph(new GitStatus { IsDirty = true, RemoteUrl = "https://example.test/r.git" });

        Assert.Equal(SymbolRegular.Edit24, glyph);
    }

    [Fact]
    public void CleanWithARemote_ShowsSynced()
    {
        var glyph = MainWindow.StatusGlyph(new GitStatus { RemoteUrl = "https://example.test/r.git" });

        Assert.Equal(SymbolRegular.CheckmarkCircle24, glyph);
    }
}

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

    // ── Real monitor rectangles, in device pixels ────────────────────────────
    //
    // A bounding box is not the desktop. Two arrangements break it: monitors at
    // different scale factors (the box, measured in system-DPI DIPs, is narrower
    // than the desktop's device-pixel extent) and monitors that are not aligned
    // (the box covers points no monitor does).

    // 3840x2160 primary at 200% beside a 1920x1080 secondary at 100%. In device
    // pixels the desktop runs to x=5760; SystemParameters.VirtualScreenWidth
    // reports it as 2880 DIPs.
    private static readonly MainWindow.ScreenRect[] MixedDpi =
    [
        new(0, 0, 3840, 2160),
        new(3840, 0, 1920, 1080),
    ];

    // Primary bottom-left, secondary above and to its right: the bounding box
    // covers the top-left and bottom-right corners that neither monitor does.
    private static readonly MainWindow.ScreenRect[] LShaped =
    [
        new(0, 0, 1920, 1080),
        new(1920, -1080, 1920, 1080),
    ];

    [Fact]
    public void PositionOnALowerDpiSecondary_SurvivesInDevicePixels()
    {
        var result = MainWindow.ClampToMonitors(3900, 100, 1200, 800, MixedDpi);

        Assert.Equal((3900.0, 100.0), result);
    }

    [Fact]
    public void SameMixedDpiPosition_IsDisplacedByTheVirtualScreenBoundingBox()
    {
        // The regression this replaces: the DIP bounding box is 2880 wide, so a
        // position valid on the secondary clamps to a monitor it is not on.
        var result = MainWindow.ClampToVirtualScreen(3900, 100, 1200, 800, 0, 0, 2880, 2160);

        Assert.Equal((2780.0, 100.0), result);
    }

    [Fact]
    public void PositionInAnLShapedDeadZone_MovesOntoTheNearerMonitor()
    {
        // Right of the primary and below the secondary: inside the bounding box,
        // on no monitor. The secondary is the cheaper move (750 px up vs 780 left).
        var result = MainWindow.ClampToMonitors(2600, 700, 1200, 800, LShaped);

        Assert.Equal((2600.0, -50.0), result);
    }

    [Fact]
    public void PositionSpanningAnLShapedGap_IsPreserved()
    {
        // Straddling the seam still shows 120x180 on the primary, so it is left alone.
        var result = MainWindow.ClampToMonitors(1800, 900, 1200, 800, LShaped);

        Assert.Equal((1800.0, 900.0), result);
    }

    [Fact]
    public void PositionOnAMonitorThatVanished_MovesOntoOneThatRemains()
    {
        // Saved on the secondary of the mixed-DPI pair, restored after unplugging it.
        var result = MainWindow.ClampToMonitors(4200, 300, 1200, 800, [MixedDpi[0]]);

        Assert.Equal((3740.0, 300.0), result);
    }

    [Fact]
    public void NegativeOriginMonitor_KeepsItsOwnPositions()
    {
        MainWindow.ScreenRect[] screens = [new(-1920, -200, 1920, 1080), new(0, 0, 1920, 1080)];

        var result = MainWindow.ClampToMonitors(-1800, -150, 1200, 800, screens);

        Assert.Equal((-1800.0, -150.0), result);
    }

    [Fact]
    public void NoMonitors_IsRejected()
    {
        // Enumeration produced nothing: leave the window where the OS put it.
        Assert.Null(MainWindow.ClampToMonitors(100, 100, 1200, 800, []));
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    public void NonFinitePositionAcrossMonitors_IsRejected(double left, double top)
    {
        Assert.Null(MainWindow.ClampToMonitors(left, top, 1200, 800, MixedDpi));
    }

    [Fact]
    public void GarbageSizeAcrossMonitors_StillLandsOnAMonitor()
    {
        // NaN height degrades to the minimum, so the whole window is forced in view.
        var result = MainWindow.ClampToMonitors(9000, 5000, 1200, double.NaN, MixedDpi);

        Assert.Equal((5660.0, 1030.0), result);
    }
}
