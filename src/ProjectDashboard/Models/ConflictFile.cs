namespace ProjectDashboard.Models;

/// <summary>Which pair of merge stages a conflict preview renders.</summary>
public enum ConflictComparison
{
    BaseToOurs,
    BaseToTheirs,
    OursToTheirs
}

/// <summary>
/// One unmerged path as the conflict panel lists it: the shape git recorded, which stages the
/// index holds for it, and — when the panel cannot act on it — the reason, said in full.
///
/// A row is rebuilt from a fresh read after every resolution, so nothing here is settable.
/// </summary>
public sealed class ConflictFile
{
    public required string Path { get; init; }

    /// <summary>The two-letter unmerged code from `git status --porcelain=v2`, e.g. "UU".</summary>
    public required string Code { get; init; }

    public bool HasBase { get; init; }
    public bool HasOurs { get; init; }
    public bool HasTheirs { get; init; }

    /// <summary>
    /// The index entries a resolution records for each side, so taking a side records THAT blob
    /// rather than re-reading a working-tree file anything may have written since.
    /// </summary>
    public Services.Surgery.ConflictStage? OursStage { get; init; }
    public Services.Surgery.ConflictStage? TheirsStage { get; init; }

    /// <summary>The index holds this path as a gitlink; taking a side of one picks a commit, not content.</summary>
    public bool IsGitlink { get; init; }

    /// <summary>Why this row cannot be resolved here, or empty when it can.</summary>
    public string Refusal { get; init; } = "";

    public bool IsRefused => Refusal.Length > 0;

    /// <summary>
    /// True where the side holds no content and taking it means recording its deletion. Both
    /// sides of a path neither side kept are false — there is nothing to take either way.
    /// </summary>
    public bool OursDeletes => !HasOurs && HasTheirs;
    public bool TheirsDeletes => !HasTheirs && HasOurs;

    public bool CanTakeOurs => !IsRefused && (HasOurs || OursDeletes);
    public bool CanTakeTheirs => !IsRefused && (HasTheirs || TheirsDeletes);

    /// <summary>What each button does to this path, which for a side that deleted it is a removal.</summary>
    public string TakeOursLabel => HasOurs ? "Take ours" : "Take ours (delete)";
    public string TakeTheirsLabel => HasTheirs ? "Take theirs" : "Take theirs (delete)";

    public string KindLabel => LabelFor(Code);

    /// <summary>The row's whole meaning in one line, for the reader a list is announced to.</summary>
    public string AccessibleName =>
        IsRefused ? $"{Path}, {KindLabel}, {Refusal}" : $"{Path}, {KindLabel}";

    /// <summary>git's unmerged status codes, spelled out. Anything unrecognized is named as unmerged only.</summary>
    public static string LabelFor(string code) => code switch
    {
        "UU" => "both modified",
        "AA" => "both added",
        "AU" => "added by us",
        "UA" => "added by them",
        "DU" => "deleted by us",
        "UD" => "deleted by them",
        "DD" => "both deleted",
        _ => "unmerged"
    };
}
