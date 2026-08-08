namespace ProjectDashboard.Models;

/// <summary>
/// One drawable row of the graph: a commit plus the lane state at both edges of its band.
///
/// <see cref="GraphCommit.OpenLanes"/> is POST-row state, so the lanes entering a row are the
/// previous row's — and for the first row of a page, <see cref="CommitGraphPage.IncomingLanes"/>.
/// Reading only the row's own lanes loses every edge that crosses the page's top, which no page
/// after the first could otherwise draw.
/// </summary>
public sealed class CommitGraphRow
{
    public CommitGraphRow(GraphCommit commit, IReadOnlyList<int> incomingLanes)
    {
        Commit = commit;
        IncomingLanes = incomingLanes;

        var outgoing = commit.OpenLanes;
        var lane = commit.Lane;
        PassThroughLanes = [.. incomingLanes.Where(l => l != lane && outgoing.Contains(l))];
        // A lane open above the row that is closed below it expected this commit's sha and
        // collapsed into its node: a child edge converging here, not a lane that vanished.
        MergingLanes = [.. incomingLanes.Where(l => l != lane && !outgoing.Contains(l))];
        // A lane open below the row that was not open above it is where an additional parent
        // of a merge starts.
        BranchingLanes = [.. outgoing.Where(l => l != lane && !incomingLanes.Contains(l))];
        HasEdgeAbove = incomingLanes.Contains(lane);
        HasEdgeBelow = outgoing.Contains(lane);
    }

    public GraphCommit Commit { get; }

    /// <summary>Columns open at the row's top edge.</summary>
    public IReadOnlyList<int> IncomingLanes { get; }

    /// <summary>Columns crossing the whole row without touching its commit.</summary>
    public IReadOnlyList<int> PassThroughLanes { get; }

    /// <summary>Columns entering from above and ending at this commit.</summary>
    public IReadOnlyList<int> MergingLanes { get; }

    /// <summary>Columns leaving this commit downwards for an additional parent.</summary>
    public IReadOnlyList<int> BranchingLanes { get; }

    /// <summary>A child of this commit sits above it in the same column.</summary>
    public bool HasEdgeAbove { get; }

    /// <summary>The first parent continues below in the same column.</summary>
    public bool HasEdgeBelow { get; }

    public int Lane => Commit.Lane;
    public string Sha => Commit.Sha;
    public string ShortSha => Commit.ShortSha;
    public string Author => Commit.Author;
    public DateTimeOffset Date => Commit.Date;
    public string Subject => Commit.Subject;
    public bool IsMerge => Commit.IsMerge;
    public bool IsRoot => Commit.IsRoot;
    public IReadOnlyList<GraphRef> Refs => Commit.Refs;

    /// <summary>The row's decorations as one label, or "" when it carries none.</summary>
    public string RefLabel => string.Join("  ", Refs.Select(r => r.Name));

    public bool HasRefs => Refs.Count > 0;

    /// <summary>Rows for one page, threading each row's outgoing lanes into the next row's incoming.</summary>
    public static List<CommitGraphRow> ForPage(CommitGraphPage page)
    {
        var rows = new List<CommitGraphRow>(page.Commits.Count);
        var incoming = page.IncomingLanes;
        foreach (var commit in page.Commits)
        {
            rows.Add(new CommitGraphRow(commit, incoming));
            incoming = commit.OpenLanes;
        }
        return rows;
    }
}
