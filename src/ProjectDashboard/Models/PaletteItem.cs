using Wpf.Ui.Controls;

namespace ProjectDashboard.Models;

/// <summary>One row in the Ctrl+K command palette: a project jump or a global action.</summary>
public sealed class PaletteItem
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public SymbolRegular Icon { get; init; } = SymbolRegular.ArrowRight24;
    /// <summary>Lowercased haystack for matching (title + subtitle + keywords).</summary>
    public string SearchText { get; init; } = "";
    /// <summary>Invoked when the row is chosen.</summary>
    public Action Invoke { get; init; } = () => { };

    /// <summary>
    /// Subsequence + substring score, higher is better, -1 = no match. A contiguous
    /// substring beats a scattered subsequence; an earlier match beats a later one.
    /// </summary>
    public int Score(string queryLower)
    {
        if (queryLower.Length == 0) return 0;
        var hay = SearchText;

        var idx = hay.IndexOf(queryLower, StringComparison.Ordinal);
        if (idx >= 0) return 1000 - idx; // contiguous match, prefer earlier

        // Fall back to in-order subsequence.
        int qi = 0;
        foreach (var c in hay)
        {
            if (qi < queryLower.Length && c == queryLower[qi]) qi++;
            if (qi == queryLower.Length) return 200;
        }
        return -1;
    }
}
