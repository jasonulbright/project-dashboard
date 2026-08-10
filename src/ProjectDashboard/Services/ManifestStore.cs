using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Stores per-project manifests in a single path-keyed index that lives OUTSIDE
/// project source trees, as manifests.json under AppPaths.RoamingDir.
///
/// Shape: { "SchemaVersion": 2, "Entries": { "C:\\projects\\tabkit": { "Manifest": {…},
/// "Fingerprint": {…}, "FirstSeenUtc": …, "LastSeenUtc": … }, … } }
/// Keys are full repo paths, compared case-insensitively (Windows). Path-keying
/// avoids name collisions between repos that share a folder name; the fingerprint beside each
/// entry is what re-keys a record whose folder moved, and is never written into the repository.
///
/// A document whose root is a bare path→manifest map is the shape written before fingerprints.
/// It is read as one entry per key with no fingerprint and no stamps, and the next write records
/// the richer shape — losslessly, and without a load that writes.
/// </summary>
public class ManifestStore
{
    // Roaming: durable user state (not cache). Cache stays in the local dir.
    private static readonly string StoreDir = AppPaths.RoamingDir;

    private static readonly string IndexPath = AppPaths.ManifestIndexFile;

    internal const int SchemaVersion = 2;

    /// <summary>
    /// How far a last-seen stamp may fall behind before it alone is worth a write. Without a
    /// floor, every scan rewrites the one file holding metadata nothing else can reconstruct.
    /// </summary>
    private static readonly TimeSpan SeenWriteInterval = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private Dictionary<string, ManifestEntry>? _index;

