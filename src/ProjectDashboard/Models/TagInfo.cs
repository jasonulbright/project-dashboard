namespace ProjectDashboard.Models;

/// <summary>One entry from refs/tags. Annotated tags carry a message subject and tagger date; lightweight tags do not.</summary>
public sealed class TagInfo
{
    public string Name { get; init; } = "";
    public bool IsAnnotated { get; init; }
    /// <summary>The commit the tag ultimately points at (dereferenced for annotated tags).</summary>
    public string TargetSha { get; init; } = "";
    /// <summary>Tag message subject; null for lightweight tags.</summary>
    public string? Subject { get; init; }
    /// <summary>Tagger date; null for lightweight tags.</summary>
    public DateTimeOffset? TaggerDate { get; init; }

    /// <summary>Subject of the commit the tag ultimately points at, for both kinds of tag.</summary>
    public string TargetSubject { get; init; } = "";

    /// <summary>Date of the commit the tag ultimately points at; null when git reported none.</summary>
    public DateTimeOffset? TargetDate { get; init; }

    /// <summary>
    /// When the ref itself came to be: the tagger date where one was recorded, otherwise the
    /// commit's own date, which is all a lightweight tag has.
    /// </summary>
    public DateTimeOffset? DisplayDate => TaggerDate ?? TargetDate;

    public string KindLabel => IsAnnotated ? "annotated" : "lightweight";
}

/// <summary>
/// A repository's tags, or why they could not be read. A ref read git could not perform exits
/// non-zero rather than throwing, so an empty list alone cannot be told apart from a repository
/// that has never been tagged.
/// </summary>
public sealed record TagsResult(List<TagInfo> Tags, bool HasError = false, string ErrorText = "");
