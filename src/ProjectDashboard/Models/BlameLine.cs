namespace ProjectDashboard.Models;

/// <summary>One source line attributed to the commit that last touched it, from `git blame --porcelain`.</summary>
public sealed class BlameLine
{
    public string Sha { get; init; } = "";

    /// <summary>Display form only; the gutter shows this and every lookup uses <see cref="Sha"/>.</summary>
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;

    public string Author { get; init; } = "";
    public DateTimeOffset? Date { get; init; }
    /// <summary>1-based line number in the final file.</summary>
    public int LineNumber { get; init; }
    public string Text { get; init; } = "";

    /// <summary>
    /// <see cref="Text"/> carrying its own leading separator; empty for a blank line, which a
    /// composed name would otherwise end a separator on.
    /// </summary>
    public string TextSuffix => Text.Length == 0 ? "" : $": {Text}";
    /// <summary>True when the attributing commit is a blame boundary (root or walk limit).</summary>
    public bool IsBoundary { get; init; }
}

/// <summary>
/// One file's blame, or why it has none. A blame git could not perform exits non-zero rather
/// than throwing, so an empty list alone cannot be told apart from an empty file.
/// </summary>
public sealed record BlameResult(List<BlameLine> Lines, bool HasError = false, string ErrorText = "");
