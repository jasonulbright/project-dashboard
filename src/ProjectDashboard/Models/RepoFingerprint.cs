using System.Text.Json.Serialization;

namespace ProjectDashboard.Models;

/// <summary>
/// What a repository is, told apart from where its folder currently sits. Recorded app-side
/// beside the manifest; nothing here is ever written into a repository.
///
/// The root-commit set and the remote URL are the only fields strong enough to identify a
/// repository. <see cref="FolderName"/> is recorded for the surfaces that name an orphan and is
/// never an input to a match: a name two folders share would turn a rename into a wrong
/// adoption, and a wrong adoption puts one project's notes on another.
/// </summary>
public sealed class RepoFingerprint
{
    /// <summary>Every parentless commit reachable from any ref, sorted. Empty for a repository with no commits.</summary>
    public string[] RootCommitOids { get; set; } = [];

    /// <summary>The default remote, normalized by <see cref="NormalizeRemote"/>. Empty when there is none.</summary>
    public string RemoteUrl { get; set; } = "";

    public string FolderName { get; set; } = "";

    /// <summary>Whether this carries a field a match may be decided on. Derived, never stored.</summary>
    [JsonIgnore]
    public bool IsStrong => RootCommitOids.Length > 0 || RemoteUrl.Length > 0;

    public static RepoFingerprint For(string folderName, IEnumerable<string> rootCommitOids, string? remoteUrl) => new()
    {
        RootCommitOids = [.. rootCommitOids
            .Select(oid => oid.Trim())
            .Where(oid => oid.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(oid => oid, StringComparer.OrdinalIgnoreCase)],
        RemoteUrl = NormalizeRemote(remoteUrl),
        FolderName = folderName,
    };

    /// <summary>
    /// One spelling for a remote. The same repository is reached over https, ssh, and scp-like
    /// syntax with and without a <c>.git</c> suffix, and two records of one remote in different
    /// spellings would leave a moved repository unmatched.
    /// </summary>
    public static string NormalizeRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim();

        if (GitRemote.Parse(trimmed) is { } remote)
            return $"{remote.Host}/{remote.Owner}/{remote.Repo}".ToLowerInvariant();

        // A local or file:// remote names a path rather than a host. Compared as a path, with
        // the scheme, the separator flavour and the .git suffix out of the comparison.
        var path = trimmed;
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = path[7..];
        path = path.Replace('/', '\\');
        if (path.Length > 2 && path[0] == '\\' && path[2] == ':') path = path[1..];
        path = path.TrimEnd('\\');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4].TrimEnd('\\');
        return path.ToLowerInvariant();
    }

    /// <summary>
    /// Whether a stored fingerprint and a live one describe one repository.
    ///
    /// Root commits decide it when both sides have them. A remote URL alone decides it only while
    /// the stored side carries no root commit to contradict it: a fork and its upstream share a
    /// URL for as long as the remote is not re-pointed, and a history rewrite replaces the root
    /// commits under an unchanged one.
    /// </summary>
    public static bool Matches(RepoFingerprint? stored, RepoFingerprint? live)
    {
        if (stored is null || live is null) return false;
        if (!stored.IsStrong || !live.IsStrong) return false;

        if (stored.RootCommitOids.Length > 0 && live.RootCommitOids.Length > 0)
            return stored.RootCommitOids.SequenceEqual(live.RootCommitOids, StringComparer.OrdinalIgnoreCase);

        return stored.RootCommitOids.Length == 0
            && stored.RemoteUrl.Length > 0
            && string.Equals(stored.RemoteUrl, live.RemoteUrl, StringComparison.OrdinalIgnoreCase);
    }

    public bool SameAs(RepoFingerprint? other) =>
        other is not null
        && RootCommitOids.SequenceEqual(other.RootCommitOids, StringComparer.OrdinalIgnoreCase)
        && string.Equals(RemoteUrl, other.RemoteUrl, StringComparison.OrdinalIgnoreCase)
        && string.Equals(FolderName, other.FolderName, StringComparison.OrdinalIgnoreCase);

    public RepoFingerprint Copy() => new()
    {
        RootCommitOids = [.. RootCommitOids],
        RemoteUrl = RemoteUrl,
        FolderName = FolderName,
    };
}
