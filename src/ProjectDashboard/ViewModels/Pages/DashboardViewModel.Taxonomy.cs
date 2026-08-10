using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The card chips for the four metadata fields, and setting one of those fields on several
/// projects at once. Both read the reader's own lists: a chip's colour and a picker's values come
/// from the same place, so a value renamed in Settings cannot leave one of them behind.
/// </summary>
public partial class DashboardViewModel
{
    /// <summary>Whether the grid is picking cards rather than opening them.</summary>
    [ObservableProperty] private bool _isSelectionMode;

    /// <summary>How many of the cards currently on screen are ticked.</summary>
    public int SelectedCount => FilteredProjects.Count(p => p.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectionModeLabel => IsSelectionMode ? "Done" : "Select";

    /// <summary>
    /// What the selection chip says. A count alone would not say which cards it means, and the
    /// selection is deliberately only ever the cards the current filter shows.
    /// </summary>
    public string SelectionSummary => SelectedCount switch
    {
        0 => "No cards selected",
        1 => "1 card selected",
        _ => $"{SelectedCount} cards selected",
    };

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value) ClearSelectionFlags();
        OnPropertyChanged(nameof(SelectionModeLabel));
        NotifySelection();
    }

    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    /// <summary>Raised by a card's tick box once it has already written the model.</summary>
    [RelayCommand]
    private void SelectionChanged() => NotifySelection();

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var project in FilteredProjects) project.IsSelected = Selectable(project);
        NotifySelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        ClearSelectionFlags();
        NotifySelection();
    }

    private void ClearSelectionFlags()
    {
        foreach (var project in Projects) project.IsSelected = false;
        foreach (var project in _hiddenSnapshot) project.IsSelected = false;
    }

    /// <summary>
    /// A card with no working tree has no path to key a metadata record on. Ticking one would
    /// give a count the bulk edit could not act on.
    /// </summary>
    private static bool Selectable(ProjectInfo project) =>
        !project.IsRemoteOnly && project.FullPath.Length > 0;

    internal void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    /// <summary>
    /// Drops the tick from every card the current filter no longer shows. A selection is only
    /// ever what is on screen, so a card that scrolled out of the filter must not still be in the
    /// set a bulk edit writes to.
    /// </summary>
    private void DropSelectionOutsideView()
    {
        var visible = FilteredProjects.ToHashSet();
        foreach (var project in Projects)
            if (project.IsSelected && !visible.Contains(project)) project.IsSelected = false;
        foreach (var project in _hiddenSnapshot)
            if (project.IsSelected && !visible.Contains(project)) project.IsSelected = false;
        NotifySelection();
    }

    /// <summary>
    /// Carries a rename applied to the stored records onto the cards already on screen. The
    /// cascade rewrote the index, not the models the grid is holding; without this a card would
    /// keep showing the old value — marked as one no list holds — until the next scan.
    /// </summary>
    internal void ApplyTaxonomyRenames(IReadOnlyList<TaxonomyRename> renames)
    {
        if (renames.Count == 0) return;

        foreach (var project in Projects.Concat(_hiddenSnapshot))
        {
            // Matched against the values as they were read, the same way the store's own pass
            // does: two values trading names must swap rather than both become the second.
            var before = Taxonomy.Fields.ToDictionary(f => f, f => Taxonomy.ValueOf(project.Manifest, f));
            foreach (var rename in renames)
                if (string.Equals(before[rename.Field], rename.From, StringComparison.OrdinalIgnoreCase))
                    Taxonomy.SetValue(project.Manifest, rename.Field, rename.To);
        }

        ApplyTaxonomyBadges();
        RefreshCategoryChoices();
        ApplyFilters();
    }

    /// <summary>Re-resolves every card's chips against the reader's current lists.</summary>
    internal void ApplyTaxonomyBadges()
    {
        var config = _settingsService.Load().Taxonomy ?? Taxonomy.Seed();
        foreach (var project in Projects) ApplyTaxonomyBadges(project, config);
        foreach (var project in _hiddenSnapshot) ApplyTaxonomyBadges(project, config);
    }

    private static void ApplyTaxonomyBadges(ProjectInfo project, TaxonomyConfig config)
    {
        project.TypeBadge = Taxonomy.Badge(config, TaxonomyField.Type, project.Manifest.ProjectType);
        project.StatusBadge = Taxonomy.Badge(config, TaxonomyField.Status, project.Manifest.Status);
        project.CategoryBadge = Taxonomy.Badge(config, TaxonomyField.Category, project.Manifest.Category);
        project.ScheduleBadge = Taxonomy.Badge(config, TaxonomyField.Schedule, project.Manifest.ValidationSchedule);
    }

    /// <summary>What a reader picked in the bulk-edit dialog.</summary>
    internal sealed record BulkTaxonomyChoice(TaxonomyField Field, string Value);

    /// <summary>Shown when the action is offered with nothing to act on.</summary>
    internal const string NothingSelectedNotice =
        "Set metadata: tick at least one card first.";

    /// <summary>
    /// The dialog. Virtual so the apply path can be exercised without a window: the outcome
    /// reporting below is the part that has to be right, and a modal would put it out of reach.
    /// </summary>
    internal virtual async Task<BulkTaxonomyChoice?> PromptForBulkTaxonomyAsync(int cardCount)
    {
        var config = _settingsService.Load().Taxonomy ?? Taxonomy.Seed();

        var fieldPicker = new System.Windows.Controls.ComboBox
        {
            ItemsSource = Taxonomy.Fields.Select(Taxonomy.Label).ToList(),
            SelectedIndex = 2,
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(fieldPicker, "Which metadata field to set");

        var valuePicker = new System.Windows.Controls.ComboBox
        {
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(valuePicker, "What to set it to");

        void LoadValues()
        {
            var field = Taxonomy.Fields[Math.Max(0, fieldPicker.SelectedIndex)];
            valuePicker.ItemsSource = Taxonomy.Entries(config, field).Select(e => e.Name).ToList();
            valuePicker.SelectedIndex = 0;
        }
        fieldPicker.SelectionChanged += (_, _) => LoadValues();
        LoadValues();

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Set metadata on the selected projects",
            Content = new System.Windows.Controls.StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.TextBlock
                    {
                        Text = $"This writes one metadata field on {cardCount} " +
                               $"{(cardCount == 1 ? "project" : "projects")} — the cards ticked in the grid. " +
                               "Each project's other metadata is left as it is. " +
                               "Every write is reported, including any that fail.",
                        TextWrapping = System.Windows.TextWrapping.Wrap,
                        MaxWidth = 520,
                    },
                    fieldPicker,
                    valuePicker,
                },
            },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
        };

        if (await dialog.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return null;
        if (valuePicker.SelectedItem is not string value || value.Length == 0) return null;
        return new BulkTaxonomyChoice(Taxonomy.Fields[Math.Max(0, fieldPicker.SelectedIndex)], value);
    }

    [RelayCommand]
    private async Task BulkSetTaxonomy()
    {
        var targets = FilteredProjects.Where(p => p.IsSelected && Selectable(p)).ToList();
        if (targets.Count == 0)
        {
            OpStatusText = NothingSelectedNotice;
            return;
        }

        if (await PromptForBulkTaxonomyAsync(targets.Count) is not { } choice) return;

        // Claimed after the dialog, never across it: holding the gate open for as long as a modal
        // is on screen stalls every queued re-scan behind it.
        if (TryClaimBulkOp() is not { } claim) { OpStatusText = BulkOpBusyNotice; return; }
        try
        {
            OpStatusText = $"Setting {Taxonomy.Label(choice.Field)}…";
            var failures = new List<(string Name, string Reason)>();
            var written = 0;

            foreach (var project in targets)
            {
                if (_busyRegistry.IsBusy(project.FullPath))
                {
                    failures.Add((project.DisplayName, "a repository operation is running"));
                    continue;
                }

                var edited = project.Manifest.Copy();
                Taxonomy.SetValue(edited, choice.Field, choice.Value);

                if (!await _discoveryService.SaveManifestAsync(project.FullPath, edited, project.Fingerprint))
                {
                    failures.Add((project.DisplayName, "the metadata file could not be written"));
                    continue;
                }

                // Adopted only once the store reports the write durable, so a card never shows a
                // value that is not on disk.
                project.Manifest = edited;
                written++;
            }

            ApplyTaxonomyBadges();
            RefreshCategoryChoices();
            ApplyFilters();
            NotifySummary();
            OpStatusText = DescribeBulkSet(choice.Field, choice.Value, targets.Count, written, failures);
        }
        finally { ReleaseBulkOp(claim); }
    }

    /// <summary>
    /// The one line a bulk edit leaves behind. It names what was attempted, how much of it
    /// landed, and every project that did not — a tally alone would leave a reader with a partial
    /// apply they cannot act on.
    /// </summary>
    internal static string DescribeBulkSet(
        TaxonomyField field, string value, int attempted, int written,
        IReadOnlyList<(string Name, string Reason)> failures)
    {
        var head = $"Set {Taxonomy.Label(field)} to \"{value}\" on {attempted} " +
                   $"{(attempted == 1 ? "project" : "projects")} — {written} succeeded";

        if (failures.Count == 0) return head + ".";

        var listed = failures.Take(5).Select(f => $"{f.Name} ({f.Reason})");
        var tail = failures.Count > 5 ? $", and {failures.Count - 5} more" : "";
        return $"{head}, {failures.Count} failed: {string.Join(", ", listed)}{tail}.";
    }
}
