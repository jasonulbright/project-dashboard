using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The saved-metadata section on the Settings page. Descriptions, categories, statuses and notes
/// are typed by hand and stored outside repositories, so this page is where a reader sees how
/// many records exist, which of them name a folder that is no longer there, and — only by asking
/// — drops one. Nothing here deletes a record on its own.
/// </summary>
public partial class SettingsViewModel
{
    public ObservableCollection<ProjectMetadataRow> MetadataOrphans { get; } = [];

    /// <summary>How many repositories carry saved metadata, and how many records name no folder.</summary>
    [ObservableProperty] private string _metadataSummary = "";

    /// <summary>What the last forget did, or why the list cannot be shown yet.</summary>
    [ObservableProperty] private string _metadataStatus = "";

    public bool HasMetadataOrphans => MetadataOrphans.Count > 0;

    /// <summary>
    /// The orphan scan this page last started. Held so a caller can wait for the read rather than
    /// poll what it writes: an empty list before the read starts and an empty list after it
    /// finishes say different things.
    /// </summary>
    internal Task MetadataLoad { get; private set; } = Task.CompletedTask;

    private void LoadMetadata() => MetadataLoad = LoadMetadataAsync();

    /// <summary>
    /// Off the dispatcher: classifying a record needs an existence probe per stored path, and a
    /// path on a share that has gone away blocks for that share's own timeout.
    /// </summary>
    private async Task LoadMetadataAsync()
    {
        var stored = _manifests.Snapshot();

        // Read from the scan rather than probed here: which folder a record sits under decides
        // whether it can be called gone at all, and a folder on a share that has dropped answers
        // an existence probe only after that share's own timeout.
        IReadOnlyList<RootStatus> roots = _discovery?.LastRootStatuses
            ?? (_dashboardViewModel is { } dashboard ? dashboard.RootStatuses : []);
        IEnumerable<string> live = _discovery is { } discovery ? discovery.LastFingerprints.Keys : [];

        var orphans = await Task.Run(() => ManifestIdentity.Orphans(stored, live, roots));

        MetadataOrphans.Clear();
        foreach (var orphan in orphans) MetadataOrphans.Add(ProjectMetadataRow.From(orphan));

        MetadataSummary = DescribeMetadata(stored.Count, orphans.Count, roots.Count);
        OnPropertyChanged(nameof(HasMetadataOrphans));
    }

    /// <summary>
    /// The counts, and what they are not: before a scan has reported on the folders, no record
    /// can be said to name a folder that is gone, and claiming zero would read as a clean bill.
    /// </summary>
    internal static string DescribeMetadata(int stored, int orphans, int scannedRoots)
    {
        var held = stored == 1
            ? "1 project has saved metadata."
            : $"{stored} projects have saved metadata.";

        if (scannedRoots == 0) return $"{held} No scan has reported yet, so none of it has been checked against your folders.";
        if (orphans == 0) return $"{held} Every record names a folder that is still there.";
        return orphans == 1
            ? $"{held} 1 record names a folder that is no longer there."
            : $"{held} {orphans} records name folders that are no longer there.";
    }

    [RelayCommand]
    private async Task ForgetMetadata(ProjectMetadataRow? row)
    {
        if (row is null) return;

        if (!_manifests.Forget([row.Path]))
        {
            MetadataStatus = $"Could not forget {row.Name} — the metadata file could not be written. See the log for details.";
            return;
        }

        MetadataStatus = $"Forgot the saved metadata for {row.Path}.";
        await LoadMetadataAsync();
    }

    [RelayCommand]
    private async Task ForgetAllMetadata()
    {
        var paths = MetadataOrphans.Select(r => r.Path).ToList();
        if (paths.Count == 0) return;

        if (!_manifests.Forget(paths))
        {
            MetadataStatus = "Could not forget those records — the metadata file could not be written. See the log for details.";
            return;
        }

        MetadataStatus = paths.Count == 1
            ? "Forgot 1 record."
            : $"Forgot {paths.Count} records.";
        await LoadMetadataAsync();
    }
}

/// <summary>One saved record whose folder the last scan did not find.</summary>
public sealed class ProjectMetadataRow
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The record's own description, shortened. Empty when it carries none.</summary>
    public string Description { get; init; } = "";

    public string LastSeen { get; init; } = "";

    internal const int DescriptionLength = 90;

    public static ProjectMetadataRow From(ManifestOrphan orphan) => new()
    {
        Path = orphan.Path,
        Name = orphan.Name,
        Description = Shorten(orphan.Description),
        LastSeen = orphan.LastSeenUtc is { } seen
            ? $"Last seen {seen.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "Not seen by a scan on this machine",
    };

    private static string Shorten(string description)
    {
        var text = description.Trim();
        return text.Length <= DescriptionLength ? text : text[..DescriptionLength].TrimEnd() + "…";
    }
}
