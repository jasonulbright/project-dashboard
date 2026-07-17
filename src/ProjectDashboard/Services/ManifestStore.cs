using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

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
                if (File.Exists(IndexPath))
                {
                    var json = File.ReadAllText(IndexPath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, ProjectManifest>>(json, JsonOptions);
                    _index = new Dictionary<string, ProjectManifest>(
                        data ?? new Dictionary<string, ProjectManifest>(),
                        StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _index = new Dictionary<string, ProjectManifest>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable index would make ALL metadata appear gone. Log loudly, start empty.
                Log.Error($"Failed to read manifest index at {IndexPath}", ex);
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
            var json = JsonSerializer.Serialize(index, JsonOptions);
            File.WriteAllText(IndexPath, json);
        }
        catch (Exception ex)
        {
            // Save failure = silent data loss on next launch. At least make it diagnosable.
            Log.Error($"Failed to persist manifest index to {IndexPath}", ex);
        }
    }
}
