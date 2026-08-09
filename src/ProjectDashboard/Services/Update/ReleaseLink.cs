namespace ProjectDashboard.Services.Update;

/// <summary>
/// The hosts and paths the update check is pinned to. Both are constants: neither the
/// endpoint that is read nor the link that may be opened is ever derived from repository
/// data or from a response body.
/// </summary>
public static class ReleaseLink
{
    public const string Owner = "jasonulbright";
    public const string Repo = "project-dashboard";

    /// <summary>The only host this feature contacts.</summary>
    public const string ApiHost = "api.github.com";

    public const string LatestReleaseEndpoint = $"https://{ApiHost}/repos/{Owner}/{Repo}/releases/latest";

    private const string SiteHost = "github.com";
    private const string ReleasesPath = $"/{Owner}/{Repo}/releases";

    /// <summary>
    /// The exact string a refused link is measured against and the page a reader is sent to.
    /// </summary>
    public const string ReleasesPage = $"https://{SiteHost}{ReleasesPath}";

    /// <summary>
    /// The form handed to the launcher for a link that passed <see cref="IsPinnedReleaseUrl"/>,
    /// or false when it did not. The parsed form is what is opened rather than the raw
    /// capture: a target padded with spaces or carrying an embedded control character passes
    /// a host comparison and would otherwise reach the shell with those characters intact.
    /// </summary>
    public static bool TryNormalize(string? candidate, out string target)
    {
        target = "";
        if (!TryPinned(candidate, out var uri)) return false;
        target = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// True only for an https link into the pinned repository's releases path on the pinned
    /// site host. The response carrying it is untrusted input and the link reaches the shell,
    /// so a host that merely ends in the pinned one, a userinfo section that puts the real
    /// host after an <c>@</c>, and a non-default port are each refused rather than normalized.
    /// </summary>
    public static bool IsPinnedReleaseUrl(string? candidate) => TryPinned(candidate, out _);

    private static bool TryPinned(string? candidate, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.Host, SiteHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (parsed.UserInfo.Length > 0) return false;
        if (!parsed.IsDefaultPort) return false;

        var path = parsed.AbsolutePath;
        if (!path.StartsWith(ReleasesPath, StringComparison.OrdinalIgnoreCase)) return false;
        // The prefix has to end on a segment boundary: /…/releases-mirror/x shares it
        // without being under it.
        if (path.Length > ReleasesPath.Length && path[ReleasesPath.Length] != '/') return false;

        uri = parsed;
        return true;
    }
}
