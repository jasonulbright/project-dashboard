namespace ProjectDashboard.Models;

/// <summary>Where a submodule's git directory lives, which decides whether it is initialized at all.</summary>
public enum SubmoduleGitDir
{
    /// <summary>No .git entry in the working tree: the submodule is not initialized (or its tree is gone).</summary>
    None,
    /// <summary>.git is a real directory inside the submodule — a non-absorbed gitdir.</summary>
    Embedded,
    /// <summary>.git is a file pointing at the superproject's .git/modules/&lt;name&gt; — an absorbed gitdir.</summary>
    Linked
}

/// <summary>
/// One submodule of a superproject. An entry exists for anything the superproject knows
/// about: a .gitmodules declaration, a gitlink recorded in the index, or both — the two
/// sets differ after a half-finished add or removal, so neither alone is the listing.
/// </summary>
public sealed class SubmoduleEntry
{
    /// <summary>.gitmodules section name; falls back to the path for a gitlink with no declaration.</summary>
    public string Name { get; init; } = "";

    /// <summary>Superproject-relative path with forward slashes, as git records it.</summary>
    public string Path { get; init; } = "";

    /// <summary>URL from .gitmodules; empty for an index-only gitlink.</summary>
    public string Url { get; init; } = "";

    /// <summary>submodule.&lt;name&gt;.branch — the branch the submodule tracks; null when unset.</summary>
    public string? TrackedBranch { get; init; }

    public bool DeclaredInGitmodules { get; init; }
    public bool RecordedInIndex { get; init; }

    /// <summary>Gitlink sha recorded in the superproject index; empty when only declared.</summary>
    public string RecordedSha { get; init; } = "";

    /// <summary>
    /// The superproject index holds unmerged stages for this gitlink. <see cref="RecordedSha"/>
    /// is then the superproject's own side (stage 2), not the incoming one, so a checkout
    /// left on our commit does not read as diverged.
    /// </summary>
    public bool IsConflicted { get; init; }

    /// <summary>The submodule's own HEAD sha; empty when uninitialized.</summary>
    public string CurrentSha { get; init; } = "";

    /// <summary>Branch checked out inside the submodule; null when detached or uninitialized.</summary>
    public string? CheckedOutBranch { get; init; }

    public bool IsDetached { get; init; }
    public bool WorkingTreeExists { get; init; }
    public bool IsInitialized { get; init; }
    public SubmoduleGitDir GitDir { get; init; }

    /// <summary>Tracked files modified inside the submodule.</summary>
    public bool HasModifiedContent { get; init; }

    /// <summary>Untracked files present inside the submodule.</summary>
    public bool HasUntrackedContent { get; init; }

    /// <summary>
    /// The submodule declares submodules of its own. Reported, never followed: listing
    /// covers one level, so a submodule cycle cannot drive an unbounded walk.
    /// </summary>
    public bool HasNestedSubmodules { get; init; }

    public bool IsDirty => HasModifiedContent || HasUntrackedContent;

    /// <summary>The checkout sits on a different commit than the superproject records.</summary>
    public bool CommitDiffersFromRecorded =>
        IsInitialized && CurrentSha.Length > 0 && RecordedSha.Length > 0 && CurrentSha != RecordedSha;
}

/// <summary>Commit counts between a submodule's checkout and the sha its superproject records.</summary>
public sealed record SubmoduleDivergence(int Ahead, int Behind);

/// <summary>Arguments for `git submodule update`.</summary>
public sealed class SubmoduleUpdateRequest
{
    /// <summary>One submodule path; null updates every submodule.</summary>
    public string? Path { get; init; }

    /// <summary>Adds --init, registering submodules that were never initialized.</summary>
    public bool Init { get; init; }

    public bool Recursive { get; init; }

    /// <summary>
    /// Adds --depth. It shapes the clone git performs for a submodule with no git
    /// directory yet. A submodule that has one — including a deinitialized submodule,
    /// whose .git/modules entry survives deinit — is re-checked-out from the objects
    /// already there and stays at full depth.
    /// </summary>
    public int? Depth { get; init; }

    /// <summary>
    /// Adds --force, which resets the submodule checkout and discards local commits and
    /// modifications there. Refused unless <see cref="ConfirmDiscard"/> is also set.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>Acknowledges that <see cref="Force"/> destroys work in the submodule checkout.</summary>
    public bool ConfirmDiscard { get; init; }
}

/// <summary>Arguments for `git submodule deinit`.</summary>
public sealed class SubmoduleDeinitRequest
{
    /// <summary>One submodule path; null deinitializes every submodule (--all).</summary>
    public string? Path { get; init; }

    /// <summary>Adds --force, which git requires when the submodule checkout has local modifications.</summary>
    public bool Force { get; init; }

    /// <summary>
    /// Acknowledges that deinit empties the submodule working tree. Deinit is refused
    /// without it — no argument combination reaches git by default.
    /// </summary>
    public bool ConfirmDiscard { get; init; }
}
