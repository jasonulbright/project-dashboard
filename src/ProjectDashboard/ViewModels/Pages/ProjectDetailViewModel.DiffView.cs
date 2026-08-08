using ProjectDashboard.Models;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The diff pane's layout (X-01). Side-by-side is a second rendering of the rows the unified
/// pane already holds — no second git call and no second parser — so both modes name the same
/// hunks, and every hunk action works unchanged in either.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Two-column rendering, persisted so the choice outlives the session.</summary>
    [ObservableProperty] private bool _diffSideBySide;

    /// <summary>The rows the two-column panes render. Empty while the unified mode is on.</summary>
    [ObservableProperty] private ObservableCollection<SideBySideRow> _diffRows = [];
    [ObservableProperty] private ObservableCollection<SideBySideRow> _commitDiffRows = [];

    /// <summary>The two-column pane's selected row, kept in step with <see cref="SelectedDiffLine"/>.</summary>
    [ObservableProperty] private SideBySideRow? _selectedDiffRow;

    /// <summary>Bound by the pane that is not showing, which has no inverse-boolean converter.</summary>
    public bool DiffUnified => !DiffSideBySide;

    public string DiffLayoutLabel => DiffSideBySide ? "Unified view" : "Side-by-side view";

    /// <summary>
    /// Guards the two directions of one selection. The row and the line are the same
    /// selection told two ways, and each setter would otherwise re-enter the other.
    /// </summary>
    private bool _syncingDiffSelection;

    [RelayCommand]
    private void ToggleDiffLayout() => ApplyDiffLayout(!DiffSideBySide);

    /// <summary>
    /// Switches the layout and persists it. The write is a read-modify-write of the file, so a
    /// setting another surface changed meanwhile is not clobbered, and a write that failed says
    /// so rather than leaving the reader with a choice that silently reverts on the next launch.
    /// </summary>
    internal void ApplyDiffLayout(bool sideBySide)
    {
        DiffSideBySide = sideBySide;
        if (_settingsService is null) return;

        var settings = _settingsService.Load();
        if (settings.DiffSideBySide == sideBySide) return;
        settings.DiffSideBySide = sideBySide;
        if (!_settingsService.Save(settings))
            SyncStatusText = "Diff layout not saved — it reverts on the next launch.";
    }

    /// <summary>Re-reads the persisted layout. The single live-apply path for it.</summary>
    internal void RefreshDiffLayout() => DiffSideBySide = ReadDiffSideBySide();

    internal virtual bool ReadDiffSideBySide() => _settingsService?.Load().DiffSideBySide ?? false;

    private void OnSettingsChangedForDiffLayout(SettingsChange change)
    {
        if (SettingsDelta.DiffLayoutChanged(change)) DiffSideBySide = change.Current.DiffSideBySide;
    }

    partial void OnDiffSideBySideChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffUnified));
        OnPropertyChanged(nameof(DiffLayoutLabel));
        RebuildDiffRows();
        RebuildCommitDiffRows();
    }

    partial void OnDiffLinesChanged(ObservableCollection<DiffLine> value) => RebuildDiffRows();

    partial void OnCommitDiffLinesChanged(ObservableCollection<DiffLine> value) => RebuildCommitDiffRows();

    /// <summary>
    /// Built only for the mode that renders them: a reader who never leaves the unified pane
    /// pays nothing for the second rendering on every diff load.
    /// </summary>
    private void RebuildDiffRows()
    {
        DiffRows = DiffSideBySide ? new ObservableCollection<SideBySideRow>(SideBySideDiff.Build(DiffLines)) : [];
        SyncSelectedDiffRow();
    }

    private void RebuildCommitDiffRows() =>
        CommitDiffRows = DiffSideBySide
            ? new ObservableCollection<SideBySideRow>(SideBySideDiff.Build(CommitDiffLines))
            : [];

    partial void OnSelectedDiffLineChanged(DiffLine? value)
    {
        if (_syncingDiffSelection) return;
        SyncSelectedDiffRow();
    }

    partial void OnSelectedDiffRowChanged(SideBySideRow? value)
    {
        if (_syncingDiffSelection) return;
        _syncingDiffSelection = true;
        // The row's own line, so the hunk gates and the patch slice read the index they
        // would have read from the unified row the reader clicked instead.
        try { SelectedDiffLine = value?.Source; }
        finally { _syncingDiffSelection = false; }
    }

    private void SyncSelectedDiffRow()
    {
        _syncingDiffSelection = true;
        try
        {
            SelectedDiffRow = SelectedDiffLine is null
                ? null
                : DiffRows.FirstOrDefault(r => r.Covers(SelectedDiffLine));
        }
        finally { _syncingDiffSelection = false; }
    }
}
