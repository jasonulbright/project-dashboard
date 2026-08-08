namespace ProjectDashboard.Models;

public sealed class GitCommit
{
    /// <summary>
    /// Full 40-character sha. An abbreviation is not an identity: git resolves any
    /// prefix of 7 or more characters, so two commits in one range can share a
    /// 7-character prefix and a lookup keyed on the abbreviation returns whichever
    /// was indexed first.
    /// </summary>
    public string Hash { get; set; } = "";

    /// <summary>Display form only; pass <see cref="Ref"/> to git.</summary>
    public string ShortHash { get; set; } = "";

    /// <summary>The revision to pass to git — never empty while either form is set.</summary>
    public string Ref => Hash.Length > 0 ? Hash : ShortHash;

    public string Author { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// One path's history, or why it has none. git reports a read it could not perform as a
/// non-zero exit, which an empty list cannot be told apart from a path nothing ever touched;
/// <see cref="HasError"/> separates them so the viewer never shows a confident empty state for
/// a read that failed.
/// </summary>
public sealed record FileHistoryResult(List<GitCommit> Commits, bool HasError = false, string ErrorText = "");
