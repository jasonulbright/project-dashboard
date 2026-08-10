namespace ProjectDashboard.Services.Safety;

/// <summary>
/// Which surface an operation came from. A closed vocabulary so a reader filters on a value
/// rather than on a substring of the label.
/// </summary>
public enum OperationCategory
{
    Rewrite,
    Surgery,
    ForcePush,
    DeepClean,
    BackupRestore,
    Working,
    Branch,
    Remote,
    Tag,
    GitHub,
    Maintenance,

    /// <summary>
    /// A backup taken on demand. The backups a coordinator takes before a destructive operation
    /// are named by that operation's own record through its backup stamp, so they are not
    /// recorded again here.
    /// </summary>
    BackupCreate,

    BackupDelete
}

/// <summary>
/// How an attempted operation ended. <see cref="Refused"/> is a gate turning the operation away
/// before it ran; <see cref="Interrupted"/> is a crash marker found at a later launch;
/// <see cref="Unknown"/> is a result that could not be classified and is never substituted for
/// <see cref="Succeeded"/> or <see cref="Failed"/>.
/// </summary>
public enum OperationOutcome
{
    Succeeded,
    Failed,
    Refused,
    Cancelled,
    Interrupted,
    Unknown
}

/// <summary>The recovering action a record describes, for the records that are one.</summary>
public enum RecoveryKind
{
    RestoreFromBackup,
    UndoOffered,
    StaleLockCleared,
    MarkerCleared
}

/// <summary>
/// Marks a record as a recovering action rather than an ordinary operation, and names the record
/// it answers. <see cref="OfId"/> is empty when the recovery answers no single recorded operation.
/// Serialized DTO, so plain get/set.
/// </summary>
public sealed class RecoveryNote
{
    public RecoveryKind Kind { get; set; }

    public DateTimeOffset AppliedUtc { get; set; }

    public string OfId { get; set; } = "";
}

/// <summary>
/// One operation this app attempted against one repository, as it is written to the per-repo
/// ledger. Serialized DTO, so plain get/set.
///
/// <see cref="Detail"/> is verbatim process output under the pinned <c>LC_ALL=C</c> locale, or the
/// verbatim refusal reason, capped at <see cref="MaxDetailLength"/>: an unbounded capture would
/// spend a whole rotation generation on one record.
/// </summary>
public sealed class OperationRecord
{
    /// <summary>Records written by a later schema are read on their own terms; this build writes 1.</summary>
    public const int CurrentSchema = 1;

    internal const int MaxDetailLength = 2000;

    internal const int MaxLabelLength = 200;

    public string Id { get; set; } = "";

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset EndedUtc { get; set; }

    public string RepoPath { get; set; } = "";

    public string RepoKey { get; set; } = "";

    public OperationCategory Category { get; set; }

    public string Label { get; set; } = "";

    public OperationOutcome Outcome { get; set; }

    public string Detail { get; set; } = "";

    /// <summary>Names the bundle under the repository's backup directory, or null when the operation took none.</summary>
    public string? BackupStamp { get; set; }

    public RecoveryNote? Recovery { get; set; }

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>
    /// A record whose identity and repository key are already resolved. The end stamp is taken
    /// here, so a caller brackets the operation by passing the start it captured before it ran.
    /// </summary>
    public static OperationRecord For(
        string repoPath,
        OperationCategory category,
        string label,
        OperationOutcome outcome,
        string detail,
        DateTimeOffset startedUtc,
        string? backupStamp = null,
        RecoveryNote? recovery = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedUtc = startedUtc,
            EndedUtc = DateTimeOffset.UtcNow,
            RepoPath = repoPath,
            RepoKey = Safety.RepoKey.For(repoPath),
            Category = category,
            Label = Clamp(label, MaxLabelLength),
            Outcome = outcome,
            Detail = Clamp(detail, MaxDetailLength),
            BackupStamp = backupStamp,
            Recovery = recovery
        };

    /// <summary>
    /// Truncates without splitting a surrogate pair. A cut landing between the two halves of one
    /// leaves an unpaired surrogate, which the JSON writer refuses to transcode — and the append
    /// swallows that throw, so the record would be dropped rather than shortened.
    /// </summary>
    private static string Clamp(string value, int max)
    {
        if (value.Length <= max) return value;
        var cut = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut] + "…";
    }
}
