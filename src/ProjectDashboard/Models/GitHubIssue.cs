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
}

public sealed class GitHubPullRequest
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsDraft { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>"passing" | "failing" | "pending" | "" (no checks).</summary>
    public string ChecksState { get; set; } = "";
    public string StateLabel => IsDraft ? "draft" : "open";
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
