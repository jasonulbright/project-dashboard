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
/// Pure-geometry checks for the saved-position clamp. The screen rectangles are
/// passed explicitly, so no display or WPF machinery is involved.
/// </summary>
public class WindowPlacementTests
{
    // The single rectangle monitor enumeration falls back to: the virtual-screen
    // bounding box. Primary 1920x1080 with a second monitor to its LEFT, so the box
    // starts at a negative X and positions there are valid.
    private static readonly MainWindow.ScreenRect[] VirtualScreenDual = [new(-1920, 0, 3840, 1080)];

    // Single primary monitor, e.g. after undocking the second one.
    private static readonly MainWindow.ScreenRect[] SinglePrimary = [new(0, 0, 1920, 1080)];

    [Fact]
    public void PositionOnMonitorLeftOfPrimary_IsPreserved()
    {
        var result = MainWindow.ClampToMonitors(-1900, 10, 1200, 800, VirtualScreenDual);

        Assert.Equal((-1900, 10), result);
    }

    [Fact]
    public void PositionFromUndockedMonitor_IsClampedBackIntoView()
    {
        // Saved on the left monitor, restored after undocking it: the rect no
        // longer intersects the remaining screen, so the left edge clamps until
        // the minimum 100 px is visible again.
        var result = MainWindow.ClampToMonitors(-2400, 200, 1200, 800, SinglePrimary);

        Assert.Equal((-1100, 200), result);
    }

    [Fact]
    public void PositionFarBeyondBottomRight_IsClampedToVisibleMargin()
    {
        var result = MainWindow.ClampToMonitors(99999, 99999, 1200, 800, SinglePrimary);

        Assert.Equal((1820, 1030), result);
    }

    [Fact]
    public void PositionAboveScreen_ClampsTopUntilMinimumVisible()
    {
        // Only 10 px of the window's bottom shows: below the 50 px floor.
        var result = MainWindow.ClampToMonitors(100, -790, 1200, 800, SinglePrimary);

        Assert.Equal((100, -750), result);
    }