    private static string NormalizeKey(string repoPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));

    private Dictionary<string, ManifestEntry> Index()
    {
        lock (_lock)
        {
            if (_index is not null) return _index;

            try
            {
                // Corrupt-index handling (quarantine + .bak recovery) lives in
                // DurableJsonFile.Read; null here means a legitimately fresh start.
                _index = Bind(DurableJsonFile.Read<JsonNode>(IndexPath, JsonOptions));
            }
            catch (Exception ex)
            {
                // Unreadable (not corrupt) index: start empty rather than crash. The
                // atomic write path keeps a .bak, so the next Save cannot destroy the
                // only copy the way an in-place truncating write could.
                Log.Error($"Failed to read manifest index at {IndexPath} — starting empty", ex);
                _index = Empty();
            }

            return _index;
        }
    }

    private static Dictionary<string, ManifestEntry> Empty() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads either stored shape. The version probe decides it: a versioned envelope read as a
    /// bare map yields one meaningless entry, and a bare map read as an envelope yields none at
    /// all — both silently, which is how metadata disappears.
    /// </summary>
    private static Dictionary<string, ManifestEntry> Bind(JsonNode? root)
    {
        if (root is not JsonObject document) return Empty();

        var entries = Empty();
        if (Property(document, "SchemaVersion") is not null && Property(document, "Entries") is JsonObject versioned)
        {
            foreach (var (key, value) in versioned)
                if (value.Deserialize<ManifestEntry>(JsonOptions) is { } entry)
                    entries[key] = entry;
            return entries;
        }

        foreach (var (key, value) in document)
            if (value.Deserialize<ProjectManifest>(JsonOptions) is { } manifest)
                entries[key] = new ManifestEntry { Manifest = manifest };
        return entries;
    }

    private static JsonNode? Property(JsonObject document, string name)
    {
        foreach (var (key, value) in document)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>
    /// Returns true and a COPY of the stored manifest for the given repo path, if present.
    /// Copies both ways (get and save) so no caller ever holds a reference into the live
    /// index — a mutated shared instance would silently persist on the next unrelated Save.
    /// </summary>
    public bool TryGet(string repoPath, out ProjectManifest? manifest)
    {
        // Remote-only projects carry an empty FullPath; a lookup on one is a
        // miss, not an exception (NormalizeKey throws on empty paths).
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            manifest = null;
            return false;
        }

        var index = Index();
        lock (_lock)
        {
            if (index.TryGetValue(NormalizeKey(repoPath), out var stored) && stored is not null)
            {
                manifest = stored.Manifest.Copy();
                return true;
            }
            manifest = null;
            return false;
        }
    }

    /// <summary>Every stored record, detached. The input to the identity pass and to the surfaces that list them.</summary>
    public IReadOnlyDictionary<string, ManifestEntry> Snapshot()
    {
        var index = Index();
        lock (_lock)
        {
            var copy = Empty();
            foreach (var (key, entry) in index) copy[key] = entry.Copy();
            return copy;
        }
    }

    /// <summary>
    /// Upserts the manifest for a repo path and persists the whole index. False means the value
    /// did not reach disk, and the in-memory index is left as it was: adopted in memory only, an
    /// edit would read as saved for the rest of the session and be gone at the next launch.
    /// </summary>
    public bool Save(string repoPath, ProjectManifest manifest)
    {
        // An empty path cannot key the index; dropping the write beats poisoning
        // the store with an unreachable "" entry, but it must not be silent.
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            Log.Warn("Manifest save ignored: empty repo path");
            return false;
        }

        var index = Index();
        lock (_lock)
        {
            var key = NormalizeKey(repoPath);
            var entry = index.TryGetValue(key, out var existing) ? existing.Copy() : new ManifestEntry();
            entry.Manifest = manifest.Copy();

            // The candidate is written before it is adopted, so a failed write leaves the live
            // index exactly as the file on disk still describes it.
            var candidate = Clone(index);
            candidate[key] = entry;
            if (!Persist(candidate)) return false;
            index[key] = entry;
            return true;
        }
    }

    /// <summary>
    /// Applies one scan's conclusions in a single write: the records that follow a repository to
    /// its new path, and what every repository the scan met was found to be.
    ///
    /// A re-key is a move, not a copy — the stale key is what the plan calls redundant, and it is
    /// dropped with the record intact at its new key. Nothing here deletes a record for a
    /// repository that was simply not found; that is only ever a reader's own decision.
    ///
    /// Returns false when the write did not reach disk, leaving the live index describing the
    /// file: a re-key held in memory alone would present metadata as moved and lose it at the
    /// next launch.
    /// </summary>
    public bool ApplyScan(
        IReadOnlyList<ManifestAdoption> adoptions,
        IReadOnlyDictionary<string, RepoFingerprint> live,
        DateTimeOffset seenAt)
    {
        var index = Index();
        lock (_lock)
        {
            var candidate = Clone(index);
            var changed = false;

            foreach (var adoption in adoptions)
            {
                var from = NormalizeKey(adoption.FromPath);
                var to = NormalizeKey(adoption.ToPath);
                if (!candidate.TryGetValue(from, out var moving) || candidate.ContainsKey(to)) continue;

                candidate.Remove(from);
                candidate[to] = moving;
                changed = true;
            }

            foreach (var (path, fingerprint) in live)
            {
                var key = NormalizeKey(path);
                if (!candidate.TryGetValue(key, out var entry)) continue;

                if (!fingerprint.SameAs(entry.Fingerprint))
                {
                    entry.Fingerprint = fingerprint.Copy();
                    changed = true;
                }
                if (entry.FirstSeenUtc is null)
                {
                    entry.FirstSeenUtc = seenAt;
                    changed = true;
                }
                if (entry.LastSeenUtc is not { } last || seenAt - last >= SeenWriteInterval)
                {
                    entry.LastSeenUtc = seenAt;
                    changed = true;
                }
            }

            if (!changed) return true;
            if (!Persist(candidate)) return false;
            Adopt(index, candidate);
            return true;
        }
    }

    /// <summary>
    /// Records what a repository is, at the path it already occupies. The path key already names
    /// the record, so this can never re-key anything: it is the refresh a history rewrite needs,
    /// having replaced the root commits the previous fingerprint carried.
    /// </summary>
    public bool RecordFingerprint(string repoPath, RepoFingerprint fingerprint)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return false;

        var index = Index();
        lock (_lock)
        {
            var key = NormalizeKey(repoPath);
            if (!index.TryGetValue(key, out var stored)) return true;
            if (fingerprint.SameAs(stored.Fingerprint)) return true;

            var candidate = Clone(index);
            candidate[key].Fingerprint = fingerprint.Copy();
            if (!Persist(candidate)) return false;
            index[key].Fingerprint = fingerprint.Copy();
            return true;
        }
    }

    /// <summary>
    /// Drops records a reader chose to forget. The only path in this type that deletes metadata,
    /// and it is reached from one place: a list the reader is looking at.
    /// </summary>
    public bool Forget(IEnumerable<string> repoPaths)
    {
        var index = Index();
        lock (_lock)
        {
            var candidate = Clone(index);
            var removed = false;
            foreach (var path in repoPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (candidate.Remove(NormalizeKey(path))) removed = true;
            }

            if (!removed) return true;
            if (!Persist(candidate)) return false;
            Adopt(index, candidate);
            return true;
        }
    }

    private static Dictionary<string, ManifestEntry> Clone(Dictionary<string, ManifestEntry> index)
    {
        var copy = Empty();
        foreach (var (key, entry) in index) copy[key] = entry.Copy();
        return copy;
    }

    private static void Adopt(Dictionary<string, ManifestEntry> index, Dictionary<string, ManifestEntry> candidate)
    {
        index.Clear();
        foreach (var (key, entry) in candidate) index[key] = entry;
    }

    private static bool Persist(Dictionary<string, ManifestEntry> index)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var document = new ManifestDocument { SchemaVersion = SchemaVersion, Entries = index };
            DurableJsonFile.Write(IndexPath, JsonSerializer.Serialize(document, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to persist manifest index to {IndexPath}", ex);
            return false;
        }
    }

    private sealed class ManifestDocument
    {
        public int SchemaVersion { get; set; }
        public Dictionary<string, ManifestEntry> Entries { get; set; } = [];
    }
}
