using System.Windows;
using System.Windows.Controls;

namespace ProjectDashboard.Helpers;

/// <summary>Which part of a two-column diff row a child fills.</summary>
public enum DiffRowCell
{
    /// <summary>The whole row: a hunk header, a mode line, or a note about the file.</summary>
    Span,
    OldNumber,
    OldText,
    NewNumber,
    NewText
}

/// <summary>
/// Row layout for the side-by-side diff: a fixed line-number gutter and a text cell per side,
/// the two cells always the same width. Equal halves are computed per row from one arranged
/// width, not from a <see cref="Grid"/> shared size scope — recycling virtualization hands a
/// row's containers to different rows as the reader scrolls, and a scope measured from whatever
/// is realized moves the boundary under them.
///
/// A row's width demand is twice the wider of its two cells, so the list's horizontal extent
/// reaches the longest line on either side; a long line scrolls into view instead of being
/// clipped at half the extent.
/// </summary>
public sealed class DiffRowPanel : Panel
{
    public static readonly DependencyProperty CellProperty = DependencyProperty.RegisterAttached(
        "Cell", typeof(DiffRowCell), typeof(DiffRowPanel),
        new FrameworkPropertyMetadata(DiffRowCell.Span, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static void SetCell(UIElement element, DiffRowCell value) => element.SetValue(CellProperty, value);

    public static DiffRowCell GetCell(UIElement element) => (DiffRowCell)element.GetValue(CellProperty);

    /// <summary>Width of one line-number gutter. Both sides get the same.</summary>
    public double GutterWidth
    {
        get => (double)GetValue(GutterWidthProperty);
        set => SetValue(GutterWidthProperty, value);
    }

    public static readonly DependencyProperty GutterWidthProperty = DependencyProperty.Register(
        nameof(GutterWidth), typeof(double), typeof(DiffRowPanel),
        new FrameworkPropertyMetadata(42d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    protected override Size MeasureOverride(Size availableSize)
    {
        var cell = 0d;
        var span = 0d;
        var height = 0d;

        foreach (UIElement child in InternalChildren)
        {
            var slot = GetCell(child);
            child.Measure(new Size(
                slot is DiffRowCell.OldNumber or DiffRowCell.NewNumber ? GutterWidth : double.PositiveInfinity,
                double.PositiveInfinity));

            height = Math.Max(height, child.DesiredSize.Height);
            if (slot is DiffRowCell.OldText or DiffRowCell.NewText)
                cell = Math.Max(cell, child.DesiredSize.Width);
            else if (slot == DiffRowCell.Span)
                span = Math.Max(span, child.DesiredSize.Width);
        }

        return new Size(Math.Max(span, (2 * GutterWidth) + (2 * cell)), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Widened to the row's share of the list's extent, so every row splits the same width
        // at the same place; never narrower than the text either side measured at.
        var cell = CellWidth();
        cell = Math.Max(cell, (finalSize.Width - (2 * GutterWidth)) / 2);

        foreach (UIElement child in InternalChildren)
            child.Arrange(GetCell(child) switch
            {
                DiffRowCell.OldNumber => new Rect(0, 0, GutterWidth, finalSize.Height),
                DiffRowCell.OldText => new Rect(GutterWidth, 0, cell, finalSize.Height),
                DiffRowCell.NewNumber => new Rect(GutterWidth + cell, 0, GutterWidth, finalSize.Height),
                DiffRowCell.NewText => new Rect((2 * GutterWidth) + cell, 0, cell, finalSize.Height),
                _ => new Rect(0, 0, finalSize.Width, finalSize.Height)
            });

        return finalSize;
    }

    private double CellWidth()
    {
        var cell = 0d;
        foreach (UIElement child in InternalChildren)
            if (GetCell(child) is DiffRowCell.OldText or DiffRowCell.NewText)
                cell = Math.Max(cell, child.DesiredSize.Width);
        return cell;
    }
}
