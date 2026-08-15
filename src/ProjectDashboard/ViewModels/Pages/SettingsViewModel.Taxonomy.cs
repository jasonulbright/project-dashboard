using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The metadata-lists section: the values the four manifest pickers offer, their order, and the
/// colour each one gives a card chip.
///
/// Every edit is held here until applied as one action. A rename has to reach every stored record
/// that holds the old value, and a value in use cannot be dropped at all — both are decided
/// against the whole list at once, so a per-keystroke write would apply half a decision.
/// </summary>
public partial class SettingsViewModel
{
    public ObservableCollection<TaxonomyListEditor> TaxonomyLists { get; } = [];

    /// <summary>What the last apply did, or why it refused. Never a summary of a partial apply.</summary>
    [ObservableProperty] private string _taxonomyStatus = "";

    private void LoadTaxonomy(AppSettings settings)
    {
        var config = settings.Taxonomy ?? Taxonomy.Seed();

        TaxonomyLists.Clear();
        foreach (var field in Taxonomy.Fields)
            TaxonomyLists.Add(new TaxonomyListEditor(field, Taxonomy.Entries(config, field)));
    }

    [RelayCommand]
    private void AddTaxonomyValue(TaxonomyListEditor? editor) => editor?.Add();

    [RelayCommand]
    private void RemoveTaxonomyValue(TaxonomyRow? row) => row?.Owner.Remove(row);

    [RelayCommand]
    private void MoveTaxonomyValueUp(TaxonomyRow? row) => row?.Owner.Move(row, -1);

    [RelayCommand]
    private void MoveTaxonomyValueDown(TaxonomyRow? row) => row?.Owner.Move(row, +1);

    [RelayCommand]
    private void ResetTaxonomy()
    {
        LoadTaxonomy(_settingsService.Load());
        TaxonomyStatus = "Reloaded the saved lists; nothing was written.";
    }

    /// <summary>
    /// Applies the whole section: refuses first, then cascades renames over the stored records,
    /// then saves the lists. Nothing is written when anything is refused.
    ///
    /// Records first, lists second. A cascade that lands and a list write that does not leaves
    /// the next apply seeing the old name held by nothing, so it repeats harmlessly; the reverse
    /// order would leave stored records holding a name no list still offers.
    /// </summary>
    [RelayCommand]
    private void SaveTaxonomy()
    {
        var edited = TaxonomyLists.ToList();

        if (DescribeRefusals(edited, _manifests) is { Length: > 0 } refusal)
        {
            TaxonomyStatus = refusal;
            return;
        }

        var renames = edited
            .SelectMany(list => list.Rows
                .Where(r => r.IsRename)
                .Select(r => new TaxonomyRename(list.Field, r.OriginalName, r.Name.Trim())))
            .ToList();
        var dropped = edited
            .SelectMany(list => list.Dropped().Select(value => new TaxonomyDrop(list.Field, value)))
            .ToList();

        // One store call carries the in-use recount, the cascade, and the list write, so a
        // manifest saved between a count and the write cannot slip a value out from under its
        // own deletion. The list write runs as the store's callback for exactly that reason.
        var outcome = _manifests.ApplyTaxonomy(renames, dropped, () =>
        {
            var settings = _settingsService.Load();
            var config = settings.Taxonomy ?? new TaxonomyConfig();
            foreach (var list in edited)
                Taxonomy.Replace(config, list.Field, list.Rows.Select(r => r.ToEntry()).ToList());
            settings.Taxonomy = config;
            return _settingsService.Save(settings);
        });

        if (outcome.InUse.Count > 0)
        {
            TaxonomyStatus = DescribeInUse(outcome.InUse);
            return;
        }
        if (outcome.RecordsWriteFailed)
        {
            TaxonomyStatus = "Nothing was saved — the metadata file could not be written, so no list changed either. See the log for details.";
            return;
        }
        if (outcome.ListsWriteFailed)
        {
            TaxonomyStatus = outcome.Cascaded == 0
                ? $"Lists not saved — could not write {AppPaths.SettingsFile}. See the log for details."
                : $"Lists not saved — could not write {AppPaths.SettingsFile}. " +
                  $"{(outcome.Cascaded == 1 ? "1 stored value was" : $"{outcome.Cascaded} stored values were")} already renamed; " +
                  "applying again finishes the job.";
            return;
        }
        var cascaded = outcome.Cascaded;

        LoadTaxonomy(_settingsService.Load());
        TaxonomyStatus = DescribeApplied(renames.Count, cascaded);
    }

    internal static string DescribeApplied(int renames, int cascaded)
    {
        if (renames == 0) return "Saved the metadata lists.";
        if (cascaded == 0)
            return $"Saved the metadata lists. {(renames == 1 ? "That rename" : "Those renames")} matched no stored project.";
        return cascaded == 1
            ? "Saved the metadata lists, and renamed the value on 1 project to match."
            : $"Saved the metadata lists, and renamed the value on {cascaded} projects to match.";
    }

