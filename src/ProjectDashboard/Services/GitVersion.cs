namespace ProjectDashboard.Services;

/// <summary>
/// The version out of <c>git --version</c>.
///
/// Only the token directly after the literal <c>version</c> is read — <c>git version
/// 2.45.1.windows.1</c> — because any other dotted-numeric token on the line belongs to
/// something else: an install path, a bundled tool's version, a distributor's suffix. A
/// decision made from one of those is made from a number that has nothing to do with git.
/// </summary>
public static class GitVersion
{
    /// <summary>The token git printed, distributor suffix included, or null when the line carries none.</summary>
    public static string? TokenFrom(string versionOutput)
    {
        var tokens = (versionOutput ?? "").Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < tokens.Length; i++)
            if (string.Equals(tokens[i], "version", StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];
        return null;
    }

    /// <summary>
    /// Major and minor out of that token, or null when either is unreadable. Anything past the
    /// minor is ignored rather than parsed: a distributor's fourth and fifth components follow
    /// no scheme this application can compare.
    /// </summary>
    public static (int Major, int Minor)? MajorMinorFrom(string versionOutput)
    {
        if (TokenFrom(versionOutput) is not { } token) return null;
        var parts = token.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return null;
        return (major, minor);
    }
}
