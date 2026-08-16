using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Windows;

/// <summary>One column checkbox in the export dialog.</summary>
public sealed partial class ExportColumnRow : ObservableObject
{
    public ExportColumnRow(PortfolioColumn column, bool selected, Action changed)
    {
        Column = column;
        _selected = selected;
        _changed = changed;
    }

    public PortfolioColumn Column { get; }
    private readonly Action _changed;

    public string Key => Column.Key;

    [ObservableProperty] private bool _selected;

    partial void OnSelectedChanged(bool value) => _changed();
}

/// <summary>
/// The export dialog's state: column checklist, path mode, filters, and a live preview. The
/// preview and every summary line are recomputed whole from the current choices on any toggle —
/// never patched incrementally — so no combination of clicks can leave the preview describing a
/// mix of old and new choices.
/// </summary>
public partial class ExportDialogViewModel : ObservableObject
{
    private readonly IReadOnlyList<ProjectInfo> _all;
    private readonly IReadOnlyList<ProjectInfo> _filteredView;

    public ExportDialogViewModel(
        IReadOnlyList<ProjectInfo> allProjects,
        IReadOnlyList<ProjectInfo> currentView,
        TaxonomyConfig taxonomy,
        ExportPreferences? remembered)
    {
        _all = allProjects;
        _filteredView = currentView;

        TypeChoices = FilterChoices(taxonomy, TaxonomyField.Type);
        StatusChoices = FilterChoices(taxonomy, TaxonomyField.Status);
        CategoryChoices = FilterChoices(taxonomy, TaxonomyField.Category);

        var rememberedKeys = (remembered?.Columns ?? [])
            .Where(key => PortfolioExport.Registry.Any(c => c.Key == key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var column in PortfolioExport.Registry)
            Columns.Add(new ExportColumnRow(
                column,
                rememberedKeys.Count > 0 ? rememberedKeys.Contains(column.Key) : column.DefaultOn,
                Recalculate));

        PathMode = Enum.TryParse<ExportPathMode>(remembered?.PathMode, out var mode)
            ? mode
            : ExportPathMode.FolderName;
        _excludeHidden = remembered?.ExcludeHidden ?? false;
        _excludeRemoteOnly = remembered?.ExcludeRemoteOnly ?? false;
        _currentViewOnly = remembered?.CurrentViewOnly ?? false;
        _visibilityFilter = remembered?.VisibilityFilter ?? "";
        _typeFilter = Existing(TypeChoices, remembered?.TypeFilter);
        _statusFilter = Existing(StatusChoices, remembered?.StatusFilter);
        _categoryFilter = Existing(CategoryChoices, remembered?.CategoryFilter);

        Recalculate();
    }

    /// <summary>A remembered filter value whose taxonomy entry is gone falls back to "all".</summary>
    private static string Existing(IReadOnlyList<string> choices, string? remembered) =>
        remembered is { Length: > 0 } value && choices.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value
            : "";

    /// <summary>"" is the no-filter choice, shown as "All" by the dialog.</summary>
    private static List<string> FilterChoices(TaxonomyConfig taxonomy, TaxonomyField field) =>
        ["", .. Taxonomy.Entries(taxonomy, field).Select(e => e.Name)];

    public ObservableCollection<ExportColumnRow> Columns { get; } = [];

    public IReadOnlyList<string> TypeChoices { get; }
    public IReadOnlyList<string> StatusChoices { get; }
    public IReadOnlyList<string> CategoryChoices { get; }
    public IReadOnlyList<string> VisibilityChoices { get; } = ["", "public", "private", "local", "unknown"];

    [ObservableProperty] private ExportPathMode _pathMode;
    [ObservableProperty] private bool _excludeHidden;
    [ObservableProperty] private bool _excludeRemoteOnly;
    [ObservableProperty] private bool _currentViewOnly;
    [ObservableProperty] private string _visibilityFilter = "";
    [ObservableProperty] private string _typeFilter = "";
    [ObservableProperty] private string _statusFilter = "";
    [ObservableProperty] private string _categoryFilter = "";

    public bool PathModeFull
    {
        get => PathMode == ExportPathMode.Full;
        set { if (value) PathMode = ExportPathMode.Full; }
    }

    public bool PathModeFolderName
    {
        get => PathMode == ExportPathMode.FolderName;
        set { if (value) PathMode = ExportPathMode.FolderName; }
    }

    public bool PathModeOmit
    {
        get => PathMode == ExportPathMode.Omit;
        set { if (value) PathMode = ExportPathMode.Omit; }
    }

    partial void OnPathModeChanged(ExportPathMode value)
    {
        OnPropertyChanged(nameof(PathModeFull));
        OnPropertyChanged(nameof(PathModeFolderName));
        OnPropertyChanged(nameof(PathModeOmit));
        Recalculate();
    }

    partial void OnExcludeHiddenChanged(bool value) => Recalculate();
    partial void OnExcludeRemoteOnlyChanged(bool value) => Recalculate();
    partial void OnCurrentViewOnlyChanged(bool value) => Recalculate();
    partial void OnVisibilityFilterChanged(string value) => Recalculate();
    partial void OnTypeFilterChanged(string value) => Recalculate();
    partial void OnStatusFilterChanged(string value) => Recalculate();
    partial void OnCategoryFilterChanged(string value) => Recalculate();

    [RelayCommand]
    private void ResetToDefaults()
    {
        foreach (var row in Columns) row.Selected = row.Column.DefaultOn;
        PathMode = ExportPathMode.FolderName;
        ExcludeHidden = false;
        ExcludeRemoteOnly = false;
        CurrentViewOnly = false;
        VisibilityFilter = "";
        TypeFilter = "";
        StatusFilter = "";
        CategoryFilter = "";
    }

    /// <summary>The choices as an export or a saved preference reads them right now.</summary>
    public ExportChoices Choices() => new(
        [.. Columns.Where(r => r.Selected).Select(r => r.Key)],
        PathMode,
        ExcludeHidden,
        ExcludeRemoteOnly,
        VisibilityFilter,
        TypeFilter,
        StatusFilter,
        CategoryFilter);

    /// <summary>The projects this export would describe, source and filters applied.</summary>
    public List<ProjectInfo> ExportSet()
        => PortfolioExport.Filtered(CurrentViewOnly ? _filteredView : _all, Choices());

    public ExportPreferences ToPreferences()
    {
        var choices = Choices();
        return new ExportPreferences
        {
            Columns = [.. choices.ColumnKeys],
            PathMode = PathMode.ToString(),
            ExcludeHidden = ExcludeHidden,
            ExcludeRemoteOnly = ExcludeRemoteOnly,
            CurrentViewOnly = CurrentViewOnly,
            VisibilityFilter = VisibilityFilter,
            TypeFilter = TypeFilter,
            StatusFilter = StatusFilter,
            CategoryFilter = CategoryFilter,
        };
    }

    internal const int PreviewRows = 20;

    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _previewMoreNotice = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _privateWarning = "";
    [ObservableProperty] private bool _canExport;

    /// <summary>
    /// The preview is the head of the actual CSV rendering, so what the reader inspects is what
    /// a cell will really hold — redaction, escaping, and all. It truncates the ROWS SHOWN and
    /// never the file: the notice under it says so whenever anything is held back.
    /// </summary>
    private void Recalculate()
    {
        var choices = Choices();
        var set = ExportSet();
        var shown = set.Count <= PreviewRows ? set : [.. set.Take(PreviewRows)];

        PreviewText = PortfolioExport.ToCsv(shown, choices with
        {
            // The set is already filtered; re-filtering the truncated head would double-apply.
            ExcludeHidden = false,
            ExcludeRemoteOnly = false,
            VisibilityFilter = "",
            TypeFilter = "",
            StatusFilter = "",
            CategoryFilter = "",
        });
        PreviewMoreNotice = set.Count > PreviewRows
            ? $"… and {set.Count - PreviewRows} more {(set.Count - PreviewRows == 1 ? "row" : "rows")} the file will hold that this preview does not show."
            : "";

        var columns = PortfolioExport.Selected(choices).Count;
        SummaryText =
            $"{set.Count} {(set.Count == 1 ? "project" : "projects")}, {columns} {(columns == 1 ? "column" : "columns")}. "
            + "Values come from the last scan — nothing is re-read from git for the export.";

        var privateCount = set.Count(p => p.GitStatus.Visibility == "private");
        PrivateWarning = privateCount == 0
            ? ""
            : $"{privateCount} of these {(privateCount == 1 ? "project is a private repository" : "projects are private repositories")} — review your column and path choices before sharing this file.";

        CanExport = set.Count > 0 && columns > 0;
    }
}
