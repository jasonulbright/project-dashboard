namespace ProjectDashboard.Services.Surgery;

/// <summary>What one commit in a combined plan does when the range is replayed.</summary>
public enum RebaseStepAction
{
    /// <summary>Replayed as its own commit, at the position the plan gives it.</summary>
    Pick,

    /// <summary>Left out of the replay.</summary>
    Drop,

    /// <summary>Folded into the commit the plan puts immediately before it.</summary>
    Fixup
}

/// <summary>
/// One commit's place in a combined plan. <see cref="NewMessage"/> belongs on a
/// <see cref="RebaseStepAction.Pick"/> only: a dropped commit has no message to set, and a
/// folded one contributes none — the message of a fold is the anchor's.
/// </summary>
public sealed record RebaseStep(string Sha, RebaseStepAction Action, string? NewMessage = null);

/// <summary>
/// A whole replay in one object: every commit in the range, in the order it is to be replayed,
/// each with what happens to it. Reorder, drop, squash and reword are positions and actions in
/// the same list rather than separate operations, so one apply carries all of them.
/// </summary>
public sealed class RebaseTodo
{
    public required IReadOnlyList<RebaseStep> Steps { get; init; }
}

/// <summary>What a todo line asks git to do, before message files exist to point exec lines at.</summary>
public enum RebaseCommandKind
{
    Pick,
    Fixup,

    /// <summary>An amend that installs <see cref="RebaseCommand.Message"/> on whatever the replay has just built.</summary>
    AmendMessage
}

/// <summary>One rendered-to-be todo command. <see cref="Message"/> is set only on <see cref="RebaseCommandKind.AmendMessage"/>.</summary>
public sealed record RebaseCommand(RebaseCommandKind Kind, string Sha, string Subject, string? Message = null);

/// <summary>
/// One commit the plan produces. <see cref="Sha"/> is the pre-replay id of the commit the new
/// one grows from — every replayed commit gets a new id, so no plan can name the id it ends up
/// with. <see cref="FoldedSubjects"/> names what lands inside it.
/// </summary>
public sealed record RebaseResultCommit(string Sha, string Subject, IReadOnlyList<string> FoldedSubjects)
{
    public string ShortSha => SurgeryText.Short(Sha);

    /// <summary>The commit as one display line: id, message subject, and what folded into it.</summary>
    public string Line =>
        FoldedSubjects.Count == 0 ? $"{ShortSha}  {Subject}" : $"{ShortSha}  {Subject} + {string.Join(" + ", FoldedSubjects)}";
}

/// <summary>
/// A compiled plan: the commands git is to be given and the commit list they produce, from one
/// walk of the plan. Both come from the same pass so a preview built from
/// <see cref="Result"/> cannot describe a history the <see cref="Commands"/> would not produce.
/// A <see cref="Refusal"/> leaves both empty.
/// </summary>
public sealed class RebaseTodoCompilation
{
    public string? Refusal { get; init; }

    public IReadOnlyList<RebaseCommand> Commands { get; init; } = [];

    public IReadOnlyList<RebaseResultCommit> Result { get; init; } = [];

    public bool IsValid => Refusal is null;

    internal static RebaseTodoCompilation Refused(string reason) => new() { Refusal = reason };
}

/// <summary>
/// Turns a combined plan into the commands one interactive rebase runs, or into a refusal that
/// names the contradiction. Pure: no repository is read and no git process starts, so every
/// impossible combination is reported before anything can move.
///
/// The combinations refused here are the ones a replay cannot express rather than ones git
/// would merely dislike: a commit listed twice, a plan that does not cover its range, a fold
/// with nothing before it to fold into, a fold whose anchor the same plan drops, a message set
/// on a commit the same plan drops or folds away, and a plan that empties the branch.
/// </summary>
public static class RebaseTodoCompiler
{
    public static RebaseTodoCompilation Compile(RebaseTodo todo, IReadOnlyList<RebaseCommit> commits)
    {
        if (todo.Steps.Count == 0)
            return RebaseTodoCompilation.Refused("the plan is empty — there is nothing to replay");

        var index = ScopeShaIndex.For(commits);
        var resolved = new List<(RebaseCommit Commit, RebaseStepAction Action, string? Message)>(todo.Steps.Count);
        var seen = new Dictionary<string, RebaseStepAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in todo.Steps)
        {
            var commit = index.Resolve(step.Sha);
            if (commit is null)
                return RebaseTodoCompilation.Refused(index.Rejection(step.Sha));
            if (seen.TryGetValue(commit.Sha, out var first))
                return RebaseTodoCompilation.Refused(
                    $"the plan lists commit {SurgeryText.Short(commit.Sha)} twice — as {Describe(first)} and as {Describe(step.Action)}");
            seen[commit.Sha] = step.Action;
            resolved.Add((commit, step.Action, step.NewMessage));
        }

        // Every commit resolves into the range and none is listed twice, so matching counts make
        // the plan a permutation of the range. A short plan is a stale one: the commits it never
        // named would silently leave the branch.
        if (resolved.Count != commits.Count)
            return RebaseTodoCompilation.Refused(
                $"the plan lists {resolved.Count} of the {commits.Count} commit(s) in the range — " +
                "every commit has to be listed, dropped ones included");

