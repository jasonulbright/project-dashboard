using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectDashboard.Models;
using ProjectDashboard.Views.Controls;

namespace ProjectDashboard.Tests;

/// <summary>
/// The graph's lane glyphs, rendered to pixels. A view-model test can prove which lanes a row
/// declares; only a render proves the element turns them into marks in the right columns — and a
/// silently blank element looks exactly like a repository with a simple history.
/// WPF drawing requires an STA thread; no Application is needed.
/// </summary>
public class CommitGraphLanesRenderTests
{
    private const double LaneWidth = 16;
    private const int Width = 48;
    private const int Height = 20;

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static byte[] RenderRow(CommitGraphRow row)
    {
        var element = new CommitGraphLanes
        {
            Row = row,
            LaneWidth = LaneWidth,
            Width = Width,
            Height = Height
        };
        element.Measure(new Size(Width, Height));
        element.Arrange(new Rect(0, 0, Width, Height));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    /// <summary>
    /// Whether anything was drawn within one pixel of a lane's centre at that height. The stroke
    /// straddles the centre, so the exact column it lands in depends on rounding.
    /// </summary>
    private static bool InkAtLane(byte[] pixels, int lane, int y)
    {
        var centre = (int)((lane + 0.5) * LaneWidth);
        for (var x = centre - 1; x <= centre + 1; x++)
        {
            if (x < 0 || x >= Width) continue;
            if (pixels[((y * Width) + x) * 4 + 3] > 0) return true;
        }
        return false;
    }

    private static GraphCommit Commit(string sha, int lane, int[] openLanes, params string[] parents) =>
        new()
        {
            Sha = sha,
            ShortSha = sha,
            Parents = parents,
            Lane = lane,
            OpenLanes = openLanes
        };

    [Fact]
    public void ARowDrawsItsNodeAndThePassThroughLaneBesideIt()
    {
        RunSta(() =>
        {
            // Commit on lane 1, with lane 0 open above and below it and untouched by it.
            var row = new CommitGraphRow(Commit("c", lane: 1, openLanes: [0, 1], "p"), incomingLanes: [0, 1]);
            Assert.Equal([0], row.PassThroughLanes);

            var pixels = RenderRow(row);
            Assert.True(InkAtLane(pixels, 0, 2), "lane 0 must cross the top of the row");
            Assert.True(InkAtLane(pixels, 0, Height - 3), "lane 0 must cross the bottom of the row");
            Assert.True(InkAtLane(pixels, 1, 2), "the commit's own lane carries its child's edge");
            Assert.True(InkAtLane(pixels, 1, Height / 2), "the commit's node must be drawn");
            Assert.False(InkAtLane(pixels, 2, Height / 2), "no lane is open past the second column");
        });
    }

    /// <summary>
    /// A root has no first parent, so nothing continues below it; a tip has no child, so nothing
    /// arrives above it. Drawing either edge anyway invents a line to a commit that is not there.
    /// </summary>
    [Fact]
    public void ARootTipDrawsANodeAndNoEdgeAtAll()
    {
        RunSta(() =>
        {
            var row = new CommitGraphRow(Commit("r", lane: 0, openLanes: []), incomingLanes: []);
            Assert.True(row.IsRoot);
            Assert.False(row.HasEdgeAbove);
            Assert.False(row.HasEdgeBelow);

            var pixels = RenderRow(row);
            Assert.True(InkAtLane(pixels, 0, Height / 2), "the root's node must be drawn");
            Assert.False(InkAtLane(pixels, 0, 1), "nothing arrives from above a tip");
            Assert.False(InkAtLane(pixels, 0, Height - 2), "nothing continues below a root");
        });
    }

    /// <summary>
    /// A merge's extra parent opens a column that exists only below the row, so its edge must
    /// reach the bottom edge in that column — the row beneath it draws the rest of the line.
    /// </summary>
    [Fact]
    public void AMergeDrawsAnEdgeIntoTheColumnItsSecondParentOpens()
    {
        RunSta(() =>
        {
            var row = new CommitGraphRow(
                Commit("m", lane: 0, openLanes: [0, 1], "p0", "p1"), incomingLanes: [0]);
            Assert.Equal([1], row.BranchingLanes);

            var pixels = RenderRow(row);
            Assert.True(InkAtLane(pixels, 1, Height - 2), "the opened lane must reach the row's bottom edge");
            Assert.False(InkAtLane(pixels, 1, 1), "that lane does not exist above the merge");
        });
    }

    /// <summary>
    /// A lane that closes at a commit converged into it: its edge must arrive from the top of
    /// that column, or the line a reader was following down the page simply stops.
    /// </summary>
    [Fact]
    public void AClosingLaneDrawsAnEdgeArrivingFromAbove()
    {
        RunSta(() =>
        {
            var row = new CommitGraphRow(Commit("a", lane: 0, openLanes: [0], "p"), incomingLanes: [0, 1]);
            Assert.Equal([1], row.MergingLanes);

            var pixels = RenderRow(row);
            Assert.True(InkAtLane(pixels, 1, 1), "the closing lane must arrive from the row's top edge");
            Assert.False(InkAtLane(pixels, 1, Height - 2), "that lane does not exist below this row");
        });
    }
}
