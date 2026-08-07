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
}
