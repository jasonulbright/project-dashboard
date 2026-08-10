using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// The allowed values behind the four manifest fields: what they start as, how a stored value is
/// read against them, and what a card draws for it.
///
/// A manifest field has never been constrained to its list — a hand-edited or imported record can
/// hold anything — so every read here treats a value outside the list as a value, not as an error
/// to correct.
/// </summary>
public static class Taxonomy
{
    /// <summary>
    /// The lists as they were compiled into the manifest editor before they became editable.
    /// Seeding from these makes the first load a no-op for every project already using one.
    /// </summary>
    public static TaxonomyConfig Seed() => new()
    {
        Types =
        [
            Entry("mecm-tool"), Entry("powershell-script"), Entry("web-app"), Entry("game"),
            Entry("framework"), Entry("library"), Entry("dashboard"), Entry("unknown"),
        ],
        Statuses =
        [
            Entry("active", TaxonomyPalette.Good),
            Entry("maintenance", TaxonomyPalette.Warn),
            Entry("archived", TaxonomyPalette.Neutral),
            Entry("experimental", TaxonomyPalette.Accent),
        ],
        Categories =
        [
            Entry("MECM"), Entry("Web"), Entry("Games"),
            Entry("Infrastructure"), Entry("Utilities"), Entry("Uncategorized"),
        ],
        Schedules =
        [
            Entry("none", TaxonomyPalette.None, showOnCard: false),
            Entry("daily", TaxonomyPalette.Bad),
            Entry("weekly", TaxonomyPalette.Info),
            Entry("monthly", TaxonomyPalette.Info),
        ],
    };

    private static TaxonomyEntry Entry(string name, string color = TaxonomyPalette.None, bool showOnCard = true) =>
        new() { Name = name, Color = color, ShowOnCard = showOnCard };

    public static readonly IReadOnlyList<TaxonomyField> Fields =
        [TaxonomyField.Type, TaxonomyField.Status, TaxonomyField.Category, TaxonomyField.Schedule];

    /// <summary>What a reader is told the field is called, in a sentence.</summary>
    public static string Label(TaxonomyField field) => field switch
    {
        TaxonomyField.Type => "type",
        TaxonomyField.Status => "status",
        TaxonomyField.Category => "category",
        _ => "validation schedule",
    };

    /// <summary>The section heading for one list.</summary>
    public static string Heading(TaxonomyField field) => field switch
    {
        TaxonomyField.Type => "Types",
        TaxonomyField.Status => "Statuses",
        TaxonomyField.Category => "Categories",
        _ => "Validation schedules",
    };

    /// <summary>
    /// Brings a settings object with no taxonomy of its own up to one, in memory. An empty list
    /// is the shape of a file written before the lists existed; a reader who deletes every entry
    /// of one list gets it seeded again rather than an editor with nothing to pick from.
    /// </summary>
    public static void EnsureSeeded(AppSettings settings)
    {
        settings.Taxonomy ??= new TaxonomyConfig();
        var seed = Seed();

        if (settings.Taxonomy.Types.Count == 0) settings.Taxonomy.Types = seed.Types;
        if (settings.Taxonomy.Statuses.Count == 0) settings.Taxonomy.Statuses = seed.Statuses;
        if (settings.Taxonomy.Categories.Count == 0) settings.Taxonomy.Categories = seed.Categories;
        if (settings.Taxonomy.Schedules.Count == 0) settings.Taxonomy.Schedules = seed.Schedules;
    }

    public static List<TaxonomyEntry> Entries(TaxonomyConfig config, TaxonomyField field) => field switch
    {
        TaxonomyField.Type => config.Types,
        TaxonomyField.Status => config.Statuses,
        TaxonomyField.Category => config.Categories,
        _ => config.Schedules,
    };

    public static void Replace(TaxonomyConfig config, TaxonomyField field, List<TaxonomyEntry> entries)
    {
        switch (field)
        {
            case TaxonomyField.Type: config.Types = entries; break;
            case TaxonomyField.Status: config.Statuses = entries; break;
            case TaxonomyField.Category: config.Categories = entries; break;
            default: config.Schedules = entries; break;
        }
    }

    public static string ValueOf(ProjectManifest manifest, TaxonomyField field) => field switch
    {
        TaxonomyField.Type => manifest.ProjectType,
        TaxonomyField.Status => manifest.Status,
        TaxonomyField.Category => manifest.Category,
        _ => manifest.ValidationSchedule,
    };

    public static void SetValue(ProjectManifest manifest, TaxonomyField field, string value)
    {
        switch (field)
        {
            case TaxonomyField.Type: manifest.ProjectType = value; break;
            case TaxonomyField.Status: manifest.Status = value; break;
            case TaxonomyField.Category: manifest.Category = value; break;
            default: manifest.ValidationSchedule = value; break;
        }
    }

    public static TaxonomyEntry? Find(TaxonomyConfig config, TaxonomyField field, string value) =>
        Entries(config, field).FirstOrDefault(e => string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The chip for one stored value. A value the list does not hold keeps its own text, draws
    /// untinted, and is marked as off-list; it is never mapped onto the nearest entry.
    /// </summary>
    public static TaxonomyBadge Badge(TaxonomyConfig config, TaxonomyField field, string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return TaxonomyBadge.Hidden;

        if (Find(config, field, text) is not { } entry)
            return new TaxonomyBadge
            {
                Text = text,
                Color = TaxonomyPalette.None,
                OffList = true,
                Visible = true,
                FieldLabel = Label(field),
            };

        return new TaxonomyBadge
        {
            Text = text,
            Color = TaxonomyPalette.Normalize(entry.Color),
            OffList = false,
            Visible = entry.ShowOnCard,
            FieldLabel = Label(field),
        };
    }

    /// <summary>
    /// The picker's items for one field: the list, plus the stored value when the list does not
    /// hold it. Without the extra item a combo box bound to an off-list value shows nothing
    /// selected, and the first save writes whatever the reader picks over a value they never saw.
    /// </summary>
    public static List<string> Choices(TaxonomyConfig config, TaxonomyField field, string current)
    {
        var names = Entries(config, field).Select(e => e.Name).ToList();
        var value = (current ?? "").Trim();
        if (value.Length > 0 && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
            names.Add(value);
        return names;
    }
}
