using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Decides which stored records belong to which discovered repositories, and which belong to no
/// repository this scan could see. Pure: every rule below is assertable without a disk, because
/// the cost of getting one wrong is one project's notes appearing on another.
///
/// Two rules are absolute and neither may be relaxed for convenience:
/// a record is re-keyed only on an unambiguous one-to-one match, and only onto a repository that
/// carries no record of its own. Anything else is reported and left alone.
/// </summary>
public static class ManifestIdentity
{
    /// <summary>
    /// Matches stored records against what the scan found.
    ///
    /// <paramref name="live"/> maps each discovered repository's normalized path to what it was
    /// read to be; a repository whose fingerprint could not be read (one under an operation's
    /// lease) is absent from it and is neither adopted onto nor counted against a match.
    /// <paramref name="pathExists"/> is the on-disk probe, injected so the rules are testable
    /// without building the folders they describe.
    /// </summary>
    public static ManifestIdentityReport Reconcile(
        IReadOnlyDictionary<string, ManifestEntry> stored,
        IReadOnlyDictionary<string, RepoFingerprint> live,
        IReadOnlyList<RootStatus> roots,
        Func<string, bool>? pathExists = null)
    {
        var orphanKeys = OrphanKeys(stored, live.Keys, roots, pathExists);
        if (orphanKeys.Count == 0) return ManifestIdentityReport.Empty;

        // Only a repository with no record of its own can be adopted onto, but the ambiguity
        // count spans every repository: a record matching one free repository AND one that is
        // already spoken for is still ambiguous, and picking the free one would be a guess.
        var matches = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var claimedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in orphanKeys)
        {
            var found = new List<string>();
            foreach (var (path, fingerprint) in live)
                if (RepoFingerprint.Matches(stored[key].Fingerprint, fingerprint))
                {
                    found.Add(path);
                    claimedBy[path] = claimedBy.GetValueOrDefault(path) + 1;
                }
            matches[key] = found;
        }

        var adoptions = new List<ManifestAdoption>();
        var refusals = new List<ManifestRefusal>();

        foreach (var key in orphanKeys)
        {
            var found = matches[key];
            if (found.Count == 0) continue;

            var name = NameOf(key);
            if (found.Count > 1)
            {
                refusals.Add(new ManifestRefusal(key, name, ManifestRefusalReason.SeveralRepositoriesMatch, found));
                continue;
            }

            var target = found[0];
            if (claimedBy[target] > 1)
            {
                refusals.Add(new ManifestRefusal(key, name, ManifestRefusalReason.SeveralRecordsMatch, [target]));
                continue;
            }
            if (stored.ContainsKey(target))
            {
                refusals.Add(new ManifestRefusal(key, name, ManifestRefusalReason.TargetAlreadyHasMetadata, [target]));
                continue;
            }

            adoptions.Add(new ManifestAdoption(key, target, name));
        }

        var adopted = new HashSet<string>(adoptions.Select(a => a.FromPath), StringComparer.OrdinalIgnoreCase);
        var orphans = orphanKeys
            .Where(key => !adopted.Contains(key))
            .Select(key => new ManifestOrphan(
                key, NameOf(key), stored[key].Manifest.Description, stored[key].LastSeenUtc))
            .ToList();

