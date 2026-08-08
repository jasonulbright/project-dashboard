using System.Windows;
using System.Windows.Media;
using ProjectDashboard.Models;

namespace ProjectDashboard.Views.Controls;

/// <summary>
/// The lane glyphs of one commit-graph row, drawn directly rather than composed from shapes:
/// a row carries up to one edge per open lane, and a Path per edge would put thousands of
/// framework elements behind a scrolling list.
///
/// Everything drawn comes from the row's two lane sets, so a row is drawable on its own and the
/// pane never has to look at its neighbours: the top half belongs to the lanes open above the
/// row, the bottom half to the lanes open below it.
/// </summary>
public sealed class CommitGraphLanes : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(CommitGraphRow), typeof(CommitGraphLanes),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LaneWidthProperty = DependencyProperty.Register(
        nameof(LaneWidth), typeof(double), typeof(CommitGraphLanes),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public CommitGraphRow? Row
    {
        get => (CommitGraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public double LaneWidth
    {
        get => (double)GetValue(LaneWidthProperty);
        set => SetValue(LaneWidthProperty, value);
    }

    /// <summary>
    /// Lane colours, taken by <c>lane % length</c>. Chosen to stay legible on both the light and
    /// the dark application background, which a theme brush cannot do for eight distinct hues.
    /// </summary>
    private static readonly Color[] LaneColors =
    [
        Color.FromRgb(0x4C, 0x8D, 0xD9),
        Color.FromRgb(0x3F, 0xA4, 0x5B),
        Color.FromRgb(0xC9, 0x75, 0x2B),
        Color.FromRgb(0xA2, 0x64, 0xC4),
        Color.FromRgb(0xC7, 0x5C, 0x67),
        Color.FromRgb(0x2F, 0xA3, 0xA0),
        Color.FromRgb(0xB0, 0x8A, 0x2E),
        Color.FromRgb(0x7A, 0x86, 0xC2),
    ];

    private static readonly Pen[] LanePens = BuildPens();
    private static readonly Brush[] LaneBrushes = BuildBrushes();

    private static Pen[] BuildPens()
    {
        var pens = new Pen[LaneColors.Length];
        for (var i = 0; i < pens.Length; i++)
        {
            pens[i] = new Pen(new SolidColorBrush(LaneColors[i]), 1.6);
            pens[i].Freeze();
        }
        return pens;
    }

    private static Brush[] BuildBrushes()
    {
        var brushes = new Brush[LaneColors.Length];
        for (var i = 0; i < brushes.Length; i++)
        {
            brushes[i] = new SolidColorBrush(LaneColors[i]);
            brushes[i].Freeze();
        }
        return brushes;
    }

    private static Pen PenFor(int lane) => LanePens[((lane % LanePens.Length) + LanePens.Length) % LanePens.Length];
    private static Brush BrushFor(int lane) => LaneBrushes[((lane % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length];

    private double CenterOf(int lane) => (lane + 0.5) * LaneWidth;

    protected override void OnRender(DrawingContext dc)
    {
        if (Row is not { } row) return;
        var height = ActualHeight;
        if (height <= 0) return;
        var mid = height / 2;
        var x = CenterOf(row.Lane);

        foreach (var lane in row.PassThroughLanes)
        {
            var lx = CenterOf(lane);
            dc.DrawLine(PenFor(lane), new Point(lx, 0), new Point(lx, height));
        }

        if (row.HasEdgeAbove) dc.DrawLine(PenFor(row.Lane), new Point(x, 0), new Point(x, mid));
        if (row.HasEdgeBelow) dc.DrawLine(PenFor(row.Lane), new Point(x, mid), new Point(x, height));

        // A converging edge is coloured by the lane it comes FROM, so the line a reader has been
        // following down the page keeps its colour into the node it ends at.
        foreach (var lane in row.MergingLanes)
            dc.DrawGeometry(null, PenFor(lane), Elbow(CenterOf(lane), 0, x, mid));

        // A diverging edge is coloured by the lane it opens, which is the colour that lane keeps
        // for every row below.
        foreach (var lane in row.BranchingLanes)
            dc.DrawGeometry(null, PenFor(lane), Elbow(CenterOf(lane), height, x, mid));

        DrawNode(dc, row, x, mid);
    }

    /// <summary>
    /// One edge between a lane's column at <paramref name="fromY"/> and the node at
    /// (<paramref name="toX"/>, <paramref name="toY"/>), bending at the node's row so the
    /// vertical run stays aligned with the lane's other rows.
    /// </summary>
    private static Geometry Elbow(double fromX, double fromY, double toX, double toY)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(fromX, fromY), isFilled: false, isClosed: false);
            ctx.QuadraticBezierTo(new Point(fromX, toY), new Point(toX, toY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawNode(DrawingContext dc, CommitGraphRow row, double x, double y)
    {
        var brush = BrushFor(row.Lane);
        if (row.IsRoot)
        {
            dc.DrawRectangle(brush, null, new Rect(x - 3.5, y - 3.5, 7, 7));
            return;
        }
        dc.DrawEllipse(brush, null, new Point(x, y), 3.5, 3.5);
        // A merge takes a second ring so the row it sits on is identifiable without following
        // its edges back.
        if (row.IsMerge) dc.DrawEllipse(null, PenFor(row.Lane), new Point(x, y), 5.5, 5.5);
    }
}
