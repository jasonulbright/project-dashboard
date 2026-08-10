namespace ProjectDashboard.Models;

public sealed class GitHubIssue
{
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Author { get; set; } = "";
    /// <summary>Comma-joined label names ("" when none).</summary>
    public string Labels { get; set; } = "";
    public bool HasLabels => Labels.Length > 0;

    /// <summary>
    /// What the row says this issue is. A list filtered to closed or all carries rows a row
    /// drawn for an open-only list would announce as open.
    /// </summary>
    public string StateLabel => State.Length == 0 ? "open" : State;
}

public sealed class GitHubPullRequest
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsDraft { get; set; }
    /// <summary>"open" | "closed" | "merged" | "" (unread).</summary>
    public string State { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>"passing" | "failing" | "pending" | "" (no checks).</summary>
    public string ChecksState { get; set; } = "";

    /// <summary>
    /// What the row says this pull request is. A draft is only ever a draft while it is open, and
    /// a list read under a closed or all filter carries rows this label must not call open.
    /// </summary>
    public string StateLabel => State switch
    {
        "closed" or "merged" => State,
        _ => IsDraft ? "draft" : "open",
    };
}

/// <summary>One repo of the signed-in user, for the clone picker.</summary>
public sealed class RemoteRepo
{
    public string NameWithOwner { get; set; } = "";
    public string Description { get; set; } = "";
    public string Visibility { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string Name => NameWithOwner.Contains('/') ? NameWithOwner[(NameWithOwner.IndexOf('/') + 1)..] : NameWithOwner;
}
