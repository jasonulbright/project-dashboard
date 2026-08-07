namespace ProjectDashboard.Models;

/// <summary>One comment on an issue or pull request.</summary>
public sealed class IssueComment
{
    public string Author { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string Body { get; init; } = "";
}

/// <summary>Full issue view (body + comment thread), fetched on demand per issue.</summary>
public sealed class IssueDetail
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Lowercase: "open" | "closed".</summary>
    public string State { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>"" when the account is deleted (gh serializes author as null).</summary>
    public string Author { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Comma-joined label names for display ("" when none). Not round-trippable:
    /// a label name may itself contain a comma. Use <see cref="LabelNames"/> to act on labels.</summary>
    public string Labels { get; init; } = "";
    /// <summary>Label names exactly as the API returned them, commas and all.</summary>
    public IReadOnlyList<string> LabelNames { get; init; } = [];
    /// <summary>Comma-joined assignee logins ("" when none).</summary>
    public string Assignees { get; init; } = "";
    /// <summary>Milestone title, "" when none.</summary>
    public string Milestone { get; init; } = "";
    public IReadOnlyList<IssueComment> Comments { get; init; } = [];
    public string Url { get; init; } = "";
}

/// <summary>Full pull-request view, fetched on demand per PR.</summary>
public sealed class PullRequestDetail
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Lowercase: "open" | "closed" | "merged".</summary>
    public string State { get; init; } = "";
    public string Body { get; init; } = "";
    public string Author { get; init; } = "";
    public bool IsDraft { get; init; }
    public string BaseRef { get; init; } = "";
    public string HeadRef { get; init; } = "";
    /// <summary>Lowercase: "mergeable" | "conflicting" | "unknown" ("" when absent).</summary>
    public string Mergeable { get; init; } = "";
    /// <summary>Lowercase merge-state status ("clean", "blocked", "dirty", ...; "" when absent).</summary>
    public string MergeStateStatus { get; init; } = "";
    /// <summary>Null means the count could not be fetched — not zero.</summary>
    public int? ChangedFiles { get; init; }
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
    /// <summary>"passing" | "failing" | "pending" | "" (no checks).</summary>
    public string ChecksState { get; init; } = "";
    /// <summary>Lowercase review decision ("approved", "changes_requested", "review_required"; "" when none).</summary>
    public string ReviewDecision { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<IssueComment> Comments { get; init; } = [];
    public string Url { get; init; } = "";
}
