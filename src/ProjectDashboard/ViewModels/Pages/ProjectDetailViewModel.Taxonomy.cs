using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The manifest editor's four pickers. Their contents are the reader's own lists, re-read on
/// every settings write rather than held from construction, so a value renamed in Settings
/// reaches a page already on screen.
/// </summary>
public partial class ProjectDetailViewModel
{
    public ObservableCollection<string> ProjectTypes { get; } = [];
    public ObservableCollection<string> Statuses { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<string> Schedules { get; } = [];

    /// <summary>
    /// Names the fields whose stored value no list holds, or empty when every value is known.
    /// A picker showing such a value gives no sign that it is unknown, and silence there reads
    /// as a list that contains it.
    /// </summary>
    [ObservableProperty] private string _offListNotice = "";

    private TaxonomyConfig CurrentTaxonomy() =>
        _settingsService?.Load().Taxonomy ?? Taxonomy.Seed();

    /// <summary>
    /// Rebuilds every picker around the values currently selected. Called after the editor takes
    /// a project's stored values and after any settings write: both can leave a picker whose list
    /// does not hold what is selected, and a combo box in that state shows nothing selected.
    /// </summary>
    internal void RefreshTaxonomyChoices()
    {
        var config = CurrentTaxonomy();

        Fill(ProjectTypes, Taxonomy.Choices(config, TaxonomyField.Type, SelectedProjectType));
        Fill(Statuses, Taxonomy.Choices(config, TaxonomyField.Status, SelectedStatus));
        Fill(Categories, Taxonomy.Choices(config, TaxonomyField.Category, SelectedCategory));
        Fill(Schedules, Taxonomy.Choices(config, TaxonomyField.Schedule, ValidationSchedule));

        OffListNotice = DescribeOffList(config,
            (TaxonomyField.Type, SelectedProjectType),
            (TaxonomyField.Status, SelectedStatus),
            (TaxonomyField.Category, SelectedCategory),
            (TaxonomyField.Schedule, ValidationSchedule));
    }

    /// <summary>
    /// Replaces the contents in place. A fresh collection would drop the combo box's binding to
    /// the old one and clear a selection the reader has not changed.
    /// </summary>
    private static void Fill(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        if (target.SequenceEqual(values, StringComparer.Ordinal)) return;
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    internal static string DescribeOffList(TaxonomyConfig config, params (TaxonomyField Field, string Value)[] selected)
    {
        var unknown = selected
            .Where(s => s.Value.Trim().Length > 0 && Taxonomy.Find(config, s.Field, s.Value) is null)
            .Select(s => $"{Taxonomy.Label(s.Field)} \"{s.Value.Trim()}\"")
            .ToList();

        if (unknown.Count == 0) return "";
        return unknown.Count == 1
            ? $"This project's {unknown[0]} is not in your metadata lists. It is kept as it is — add it in Settings to reuse it."
            : $"These values are not in your metadata lists: {string.Join(", ", unknown)}. They are kept as they are — add them in Settings to reuse them.";
    }

    private void OnSettingsChangedForTaxonomy(SettingsChange change)
    {
        if (SettingsDelta.TaxonomyChanged(change)) RefreshTaxonomyChoices();
    }

    /// <summary>
    /// Carries a rename cascade onto the selections this page is already holding. The store
    /// rewrote its records, but these fields were read before that — left as they were, the next
    /// Save would write the old names back over the cascade, and the reader would see a rename
    /// they applied in Settings quietly undone by a page they never edited.
    /// </summary>
    internal void OnTaxonomyValuesRenamed(IReadOnlyList<TaxonomyRename> renames)
    {
        var before = new Dictionary<TaxonomyField, string>
        {
            [TaxonomyField.Type] = SelectedProjectType,
            [TaxonomyField.Status] = SelectedStatus,
            [TaxonomyField.Category] = SelectedCategory,
            [TaxonomyField.Schedule] = ValidationSchedule,
        };
        foreach (var rename in renames)
        {
            // Matched against the values as they were read: two values trading names must swap.
            if (!string.Equals(before[rename.Field], rename.From, StringComparison.OrdinalIgnoreCase)) continue;
            switch (rename.Field)
            {
                case TaxonomyField.Type: SelectedProjectType = rename.To; break;
                case TaxonomyField.Status: SelectedStatus = rename.To; break;
                case TaxonomyField.Category: SelectedCategory = rename.To; break;
                default: ValidationSchedule = rename.To; break;
            }
            Taxonomy.SetValue(_manifestBaseline, rename.Field, rename.To);
        }
        RefreshTaxonomyChoices();
    }
}
