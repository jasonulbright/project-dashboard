using ProjectDashboard.Models;
using ProjectDashboard.Views.Windows;

namespace ProjectDashboard.Tests;

/// <summary>
/// The palette re-filters twice for one query: once on the keystroke, and again when
/// the repository fan-out returns, which can be seconds later. Resetting the selection
/// on that second pass moves the highlight while the user is arrowing through the list,
/// so Enter opens a row they never chose.
/// </summary>
public class PaletteSelectionTests
{
    [Fact]
    public void LateResults_KeepTheRowTheUserHighlighted()
    {
        var rows = Rows("alpha", "bravo", "charlie");
        var withFileHits = new List<PaletteItem>(rows) { Row("charlie/readme.md") };

        Assert.Same(rows[2], PaletteSelection.AfterRefilter(rows[2], withFileHits));
    }

    [Fact]
    public void ARowThatDidNotSurvive_FallsBackToTheTop()
    {
        var rows = Rows("alpha", "bravo");
        var gone = Row("charlie");

        Assert.Same(rows[0], PaletteSelection.AfterRefilter(gone, rows));
    }

    [Fact]
    public void AFreshFilter_SelectsTheTopRow()
    {
        var rows = Rows("alpha", "bravo");

        Assert.Same(rows[0], PaletteSelection.AfterRefilter(null, rows));
    }

    [Fact]
    public void NoMatches_SelectNothing()
        => Assert.Null(PaletteSelection.AfterRefilter(Row("alpha"), []));

    private static List<PaletteItem> Rows(params string[] titles) => [.. titles.Select(Row)];

    private static PaletteItem Row(string title) => new() { Title = title, SearchText = title };
}