    /// <summary>
    /// Everything wrong with the edited lists, in one message, or empty when they can be offered
    /// to the store. Reported together rather than one at a time: a reader fixing four names
    /// should not have to press Save four times to find the fourth. The in-use counts here are
    /// advisory wording only — the count that gates the write is retaken by
    /// <see cref="ManifestStore.ApplyTaxonomy"/> under the same lock as the write itself, so a
    /// manifest saved after this read still refuses there rather than orphaning its value.
    /// </summary>
    internal static string DescribeRefusals(
        IReadOnlyList<TaxonomyListEditor> edited, ManifestStore manifests)
    {
        var problems = new List<string>();

        foreach (var list in edited)
        {
            var label = list.Heading.ToLowerInvariant();

            if (list.Rows.Count == 0)
            {
                problems.Add($"the {label} list is empty — a manifest picker with nothing in it can offer no value");
                continue;
            }

            if (list.Rows.Any(r => r.Name.Trim().Length == 0))
                problems.Add($"a {label} entry has no name");

            foreach (var duplicate in list.Rows
                         .Select(r => r.Name.Trim())
                         .Where(n => n.Length > 0)
                         .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                problems.Add($"\"{duplicate.Key}\" appears twice in the {label} list");

            foreach (var dropped in list.Dropped())
            {
                var used = manifests.CountUsing(list.Field, dropped);
                if (used == 0) continue;
                problems.Add(InUseProblem(new TaxonomyValueInUse(list.Field, dropped, used)));
            }
        }

        return problems.Count == 0
            ? ""
            : $"Nothing was saved. {string.Join("; ", problems)}.";
    }

    /// <summary>The store's refusal, in the same voice: which values are still held, and by how many.</summary>
    internal static string DescribeInUse(IReadOnlyList<TaxonomyValueInUse> inUse) =>
        $"Nothing was saved. {string.Join("; ", inUse.Select(InUseProblem))}.";

    private static string InUseProblem(TaxonomyValueInUse u) =>
        $"\"{u.Value}\" is still the {Taxonomy.Label(u.Field)} of {u.Count} " +
        $"{(u.Count == 1 ? "project" : "projects")} — set {(u.Count == 1 ? "it" : "them")} to something else first";
}

/// <summary>One of the four editable lists, with the rows a reader is arranging.</summary>
public sealed partial class TaxonomyListEditor : ObservableObject
{
    public TaxonomyField Field { get; }
    public string Heading { get; }
    public string Description { get; }

    public ObservableCollection<TaxonomyRow> Rows { get; } = [];

    /// <summary>
    /// The names this list held when it was loaded. Carried on the editor rather than inferred
    /// from the rows, so a row deleted from the collection is still known to have existed.
    /// </summary>
    private readonly List<string> _originals;

    public TaxonomyListEditor(TaxonomyField field, IReadOnlyList<TaxonomyEntry> entries)
    {
        Field = field;
        Heading = Taxonomy.Heading(field);
        Description = field switch
        {
            TaxonomyField.Type => "What kind of thing a project is.",
            TaxonomyField.Status => "Where a project stands. Shown as the coloured chip on its card.",
            TaxonomyField.Category => "How projects are grouped, and what the dashboard's category filter offers.",
            _ => "How often a project is due a check. A value set not to show draws no chip at all.",
        };
        _originals = entries.Select(e => e.Name).ToList();
        foreach (var entry in entries) Rows.Add(new TaxonomyRow(this, entry));
        Renumber();
    }

    /// <summary>
    /// The saved names no row carries any more. A row renamed is not dropped — its stored records
    /// follow it — so only a name whose row was removed outright counts.
    /// </summary>
    internal IEnumerable<string> Dropped()
    {
        var kept = Rows
            .Where(r => r.OriginalName.Length > 0)
            .Select(r => r.OriginalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _originals.Where(name => !kept.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public void Add()
    {
        Rows.Add(new TaxonomyRow(this, new TaxonomyEntry()));
        Renumber();
    }

    public void Remove(TaxonomyRow row)
    {
        Rows.Remove(row);
        Renumber();
    }

    public void Move(TaxonomyRow row, int offset)
    {
        var from = Rows.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Rows.Count) return;
        Rows.Move(from, to);
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Rows.Count; i++) Rows[i].UpdatePosition(i, Rows.Count);
    }
}

/// <summary>One editable value: its name, its chip colour, and whether a card draws it.</summary>
public sealed partial class TaxonomyRow : ObservableObject
{
    internal TaxonomyListEditor Owner { get; }

    /// <summary>The name this row was loaded with, or empty when the reader added it.</summary>
    public string OriginalName { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _color;
    [ObservableProperty] private bool _showOnCard;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    public TaxonomyRow(TaxonomyListEditor owner, TaxonomyEntry entry)
    {
        Owner = owner;
        OriginalName = entry.Name;
        _name = entry.Name;
        _color = TaxonomyPalette.Normalize(entry.Color);
        _showOnCard = entry.ShowOnCard;
    }

    private static readonly IReadOnlyList<ColorChoice> Palette =
        [.. TaxonomyPalette.Keys.Select(k => new ColorChoice(k, TaxonomyPalette.Label(k)))];

    /// <summary>
    /// The picker's items. An instance property because a row is what a template binds to, and a
    /// binding path resolves against the instance — a static one would bind to nothing.
    /// </summary>
    public IReadOnlyList<ColorChoice> ColorChoices => Palette;

    public bool IsRename =>
        OriginalName.Length > 0 && !string.Equals(OriginalName, Name.Trim(), StringComparison.Ordinal);

    /// <summary>The chip a card would draw for this row, as the reader is arranging it.</summary>
    public TaxonomyBadge Preview => new()
    {
        Text = Name.Trim().Length == 0 ? "unnamed" : Name.Trim(),
        Color = Color,
        Visible = true,
        FieldLabel = Taxonomy.Label(Owner.Field),
    };

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Preview));

    partial void OnColorChanged(string value) => OnPropertyChanged(nameof(Preview));

    internal void UpdatePosition(int index, int count)
    {
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
    }

    public TaxonomyEntry ToEntry() => new()
    {
        Name = Name.Trim(),
        Color = TaxonomyPalette.Normalize(Color),
        ShowOnCard = ShowOnCard,
    };
}

/// <summary>One entry in the colour picker: the stored key, and what it is called on screen.</summary>
public sealed record ColorChoice(string Key, string Label);
