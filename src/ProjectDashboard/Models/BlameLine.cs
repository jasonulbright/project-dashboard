namespace ProjectDashboard.Models;

/// <summary>One source line attributed to the commit that last touched it, from `git blame --porcelain`.</summary>
public sealed class BlameLine
{
    public string Sha { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset? Date { get; init; }
    /// <summary>1-based line number in the final file.</summary>
    public int LineNumber { get; init; }
    public string Text { get; init; } = "";
    /// <summary>True when the attributing commit is a blame boundary (root or walk limit).</summary>
    public bool IsBoundary { get; init; }
}
