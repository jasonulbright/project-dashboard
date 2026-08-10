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

/// <summary>
/// Workflow-run status the UI offers; maps to the exact gh flag token. The members are named for
/// what a reader filtering runs asks for rather than for GitHub's own spelling of it, so the
/// picker binds the enum directly and the token stays the API's.
/// </summary>
public enum WorkflowRunStatus
{
    Any,
    Queued,
    Running,
    Completed,
    Succeeded,
    Failed,
    Cancelled,
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

    /// <summary>
    /// The gh status token, or null for the row that filters nothing — which is a selection the
    /// picker offers rather than a token gh has, so the flag is left off the read entirely.
    /// </summary>
    public static string? Token(this WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Any => null,
        WorkflowRunStatus.Queued => "queued",
        WorkflowRunStatus.Running => "in_progress",
        WorkflowRunStatus.Completed => "completed",
        WorkflowRunStatus.Succeeded => "success",
        WorkflowRunStatus.Failed => "failure",
        WorkflowRunStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>
    /// The status a read was made under, back from the token it carried, on the same terms as
    /// <see cref="ParseListState"/>. An unrecognized token is the unfiltered row: a surface names
    /// the facets it can account for and claims no filter it cannot.
    /// </summary>
    public static WorkflowRunStatus ParseRunStatus(string? token) => token switch
    {
        "queued" => WorkflowRunStatus.Queued,
        "in_progress" => WorkflowRunStatus.Running,
        "completed" => WorkflowRunStatus.Completed,
        "success" => WorkflowRunStatus.Succeeded,
        "failure" => WorkflowRunStatus.Failed,
        "cancelled" => WorkflowRunStatus.Cancelled,
        _ => WorkflowRunStatus.Any,
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
