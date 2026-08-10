namespace ProjectDashboard.Models;

/// <summary>
/// One repository's stored record: the metadata a reader typed, what the repository was when it
/// was last seen, and when that was. The path remains the key; the fingerprint is what lets the
/// record follow a folder that moves.
/// </summary>
public sealed class ManifestEntry
{
    public ProjectManifest Manifest { get; set; } = new();

    /// <summary>Null for a record lifted from a store written before fingerprints, until a scan meets it.</summary>
    public RepoFingerprint? Fingerprint { get; set; }

    public DateTimeOffset? FirstSeenUtc { get; set; }

    /// <summary>When a scan last found the repository at this path. Null until one has.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }

    public ManifestEntry Copy() => new()
    {
        Manifest = Manifest.Copy(),
        Fingerprint = Fingerprint?.Copy(),
        FirstSeenUtc = FirstSeenUtc,
        LastSeenUtc = LastSeenUtc,
    };
}

/// <summary>A stored record re-keyed onto the path its repository turned up at.</summary>
public sealed record ManifestAdoption(string FromPath, string ToPath, string Name);

/// <summary>
/// A stored record whose repository was not found, and which nothing says is gone: it is kept
/// until a reader forgets it. Hand-typed metadata is not reconstructible, so an automatic
/// deletion is the one outcome this design refuses.
/// </summary>
public sealed record ManifestOrphan(string Path, string Name, string Description, DateTimeOffset? LastSeenUtc);

/// <summary>Why a stored record was left where it was rather than re-keyed.</summary>
public enum ManifestRefusalReason
{
    /// <summary>The record matched more than one repository — two clones of one upstream, a fork.</summary>
    SeveralRepositoriesMatch,

    /// <summary>More than one stored record matched the same repository.</summary>
    SeveralRecordsMatch,

    /// <summary>The repository it matched already carries metadata of its own, which adoption would overwrite.</summary>
    TargetAlreadyHasMetadata,
}

/// <summary>An adoption that was refused, and the repositories that made it ambiguous.</summary>
public sealed record ManifestRefusal(
    string Path, string Name, ManifestRefusalReason Reason, IReadOnlyList<string> Candidates);

/// <summary>
/// What one scan's identity pass concluded. Reported rather than logged: a record that moved and
/// a record that could not be placed are both facts about the reader's own metadata.
/// </summary>
public sealed record ManifestIdentityReport(
    IReadOnlyList<ManifestAdoption> Adoptions,
    IReadOnlyList<ManifestRefusal> Refusals,
    IReadOnlyList<ManifestOrphan> Orphans)
{
    public static ManifestIdentityReport Empty { get; } = new([], [], []);

    public bool HasNews => Adoptions.Count > 0 || Refusals.Count > 0;
}
