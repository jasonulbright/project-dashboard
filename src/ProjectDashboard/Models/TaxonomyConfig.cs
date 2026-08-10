namespace ProjectDashboard.Models;

/// <summary>The four manifest fields whose allowed values a reader can edit.</summary>
public enum TaxonomyField
{
    Type,
    Status,
    Category,
    Schedule,
}

/// <summary>
/// One allowed value, and how a card draws it. Position in its list is display order, so no
/// sort index exists to disagree with it.
/// </summary>
public sealed class TaxonomyEntry
{
    public string Name { get; set; } = "";

    /// <summary>
    /// A key from <see cref="TaxonomyPalette"/>, or empty for the untinted chip. Keys rather than
    /// colour literals: each key resolves to a pair whose contrast is held against both themes'
    /// surfaces, and a literal stored here would hold against neither when the theme changes.
    /// </summary>
    public string Color { get; set; } = "";

    /// <summary>
    /// Whether a card draws a chip for this value. False is how a value means "nothing to say" —
    /// the seeded validation schedule "none" — without a card badge keyed to that literal string,
    /// which renaming the value would silently stop matching.
    /// </summary>
    public bool ShowOnCard { get; set; } = true;

    public TaxonomyEntry Copy() => new() { Name = Name, Color = Color, ShowOnCard = ShowOnCard };
}

/// <summary>One value's old and new spelling, as a cascade over the stored records reads it.</summary>
public sealed record TaxonomyRename(TaxonomyField Field, string From, string To);

/// <summary>The allowed values for each of the four manifest taxonomies, in display order.</summary>
public sealed class TaxonomyConfig
{
    public List<TaxonomyEntry> Types { get; set; } = [];
    public List<TaxonomyEntry> Statuses { get; set; } = [];
    public List<TaxonomyEntry> Categories { get; set; } = [];
    public List<TaxonomyEntry> Schedules { get; set; } = [];
}

/// <summary>
/// The chip colours a value may carry. Every key names a foreground and a background already
/// tuned per theme, so a value cannot be given a colour that fails the contrast floor.
/// </summary>
public static class TaxonomyPalette
{
    public const string None = "";
    public const string Good = "good";
    public const string Warn = "warn";
    public const string Bad = "bad";
    public const string Accent = "private";
    public const string Info = "info";
    public const string Neutral = "neutral";

    public static readonly IReadOnlyList<string> Keys = [None, Good, Warn, Bad, Accent, Info, Neutral];

    /// <summary>What the picker calls each key. Named for what it reads as, not for its hex.</summary>
    public static string Label(string key) => key switch
    {
        Good => "Green",
        Warn => "Amber",
        Bad => "Red",
        Accent => "Violet",
        Info => "Blue",
        Neutral => "Grey",
        _ => "No colour",
    };

    /// <summary>An unknown key — a hand-edited settings file — draws untinted rather than throwing.</summary>
    public static string Normalize(string? key) =>
        key is not null && Keys.Contains(key, StringComparer.OrdinalIgnoreCase)
            ? Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            : None;
}

/// <summary>
/// What a card draws for one manifest field: the value as stored, the palette key that tints it,
/// and whether the value is one the reader's list still holds. An off-list value is drawn as
/// itself and marked — never swapped for a list entry, which is how a hand-edited or imported
/// value would disappear without a write.
/// </summary>
public sealed class TaxonomyBadge
{
    public string Text { get; init; } = "";
    public string Color { get; init; } = TaxonomyPalette.None;
    public bool OffList { get; init; }
    public bool Visible { get; init; }

    /// <summary>The field this value belongs to, spelled the way a reader is told about it.</summary>
    public string FieldLabel { get; init; } = "";

    public static readonly TaxonomyBadge Hidden = new();

    public string AccessibleName => OffList
        ? $"{FieldLabel} {Text}, not in your {FieldLabel} list"
        : $"{FieldLabel} {Text}";

    public string Tip => OffList
        ? $"\"{Text}\" is not in your {FieldLabel} list. It is kept as it is; add it in Settings to give it a colour."
        : Text;
}
