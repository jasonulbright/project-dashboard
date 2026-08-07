namespace ProjectDashboard.Models;

/// <summary>One linked or primary worktree from `git worktree list --porcelain`.</summary>
public sealed class WorktreeEntry
{
    public string Path { get; init; } = "";
    public string HeadSha { get; init; } = "";
    /// <summary>Short branch name, null when bare or detached.</summary>
    public string? Branch { get; init; }
    public bool IsBare { get; init; }
    public bool IsDetached { get; init; }
    public bool IsLocked { get; init; }
}
