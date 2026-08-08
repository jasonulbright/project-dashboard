namespace ProjectDashboard.Models;

public enum GraphRefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
    Head,
    Other
}

/// <summary>One decoration on a commit. Name is the short form: "main", "origin/main", "v1.2.0", "HEAD".</summary>
public sealed record GraphRef(GraphRefKind Kind, string Name);

/// <summary>One row of the commit graph: the commit, its parent edges, decorations, and column.</summary>
public sealed class GraphCommit
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";

    /// <summary>Parent shas in git's order; index 0 is the first parent.</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];

    public string Author { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string Subject { get; init; } = "";
    public IReadOnlyList<GraphRef> Refs { get; init; } = [];

    public bool IsMerge => Parents.Count > 1;
    public bool IsRoot => Parents.Count == 0;

    /// <summary>Column this commit is drawn in.</summary>
    public int Lane { get; internal set; }

    /// <summary>
    /// Columns still awaiting a commit immediately after this row, so a renderer knows
    /// which lanes pass straight through it.
    /// </summary>
    public IReadOnlyList<int> OpenLanes { get; internal set; } = [];
}

/// <summary>What slice of which ref set a graph request covers.</summary>
public sealed class CommitGraphRequest
{
    /// <summary>Commits returned when the caller states no preference.</summary>
    public const int DefaultTake = 200;

    /// <summary>Ceiling on a single request; a larger Take is clamped to it.</summary>
    public const int MaxTake = 2000;

    /// <summary>Explicit revision arguments; null means every local branch plus HEAD.</summary>
    public IReadOnlyList<string>? Refs { get; init; }

    /// <summary>Single-branch mode: history of one ref only. Takes precedence over <see cref="Refs"/>.</summary>
    public string? Branch { get; init; }

    public int Skip { get; init; }
    public int Take { get; init; } = DefaultTake;

    public int NormalizedSkip => Skip < 0 ? 0 : Skip;
    public int NormalizedTake => Take <= 0 ? DefaultTake : Math.Min(Take, MaxTake);
}

/// <summary>One page of the graph plus the width a renderer must reserve for it.</summary>
public sealed class CommitGraphPage
{
    public List<GraphCommit> Commits { get; init; } = [];

    /// <summary>True when at least one commit exists beyond this page.</summary>
    public bool HasMore { get; init; }

    public int Skip { get; init; }

    /// <summary>Columns in use across this page, counting lanes that only pass through.</summary>
    public int LaneCount { get; init; }
}
