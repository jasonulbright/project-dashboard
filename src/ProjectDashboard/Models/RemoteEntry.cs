namespace ProjectDashboard.Models;

/// <summary>One configured remote with its fetch and push URLs (they can differ).</summary>
public sealed class RemoteEntry
{
    public string Name { get; init; } = "";
    public string FetchUrl { get; init; } = "";
    public string PushUrl { get; init; } = "";
}

/// <summary>
/// A repository's remotes, or why they could not be read. git reports a read it could not
/// perform as a non-zero exit rather than a throw, so an empty list alone cannot be told apart
/// from a repository with no remotes configured.
/// </summary>
public sealed record RemotesResult(List<RemoteEntry> Remotes, bool HasError = false, string ErrorText = "");

/// <summary>
/// A repository's remote-tracking branches, or why they could not be read. Same reason
/// <see cref="RemotesResult"/> carries the flag: the empty list is otherwise ambiguous.
/// </summary>
public sealed record RemoteBranchesResult(List<string> Branches, bool HasError = false, string ErrorText = "");
