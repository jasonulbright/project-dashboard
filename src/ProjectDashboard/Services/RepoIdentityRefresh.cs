using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Re-reads what a repository is after an operation replaced its history, and records it against
/// the path the repository already occupies.
///
/// Both directions of a history rewrite need this. The rewrite replaces the root commits the
/// stored fingerprint was taken from; the restore puts the original ones back. A record left
/// describing history the repository no longer has would fail to recognise its own repository if
/// the folder moved before the next full scan — the metadata would be stranded by an operation
/// this app performed itself.
/// </summary>
internal static class RepoIdentityRefresh
{
    /// <summary>
    /// Never allowed to fail the operation that called it: the record is still reachable by its
    /// path key, and the next full scan records the same thing. A no-op when the host supplied no
    /// store, and when the path carries no record.
    /// </summary>
    public static async Task RecordAsync(GitService git, ManifestStore? manifests, string repoPath)
    {
        if (manifests is null || string.IsNullOrWhiteSpace(repoPath)) return;

        try
        {
            var status = await git.GetStatusAsync(repoPath, CancellationToken.None);
            manifests.RecordFingerprint(repoPath, RepoFingerprint.For(
                Path.GetFileName(RepoPaths.Normalize(repoPath)),
                await git.GetRootCommitsAsync(repoPath, CancellationToken.None),
                status.RemoteUrl));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not re-read what {repoPath} is after its history changed", ex);
        }
    }
}
