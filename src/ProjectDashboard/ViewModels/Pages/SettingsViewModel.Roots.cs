using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The projects-folder list on the Settings page. Order is meaningful — it is the tie-break
/// between two folders holding a repository of the same name — so the rows are reorderable and
/// the list is written back in the order shown.
///
/// Every edit here is a pending edit until Save; the page's Save is the single write, and the
/// live-apply table is what turns it into a re-scan and a re-pointed watcher.
/// </summary>
public partial class SettingsViewModel
{
    public ObservableCollection<ProjectRootRow> ProjectRoots { get; } = [];

    /// <summary>What the last add, move, or removal did, or why it was refused.</summary>
    [ObservableProperty] private string _rootsStatus = "";

    public bool HasNoRoots => ProjectRoots.Count == 0;

    private void LoadRoots(AppSettings settings)
    {
        ProjectRoots.Clear();
        foreach (var root in ProjectRootSettings.Effective(settings))
            ProjectRoots.Add(ProjectRootRow.From(root, isDefault: RepoPaths.Equal(root.Path, settings.DefaultRootPath)));

        EnsureOneDefault();
        ApplyRootStatuses();
        OnPropertyChanged(nameof(HasNoRoots));
    }

    /// <summary>
    /// Copies the last scan's per-root outcome onto the rows. A row the scan has not reported on
    /// says so rather than claiming the folder is fine.
    /// </summary>
    private void ApplyRootStatuses()
    {
        IReadOnlyList<RootStatus> statuses = _dashboardViewModel is { } dashboard ? dashboard.RootStatuses : [];
        foreach (var row in ProjectRoots)
            row.ApplyStatus(statuses.FirstOrDefault(s => RepoPaths.Equal(s.Path, row.Path)));
    }

    private ProjectRoot[] RootsFromRows() => [.. ProjectRoots.Select(r => r.ToRoot())];

    private string DefaultRootFromRows() =>
        ProjectRoots.FirstOrDefault(r => r.IsDefault)?.Path ?? ProjectRoots.FirstOrDefault()?.Path ?? "";

    /// <summary>
    /// One row is the default, always, while any row exists — New Project and Clone need a single
    /// destination, and a list with no default would refuse both for no reason the user chose.
    /// </summary>
    private void EnsureOneDefault()
    {
        if (ProjectRoots.Count == 0) return;
        if (ProjectRoots.Count(r => r.IsDefault) == 1) return;

        var chosen = ProjectRoots.FirstOrDefault(r => r.IsDefault && r.Enabled)
            ?? ProjectRoots.FirstOrDefault(r => r.IsDefault)
            ?? ProjectRoots.FirstOrDefault(r => r.Enabled)
            ?? ProjectRoots[0];

        foreach (var row in ProjectRoots) row.IsDefault = ReferenceEquals(row, chosen);
    }

