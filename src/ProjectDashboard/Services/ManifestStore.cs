using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Crash-safe persistence for the JSON state files. A write lands in a sibling
/// .tmp that is flushed to disk and atomically swapped in (the previous version
/// survives as .bak), so an interrupted write can never truncate the live file.
/// A file that no longer parses is quarantined as .corrupt-&lt;utcstamp&gt; —
/// never discarded — and the .bak is tried before the caller falls back to
/// defaults. This is the required pattern for every durable store.
/// </summary>
internal static class DurableJsonFile
{
    public static void Write(string path, string json)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            // Force the tmp contents to disk BEFORE the swap; otherwise a power cut
            // can commit the rename while the data is still in the OS write cache.
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    /// <summary>Null means nothing usable on disk: missing file, or corrupt with no readable backup.</summary>
    public static T? Read<T>(string path, JsonSerializerOptions options) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (JsonException ex)
        {
            var quarantine = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
            Log.Error($"Corrupt JSON in {path} — quarantining as {Path.GetFileName(quarantine)}, trying .bak", ex);
            try { File.Move(path, quarantine); }
            catch (Exception moveEx) { Log.Error($"Failed to quarantine {path}", moveEx); }

            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var bakJson = File.ReadAllText(bak);
                    var recovered = JsonSerializer.Deserialize<T>(bakJson, options);
                    // Restore the live file now; recovery held only in memory would
                    // evaporate on a session that never saves, and the NEXT launch
                    // would then silently start from defaults.
                    try { Write(path, bakJson); }
                    catch (Exception writeEx) { Log.Error($"Failed to restore {path} from {bak}", writeEx); }
                    Log.Warn($"Recovered {path} from {bak}");
                    return recovered;
                }
                catch (Exception bakEx)
                {
                    Log.Error($"Backup {bak} is also unreadable", bakEx);
                }
            }

            Log.Error($"No usable backup for {path} — starting from defaults");
            return null;
        }
    }
}

/// <summary>
/// Stores per-project manifests in a single path-keyed index that lives OUTSIDE
/// project source trees, under %APPDATA%\ProjectDashboard\manifests.json.
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

    /// <summary>Upserts the manifest for a repo path and persists the whole index.</summary>
    public void Save(string repoPath, ProjectManifest manifest)
    {
        var index = Index();
        lock (_lock)
        {
            index[NormalizeKey(repoPath)] = Clone(manifest);
            Persist(index);
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

    private static void Persist(Dictionary<string, ProjectManifest> index)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            DurableJsonFile.Write(IndexPath, JsonSerializer.Serialize(index, JsonOptions));
        }
        catch (Exception ex)
        {
            // Save failure = silent data loss on next launch. At least make it diagnosable.
            Log.Error($"Failed to persist manifest index to {IndexPath}", ex);
        }
    }
}
