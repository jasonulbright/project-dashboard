using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ProjectDashboard.Helpers;

namespace ProjectDashboard.Tests;

/// <summary>
/// The side-by-side row's layout. The two halves have to be equal and to land at the same place
/// on every row: a boundary that moves from row to row, or as the reader scrolls, reads as the
/// pane redrawing itself. A shared size scope cannot deliver that under recycling
/// virtualization, which hands one row's containers to another row.
/// </summary>
public class DiffRowPanelTests
{
    private const double Gutter = 42;

    [Fact]
    public void TheTwoTextCells_AreTheSameWidthWhicheverSideIsWider() =>
        RunSta(() =>
        {
            var (panel, cells) = Measured(oldWidth: 600, newWidth: 150);

            Assert.Equal(Slot(cells[DiffRowCell.OldText]).Width, Slot(cells[DiffRowCell.NewText]).Width, 3);
            Assert.Equal(600, Slot(cells[DiffRowCell.OldText]).Width, 3);
            Assert.Equal(Gutter, Slot(cells[DiffRowCell.OldText]).X, 3);
            Assert.Equal((2 * Gutter) + 600, Slot(cells[DiffRowCell.NewText]).X, 3);
            Assert.Equal(Gutter + 600, Slot(cells[DiffRowCell.NewNumber]).X, 3);
            Assert.Equal(0, Slot(cells[DiffRowCell.OldNumber]).X, 3);
            Assert.Equal((2 * Gutter) + 1200, panel.DesiredSize.Width, 3);
        });

    /// <summary>
    /// The row asks for twice the wider cell. A row that asked only for what its two lines
    /// occupy would be arranged at that width and split it in half, clipping the long side at
    /// half its length with nothing to scroll to.
    /// </summary>
    [Fact]
    public void ARowDemandsEnoughWidth_ForTheLongestLineOnEitherSide() =>
        RunSta(() =>
        {
            var (panel, cells) = Measured(oldWidth: 4000, newWidth: 10);

            Assert.True(panel.DesiredSize.Width >= (2 * Gutter) + (2 * 4000));
            Assert.Equal(4000, Slot(cells[DiffRowCell.OldText]).Width, 3);
        });

    /// <summary>
    /// Arranged wider than it asked for — the pane's own width, or the extent a longer row set —
    /// the row still splits the space evenly rather than leaving one side at its text width.
    /// </summary>
    [Fact]
    public void ArrangedWiderThanItAskedFor_TheRowStillSplitsEvenly() =>
        RunSta(() =>
        {
            var (_, cells) = Measured(oldWidth: 100, newWidth: 100, arrangeWidth: 1084);

            Assert.Equal(500, Slot(cells[DiffRowCell.OldText]).Width, 3);
            Assert.Equal(500, Slot(cells[DiffRowCell.NewText]).Width, 3);
            Assert.Equal(Gutter + 500, Slot(cells[DiffRowCell.NewNumber]).X, 3);
        });

    /// <summary>Every row of one pane splits at the same x, whatever each row holds.</summary>
    [Fact]
    public void EveryRowArrangedAtOneWidth_PutsTheBoundaryAtTheSamePlace() =>
        RunSta(() =>
        {
            var extent = Measured(oldWidth: 900, newWidth: 300).Panel.DesiredSize.Width;

            var wide = Measured(oldWidth: 900, newWidth: 300, arrangeWidth: extent).Cells;
            var narrow = Measured(oldWidth: 20, newWidth: 20, arrangeWidth: extent).Cells;

            Assert.Equal(Slot(wide[DiffRowCell.NewText]).X, Slot(narrow[DiffRowCell.NewText]).X, 3);
            Assert.Equal(Slot(wide[DiffRowCell.OldText]).Width, Slot(narrow[DiffRowCell.OldText]).Width, 3);
        });

    /// <summary>A header spans the row: neither gutter names a line on it.</summary>
    [Fact]
    public void ASpanningChild_TakesTheWholeRow() =>
        RunSta(() =>
        {
            var (_, cells) = Measured(oldWidth: 100, newWidth: 100, arrangeWidth: 800, spanWidth: 300);

            Assert.Equal(0, Slot(cells[DiffRowCell.Span]).X, 3);
            Assert.Equal(800, Slot(cells[DiffRowCell.Span]).Width, 3);
        });

    private static Rect Slot(FrameworkElement element) => LayoutInformation.GetLayoutSlot(element);

    /// <summary>
    /// A row measured and arranged like the pane arranges one: measured unbounded, because the
    /// list scrolls horizontally, then arranged at the width the pane hands every row.
    /// </summary>
    private static (DiffRowPanel Panel, Dictionary<DiffRowCell, FrameworkElement> Cells) Measured(
        double oldWidth, double newWidth, double? arrangeWidth = null, double spanWidth = 0)
    {
        var panel = new DiffRowPanel { GutterWidth = Gutter };
        var cells = new Dictionary<DiffRowCell, FrameworkElement>
        {
            [DiffRowCell.Span] = Cell(spanWidth),
            [DiffRowCell.OldNumber] = Cell(30),
            [DiffRowCell.OldText] = Cell(oldWidth),
            [DiffRowCell.NewNumber] = Cell(30),
            [DiffRowCell.NewText] = Cell(newWidth)
        };
        foreach (var (slot, cell) in cells)
        {
            DiffRowPanel.SetCell(cell, slot);
            panel.Children.Add(cell);
        }

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, arrangeWidth ?? panel.DesiredSize.Width, panel.DesiredSize.Height));
        return (panel, cells);
    }

    private static FrameworkElement Cell(double width) => new Border { Width = width, Height = 16 };

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