    [RelayCommand]
    private void AddRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add a projects folder",
            InitialDirectory = ProjectRoots.FirstOrDefault()?.Path ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        AddRootPath(dialog.FolderName);
    }

    /// <summary>
    /// Adds a folder, or refuses it by name. A folder already listed, or one that nests with a
    /// listed folder, would have the scan walk the same tree twice and leave the reader guessing
    /// which row a card came from.
    /// </summary>
    internal void AddRootPath(string folder)
    {
        var path = RepoPaths.Normalize(folder);
        if (path.Length == 0)
        {
            RootsStatus = "That folder has no usable path.";
            return;
        }

        foreach (var existing in ProjectRoots)
        {
            if (RepoPaths.Equal(existing.Path, path))
            {
                RootsStatus = $"{path} is already in the list.";
                return;
            }
            if (RepoPaths.IsAtOrUnder(path, existing.Path))
            {
                RootsStatus = $"{path} is inside {existing.Path}, which is already in the list.";
                return;
            }
            if (RepoPaths.IsAtOrUnder(existing.Path, path))
            {
                RootsStatus = $"{path} contains {existing.Path}, which is already in the list.";
                return;
            }
        }

        ProjectRoots.Add(ProjectRootRow.From(new ProjectRoot { Path = path }, isDefault: ProjectRoots.Count == 0));
        EnsureOneDefault();
        ApplyRootStatuses();
        OnPropertyChanged(nameof(HasNoRoots));
        RootsStatus = $"Added {path}. Save to scan it.";
    }

    [RelayCommand]
    private void RemoveRoot(ProjectRootRow? row)
    {
        if (row is null || !ProjectRoots.Remove(row)) return;

        EnsureOneDefault();
        OnPropertyChanged(nameof(HasNoRoots));
        RootsStatus = ProjectRoots.Count == 0
            ? $"Removed {row.Path}. No projects folder is left — the dashboard has nothing to scan."
            : $"Removed {row.Path}. Save to drop its cards.";
    }

    [RelayCommand]
    private void MoveRootUp(ProjectRootRow? row) => MoveRoot(row, -1);

    [RelayCommand]
    private void MoveRootDown(ProjectRootRow? row) => MoveRoot(row, +1);

    private void MoveRoot(ProjectRootRow? row, int offset)
    {
        if (row is null) return;
        var from = ProjectRoots.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= ProjectRoots.Count) return;

        ProjectRoots.Move(from, to);
        RootsStatus = $"{row.Path} is now folder {to + 1} of {ProjectRoots.Count}. Save to apply the new order.";
    }

    [RelayCommand]
    private void MakeDefaultRoot(ProjectRootRow? row)
    {
        if (row is null || !ProjectRoots.Contains(row)) return;

        foreach (var other in ProjectRoots) other.IsDefault = ReferenceEquals(other, row);
        RootsStatus = $"New projects and clones will go in {row.Path}.";
    }

    [RelayCommand]
    private void BrowseRootFolder(ProjectRootRow? row)
    {
        if (row is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose this projects folder",
            InitialDirectory = Directory.Exists(row.Path) ? row.Path : "",
        };
        if (dialog.ShowDialog() != true) return;

        row.Path = RepoPaths.Normalize(dialog.FolderName);
        RootsStatus = $"Folder set to {row.Path}. Save to scan it.";
    }
}

/// <summary>One editable projects folder on the Settings page.</summary>
public partial class ProjectRootRow : ObservableObject
{
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _isDefault;

    /// <summary>Comma-separated, as typed. Names match at any depth; paths match one place.</summary>
    [ObservableProperty] private string _excludedDirectories = "";

    [ObservableProperty] private int _scanDepth = ProjectRootSettings.MinDepth;

    /// <summary>What the last scan found this folder to be; empty until one has reported.</summary>
    [ObservableProperty] private string _status = NotScannedYet;

    internal const string NotScannedYet = "Not scanned yet";

    /// <summary>
    /// The depths the page offers, one entry per level the walk will accept. Exposed per row
    /// because a row is what a data template binds against.
    /// </summary>
    public IReadOnlyList<ScanDepthChoice> DepthChoices => Depths;

    private static readonly ScanDepthChoice[] Depths =
    [
        new(1, "Top level only"),
        new(2, "2 levels"),
        new(3, "3 levels"),
        new(4, "4 levels"),
    ];

    public static ProjectRootRow From(ProjectRoot root, bool isDefault) => new()
    {
        Path = root.Path,
        Label = root.Label,
        Enabled = root.Enabled,
        ExcludedDirectories = string.Join(", ", root.ExcludedDirectories),
        ScanDepth = ProjectRootSettings.ClampDepth(root.MaxDepth),
        IsDefault = isDefault,
    };

    public ProjectRoot ToRoot() => new()
    {
        Path = Path.Trim(),
        Label = Label.Trim(),
        Enabled = Enabled,
        ExcludedDirectories = ProjectRootSettings.CleanExclusions(
            ExcludedDirectories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        MaxDepth = ProjectRootSettings.ClampDepth(ScanDepth),
    };

    internal void ApplyStatus(RootStatus? status)
    {
        if (!Enabled) { Status = "Off"; return; }
        if (status is null) { Status = NotScannedYet; return; }

        Status = status.Availability switch
        {
            RootAvailability.Missing => "Not there",
            RootAvailability.Unreadable => "Could not be read",
            RootAvailability.Disabled => "Off",
            _ => status.Truncated
                ? $"{status.RepositoryCount}+ repositories — the scan stopped early"
                : $"{status.RepositoryCount} repositories",
        };
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!value) Status = "Off";
    }
}

/// <summary>A scan-depth option, with the wording the page shows for it.</summary>
public sealed record ScanDepthChoice(int Value, string Label);
