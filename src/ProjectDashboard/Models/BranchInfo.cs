namespace ProjectDashboard.Models;

/// <summary>One local branch from for-each-ref.</summary>
public sealed class BranchInfo
{
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }
    public string Upstream { get; init; } = "";
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public DateTimeOffset? LastCommit { get; init; }

    public string TrackLabel =>
        UpstreamGone ? "upstream gone"
        : Upstream.Length == 0 ? "no upstream"
        : (Ahead, Behind) switch
        {
            (0, 0) => "up to date",
            (var a, 0) => $"{a} ahead",
            (0, var b) => $"{b} behind",
            var (a, b) => $"{a} ahead, {b} behind"
        };
}

/// <summary>One stash entry.</summary>
public sealed class StashEntry
{
    public string Ref { get; init; } = "";      // stash@{0}
    public string Subject { get; init; } = "";
    public DateTimeOffset? Date { get; init; }
}

/// <summary>One changed file within a commit.</summary>
public sealed class CommitFile
{
    public string Status { get; init; } = "";   // A/M/D/Rnnn...
    public string Path { get; init; } = "";
    public string StatusLabel => Status.Length == 0 ? "" : Status[0] switch
    {
        'A' => "added",
        'M' => "modified",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        _ => Status
    };
}
