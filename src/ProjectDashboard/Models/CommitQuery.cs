namespace ProjectDashboard.Models;

/// <summary>Optional constraints for a paged history query; all combine (AND).</summary>
public sealed class CommitFilter
{
    /// <summary>Substring/regex matched against commit messages (`--grep`).</summary>
    public string? MessageGrep { get; init; }
    /// <summary>Author name/email pattern (`--author`).</summary>
    public string? Author { get; init; }
    /// <summary>Restrict history to a single path (`-- <path>`).</summary>
    public string? Path { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }

    public bool IsEmpty =>
        string.IsNullOrEmpty(MessageGrep) && string.IsNullOrEmpty(Author)
        && string.IsNullOrEmpty(Path) && Since is null && Until is null;
}

/// <summary>One page of history plus whether a further page exists.</summary>
public sealed class CommitPage
{
    public List<GitCommit> Commits { get; init; } = [];
    /// <summary>True when at least one commit exists beyond this page.</summary>
    public bool HasMore { get; init; }
}
