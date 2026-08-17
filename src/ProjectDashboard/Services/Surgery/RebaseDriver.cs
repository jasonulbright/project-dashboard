using System.IO;
using System.Text;

namespace ProjectDashboard.Services.Surgery;

/// <summary>
/// Drives `git rebase -i` without ever opening an editor, so a windowless app can reorder,
/// drop, squash, and reword commits.
///
/// The mechanism, isolated here because it is the fragile part on Windows:
///
/// GIT_SEQUENCE_EDITOR is `cp "&lt;prepared-todo&gt;"`. Git appends the generated todo's path as
/// the last argument and runs the whole thing through its bundled MSYS `sh`, so the effective
/// command is `cp "&lt;prepared&gt;" "&lt;generated&gt;"` and the prepared todo replaces the generated
/// one wholesale. A `cmd /c copy /y "&lt;prepared&gt;"` form does NOT work on Git for Windows: the
/// todo path git appends is an MSYS-style forward-slash path, and handing it to native cmd.exe
/// mangles it (`copy` reports a truncated component as an unknown command), so the todo is
/// never written and the rebase fails. `cp` resolves from the bundled `/usr/bin` inside git's
/// own shell, which does not depend on the app's PATH.
///
/// GIT_EDITOR is `true`: nothing may ever block on a message editor. Rewording therefore rides
/// on `exec &lt;git&gt; commit --amend -F &lt;msgfile&gt;` lines in the todo rather than on `reword`, and
/// squash-with-a-new-message is `fixup` lines plus one trailing amend exec.
///
/// A stopped rebase is never resolved automatically. Under
/// <see cref="RebaseConflictPolicy.AbortAndReport"/> the driver runs `git rebase --abort`, which
/// returns refs, the index, and tracked content to their exact pre-operation state, and reports
/// the commit that stopped it. Untracked files are outside that guarantee: git leaves behind
/// whatever a hook wrote into the worktree during the replay, so the new ones are listed in
/// <see cref="RebaseRunResult.UntrackedAdded"/> instead of being left for the next operation's
/// tree gate to refuse as changes the user never made. A rebase that exceeds its timeout is
/// killed and aborted regardless of policy.
///
/// `--empty` is explicit rather than inherited: a replayed commit that becomes empty is a stop
/// the caller is told about, never a commit silently dropped from the history. Its value is
/// version-dependent — `stop` exists only from Git 2.45, `ask` is the older spelling and is
/// warned about on newer builds — so the git version is probed once and the accepted spelling
/// used. Sending the wrong one either kills every rebase at startup or makes a deprecation
/// warning the first line of every failure message.
///
/// Commit signing is never overridden on this driver's own initiative — that would strip
/// signatures the user asked for. A repository configured to sign with a key whose passphrase is
/// not cached stalls on a pinentry prompt this app cannot answer, and the timeout then kills and
/// aborts the rebase, so the caller decides: with `disableSigning` the run carries
/// `-c commit.gpgsign=false`, and without it the replay signs exactly as configured.
/// </summary>
public class RebaseDriver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromMinutes(2);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Config pinned for every driven rebase. A user's own settings would otherwise change what
    /// a todo means: missingCommitsCheck=error refuses a drop outright, autoStash silently
    /// stashes past the clean-tree gate, updateRefs moves branches the operation never named,
    /// abbreviateCommands and rebaseMerges change todo grammar, and autoSquash reinterprets
    /// `fixup!` subjects the driver did not ask to be reinterpreted.
    /// </summary>
    private static readonly string[] ConfigPins =
    [
        "-c", "core.quotepath=false",
        "-c", "rebase.missingCommitsCheck=ignore",
        "-c", "rebase.autoSquash=false",
        "-c", "rebase.autoStash=false",
        "-c", "rebase.updateRefs=false",
        "-c", "rebase.abbreviateCommands=false",
        "-c", "rebase.rebaseMerges=false"
    ];

    private static readonly Dictionary<string, string> EmptyMessages = new(StringComparer.Ordinal);

    /// <summary>A scratch tree younger than this may belong to a rebase another process is still starting.</summary>
    private static readonly TimeSpan ScratchGrace = TimeSpan.FromDays(1);

    private const string OwnerFileName = "repo-path.txt";

    private readonly GitService _git;
    private readonly string _gitExe;
    private readonly string _workRoot;
    private bool _swept;
    private string? _emptyMode;

    public RebaseDriver(GitService git, string? workRoot = null)
    {
        _git = git;
        // The amend exec lines name git in a command line of their own; they must name the same
        // binary GitService starts the rebase with, or one operation runs two git builds.
        _gitExe = GitService.ResolveGitExe();
        _workRoot = workRoot ?? Path.Combine(AppPaths.LocalDir, "surgery-work");
    }

    /// <summary>
    /// The last <paramref name="depth"/> commits of HEAD, oldest first, plus the base they
    /// replay onto. Refuses a range containing a merge: an interactive rebase without
    /// `--rebase-merges` would flatten it, silently discarding one side of the merge.
    /// </summary>
    public async Task<RebaseScope> LoadScopeAsync(string repoPath, int depth, CancellationToken ct = default)
    {
        if (depth < 1)
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "depth must be at least 1");

        var log = await _git.RunAsync(repoPath,
            ["log", "--reverse", "-n", depth.ToString(), "--format=%H%x1f%P%x1f%s", "HEAD"], ct, ShortTimeout);
        if (!log.Success)
            throw new InvalidOperationException($"could not read history of '{repoPath}': {log.FirstError}");

        var commits = new List<RebaseCommit>();
        var parentsOfOldest = Array.Empty<string>();
        foreach (var raw in log.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = raw.TrimEnd('\r').Split('\u001f');
            if (fields.Length < 3) continue;
            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length > 1)
                throw new InvalidOperationException(
                    $"commit {fields[0][..Math.Min(8, fields[0].Length)]} is a merge — an interactive rebase would flatten it; narrow the range");
            if (commits.Count == 0) parentsOfOldest = parents;
            commits.Add(new RebaseCommit(fields[0], fields[2]));
        }

        if (commits.Count == 0)
            throw new InvalidOperationException($"'{repoPath}' has no commits to edit");

        return new RebaseScope
        {
            RepoPath = repoPath,
            BaseSha = parentsOfOldest.Length == 1 ? parentsOfOldest[0] : null,
            Commits = commits
        };
    }

    /// <summary>Replays <paramref name="scope"/>'s commits in the given order. The order must be a permutation of the scope.</summary>
    public Task<RebaseRunResult> ReorderAsync(
        RebaseScope scope, IReadOnlyList<string> shasInNewOrder,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
    {
        var index = Index(scope);
        if (shasInNewOrder.Count != scope.Commits.Count)
            return Refuse($"reorder must list all {scope.Commits.Count} commit(s) in the range, got {shasInNewOrder.Count}");

        var todo = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in shasInNewOrder)
        {
            var commit = index.Resolve(sha);
            if (commit is null)
                return Refuse(index.Rejection(sha));
            if (!seen.Add(commit.Sha))
                return Refuse($"commit {Short(sha)} listed twice in the new order");
            todo.Add(Pick(commit));
        }

        return RunTodoAsync(scope, todo, EmptyMessages, policy, disableSigning, ct);
    }

    /// <summary>Removes commits from the replay. At least one must remain — emptying a branch is a reset, not a rebase.</summary>
    public Task<RebaseRunResult> DropAsync(
        RebaseScope scope, IReadOnlyList<string> shasToDrop,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
    {
        var index = Index(scope);
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in shasToDrop)
        {
            var commit = index.Resolve(sha);
            if (commit is null)
                return Refuse(index.Rejection(sha));
            drop.Add(commit.Sha);
        }
        if (drop.Count == 0)
            return Refuse("no commits selected to drop");

        var kept = scope.Commits.Where(c => !drop.Contains(c.Sha)).ToList();
        if (kept.Count == 0)
            return Refuse("dropping every commit in the range would empty the branch — use a reset instead");

        return RunTodoAsync(scope, kept.Select(Pick).ToList(), EmptyMessages, policy, disableSigning, ct);
    }

    /// <summary>
    /// Folds a contiguous run of commits into its first commit. With <paramref name="newMessage"/>
    /// null the first commit's message is kept (`fixup`); otherwise the fixups are followed by one
    /// `exec ... commit --amend -F` that installs the new message.
    /// </summary>
    public Task<RebaseRunResult> SquashAsync(
        RebaseScope scope, IReadOnlyList<string> shasToFold, string? newMessage = null,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
    {
        var index = Index(scope);
        if (shasToFold.Count < 2)
            return Refuse("a squash needs at least two commits");

        var resolved = new List<RebaseCommit>();
        foreach (var sha in shasToFold)
        {
            var commit = index.Resolve(sha);
            if (commit is null)
                return Refuse(index.Rejection(sha));
            resolved.Add(commit);
        }

        // Contiguity is what makes a fixup run well defined: git folds each fixup into whatever
        // HEAD is at that point, so a gap would silently absorb the commits in between.
        var positions = resolved.Select(c => IndexOf(scope, c.Sha)).OrderBy(i => i).ToList();
        if (positions.Distinct().Count() != positions.Count)
            return Refuse("the same commit was listed twice in the squash");
        if (positions[^1] - positions[0] != positions.Count - 1)
            return Refuse("only a contiguous run of commits can be squashed together");

        var fold = new HashSet<string>(resolved.Select(c => c.Sha), StringComparer.OrdinalIgnoreCase);
        var first = scope.Commits[positions[0]];
        var last = scope.Commits[positions[^1]];

        var messageFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageToken = MessageToken("squash");
        if (newMessage is not null) messageFiles[messageToken] = newMessage;

        var todo = new List<string>();
        foreach (var commit in scope.Commits)
        {
            if (!fold.Contains(commit.Sha)) { todo.Add(Pick(commit)); continue; }
            todo.Add(commit.Sha == first.Sha ? Pick(commit) : $"fixup {commit.Sha} {commit.Subject}".TrimEnd());
            if (commit.Sha == last.Sha && newMessage is not null)
                todo.Add(AmendExec(messageToken));
        }

        return RunTodoAsync(scope, todo, messageFiles, policy, disableSigning, ct);
    }

    /// <summary>
    /// Replaces one commit's message at any depth. `reword` would open an editor, so the todo
    /// picks the commit and amends it from a file instead.
    /// </summary>
    public Task<RebaseRunResult> RewordAsync(
        RebaseScope scope, string sha, string newMessage,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newMessage))
            return Refuse("a commit message cannot be empty");
        var index = Index(scope);
        var target = index.Resolve(sha);
        if (target is null)
            return Refuse(index.Rejection(sha));

        var messageToken = MessageToken("reword");
        var todo = new List<string>();
        foreach (var commit in scope.Commits)
        {
            todo.Add(Pick(commit));
            if (commit.Sha == target.Sha) todo.Add(AmendExec(messageToken));
        }

        return RunTodoAsync(scope, todo,
            new Dictionary<string, string>(StringComparer.Ordinal) { [messageToken] = newMessage },
            policy, disableSigning, ct);
    }

    /// <summary>
    /// Folds <paramref name="fixupSha"/> into <paramref name="targetSha"/> through an explicit
    /// todo: every commit in the scope is picked in its recorded order and the fixup is moved to
    /// sit directly after its target. `--autosquash` is deliberately not used — it would also
    /// rearrange and fold any `fixup!`/`squash!` commit the user made themselves, and a
    /// `squash!` would rewrite the target's message.
    /// </summary>
    public virtual Task<RebaseRunResult> FoldFixupAsync(
        RebaseScope scope, string targetSha, string fixupSha,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
    {
        var index = Index(scope);
        var target = index.Resolve(targetSha);
        if (target is null)
            return Refuse(index.Rejection(targetSha));
        var fixup = index.Resolve(fixupSha);
        if (fixup is null)
            return Refuse(index.Rejection(fixupSha));
        if (string.Equals(target.Sha, fixup.Sha, StringComparison.OrdinalIgnoreCase))
            return Refuse("a commit cannot be folded into itself");

        var todo = new List<string>();
        foreach (var commit in scope.Commits)
        {
            // The fixup is emitted at its new home below, never at its recorded position.
            if (string.Equals(commit.Sha, fixup.Sha, StringComparison.OrdinalIgnoreCase)) continue;
            todo.Add(Pick(commit));
            if (string.Equals(commit.Sha, target.Sha, StringComparison.OrdinalIgnoreCase))
                todo.Add($"fixup {fixup.Sha} {fixup.Subject}".TrimEnd());
        }

        return RunTodoAsync(scope, todo, EmptyMessages, policy, disableSigning, ct);
    }

    /// <summary>
    /// Replays the scope as one combined plan: reorder, drop, squash and reword in a single
    /// todo. The plan is compiled before any git process starts, so a combination no replay can
    /// express is a refusal naming the contradiction rather than a rebase that stops part-way.
    /// </summary>
    public virtual Task<RebaseRunResult> RunPlanAsync(
        RebaseScope scope, RebaseTodo todo,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
    {
        var compiled = RebaseTodoCompiler.Compile(todo, scope.Commits);
        if (!compiled.IsValid)
            return Refuse(compiled.Refusal!);

        var messageFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>(compiled.Commands.Count);
        foreach (var command in compiled.Commands)
        {
            switch (command.Kind)
            {
                case RebaseCommandKind.Pick:
                    lines.Add($"pick {command.Sha} {command.Subject}".TrimEnd());
                    break;
                case RebaseCommandKind.Fixup:
                    lines.Add($"fixup {command.Sha} {command.Subject}".TrimEnd());
                    break;
                case RebaseCommandKind.AmendMessage:
                    var token = MessageToken("plan-" + messageFiles.Count);
                    messageFiles[token] = command.Message!;
                    lines.Add(AmendExec(token));
                    break;
            }
        }

        return RunTodoAsync(scope, lines, messageFiles, policy, disableSigning, ct);
    }

    /// <summary>Runs an explicit todo against a scope. Public so the sequence-editor mechanism itself is directly testable.</summary>
    public virtual Task<RebaseRunResult> RunTodoAsync(
        RebaseScope scope, IReadOnlyList<string> todoLines, IReadOnlyDictionary<string, string> messageFiles,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, bool disableSigning = false, CancellationToken ct = default)
        => RunAsync(scope.RepoPath, scope.BaseSha, todoLines, messageFiles, policy, disableSigning, ct);

    /// <summary>
    /// The argument vector for one driven rebase, exposed so the flags themselves are assertable.
    /// With <paramref name="disableSigning"/> the run carries `-c commit.gpgsign=false`, which git
    /// exports to every child process it starts, so the amend execs in the todo are covered by the
    /// same pin rather than needing one of their own.
    /// </summary>
    public static IReadOnlyList<string> BuildRebaseArgs(string? baseSha, string emptyMode, bool disableSigning = false)
    {
        var args = new List<string>(ConfigPins);
        if (disableSigning) args.AddRange(["-c", "commit.gpgsign=false"]);
        args.AddRange(["rebase", "-i", "--empty=" + emptyMode]);
        if (baseSha is null) args.Add("--root");
        else { args.Add("--onto"); args.Add(baseSha); args.Add(baseSha); }
        return args;
    }

    /// <summary>
    /// The `--empty` spelling a `git --version` line accepts: `stop` from 2.45, `ask` before it.
    ///
    /// An unreadable version answers `ask`, which every version that has `--empty` accepts — a
    /// deprecation warning on a new git costs a noisier message, an unknown value costs the whole
    /// operation.
    /// </summary>
    public static string EmptyModeFor(string gitVersionOutput) =>
        GitVersion.MajorMinorFrom(gitVersionOutput) is { } version
        && (version.Major > 2 || (version.Major == 2 && version.Minor >= 45))
            ? "stop"
            : "ask";

    private async Task<string> EmptyModeAsync(string repoPath, CancellationToken ct)
    {
        if (_emptyMode is not null) return _emptyMode;
        var version = await _git.RunAsync(repoPath, ["--version"], ct, ShortTimeout);
        return _emptyMode = EmptyModeFor(version.Success ? version.StdOut : "");
    }

    private async Task<RebaseRunResult> RunAsync(
        string repoPath, string? baseSha, IReadOnlyList<string> todoLines,
        IReadOnlyDictionary<string, string> messageFiles,
        RebaseConflictPolicy policy, bool disableSigning, CancellationToken ct)
    {
        if (todoLines.Count == 0)
            return RebaseRunResult.Failed("the rebase todo is empty — nothing to do");

        SweepStaleScratch();
        var scratch = Path.Combine(_workRoot, "rebase-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(scratch);
        var keepScratch = false;
        try
        {
            WriteScratchOwner(scratch, repoPath);

            // Message files land in the scratch first: their absolute paths have to be known
            // before the exec lines that reference them are written.
            var resolvedTodo = MaterializeMessageFiles(todoLines, messageFiles, scratch);

            // Only the two variables this driver adds; the non-interactive pair comes from
            // GitService, which is also what starts the process.
            var env = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = SequenceEditorFor(WriteTodo(resolvedTodo, scratch)),
                ["GIT_EDITOR"] = "true"
            };

            var untrackedBefore = await UntrackedAsync(repoPath, ct);
            var args = BuildRebaseArgs(baseSha, await EmptyModeAsync(repoPath, ct), disableSigning);
            var run = await _git.RunAsync(repoPath, args, env, ct, DefaultTimeout);

            if (run.Success && !await IsRebaseInProgressAsync(repoPath, ct))
                return new RebaseRunResult
                {
                    Success = true,
                    HeadAfter = await HeadShaAsync(repoPath, ct),
                    Todo = resolvedTodo
                };

            var stopped = await HandleStopAsync(repoPath, run, resolvedTodo, untrackedBefore, policy, ct);
            // A rebase left stopped for the terminal still has our todo in its state dir, and
            // its exec lines point at message files in this scratch: deleting them would make
            // `git rebase --continue` fail on a missing file.
            keepScratch = stopped.LeftStopped;
            return stopped;
        }
        finally
        {
            if (!keepScratch) TryDeleteTree(scratch);
        }
    }

    /// <summary>
    /// Classifies a non-clean rebase and applies the conflict policy. A timeout is always
    /// aborted: ProcessRunner has already killed the git tree, so leaving the state dir behind
    /// would strand the repository mid-rebase with nothing driving it.
    /// </summary>
    private async Task<RebaseRunResult> HandleStopAsync(
        string repoPath, ProcessResult run, IReadOnlyList<string> todo, IReadOnlyList<string> untrackedBefore,
        RebaseConflictPolicy policy, CancellationToken ct)
    {
        var inProgress = await IsRebaseInProgressAsync(repoPath, ct);
        var output = run.StdErr + "\n" + run.StdOut;

        if (!inProgress)
            return new RebaseRunResult
            {
                Success = false,
                FailureReason = run.TimedOut
                    ? $"the rebase timed out after {DefaultTimeout.TotalMinutes:0} minute(s) and was killed"
                    : $"git rebase failed to start: {run.FirstError}",
                TimedOut = run.TimedOut,
                HeadAfter = await HeadShaAsync(repoPath, ct),
                Todo = todo
            };

        var (sha, subject) = await ReadStoppedCommitAsync(repoPath, ct);
        var empty = output.Contains("is now empty", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("could result in an empty commit", StringComparison.OrdinalIgnoreCase);
        var named = sha is null ? "a commit" : $"{Short(sha)}{(subject is null ? "" : $" ({subject})")}";
        var cause = run.TimedOut
            ? $"the rebase timed out after {DefaultTimeout.TotalMinutes:0} minute(s) while replaying {named}"
            : empty
                ? $"replaying {named} would produce an empty commit"
                : $"{named} conflicts with the new history";

        if (policy == RebaseConflictPolicy.LeaveStopped && !run.TimedOut)
            return new RebaseRunResult
            {
                Success = false,
                FailureReason = cause + " — the rebase is stopped; finish or abort it from the conflict panel or a terminal",
                ConflictCommit = sha,
                ConflictSubject = subject,
                StoppedEmpty = empty,
                LeftStopped = true,
                HeadAfter = await HeadShaAsync(repoPath, ct),
                Todo = todo
            };

        var abort = await _git.RunAsync(repoPath, ["rebase", "--abort"], ct, AbortTimeout);
        var stillInProgress = await IsRebaseInProgressAsync(repoPath, ct);
        if (!abort.Success || stillInProgress)
            return new RebaseRunResult
            {
                Success = false,
                FailureReason = cause + $" — and `git rebase --abort` did not clear it: {abort.FirstError}. " +
                                "The repository is still mid-rebase; recover from the backup or finish it in a terminal.",
                ConflictCommit = sha,
                ConflictSubject = subject,
                StoppedEmpty = empty,
                LeftStopped = true,
                TimedOut = run.TimedOut,
                HeadAfter = await HeadShaAsync(repoPath, ct),
                Todo = todo
            };

        // The abort restores refs, index and tracked content; untracked files written during the
        // replay (by a hook) are git's to leave behind, and the next gated operation refuses a
        // tree carrying them, so they are named here rather than discovered later.
        var added = await NewUntrackedAsync(repoPath, untrackedBefore, ct);
        var untrackedNote = added.Count == 0
            ? ""
            : $" {added.Count} untracked file(s) written during the replay were left in the working tree: {Join(added)}.";

        return new RebaseRunResult
        {
            Success = false,
            FailureReason = cause + " — the rebase was aborted; refs, index and tracked content are unchanged." + untrackedNote,
            ConflictCommit = sha,
            ConflictSubject = subject,
            StoppedEmpty = empty,
            TimedOut = run.TimedOut,
            Aborted = true,
            RepositoryUntouched = true,
            UntrackedAdded = added,
            HeadAfter = await HeadShaAsync(repoPath, ct),
            Todo = todo
        };
    }

    /// <summary>Untracked, non-ignored paths. Ignored files are excluded: the tree gate ignores them too.</summary>
    private async Task<IReadOnlyList<string>> UntrackedAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, ["ls-files", "--others", "--exclude-standard"], ct, ShortTimeout);
        if (!result.Success) return [];
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    private async Task<IReadOnlyList<string>> NewUntrackedAsync(
        string repoPath, IReadOnlyList<string> before, CancellationToken ct)
    {
        var known = new HashSet<string>(before, StringComparer.Ordinal);
        return (await UntrackedAsync(repoPath, ct)).Where(p => !known.Contains(p)).ToList();
    }

    private static string Join(IReadOnlyList<string> paths)
    {
        var named = paths.Take(10).ToList();
        var listed = string.Join(", ", named);
        if (paths.Count > named.Count) listed += $", … (+{paths.Count - named.Count} more)";
        return listed;
    }

    /// <summary>
    /// The commit a stopped rebase halted on. `stopped-sha` is written for a conflicted pick;
    /// the last line of `done` is the command git was executing and covers the cases where it
    /// is not (an empty-pick stop, a failed exec).
    /// </summary>
    private async Task<(string? Sha, string? Subject)> ReadStoppedCommitAsync(string repoPath, CancellationToken ct)
    {
        var gitDir = await ResolveGitDirAsync(repoPath, ct);
        if (gitDir is null) return (null, null);

        string? sha = null;
        foreach (var dirName in new[] { "rebase-merge", "rebase-apply" })
        {
            var dir = Path.Combine(gitDir, dirName);
            if (!Directory.Exists(dir)) continue;

            sha = ReadFirstToken(Path.Combine(dir, "stopped-sha"))
                  ?? ReadFirstToken(Path.Combine(dir, "original-commit"))
                  ?? ReadTodoLineSha(Path.Combine(dir, "done"));
            if (sha is not null) break;
        }
        if (sha is null) return (null, null);

        var full = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", sha + "^{commit}"], ct, ShortTimeout);
        if (full.Success) sha = full.StdOut.Trim();

        var subject = await _git.RunAsync(repoPath, ["log", "-1", "--format=%s", sha], ct, ShortTimeout);
        return (sha, subject.Success ? subject.StdOut.Trim() : null);
    }

    private static string? ReadFirstToken(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text.Split(' ', '\n', '\r')[0];
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read rebase state file {path}", ex);
            return null;
        }
    }

    /// <summary>Sha from the last real command in a rebase `done` file (`pick &lt;sha&gt; &lt;subject&gt;`).</summary>
    private static string? ReadTodoLineSha(string donePath)
    {
        try
        {
            if (!File.Exists(donePath)) return null;
            foreach (var raw in File.ReadAllLines(donePath).Reverse())
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] is "pick" or "p" or "fixup" or "f" or "squash" or "s" or "reword" or "r" or "edit" or "e")
                    return parts[1];
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read rebase done file {donePath}", ex);
        }
        return null;
    }

    private async Task<bool> IsRebaseInProgressAsync(string repoPath, CancellationToken ct)
    {
        var gitDir = await ResolveGitDirAsync(repoPath, ct);
        return gitDir is not null &&
               (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
                Directory.Exists(Path.Combine(gitDir, "rebase-apply")));
    }

    private Task<string?> ResolveGitDirAsync(string repoPath, CancellationToken ct) =>
        _git.ResolveGitDirAsync(repoPath, ct, ShortTimeout);

    private async Task<string> HeadShaAsync(string repoPath, CancellationToken ct)
    {
        var head = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", "HEAD"], ct, ShortTimeout);
        return head.Success ? head.StdOut.Trim() : "";
    }

    // ── todo construction ─────────────────────────────────────────────────

    private static string Pick(RebaseCommit commit) => $"pick {commit.Sha} {commit.Subject}".TrimEnd();

    /// <summary>
    /// A placeholder standing in for a message file's path until the scratch dir exists.
    /// Wrapped in control characters so it can never collide with a commit subject on a
    /// neighbouring pick line.
    /// </summary>
    private static string MessageToken(string name) => "\u0001" + name + "\u0001";

    /// <summary>
    /// An amend exec whose message-file path is filled in once the scratch dir exists.
    /// --no-verify matches git's own replay: a rebase does not run commit hooks for the
    /// commits it picks, and a hook firing here would stop the rebase mid-run.
    ///
    /// The message cleanup pin is the same one every other message-carrying git call in this app
    /// uses: under `strip` a `#`-prefixed subject is dropped as commentary — storing a message
    /// that differs from the one the caller confirmed — and a message whose every line starts
    /// with `#` empties, which fails the exec and surfaces as a stopped rebase.
    /// </summary>
    private string AmendExec(string messageToken) =>
        $"exec {ShellArg(_gitExe)} commit --amend --no-verify {GitService.MessageCleanupPin} -F {messageToken}";

    /// <summary>
    /// Writes each message to the scratch and substitutes its shell-quoted path for the
    /// placeholder token in the todo. The token is a control character, so it cannot collide
    /// with a commit subject.
    /// </summary>
    private static List<string> MaterializeMessageFiles(
        IReadOnlyList<string> todoLines, IReadOnlyDictionary<string, string> messageFiles, string scratch)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var (token, message) in messageFiles)
        {
            var path = Path.Combine(scratch, $"message-{index++}.txt");
            // No trailing-newline fixup: `commit -F` applies its own whitespace cleanup, and
            // adding one here would silently differ from what the caller asked to store.
            File.WriteAllText(path, message, Utf8NoBom);
            replacements[token] = ShellArg(path);
        }

        var resolved = new List<string>(todoLines.Count);
        foreach (var line in todoLines)
        {
            var text = line;
            foreach (var (token, path) in replacements)
                text = text.Replace(token, path, StringComparison.Ordinal);
            resolved.Add(text);
        }
        return resolved;
    }

    private static string WriteTodo(IReadOnlyList<string> todoLines, string scratch)
    {
        var path = Path.Combine(scratch, "prepared-todo");
        // LF only: git parses the todo line by line and a CR would ride along into subjects.
        File.WriteAllText(path, string.Join('\n', todoLines) + "\n", Utf8NoBom);
        return path;
    }

    /// <summary>`cp "&lt;prepared&gt;"`; git appends the generated todo path, so the copy overwrites it.</summary>
    private static string SequenceEditorFor(string preparedTodoPath) => "cp " + ShellArg(preparedTodoPath);

    /// <summary>
    /// One double-quoted argument for git's MSYS shell. Backslashes become forward slashes —
    /// both Windows and MSYS accept them, and a trailing `\"` inside double quotes would
    /// otherwise escape the closing quote.
    /// </summary>
    private static string ShellArg(string path)
    {
        var forward = path.Replace('\\', '/');
        var escaped = new StringBuilder(forward.Length + 2);
        escaped.Append('"');
        foreach (var c in forward)
        {
            if (c is '"' or '$' or '`') escaped.Append('\\');
            escaped.Append(c);
        }
        escaped.Append('"');
        return escaped.ToString();
    }

    // ── small helpers ─────────────────────────────────────────────────────

    private static ScopeShaIndex Index(RebaseScope scope) => ScopeShaIndex.For(scope.Commits);

    private static int IndexOf(RebaseScope scope, string sha)
    {
        for (var i = 0; i < scope.Commits.Count; i++)
            if (string.Equals(scope.Commits[i].Sha, sha, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Short(string sha) => SurgeryText.Short(sha);

    private static Task<RebaseRunResult> Refuse(string reason) => Task.FromResult(RebaseRunResult.Failed(reason));

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log.Warn($"could not delete surgery scratch tree {path}", ex); }
    }

    // ── scratch reclamation ───────────────────────────────────────────────

    /// <summary>Records which repository a scratch tree serves, so the sweep can tell a live one from a leak.</summary>
    private static void WriteScratchOwner(string scratch, string repoPath)
    {
        try { File.WriteAllText(Path.Combine(scratch, OwnerFileName), repoPath, Utf8NoBom); }
        catch (Exception ex) { Log.Warn($"could not record the owner of surgery scratch {scratch}", ex); }
    }

    /// <summary>
    /// Reclaims scratch trees the `finally` never reached — a crash or a kill during a rebase.
    /// A tree is kept while its repository is still mid-rebase, because the stopped todo's exec
    /// lines point at message files inside it and `git rebase --continue` would fail without
    /// them. Runs once per driver instance, before the first rebase.
    /// </summary>
    private void SweepStaleScratch()
    {
        if (_swept) return;
        _swept = true;
        try
        {
            if (!Directory.Exists(_workRoot)) return;
            var cutoff = DateTime.UtcNow - ScratchGrace;
            foreach (var dir in Directory.GetDirectories(_workRoot))
            {
                if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                var owner = ReadScratchOwner(dir);
                if (owner is not null && HasRebaseState(owner)) continue;
                TryDeleteTree(dir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not sweep the surgery scratch root {_workRoot}", ex);
        }
    }

    /// <summary>
    /// Where a stopped rebase came from, for a surface deciding whether it may be continued.
    /// </summary>
    public enum StoppedRebaseOrigin
    {
        /// <summary>No rebase state directory survives in the repository.</summary>
        NotStopped,

        /// <summary>This driver stopped it and every message file its todo names is still on disk.</summary>
        StartedHere,

        /// <summary>Its todo names message files that no longer exist; `--continue` fails on the missing file.</summary>
        MessagesReclaimed,

        /// <summary>No scratch tree here owns it — it was started outside this application.</summary>
        StartedElsewhere
    }

    /// <summary>
    /// Classifies the rebase a repository is stopped in. Filesystem reads only, so a surface can
    /// consult it on every working-state refresh.
    ///
    /// A stopped todo's exec lines name message files inside this driver's scratch, and
    /// `git rebase --continue` fails on a missing one. The scratch is kept for exactly that reason
    /// while a rebase is stopped, but a repository moved or renamed since then reads as
    /// no-longer-rebasing to the sweep, which then reclaims the tree the stopped todo still points
    /// at. A continue offered over that state fails where a terminal would fail too, with no
    /// explanation either place; refusing it and offering the abort is the honest answer.
    /// </summary>
    public StoppedRebaseOrigin InspectStoppedRebase(string repoPath)
    {
        if (!HasRebaseState(repoPath)) return StoppedRebaseOrigin.NotStopped;

        var missing = TodoNamesMissingScratchFile(repoPath);
        if (missing) return StoppedRebaseOrigin.MessagesReclaimed;
        return DroveThisRebase(repoPath) ? StoppedRebaseOrigin.StartedHere : StoppedRebaseOrigin.StartedElsewhere;
    }

    /// <summary>
    /// Whether a scratch tree under this driver's root drove the rebase this repository is stopped
    /// in — proven by the todo, not by the owner name alone.
    ///
    /// Ownership by repository path alone would let a scratch tree left behind by an EARLIER
    /// rebase of the same repository claim a later one somebody started in a terminal, and the
    /// abort-only rule for a rebase begun outside this app would silently stop applying.
    ///
    /// The todo git is running is `done` (every command through the one it stopped on) followed by
    /// `git-rebase-todo` (the rest), both in the form the sequence editor supplied. Its own
    /// `git-rebase-todo.backup` is NOT that list — it is the todo git generated BEFORE the editor
    /// replaced it, so it never matches a prepared one and cannot answer this.
    /// </summary>
    private bool DroveThisRebase(string repoPath)
    {
        try
        {
            if (!Directory.Exists(_workRoot)) return false;
            if (GitDirOf(repoPath) is not { } gitDir) return false;

            var running = RunningTodo(Path.Combine(gitDir, "rebase-merge"));
            if (running.Count == 0) return false;

            foreach (var dir in Directory.GetDirectories(_workRoot))
            {
                if (ReadScratchOwner(dir) is not { } owner || !SamePath(owner, repoPath)) continue;
                var prepared = Path.Combine(dir, "prepared-todo");
                if (!File.Exists(prepared)) continue;
                if (running.SequenceEqual(TodoCommands(File.ReadAllLines(prepared)))) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not match the stopped rebase of {repoPath} to a scratch tree", ex);
            return false;
        }
    }

    /// <summary>The whole todo the stopped rebase is running: what it has done, then what is left.</summary>
    private static List<string> RunningTodo(string stateDir)
    {
        var done = Path.Combine(stateDir, "done");
        var remaining = Path.Combine(stateDir, "git-rebase-todo");
        List<string> todo = [];
        if (File.Exists(done)) todo.AddRange(TodoCommands(File.ReadAllLines(done)));
        if (File.Exists(remaining)) todo.AddRange(TodoCommands(File.ReadAllLines(remaining)));
        return todo;
    }

    /// <summary>A todo's command lines, without the comments and blanks git writes around them.</summary>
    private static List<string> TodoCommands(IEnumerable<string> lines) =>
        [.. lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    /// <summary>
    /// Whether the remaining todo names a file under this driver's scratch root that is gone. The
    /// exec lines carry shell-quoted absolute paths, so a quoted token under the root is the whole
    /// of what has to be checked.
    /// </summary>
    private bool TodoNamesMissingScratchFile(string repoPath)
    {
        try
        {
            var gitDir = GitDirOf(repoPath);
            if (gitDir is null) return false;
            var todo = Path.Combine(gitDir, "rebase-merge", "git-rebase-todo");
            if (!File.Exists(todo)) return false;

            var root = _workRoot.Replace('\\', '/');
            foreach (var line in File.ReadAllLines(todo))
            {
                if (!line.StartsWith("exec ", StringComparison.Ordinal)) continue;
                foreach (var quoted in QuotedTokens(line))
                {
                    if (!quoted.Replace('\\', '/').StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(quoted)) return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the stopped rebase todo of {repoPath}", ex);
            return false;
        }
    }

    private static IEnumerable<string> QuotedTokens(string line)
    {
        var from = line.IndexOf('"');
        while (from >= 0)
        {
            var to = line.IndexOf('"', from + 1);
            if (to < 0) yield break;
            yield return line[(from + 1)..to];
            from = line.IndexOf('"', to + 1);
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("could not compare two repository paths", ex);
            return false;
        }
    }

    private static string? ReadScratchOwner(string scratch)
    {
        try
        {
            var path = Path.Combine(scratch, OwnerFileName);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the owner of surgery scratch {scratch}", ex);
            return null;
        }
    }

    /// <summary>
    /// Whether a rebase state directory survives in that repository. Resolved from the filesystem
    /// rather than from git so the sweep costs no process launches; a `.git` file is a linked
    /// worktree and names its real git dir.
    /// </summary>
    private static bool HasRebaseState(string repoPath)
    {
        var gitDir = GitDirOf(repoPath);
        if (gitDir is null) return false;
        return Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
               Directory.Exists(Path.Combine(gitDir, "rebase-apply"));
    }

    /// <summary>
    /// A checkout's git directory, without launching git. Null for anything this cannot answer
    /// from the layout alone — which for the callers here means the repository has no rebase to
    /// continue, and its scratch is reclaimable.
    /// </summary>
    private static string? GitDirOf(string repoPath)
    {
        try
        {
            var dotGit = Path.Combine(repoPath, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;

            var line = File.ReadAllLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null) return null;
            var target = line["gitdir:".Length..].Trim();
            return Path.IsPathRooted(target) ? target : Path.Combine(repoPath, target);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not resolve the git directory of {repoPath}", ex);
            return null;
        }
    }
}
