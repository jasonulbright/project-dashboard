namespace ProjectDashboard.Models;

/// <summary>One downloadable file attached to a release.</summary>
public sealed class ReleaseAsset
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
}

/// <summary>One GitHub release, drafts included.</summary>
public sealed class Release
{
    public string TagName { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
    /// <summary>Null for drafts — a draft has no publish moment.</summary>
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
    public string Url { get; init; } = "";
}

/// <summary>One workflow run from the Actions list.</summary>
public sealed class WorkflowRun
{
    public long Id { get; init; }
    /// <summary>Workflow name (not the per-run title).</summary>
    public string Name { get; init; } = "";
    public string DisplayTitle { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Event { get; init; } = "";
    /// <summary>"queued" | "in_progress" | "completed" (lowercase).</summary>
    public string Status { get; init; } = "";
    /// <summary>"success" | "failure" | "cancelled" | ... ; "" until the run completes.</summary>
    public string Conclusion { get; init; } = "";
    /// <summary>Null until the run actually starts (gh serializes queued runs with a zero time).</summary>
    public DateTimeOffset? StartedAt { get; init; }
    public string Url { get; init; } = "";
}

/// <summary>Repo-level settings surfaced on the Repo tab.</summary>
public sealed class RepoSettings
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Homepage { get; init; } = "";
    public IReadOnlyList<string> Topics { get; init; } = [];
    /// <summary>Lowercase: "public" | "private" | "internal".</summary>
    public string Visibility { get; init; } = "";
    public bool IsArchived { get; init; }
    public string DefaultBranch { get; init; } = "";
    /// <summary>owner/name of the parent repo when this is a fork; "" otherwise.</summary>
    public string ParentSlug { get; init; } = "";
    public bool IsFork => ParentSlug.Length > 0;
}

/// <summary>One issue/PR label defined on a repo.</summary>
public sealed class Label
{
    public string Name { get; init; } = "";
    /// <summary>Hex color without the leading '#'.</summary>
    public string Color { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>One milestone defined on a repo.</summary>
public sealed class Milestone
{
    /// <summary>REST milestone number — the handle mutations address, distinct from the title.</summary>
    public int Number { get; init; }
    public string Title { get; init; } = "";
    /// <summary>"open" | "closed".</summary>
    public string State { get; init; } = "";
    public DateTimeOffset? DueOn { get; init; }
    /// <summary>Null means the count could not be fetched — not zero.</summary>
    public int? OpenIssues { get; init; }
    public int? ClosedIssues { get; init; }
}
