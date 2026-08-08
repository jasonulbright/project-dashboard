using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ProjectDashboard.Models;

namespace ProjectDashboard.Helpers;

/// <summary>
/// Renders a diff cell as runs, so the changed words inside a line can carry their own
/// background. <see cref="TextBlock.Inlines"/> is not a dependency property and cannot be
/// bound; this attached property is the binding, and it re-renders whenever a recycled
/// container is handed a different row.
/// </summary>
public static class InlineSegments
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IEnumerable<DiffSegment>), typeof(InlineSegments),
        new PropertyMetadata(null, OnRenderInputChanged));

    /// <summary>Background for the runs that differ from the paired line. Unset = no highlight.</summary>
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.RegisterAttached(
        "HighlightBrush", typeof(Brush), typeof(InlineSegments),
        new PropertyMetadata(null, OnRenderInputChanged));

    public static void SetSegments(DependencyObject element, IEnumerable<DiffSegment>? value) =>
        element.SetValue(SegmentsProperty, value);

    public static IEnumerable<DiffSegment>? GetSegments(DependencyObject element) =>
        (IEnumerable<DiffSegment>?)element.GetValue(SegmentsProperty);

    public static void SetHighlightBrush(DependencyObject element, Brush? value) =>
        element.SetValue(HighlightBrushProperty, value);

    public static Brush? GetHighlightBrush(DependencyObject element) =>
        (Brush?)element.GetValue(HighlightBrushProperty);

    // Both inputs land in either order — the brush is a literal in the template and the
    // segments arrive by binding — so each one re-renders rather than assuming the other.
    private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.TextBlock target) return;

        target.Inlines.Clear();
        if (GetSegments(target) is not { } segments) return;

        var highlight = GetHighlightBrush(target);
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.Changed && highlight is not null) run.Background = highlight;
            target.Inlines.Add(run);
        }
    }
}