    [Fact]
    public void PositionExactlyAtMinimumVisibility_IsPreserved()
    {
        // Precisely 100 px of the window's right edge is on-screen.
        var result = MainWindow.ClampToMonitors(-1100, 200, 1200, 800, SinglePrimary);

        Assert.Equal((-1100, 200), result);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.NegativeInfinity)]
    public void NonFinitePosition_IsRejected(double left, double top)
    {
        var result = MainWindow.ClampToMonitors(left, top, 1200, 800, SinglePrimary);

        Assert.Null(result);
    }

    [Fact]
    public void GarbageSize_StillProducesInBoundsPosition()
    {
        // NaN width degrades to the minimum-visibility size, so the clamp keeps
        // the whole (assumed tiny) window inside the screen.
        var result = MainWindow.ClampToMonitors(5000, 100, double.NaN, 800, SinglePrimary);

        Assert.Equal((1820, 100), result);
    }

    [Fact]
    public void NegativeOnePosition_IsGeometricallyValid()
    {
        // The caller filters the -1/-1 never-saved sentinel; the geometry itself
        // treats a 1 px overhang as an ordinary near-corner position.
        var result = MainWindow.ClampToMonitors(-1, -1, 1200, 800, SinglePrimary);

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

    // ── Saved settings → startup placement ───────────────────────────────────

    /// <summary>Where the window already is when the saved placement is applied.</summary>
    private static readonly MainWindow.ScreenRect CurrentRect = new(1000, 200, 1600, 900);

    [Fact]
    public void DeviceRectSavedOnALowerDpiSecondary_RestoresWhereItWasSaved()
    {
        // Saved at device x=4000 on the 100% secondary, relaunched with the window
        // starting on the 200% primary. A rect in device pixels does not move with
        // the starting monitor's scale, so it lands back on the secondary.
        var settings = new AppSettings { WindowDeviceRect = new SavedWindowRect(4000, 100, 1200, 800) };

        var placement = MainWindow.SavedPlacement(settings, 2, CurrentRect, MixedDpi);

        Assert.Equal(new MainWindow.ScreenRect(4000, 100, 1200, 800), placement.Rect);
        Assert.False(placement.Maximized);
    }

    [Fact]
    public void MaximizedRestore_ClampsTheRectItWillRestoreTo()
    {
        // Saved maximized on the mixed-DPI secondary, relaunched after unplugging it.
        // The saved rect is the restore bounds the maximized window carries, so the
        // clamp has to reach it before the window maximizes.
        var settings = new AppSettings
        {
            WindowDeviceRect = new SavedWindowRect(4200, 300, 1200, 800),
            WindowMaximized = true,
        };

        var placement = MainWindow.SavedPlacement(settings, 2, CurrentRect, [MixedDpi[0]]);

        Assert.Equal(new MainWindow.ScreenRect(3740, 300, 1200, 800), placement.Rect);
        Assert.True(placement.Maximized);
    }

    [Fact]
    public void LegacyDipRect_IsScaledByTheStartingMonitorAndClamped()
    {
        // A settings file predating the device-pixel rect: 4000 was DIPs on the 100%
        // secondary, and the scale it was written in is not recorded. Read against a
        // 200% start it doubles to 8000 and the clamp is what keeps it reachable.
        var settings = new AppSettings
        {
            WindowLeft = 4000, WindowTop = 200, WindowWidth = 1200, WindowHeight = 800,
        };

        var placement = MainWindow.SavedPlacement(settings, 2, CurrentRect, MixedDpi);

        Assert.Equal(new MainWindow.ScreenRect(5660, 400, 2400, 1600), placement.Rect);
    }

    [Fact]
    public void LegacyDipRect_AtTheStartingMonitorsOwnScale_IsExact()
    {
        var settings = new AppSettings
        {
            WindowLeft = 300, WindowTop = 150, WindowWidth = 1200, WindowHeight = 800,
        };

        var placement = MainWindow.SavedPlacement(settings, 1, CurrentRect, MixedDpi);

        Assert.Equal(new MainWindow.ScreenRect(300, 150, 1200, 800), placement.Rect);
    }

    [Fact]
    public void NeverSavedPosition_LeavesTheWindowWhereItIs()
    {
        var placement = MainWindow.SavedPlacement(new AppSettings(), 1, CurrentRect, MixedDpi);

        Assert.Equal(CurrentRect, placement.Rect);
    }

    [Fact]
    public void NeverSavedPositionWhileMaximized_StillMaximizes()
    {
        var placement = MainWindow.SavedPlacement(
            new AppSettings { WindowMaximized = true }, 1, CurrentRect, MixedDpi);

        Assert.Equal(CurrentRect, placement.Rect);
        Assert.True(placement.Maximized);
    }

    [Theory]
    [InlineData(double.NaN, 800)]
    [InlineData(1200, 0)]
    [InlineData(-1200, 800)]
    public void UnusableSavedSize_KeepsTheWindowsOwnSize(double width, double height)
    {
        var settings = new AppSettings
        {
            WindowLeft = 300, WindowTop = 150, WindowWidth = width, WindowHeight = height,
        };

        var placement = MainWindow.SavedPlacement(settings, 1, CurrentRect, MixedDpi);

        Assert.Equal(300, placement.Rect.Left);
        Assert.Equal(150, placement.Rect.Top);
        Assert.True(placement.Rect.Width > 0 && placement.Rect.Height > 0);
    }

    [Fact]
    public void NonFiniteSavedPosition_LeavesTheWindowWhereItIs()
    {
        var settings = new AppSettings { WindowLeft = double.NaN, WindowTop = 150 };

        var placement = MainWindow.SavedPlacement(settings, 1, CurrentRect, MixedDpi);

        Assert.Equal(CurrentRect, placement.Rect);
    }
}

/// <summary>
/// Whether a placement call took. A move onto a monitor of a different scale factor
/// is answered with a DPI-scaled suggested rect that overwrites the requested one, so
/// the restore is only settled once the window reads back what was asked for.
/// </summary>
public class WindowRectApplicationTests
{
    private static readonly MainWindow.ScreenRect Requested = new(4000, 100, 1200, 800);

    [Fact]
    public void RectReadBackVerbatim_ReadsAsApplied()
    {
        Assert.True(MainWindow.RectApplied(Requested, new MainWindow.ScreenRect(4000, 100, 1200, 800)));
    }

    [Fact]
    public void RectHalvedByADpiChange_ReadsAsNotApplied()
    {
        // Requested on a 100% monitor from a 200% one: the suggested rect that lands
        // over it keeps the position and halves the size.
        Assert.False(MainWindow.RectApplied(Requested, new MainWindow.ScreenRect(4000, 100, 600, 400)));
    }

    [Fact]
    public void RectDoubledByADpiChange_ReadsAsNotApplied()
    {
        Assert.False(MainWindow.RectApplied(Requested, new MainWindow.ScreenRect(4000, 100, 2400, 1600)));
    }

    [Fact]
    public void SameSizeAtADifferentPosition_ReadsAsNotApplied()
    {
        Assert.False(MainWindow.RectApplied(Requested, new MainWindow.ScreenRect(3990, 100, 1200, 800)));
    }

    [Fact]
    public void FractionalRequest_MatchesTheWholePixelsItIsAppliedAs()
    {
        // A rect scaled out of the legacy DIP fields is fractional; the window can
        // only ever report the truncated one, and that is not a failed application.
        var fractional = new MainWindow.ScreenRect(300.75, 150.5, 2026.25, 1028.75);

        Assert.True(MainWindow.RectApplied(fractional, new MainWindow.ScreenRect(300, 150, 2026, 1028)));
    }
}
