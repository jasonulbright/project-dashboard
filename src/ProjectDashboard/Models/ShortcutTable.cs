namespace ProjectDashboard.Models;

/// <summary>One row of the shortcut cheat sheet.</summary>
public sealed record ShortcutEntry(string Group, string Gesture, string Description);

/// <summary>
/// The single source for every keyboard gesture the app registers. The cheat-sheet
/// overlay renders this table and nothing else, so a gesture added to a key handler
/// or an InputBinding without a row here is invisible to the user.
/// </summary>
public static class ShortcutTable
{
    public const string GlobalGroup = "Anywhere";
    public const string DashboardGroup = "Dashboard";
    public const string PaletteGroup = "Command palette";
    public const string DetailGroup = "Project detail";

    public static IReadOnlyList<ShortcutEntry> All { get; } =
    [
        new(GlobalGroup, "Ctrl+K", "Open the command palette"),
        new(GlobalGroup, "?", "Show this shortcut list"),
        new(GlobalGroup, "Esc", "Close the palette or this list"),
        new(GlobalGroup, "Alt+Left", "Go back"),
        new(GlobalGroup, "Backspace", "Go back (outside a text field)"),
        new(GlobalGroup, "Up / Down", "Move between sidebar items"),

        new(DashboardGroup, "Enter / Space", "Open the focused project, or activate the focused summary chip"),
        new(DashboardGroup, "Arrow keys", "Move between project cards"),
        new(DashboardGroup, "Tab", "Reach the focused card's Fetch / Pull / Push actions, then leave the grid"),

        new(PaletteGroup, "Up / Down", "Move the selection"),
        new(PaletteGroup, "Enter", "Open the selected project, result, or action"),

        new(DetailGroup, "Ctrl+1 … Ctrl+9", "Jump to work-area tabs 1–9"),
        new(DetailGroup, "Ctrl+0", "Jump to work-area tab 10"),
        // There are eleven tabs and only ten digits, so the eleventh is reachable by
        // arrow key alone; a sheet that omitted this would read as though it were unreachable.
        new(DetailGroup, "Left / Right", "Move between all eleven work-area tabs, including the eleventh"),
        new(DetailGroup, "Ctrl+Enter", "Commit the staged changes"),
        new(DetailGroup, "Enter / Space", "Open the focused list row"),
    ];

    /// <summary>Groups in table order, so the overlay's layout follows the declaration.</summary>
    public static IReadOnlyList<IGrouping<string, ShortcutEntry>> Groups { get; } =
        All.GroupBy(e => e.Group).ToList();
}
