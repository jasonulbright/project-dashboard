namespace ProjectDashboard.Services;

/// <summary>
/// How much of a working tree a search reads. Each value maps to one set of git flags, and the
/// widest one reads build output: on this repository the widest scope matched 421 files, 197 of
/// them under bin/ or obj/. That cost is why it is neither the default nor carried between opens.
/// </summary>
public enum SearchContentScope
{
    /// <summary>Files git tracks. The default, and the only scope whose cost the index bounds.</summary>
    Tracked,

    /// <summary>Tracked files plus the untracked ones the ignore rules do not exclude.</summary>
    WithUntracked,

    /// <summary>Every file in the working tree, ignored ones included — build output with them.</summary>
    Everything,
}

/// <summary>How many repositories one search covers.</summary>
public enum SearchBreadth
{
    /// <summary>Every discovered repository with a working tree.</summary>
    Portfolio,

    /// <summary>The one repository the surface belongs to.</summary>
    CurrentRepo,
}

/// <summary>
/// What one hit's file is to git. Carried on every row: a match in build output drawn like a
/// match in source reads as source.
/// </summary>
public enum SearchFileScope
{
    Tracked,
    Untracked,
    Ignored,
}

/// <summary>The scope one fan-out runs under: how much of each tree, and how many trees.</summary>
public sealed record SearchScope(SearchContentScope Content, SearchBreadth Breadth)
{
    /// <summary>Tracked content across the portfolio — what a surface opens on, every time.</summary>
    public static readonly SearchScope Default = new(SearchContentScope.Tracked, SearchBreadth.Portfolio);

    /// <summary>Tracked content in one repository.</summary>
    public static readonly SearchScope OneRepo = new(SearchContentScope.Tracked, SearchBreadth.CurrentRepo);
}

/// <summary>
/// The content scope a surface is searching under, and the only thing that changes it.
/// <see cref="Reset"/> runs on every open: a scope carried forward would spend the widest scope's
/// cost on the next keystroke without having been asked for again.
/// </summary>
public sealed class SearchScopeSelection
{
    public SearchContentScope Current { get; private set; } = SearchContentScope.Tracked;

    public void Reset() => Current = SearchContentScope.Tracked;

    /// <summary>Moves to <paramref name="scope"/>, reporting whether it moved — a re-search costs processes.</summary>
    public bool Select(SearchContentScope scope)
    {
        if (Current == scope) return false;
        Current = scope;
        return true;
    }

    /// <summary>Steps to the next scope and wraps, for the one gesture that cycles them.</summary>
    public void Cycle() =>
        Current = Current switch
        {
            SearchContentScope.Tracked => SearchContentScope.WithUntracked,
            SearchContentScope.WithUntracked => SearchContentScope.Everything,
            _ => SearchContentScope.Tracked,
        };

    /// <summary>The scope a direct-select gesture names, or null for a digit no scope answers to.</summary>
    public static SearchContentScope? ForDigit(int digit) => digit switch
    {
        1 => SearchContentScope.Tracked,
        2 => SearchContentScope.WithUntracked,
        3 => SearchContentScope.Everything,
        _ => null,
    };
}

/// <summary>
/// Every sentence the two search surfaces say about a scope or a result, in one place: the palette
/// and the detail pane run the same fan-out, and two wordings for one outcome read as two outcomes.
/// </summary>
public static class SearchScopeCopy
{
    /// <summary>The switch's own label — short enough for a chip.</summary>
    public static string Chip(SearchContentScope scope) => scope switch
    {
        SearchContentScope.WithUntracked => "+ Untracked",
        SearchContentScope.Everything => "All files",
        _ => "Tracked",
    };

    /// <summary>The scope named in a sentence, as what it reads rather than as its switch label.</summary>
    public static string Name(SearchContentScope scope) => scope switch
    {
        SearchContentScope.WithUntracked => "tracked and untracked files",
        SearchContentScope.Everything => "all files, ignored ones included",
        _ => "tracked files",
    };

    /// <summary>What the switch does, for the reader who has not pressed it yet.</summary>
    public static string ChipHint(SearchContentScope scope) => scope switch
    {
        SearchContentScope.WithUntracked => "Search tracked and untracked files",
        SearchContentScope.Everything => "Search all files, including ignored ones",
        _ => "Search tracked files only",
    };

    /// <summary>Said beside the widest switch: its cost is not obvious from its label.</summary>
    public const string EverythingNotice =
        "All files reads build output and everything else the ignore rules exclude, so it is slower and noisier.";

    /// <summary>The label a row carries when its file is not tracked; empty for one that is.</summary>
    public static string RowLabel(SearchFileScope scope) => scope switch
    {
        SearchFileScope.Untracked => "untracked",
        SearchFileScope.Ignored => "ignored",
        _ => "",
    };

    /// <summary>The results header, which names the scope in force rather than leaving it to the switches.</summary>
    public static string Header(SearchContentScope scope) => $"In files — {Name(scope)}";

    /// <summary>
    /// What the fan-out covered and what it could not, in one line. Repositories that were skipped,
    /// cut short, or errored are named separately: a count that folded them together would let a
    /// partial answer read as a complete one.
    /// </summary>
    public static string Summary(RepoSearchResult result, SearchScope scope)
    {
        var where = scope.Breadth == SearchBreadth.CurrentRepo
            ? "This repository"
            : $"{result.ReposSearched} {(result.ReposSearched == 1 ? "repository" : "repositories")}";
        var line = $"{where} searched across {Name(scope.Content)}.";

        if (result.More > 0) line += $" {result.More} more matches are not shown — narrow the search.";
        if (result.ReposTruncated > 0)
            line += $" {Count(result.ReposTruncated)} ran out of time, so what came back is partial.";
        if (result.ReposFailed > 0)
            line += $" {Count(result.ReposFailed)} reported an error and may be missing matches.";
        if (result.ReposSkipped > 0)
            line += $" {Count(result.ReposSkipped)} could not be read and went unsearched.";

        return line;
    }

    private static string Count(int repos) =>
        repos == 1 ? "1 repository" : $"{repos} repositories";
}
