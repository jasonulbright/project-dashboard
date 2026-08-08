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

    /// <summary>
    /// The primary checkout, which git always lists first and which `git worktree remove`
    /// refuses to take.
    /// </summary>
    public bool IsMain { get; init; }

    /// <summary>The administrative entry survives its working tree; `git worktree prune` is what clears it.</summary>
    public bool IsPrunable { get; init; }

    /// <summary>Git's own reason for calling the entry prunable; empty when it does not.</summary>
    public string PrunableReason { get; init; } = "";
}
