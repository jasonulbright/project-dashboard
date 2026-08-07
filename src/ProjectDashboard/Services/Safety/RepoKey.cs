using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Maps a repository's full path to one filesystem-safe key used to name its
/// per-repo backup directory.
///
/// Identity is the SHA-256 of the path after normalization (full path, trailing
/// separator trimmed, lowercased — Windows paths are case-insensitive, so two
/// spellings of one repo must resolve to one key). The 64-hex digest is the whole
/// identity: two distinct normalized paths share a key only under a SHA-256
/// collision, which is infeasible, so distinct repos never collide. A sanitized
/// leaf-name prefix is prepended purely so the directory is recognizable on disk;
/// it carries no identity and two repos with the same leaf name stay distinct
/// through their digests.
/// </summary>
public static class RepoKey
{
    private const int MaxSlugLength = 40;

    public static string For(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repo path is required.", nameof(repoPath));

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))).ToLowerInvariant();

        var slug = Sanitize(Path.GetFileName(full));
        return slug.Length > 0 ? $"{slug}-{digest}" : digest;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_')
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
            if (sb.Length >= MaxSlugLength) break;
        }
        return sb.ToString().Trim('-');
    }
}
