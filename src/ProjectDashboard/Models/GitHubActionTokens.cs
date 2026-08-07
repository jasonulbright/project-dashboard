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

/// <summary>
/// Enum → exact gh token. The UI binds enums, never free text, so the token that
/// reaches GitHubService.BuildMergeArgs / BuildReviewArgs is always one those
/// methods map — their unmapped-token throw is unreachable from the UI.
/// </summary>
public static class GitHubActionTokens
{
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
