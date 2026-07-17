namespace ProjectDashboard.Models;

/// <summary>Host/owner/repo parsed from a git remote URL.</summary>
public sealed record GitRemote(string Host, string Owner, string Repo)
{
    public bool IsGitHub => Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                         || Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the remote URL shapes git actually produces:
    ///   https://github.com/owner/repo(.git)      http variant too
    ///   git@github.com:owner/repo(.git)          scp-like
    ///   ssh://git@github.com(:port)/owner/repo(.git)
    ///   git://github.com/owner/repo(.git)
    /// Local paths (C:\..., file://) and anything unparseable return null.
    /// ".git" is stripped only as a SUFFIX — never from inside a name
    /// (e.g. "user.github.io" must survive intact).
    /// </summary>
    /// <summary>
    /// Repository folder name from any clone URL — https/ssh/git/scp AND file:// and
    /// local paths — for choosing a clone target directory. "" if none can be derived.
    /// Broader than Parse (which is GitHub-scoped); this only needs the last segment.
    /// </summary>
    public static string RepoNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim().TrimEnd('/', '\\');

        var lastSlash = trimmed.LastIndexOfAny(['/', '\\', ':']);
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Trim();
    }

    public static GitRemote? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        string host;
        string path;

        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx > 0)
        {
            var scheme = url[..schemeIdx].ToLowerInvariant();
            if (scheme is not ("http" or "https" or "ssh" or "git")) return null; // file:// etc.

            var rest = url[(schemeIdx + 3)..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return null;

            host = rest[..slash];
            path = rest[(slash + 1)..];

            var at = host.LastIndexOf('@');            // strip user@
            if (at >= 0) host = host[(at + 1)..];
            var port = host.IndexOf(':');              // strip :port
            if (port >= 0) host = host[..port];
        }
        else
        {
            // scp-like: [user@]host:path — but "C:\..." / "C:/..." is a local path, not a remote.
            var colon = url.IndexOf(':');
            if (colon <= 1) return null;               // no colon, or single-letter drive
            if (url.Contains('\\')) return null;       // windows path
            var slash = url.IndexOf('/');
            if (slash >= 0 && slash < colon) return null; // '/' before ':' → not scp-like

            host = url[..colon];
            path = url[(colon + 1)..];

            var at = host.LastIndexOf('@');
            if (at >= 0) host = host[(at + 1)..];
            if (host.Length == 0 || !host.Contains('.')) return null; // not a hostname
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        if (path.Length == 0) return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        // Owner may nest (GitLab groups): everything but the last segment.
        var owner = string.Join('/', segments[..^1]);
        var repo = segments[^1];
        return new GitRemote(host, owner, repo);
    }
}