        if (resolved.All(r => r.Action == RebaseStepAction.Drop))
            return RebaseTodoCompilation.Refused(
                "dropping every commit in the range would empty the branch — use a reset instead");

        for (var i = 0; i < resolved.Count; i++)
        {
            var (commit, action, message) = resolved[i];
            var named = SurgeryText.Short(commit.Sha);
            switch (action)
            {
                case RebaseStepAction.Drop when message is not null:
                    return RebaseTodoCompilation.Refused(
                        $"the plan rewords commit {named} and drops it in the same replay — a dropped commit has no message to set");
                case RebaseStepAction.Fixup when message is not null:
                    return RebaseTodoCompilation.Refused(
                        $"the plan rewords commit {named} and folds it into the commit before it in the same replay — " +
                        "set the message on the commit it folds into");
                case RebaseStepAction.Fixup when i == 0:
                    return RebaseTodoCompilation.Refused(
                        $"the plan folds commit {named} into the commit before it, but puts it first — nothing precedes it");
                case RebaseStepAction.Fixup when resolved[i - 1].Action == RebaseStepAction.Drop:
                    return RebaseTodoCompilation.Refused(
                        $"the plan folds commit {named} into {SurgeryText.Short(resolved[i - 1].Commit.Sha)}, " +
                        "which the same plan drops — a dropped commit cannot be a squash anchor");
                case RebaseStepAction.Pick when message is not null && string.IsNullOrWhiteSpace(message):
                    return RebaseTodoCompilation.Refused("a commit message cannot be empty");
            }
        }

        var commands = new List<RebaseCommand>();
        var anchors = new List<(string Sha, string Subject, List<string> Folded)>();
        string? pending = null;
        for (var i = 0; i < resolved.Count; i++)
        {
            var (commit, action, message) = resolved[i];
            switch (action)
            {
                case RebaseStepAction.Drop:
                    continue;
                case RebaseStepAction.Pick:
                    anchors.Add((commit.Sha, SurgeryText.FirstLine(message) ?? commit.Subject, []));
                    commands.Add(new RebaseCommand(RebaseCommandKind.Pick, commit.Sha, commit.Subject));
                    pending = message;
                    break;
                case RebaseStepAction.Fixup:
                    anchors[^1].Folded.Add(commit.Subject);
                    commands.Add(new RebaseCommand(RebaseCommandKind.Fixup, commit.Sha, commit.Subject));
                    break;
            }

            // The amend closes the run: it has to follow the last fold, or the folds after it
            // would rewrite the message it just installed.
            if (pending is null) continue;
            if (i + 1 < resolved.Count && resolved[i + 1].Action == RebaseStepAction.Fixup) continue;
            commands.Add(new RebaseCommand(RebaseCommandKind.AmendMessage, commit.Sha, commit.Subject, pending));
            pending = null;
        }

        return new RebaseTodoCompilation
        {
            Commands = commands,
            Result = anchors.Select(a => new RebaseResultCommit(a.Sha, a.Subject, a.Folded)).ToList()
        };
    }

    private static string Describe(RebaseStepAction action) => action switch
    {
        RebaseStepAction.Drop => "a drop",
        RebaseStepAction.Fixup => "a squash",
        _ => "a pick"
    };
}

/// <summary>Formatting shared by the surgery layer's refusals and previews.</summary>
internal static class SurgeryText
{
    internal static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>The subject line of a commit message, or null for a null message.</summary>
    internal static string? FirstLine(string? message)
    {
        if (message is null) return null;
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length == 0 ? message.Trim() : line;
    }
}

/// <summary>
/// Resolves a caller's sha — full or abbreviated — against one range. A prefix two commits share
/// resolves to neither, and is reported as ambiguous rather than as out of range: the two
/// refusals ask the caller for opposite corrections.
/// </summary>
internal readonly struct ScopeShaIndex(Dictionary<string, RebaseCommit> byId, HashSet<string> ambiguous)
{
    /// <summary>git's own floor for core.abbrev, so the shortest sha a caller can be shown.</summary>
    private const int MinAbbreviatedShaLength = 4;

    internal RebaseCommit? Resolve(string sha) => byId.GetValueOrDefault(sha);

    internal string Rejection(string sha) => ambiguous.Contains(sha)
        ? $"commit {SurgeryText.Short(sha)} matches more than one commit in the range — use a longer sha"
        : $"commit {SurgeryText.Short(sha)} is not in the editable range";

    internal static ScopeShaIndex For(IReadOnlyList<RebaseCommit> commits)
    {
        var map = new Dictionary<string, RebaseCommit>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in commits)
        {
            map[commit.Sha] = commit;
            // Abbreviations let a caller pass what the UI displays without re-resolving, and a
            // display sha honours core.abbrev, whose floor is four characters.
            for (var length = MinAbbreviatedShaLength; length < commit.Sha.Length; length++)
            {
                var prefix = commit.Sha[..length];
                if (map.TryGetValue(prefix, out var owner))
                {
                    // First-wins would resolve a prefix two commits share onto the wrong one and
                    // rewrite history the caller never named.
                    if (!ReferenceEquals(owner, commit)) ambiguous.Add(prefix);
                }
                else
                {
                    map[prefix] = commit;
                }
            }
        }
        foreach (var prefix in ambiguous) map.Remove(prefix);
        return new ScopeShaIndex(map, ambiguous);
    }
}
