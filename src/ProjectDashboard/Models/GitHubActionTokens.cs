namespace ProjectDashboard.Models;

/// <summary>Merge strategy the UI offers; maps to the exact gh flag token.</summary>
public enum MergeStrategy
{
    Merge,
    Squash,
    Rebase,
}

/// <summary>PR review verdict the UI offers; maps to the exact gh flag token.</summary>
public enum ReviewAction
{
    Approve,
    RequestChanges,
    Comment,
}

/// <summary>Issue/pull-request list state the UI offers; maps to the exact gh flag token.</summary>
public enum GitHubListState
{
    Open,
    Closed,
    All,
}

/// <summary>Repository visibility the UI offers; maps to the exact gh flag token.</summary>
public enum RepoVisibility
{
    Public,
    Private,
    Internal,
}

/// <summary>
/// Enum → exact gh token. The UI binds enums, never free text, so the token that
/// reaches GitHubService.BuildMergeArgs / BuildReviewArgs / BuildVisibilityArgs is
/// always one those methods map, and their refusal path is never taken from the UI.
/// </summary>
public static class GitHubActionTokens
{
    public static string Token(this RepoVisibility visibility) => visibility switch
    {
        RepoVisibility.Public => "public",
        RepoVisibility.Private => "private",
        RepoVisibility.Internal => "internal",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility)),
    };

    /// <summary>Parses gh's lowercase visibility back to the enum the picker binds.</summary>
    public static RepoVisibility? ParseVisibility(string visibility) => visibility switch
    {
        "public" => RepoVisibility.Public,
        "private" => RepoVisibility.Private,
        "internal" => RepoVisibility.Internal,
        _ => null,
    };

    public static string Token(this GitHubListState state) => state switch
    {
        GitHubListState.Open => "open",
        GitHubListState.Closed => "closed",
        GitHubListState.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>
    /// The state a read was made under, back from the token it carried. A surface labels its rows
    /// from the query that produced them rather than from the picker, which the reader may have
    /// moved on since.
    /// </summary>
    public static GitHubListState ParseListState(string token) => token switch
    {
        "open" => GitHubListState.Open,
        "closed" => GitHubListState.Closed,
        _ => GitHubListState.All,
    };

    public static string Token(this MergeStrategy strategy) => strategy switch
    {
        MergeStrategy.Merge => "merge",
        MergeStrategy.Squash => "squash",
        MergeStrategy.Rebase => "rebase",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
    };

    public static string Token(this ReviewAction action) => action switch
    {
        ReviewAction.Approve => "approve",
        ReviewAction.RequestChanges => "requestChanges",
        ReviewAction.Comment => "comment",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
