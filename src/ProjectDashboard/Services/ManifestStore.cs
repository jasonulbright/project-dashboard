using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Stores per-project manifests in a single path-keyed index that lives OUTSIDE
/// project source trees, as manifests.json under AppPaths.RoamingDir.
///
/// Shape: { "C:\\projects\\tabkit": { ...ProjectManifest... }, ... }
/// Keys are full repo paths, compared case-insensitively (Windows). Path-keying
/// avoids name collisions between repos that share a folder name.
/// </summary>
public class ManifestStore
{
    // Roaming: durable user state (not cache). Cache stays in the local dir.
    private static readonly string StoreDir = AppPaths.RoamingDir;

    private static readonly string IndexPath = AppPaths.ManifestIndexFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private Dictionary<string, ProjectManifest>? _index;

    private static string NormalizeKey(string repoPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));

    private Dictionary<string, ProjectManifest> Index()
    {
        lock (_lock)
        {
            if (_index is not null) return _index;

            try
            {
                // Corrupt-index handling (quarantine + .bak recovery) lives in
                // DurableJsonFile.Read; null here means a legitimately fresh start.
                var data = DurableJsonFile.Read<Dictionary<string, ProjectManifest>>(IndexPath, JsonOptions);
                _index = new Dictionary<string, ProjectManifest>(
                    data ?? new Dictionary<string, ProjectManifest>(),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // Unreadable (not corrupt) index: start empty rather than crash. The
                // atomic write path keeps a .bak, so the next Save cannot destroy the
                // only copy the way an in-place truncating write could.
                Log.Error($"Failed to read manifest index at {IndexPath} — starting empty", ex);
                _index = new Dictionary<string, ProjectManifest>(StringComparer.OrdinalIgnoreCase);
            }

            return _index;
        }
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
                manifest = Clone(stored);
                return true;
            }
            manifest = null;
            return false;
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
            var stored = Clone(manifest);
            // The candidate is written before it is adopted, so a failed write leaves the live
            // index exactly as the file on disk still describes it.
            var candidate = new Dictionary<string, ProjectManifest>(index, StringComparer.OrdinalIgnoreCase)
            {
                [key] = stored
            };
            if (!Persist(candidate)) return false;
            index[key] = stored;
            return true;
        }
    }

    private static ProjectManifest Clone(ProjectManifest m) => new()
    {
        Description = m.Description,
        ProjectType = m.ProjectType,
        Status = m.Status,
        Category = m.Category,
        ValidationSchedule = m.ValidationSchedule,
        Notes = m.Notes
    };

    private static bool Persist(Dictionary<string, ProjectManifest> index)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            DurableJsonFile.Write(IndexPath, JsonSerializer.Serialize(index, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to persist manifest index to {IndexPath}", ex);
            return false;
        }
    }
}