        return new ManifestIdentityReport(adoptions, refusals, orphans);
    }

    /// <summary>
    /// The stored records this scan found no repository for. Every one of the three conditions
    /// below is required:
    ///
    /// the scan did not meet the path; nothing is at the path on disk (a repository the reader
    /// narrowed their folders away from is still theirs, not a lost one); and no configured
    /// folder covering the path was unreadable, missing, or switched off — an unplugged drive
    /// makes every path under it vanish at once, and orphaning them all would put the reader's
    /// whole portfolio in the forget list.
    /// </summary>
    public static IReadOnlyList<string> OrphanKeys(
        IReadOnlyDictionary<string, ManifestEntry> stored,
        IEnumerable<string> livePaths,
        IReadOnlyList<RootStatus> roots,
        Func<string, bool>? pathExists = null)
    {
        // Nothing has reported on the folders yet, so nothing can be said to be gone.
        if (roots.Count == 0) return [];

        var exists = pathExists ?? Directory.Exists;
        var live = new HashSet<string>(livePaths.Select(RepoPaths.Normalize), StringComparer.OrdinalIgnoreCase);
        var orphans = new List<string>();
        foreach (var key in stored.Keys)
        {
            if (live.Contains(RepoPaths.Normalize(key))) continue;
            if (UnderUnavailableRoot(key, roots)) continue;
            if (SafeExists(exists, key)) continue;
            orphans.Add(key);
        }
        orphans.Sort(StringComparer.OrdinalIgnoreCase);
        return orphans;
    }

    /// <summary>The orphan list a surface shows, with the record's own description carried onto it.</summary>
    public static IReadOnlyList<ManifestOrphan> Orphans(
        IReadOnlyDictionary<string, ManifestEntry> stored,
        IEnumerable<string> livePaths,
        IReadOnlyList<RootStatus> roots,
        Func<string, bool>? pathExists = null) =>
        [.. OrphanKeys(stored, livePaths, roots, pathExists)
            .Select(key => new ManifestOrphan(
                key, NameOf(key), stored[key].Manifest.Description, stored[key].LastSeenUtc))];

    private static bool UnderUnavailableRoot(string path, IReadOnlyList<RootStatus> roots)
    {
        foreach (var root in roots)
            if (root.Availability != RootAvailability.Available && RepoPaths.IsAtOrUnder(path, root.Path))
                return true;
        return false;
    }

    /// <summary>An unreadable path is treated as present: a probe that threw proves nothing is gone.</summary>
    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try { return exists(path); }
        catch (Exception ex)
        {
            Log.Warn($"could not probe {path} while reconciling project metadata", ex);
            return true;
        }
    }

    private static string NameOf(string path)
    {
        var name = Path.GetFileName(RepoPaths.Normalize(path));
        return name.Length > 0 ? name : path;
    }

    // ── The wording the surfaces report ─────────────────────────────

    /// <summary>What a scan that re-keyed records says, or empty when it re-keyed none.</summary>
    public static string DescribeAdoptions(IReadOnlyList<ManifestAdoption> adoptions) => adoptions.Count switch
    {
        0 => "",
        1 => $"{adoptions[0].Name} is now at {adoptions[0].ToPath} — its description, category, status and notes came with it.",
        _ => $"{adoptions.Count} projects were found at new locations; their saved metadata moved with them.",
    };

    /// <summary>What a scan that refused an adoption says. Never a guess, and never silent.</summary>
    public static string DescribeRefusals(IReadOnlyList<ManifestRefusal> refusals)
    {
        if (refusals.Count == 0) return "";
        if (refusals.Count > 1)
            return $"Saved metadata for {refusals.Count} projects matches more than one repository — "
                 + "open each project to re-enter it.";

        var refusal = refusals[0];
        return refusal.Reason switch
        {
            ManifestRefusalReason.SeveralRepositoriesMatch =>
                $"{refusal.Name}'s saved metadata matches {refusal.Candidates.Count} repositories — "
                + "open either project to re-enter it.",
            ManifestRefusalReason.SeveralRecordsMatch =>
                $"Several saved records match {Describe(refusal.Candidates)} — "
                + $"{refusal.Name}'s metadata was left where it was.",
            _ =>
                $"{refusal.Name}'s saved metadata matches {Describe(refusal.Candidates)}, "
                + "which already has metadata of its own — nothing was overwritten.",
        };
    }

    private static string Describe(IReadOnlyList<string> candidates) =>
        candidates.Count > 0 ? candidates[0] : "another repository";
}
