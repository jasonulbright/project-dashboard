namespace ProjectDashboard.Models;

public sealed class GitStatus
{
    public string Branch { get; set; } = "";
    public bool IsDirty { get; set; }
    public int ModifiedCount { get; set; }
    public int UntrackedCount { get; set; }
    public int TotalChanges => ModifiedCount + UntrackedCount;
    public int AheadBy { get; set; }
    public int BehindBy { get; set; }
    public bool IsDetached { get; set; }
    public bool HasConflicts { get; set; }
    /// <summary>"" | "merge" | "rebase" | "cherry-pick" | "revert" | "bisect" — in-progress operation.</summary>
    public string ActivityLabel { get; set; } = "";
    /// <summary>True when the repo needs terminal attention (conflicts or an in-progress operation).</summary>
    public bool NeedsAttention => HasConflicts || ActivityLabel.Length > 0;

    public string AttentionLabel =>
        HasConflicts && ActivityLabel.Length > 0 ? $"{ActivityLabel} — conflicts"
        : HasConflicts ? "conflicts"
        : ActivityLabel.Length > 0 ? $"{ActivityLabel} in progress"
        : "";
    public string AheadBehindLabel => (AheadBy, BehindBy) switch
    {
        (0, 0) => "",
        (var a, 0) => $"↑{a}",
        (0, var b) => $"↓{b}",
        var (a, b) => $"↑{a} ↓{b}"
    };
    public string LatestTag { get; set; } = "";
    public DateTimeOffset? LastCommitDate { get; set; }
    public string LastCommitMessage { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string Visibility { get; set; } = "local"; // "public", "private", "local", "unknown"

    /// <summary>True when git status couldn't be read (e.g. git missing) — distinguishes "unknown" from "clean".</summary>
    public bool HasError { get; set; }
}
