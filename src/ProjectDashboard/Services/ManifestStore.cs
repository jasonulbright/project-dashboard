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
/// "Fingerprint": {…}, "FirstSeenUtc": …, "LastSeenUtc": … } }, "Forwards": { … } }
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

    /// <summary>
    /// How many times a save may follow a record that moved. A repository re-keyed repeatedly
    /// leaves a chain, and a bound is what keeps a damaged file from looping a caller forever.
    /// </summary>
    private const int MaxForwardHops = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private State? _state;

    /// <summary>The records and the trail left by the ones that moved. Mutated only under the lock.</summary>
    private sealed record State(
        Dictionary<string, ManifestEntry> Entries,
        Dictionary<string, ManifestForward> Forwards);

    private static string NormalizeKey(string repoPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));

    private State Current()
    {
        lock (_lock)
        {
            if (_state is not null) return _state;

            try
            {
                // Corrupt-index handling (quarantine + .bak recovery) lives in
                // DurableJsonFile.Read; null here means a legitimately fresh start.
                _state = Bind(DurableJsonFile.Read<JsonNode>(IndexPath, JsonOptions));
            }
            catch (Exception ex)
            {
                // Unreadable (not corrupt) index: start empty rather than crash. The
                // atomic write path keeps a .bak, so the next Save cannot destroy the
                // only copy the way an in-place truncating write could.
                Log.Error($"Failed to read manifest index at {IndexPath} — starting empty", ex);
                _state = EmptyState();
            }

            return _state;
        }
    }

    private static Dictionary<string, T> Empty<T>() => new(StringComparer.OrdinalIgnoreCase);

    private static State EmptyState() => new(Empty<ManifestEntry>(), Empty<ManifestForward>());

    /// <summary>
    /// Reads either stored shape. The version probe decides it: a versioned envelope read as a
    /// bare map yields one meaningless entry, and a bare map read as an envelope yields none at
    /// all — both silently, which is how metadata disappears.
    /// </summary>
    private static State Bind(JsonNode? root)
    {
        if (root is not JsonObject document) return EmptyState();

        var entries = Empty<ManifestEntry>();
        var forwards = Empty<ManifestForward>();

        if (Property(document, "SchemaVersion") is not null && Property(document, "Entries") is JsonObject versioned)
        {
            foreach (var (key, value) in versioned)
                if (value.Deserialize<ManifestEntry>(JsonOptions) is { } entry)
                    entries[key] = entry;

            if (Property(document, "Forwards") is JsonObject stored)
                foreach (var (key, value) in stored)
                    if (value.Deserialize<ManifestForward>(JsonOptions) is { ToPath.Length: > 0 } forward)
                        forwards[key] = forward;

            return new State(entries, forwards);
        }

        foreach (var (key, value) in document)
            if (value.Deserialize<ProjectManifest>(JsonOptions) is { } manifest)
                entries[key] = new ManifestEntry { Manifest = manifest };
        return new State(entries, forwards);
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
    ///
    /// A read never follows a record that moved. The path is the question being asked, and a
    /// different repository sitting in the vacated folder must not be handed the metadata of the
    /// one that left.
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

        var state = Current();
        lock (_lock)
        {
            if (state.Entries.TryGetValue(NormalizeKey(repoPath), out var stored) && stored is not null)
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
        var state = Current();
        lock (_lock)
        {
            var copy = Empty<ManifestEntry>();
            foreach (var (key, entry) in state.Entries) copy[key] = entry.Copy();
            return copy;
        }
    }

    /// <summary>Where each record that moved went. Detached, and read by the tests that pin the rule down.</summary>
    internal IReadOnlyDictionary<string, ManifestForward> ForwardSnapshot()
    {
        var state = Current();
        lock (_lock)
        {
            var copy = Empty<ManifestForward>();
            foreach (var (key, forward) in state.Forwards) copy[key] = forward.Copy();
            return copy;
        }
    }

    /// <summary>
    /// Upserts the manifest for a repo path and persists the whole index. False means the value
    /// did not reach disk, and the in-memory index is left as it was: adopted in memory only, an
    /// edit would read as saved for the rest of the session and be gone at the next launch.
    ///
    /// <paramref name="identity"/> is what the caller believes it is editing. A surface opened
    /// before a scan re-keyed the record still holds the old path; passing the repository's
    /// fingerprint lets the write follow the record to where it went, decided here under the same
    /// lock the re-key itself took. Resolving that outside the lock would only move the window.
    /// Null means no identity was offered and the path is taken literally.
    /// </summary>
    public bool Save(string repoPath, ProjectManifest manifest, RepoFingerprint? identity = null)
    {
        // An empty path cannot key the index; dropping the write beats poisoning
        // the store with an unreachable "" entry, but it must not be silent.
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            Log.Warn("Manifest save ignored: empty repo path");
            return false;
        }

        var state = Current();
        lock (_lock)
        {
            var key = Resolve(state, NormalizeKey(repoPath), identity);
            var entry = state.Entries.TryGetValue(key, out var existing) ? existing.Copy() : new ManifestEntry();
            entry.Manifest = manifest.Copy();

            // The candidate is written before it is adopted, so a failed write leaves the live
            // index exactly as the file on disk still describes it.
            var candidate = Clone(state);
            candidate.Entries[key] = entry;
            if (!Persist(candidate)) return false;
            Adopt(state, candidate);
            return true;
        }
    }

    /// <summary>
    /// The key a write for <paramref name="identity"/> belongs at. A path that still names a
    /// record is that record. A path a record moved off resolves to where it went, but only while
    /// the repository asking answers to the fingerprint recorded at the re-key — a fresh
    /// repository in the vacated folder does not, and keeps its own key.
    /// </summary>
    private static string Resolve(State state, string key, RepoFingerprint? identity)
    {
        if (identity is null) return key;

        var walked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        for (var hop = 0; hop < MaxForwardHops; hop++)
        {
            if (state.Entries.ContainsKey(key)) return key;
            if (!state.Forwards.TryGetValue(key, out var forward)) return key;
            if (!RepoFingerprint.Matches(forward.Fingerprint, identity)) return key;

            var next = forward.ToPath;
            if (!walked.Add(next)) return key;
            key = next;
        }

        Log.Warn($"stopped following moved project metadata after {MaxForwardHops} hops");
        return key;
    }

    /// <summary>
    /// Applies one scan's conclusions in a single write: the records that follow a repository to
    /// its new path, and what every repository the scan met was found to be.
    ///
    /// A re-key is a move, not a copy — the stale key is what the plan calls redundant, and it is
    /// dropped with the record intact at its new key, leaving a forwarding trail so a surface
    /// still holding the old path writes to the record rather than past it. Nothing here deletes a
    /// record for a repository that was simply not found; that is only ever a reader's own
    /// decision.
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
        var state = Current();
        lock (_lock)
        {
            var candidate = Clone(state);
            var changed = false;

            foreach (var adoption in adoptions)
            {
                var from = NormalizeKey(adoption.FromPath);
                var to = NormalizeKey(adoption.ToPath);
                if (!candidate.Entries.TryGetValue(from, out var moving) || candidate.Entries.ContainsKey(to)) continue;

                candidate.Entries.Remove(from);
                candidate.Entries[to] = moving;

                var identity = (live.TryGetValue(to, out var print) ? print : moving.Fingerprint)?.Copy();

                // Every trail that ended at the vacated key is re-pointed at the new one rather
                // than chained onto it: a chain's first hop names a key that no longer holds a
                // record, and pruning a dead hop would cut the trail from the path a page opened
                // two moves ago.
                foreach (var (key, forward) in candidate.Forwards)
                    if (string.Equals(forward.ToPath, from, StringComparison.OrdinalIgnoreCase))
                    {
                        forward.ToPath = to;
                        forward.Fingerprint = identity;
                        forward.RecordedUtc = seenAt;
                    }

                candidate.Forwards[from] = new ManifestForward
                {
                    ToPath = to,
                    Fingerprint = identity,
                    RecordedUtc = seenAt,
                };
                changed = true;
            }

            foreach (var (path, fingerprint) in live)
            {
                var key = NormalizeKey(path);
                if (!candidate.Entries.TryGetValue(key, out var entry)) continue;

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

            changed |= PruneForwards(candidate);

            if (!changed) return true;
            if (!Persist(candidate)) return false;
            Adopt(state, candidate);
            return true;
        }
    }

    /// <summary>
    /// Drops a trail that leads nowhere: one whose record has been forgotten, and one whose own
    /// path holds a record again. Both are how a forward ages out — it lives exactly as long as
    /// the record it points at, and never outlives a new record taking its place.
    /// </summary>
    private static bool PruneForwards(State candidate)
    {
        var dead = candidate.Forwards
            .Where(f => candidate.Entries.ContainsKey(f.Key) || !candidate.Entries.ContainsKey(f.Value.ToPath))
            .Select(f => f.Key)
            .ToList();

        foreach (var key in dead) candidate.Forwards.Remove(key);
        return dead.Count > 0;
    }

    /// <summary>
    /// Records what a repository is, at the path it already occupies. The path key already names
    /// the record, so this can never re-key anything: it is the refresh a history rewrite needs,
    /// having replaced the root commits the previous fingerprint carried.
    /// </summary>
    public bool RecordFingerprint(string repoPath, RepoFingerprint fingerprint)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return false;

        var state = Current();
        lock (_lock)
        {
            var key = NormalizeKey(repoPath);
            if (!state.Entries.TryGetValue(key, out var stored)) return true;
            if (fingerprint.SameAs(stored.Fingerprint)) return true;

            var candidate = Clone(state);
            candidate.Entries[key].Fingerprint = fingerprint.Copy();
            if (!Persist(candidate)) return false;
            Adopt(state, candidate);
            return true;
        }
    }

    /// <summary>
    /// Drops records a reader chose to forget. The only path in this type that deletes metadata,
    /// and it is reached from one place: a list the reader is looking at.
    /// </summary>
    public bool Forget(IEnumerable<string> repoPaths)
    {
        var state = Current();
        lock (_lock)
        {
            var candidate = Clone(state);
            var removed = false;
            foreach (var path in repoPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (candidate.Entries.Remove(NormalizeKey(path))) removed = true;
            }

            // A forgotten record takes its trail with it: a forward to a key nothing holds would
            // send a later save to a record that no longer exists.
            var pruned = PruneForwards(candidate);

            if (!removed && !pruned) return true;
            if (!Persist(candidate)) return false;
            Adopt(state, candidate);
            return true;
        }
    }

    /// <summary>
    /// How many stored records hold <paramref name="value"/> in <paramref name="field"/>. A
    /// display read; a deletion decision must go through <see cref="ApplyTaxonomy"/>, whose
    /// recount happens under the same lock as the write it guards.
    /// </summary>
    public int CountUsing(TaxonomyField field, string value)
    {
        var state = Current();
        lock (_lock) return CountLocked(state, field, value);
    }

    private static int CountLocked(State state, TaxonomyField field, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return state.Entries.Values.Count(entry =>
            string.Equals(Taxonomy.ValueOf(entry.Manifest, field), value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Raised after a rename cascade reaches disk, naming every rename it applied.</summary>
    public event Action<IReadOnlyList<TaxonomyRename>>? ValuesRenamed;

    /// <summary>
    /// Applies a whole taxonomy edit — dropped-value refusals, the rename cascade over stored
    /// records, and the caller's own list write — under ONE lock. A count read before this call
    /// answers a question about a store a concurrent manifest save may since have changed; here
    /// the recount, the cascade, and <paramref name="commitLists"/> run with every other write
    /// held out, so a value counted unused cannot gain a user before the lists stop offering it.
    ///
    /// Records are written before lists on purpose: a cascade that lands without its list write
    /// repeats harmlessly on the next apply, where the reverse order would leave stored records
    /// holding a name no list still offers.
    /// </summary>
    public TaxonomyApplyResult ApplyTaxonomy(
        IReadOnlyList<TaxonomyRename> renames,
        IReadOnlyList<TaxonomyDrop> dropped,
        Func<bool> commitLists)
    {
        var wanted = WantedRenames(renames);
        var state = Current();
        IReadOnlyList<TaxonomyRename>? applied = null;
        TaxonomyApplyResult result;
        lock (_lock)
        {
            var inUse = dropped
                .Select(d => new TaxonomyValueInUse(d.Field, d.Value, CountLocked(state, d.Field, d.Value)))
                .Where(u => u.Count > 0)
                .ToList();
            if (inUse.Count > 0)
            {
                result = new TaxonomyApplyResult(false, 0, inUse, false, false);
            }
            else
            {
                var candidate = Clone(state);
                var cascaded = CascadeLocked(candidate, wanted);
                if (cascaded > 0 && !Persist(candidate))
                {
                    result = new TaxonomyApplyResult(false, 0, [], RecordsWriteFailed: true, false);
                }
                else
                {
                    if (cascaded > 0) Adopt(state, candidate);
                    if (!commitLists())
                        result = new TaxonomyApplyResult(false, cascaded, [], false, ListsWriteFailed: true);
                    else
                        result = new TaxonomyApplyResult(true, cascaded, [], false, false);
                    if (cascaded > 0) applied = wanted;
                }
            }
        }
        // Outside the lock: a handler reading this store back must not re-enter it mid-write.
        if (applied is not null) ValuesRenamed?.Invoke(applied);
        return result;
    }

    /// <summary>
    /// Rewrites every record holding a renamed value, in one write. Returns how many fields
    /// changed, or null when the write did not reach disk — leaving the live index describing the
    /// file, as every other write here does.
    ///
    /// A rename is not a delete and an add: dropping the value would leave every record holding a
    /// string no list still names, which is the orphan this exists to prevent.
    ///
    /// Every rename is matched against the values as they were read, not against the ones the
    /// pass has already written. Two values that trade names would otherwise both end up as the
    /// second one, the first rename having renamed the records the second then matched.
    /// </summary>
    public int? RenameValues(IReadOnlyList<TaxonomyRename> renames)
    {
        var wanted = WantedRenames(renames);
        if (wanted.Count == 0) return 0;

        var state = Current();
        lock (_lock)
        {
            var candidate = Clone(state);
            var changed = CascadeLocked(candidate, wanted);
            if (changed == 0) return 0;
            if (!Persist(candidate)) return null;
            Adopt(state, candidate);
            return changed;
        }
    }

    private static List<TaxonomyRename> WantedRenames(IReadOnlyList<TaxonomyRename> renames) =>
        [.. renames
            .Where(r => r.From.Trim().Length > 0 && r.To.Trim().Length > 0)
            .Where(r => !string.Equals(r.From, r.To, StringComparison.Ordinal))];

    private static int CascadeLocked(State candidate, IReadOnlyList<TaxonomyRename> wanted)
    {
        var changed = 0;
        foreach (var entry in candidate.Entries.Values)
        {
            var before = Taxonomy.Fields.ToDictionary(f => f, f => Taxonomy.ValueOf(entry.Manifest, f));
            foreach (var rename in wanted)
            {
                if (!string.Equals(before[rename.Field], rename.From, StringComparison.OrdinalIgnoreCase)) continue;
                Taxonomy.SetValue(entry.Manifest, rename.Field, rename.To);
                changed++;
            }
        }
        return changed;
    }

    private static State Clone(State state)
    {
        var entries = Empty<ManifestEntry>();
        foreach (var (key, entry) in state.Entries) entries[key] = entry.Copy();

        var forwards = Empty<ManifestForward>();
        foreach (var (key, forward) in state.Forwards) forwards[key] = forward.Copy();

        return new State(entries, forwards);
    }

    private static void Adopt(State live, State candidate)
    {
        live.Entries.Clear();
        foreach (var (key, entry) in candidate.Entries) live.Entries[key] = entry;

        live.Forwards.Clear();
        foreach (var (key, forward) in candidate.Forwards) live.Forwards[key] = forward;
    }

    private static bool Persist(State state)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var document = new ManifestDocument
            {
                SchemaVersion = SchemaVersion,
                Entries = state.Entries,
                Forwards = state.Forwards,
            };
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
        public Dictionary<string, ManifestForward> Forwards { get; set; } = [];
    }
}
