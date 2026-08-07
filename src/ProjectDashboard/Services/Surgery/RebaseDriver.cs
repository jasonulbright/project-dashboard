using System.IO;
using System.Text;
using ProjectDashboard.Services.History;

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
/// returns the repository to its exact pre-operation state, and reports the commit that stopped
/// it. A rebase that exceeds its timeout is killed and aborted regardless of policy.
///
/// `--empty=stop` is explicit rather than inherited: a replayed commit that becomes empty is a
/// stop the caller is told about, never a commit silently dropped from the history.
///
/// Commit signing is deliberately left alone — overriding it would strip signatures the user
/// asked for. A repository configured to sign with a key whose passphrase is not cached will
/// stall on a pinentry prompt this app cannot answer; the timeout kills it and aborts.
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

    private readonly GitService _git;
    private readonly string _gitExe;
    private readonly string _workRoot;

    public RebaseDriver(GitService git, string? gitExecutable = null, string? workRoot = null)
    {
        _git = git;
        _gitExe = gitExecutable ?? HistoryPipeline.ResolveGitExecutable();
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
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
    {
        var byId = Index(scope);
        if (shasInNewOrder.Count != scope.Commits.Count)
            return Refuse($"reorder must list all {scope.Commits.Count} commit(s) in the range, got {shasInNewOrder.Count}");

        var todo = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in shasInNewOrder)
        {
            if (!byId.TryGetValue(sha, out var commit))
                return Refuse($"commit {Short(sha)} is not in the editable range");
            if (!seen.Add(commit.Sha))
                return Refuse($"commit {Short(sha)} listed twice in the new order");
            todo.Add(Pick(commit));
        }

        return RunTodoAsync(scope, todo, EmptyMessages, policy, ct);
    }

    /// <summary>Removes commits from the replay. At least one must remain — emptying a branch is a reset, not a rebase.</summary>
    public Task<RebaseRunResult> DropAsync(
        RebaseScope scope, IReadOnlyList<string> shasToDrop,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
    {
        var byId = Index(scope);
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha in shasToDrop)
        {
            if (!byId.TryGetValue(sha, out var commit))
                return Refuse($"commit {Short(sha)} is not in the editable range");
            drop.Add(commit.Sha);
        }
        if (drop.Count == 0)
            return Refuse("no commits selected to drop");

        var kept = scope.Commits.Where(c => !drop.Contains(c.Sha)).ToList();
        if (kept.Count == 0)
            return Refuse("dropping every commit in the range would empty the branch — use a reset instead");

        return RunTodoAsync(scope, kept.Select(Pick).ToList(), EmptyMessages, policy, ct);
    }

    /// <summary>
    /// Folds a contiguous run of commits into its first commit. With <paramref name="newMessage"/>
    /// null the first commit's message is kept (`fixup`); otherwise the fixups are followed by one
    /// `exec ... commit --amend -F` that installs the new message.
    /// </summary>
    public Task<RebaseRunResult> SquashAsync(
        RebaseScope scope, IReadOnlyList<string> shasToFold, string? newMessage = null,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
    {
        var byId = Index(scope);
        if (shasToFold.Count < 2)
            return Refuse("a squash needs at least two commits");

        var resolved = new List<RebaseCommit>();
        foreach (var sha in shasToFold)
        {
            if (!byId.TryGetValue(sha, out var commit))
                return Refuse($"commit {Short(sha)} is not in the editable range");
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

        return RunTodoAsync(scope, todo, messageFiles, policy, ct);
    }

    /// <summary>
    /// Replaces one commit's message at any depth. `reword` would open an editor, so the todo
    /// picks the commit and amends it from a file instead.
    /// </summary>
    public Task<RebaseRunResult> RewordAsync(
        RebaseScope scope, string sha, string newMessage,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newMessage))
            return Refuse("a commit message cannot be empty");
        if (!Index(scope).TryGetValue(sha, out var target))
            return Refuse($"commit {Short(sha)} is not in the editable range");

        var messageToken = MessageToken("reword");
        var todo = new List<string>();
        foreach (var commit in scope.Commits)
        {
            todo.Add(Pick(commit));
            if (commit.Sha == target.Sha) todo.Add(AmendExec(messageToken));
        }

        return RunTodoAsync(scope, todo,
            new Dictionary<string, string>(StringComparer.Ordinal) { [messageToken] = newMessage }, policy, ct);
    }

    /// <summary>
    /// Runs `git rebase -i --autosquash` and KEEPS git's generated todo — the arrangement of
    /// `fixup!`/`squash!` commits is exactly what this mode is for, so the sequence editor is a
    /// no-op here instead of the usual overwrite. Backs <see cref="CommitSurgery"/>.
    /// </summary>
    public virtual Task<RebaseRunResult> AutosquashAsync(
        string repoPath, string? baseSha,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
        => RunAsync(repoPath, baseSha, todoLines: null, messageFiles: new Dictionary<string, string>(),
                    autosquash: true, policy, ct);

    /// <summary>Runs an explicit todo against a scope. Public so the sequence-editor mechanism itself is directly testable.</summary>
    public virtual Task<RebaseRunResult> RunTodoAsync(
        RebaseScope scope, IReadOnlyList<string> todoLines, IReadOnlyDictionary<string, string> messageFiles,
        RebaseConflictPolicy policy = RebaseConflictPolicy.AbortAndReport, CancellationToken ct = default)
        => RunAsync(scope.RepoPath, scope.BaseSha, todoLines, messageFiles, autosquash: false, policy, ct);

    private async Task<RebaseRunResult> RunAsync(
        string repoPath, string? baseSha, IReadOnlyList<string>? todoLines,
        IReadOnlyDictionary<string, string> messageFiles, bool autosquash,
        RebaseConflictPolicy policy, CancellationToken ct)
    {
        if (todoLines is { Count: 0 })
            return RebaseRunResult.Failed("the rebase todo is empty — nothing to do");

        var scratch = Path.Combine(_workRoot, "rebase-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(scratch);
        var keepScratch = false;
        try
        {
            // Message files land in the scratch first: their absolute paths have to be known
            // before the exec lines that reference them are written.
            var resolvedTodo = todoLines is null ? [] : MaterializeMessageFiles(todoLines, messageFiles, scratch);

            var env = new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_OPTIONAL_LOCKS"] = "0",
                // Overwrite the generated todo, except in autosquash mode where git's own
                // arrangement IS the instruction.
                ["GIT_SEQUENCE_EDITOR"] = todoLines is null ? "true" : SequenceEditorFor(WriteTodo(resolvedTodo, scratch)),
                ["GIT_EDITOR"] = "true"
            };

            var args = new List<string>(ConfigPins) { "rebase", "-i", "--empty=stop" };
            if (autosquash) args.Add("--autosquash");
            if (baseSha is null) args.Add("--root");
            else { args.Add("--onto"); args.Add(baseSha); args.Add(baseSha); }

            var run = await ProcessRunner.RunAsync(_gitExe, args, repoPath, DefaultTimeout, env, ct);

            if (run.Success && !await IsRebaseInProgressAsync(repoPath, ct))
                return new RebaseRunResult
                {
                    Success = true,
                    HeadAfter = await HeadShaAsync(repoPath, ct),
                    Todo = resolvedTodo
                };

            var stopped = await HandleStopAsync(repoPath, run, resolvedTodo, policy, ct);
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
        string repoPath, ProcessResult run, IReadOnlyList<string> todo, RebaseConflictPolicy policy, CancellationToken ct)
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
                FailureReason = cause + " — the rebase is stopped; finish or abort it in a terminal",
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

        return new RebaseRunResult
        {
            Success = false,
            FailureReason = cause + " — the rebase was aborted and the repository is unchanged",
            ConflictCommit = sha,
            ConflictSubject = subject,
            StoppedEmpty = empty,
            TimedOut = run.TimedOut,
            Aborted = true,
            HeadAfter = await HeadShaAsync(repoPath, ct),
            Todo = todo
        };
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

    private async Task<string?> ResolveGitDirAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, ["rev-parse", "--git-dir"], ct, ShortTimeout);
        if (!result.Success) return null;
        var gitDir = result.StdOut.Trim();
        return gitDir.Length == 0 ? null : Path.IsPathRooted(gitDir) ? gitDir : Path.Combine(repoPath, gitDir);
    }

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
    /// </summary>
    private string AmendExec(string messageToken) =>
        $"exec {ShellArg(_gitExe)} commit --amend --no-verify -F {messageToken}";

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

    private static Dictionary<string, RebaseCommit> Index(RebaseScope scope)
    {
        var map = new Dictionary<string, RebaseCommit>(StringComparer.OrdinalIgnoreCase);
        foreach (var commit in scope.Commits)
        {
            map[commit.Sha] = commit;
            // Abbreviations let a caller pass what the UI displays without re-resolving.
            for (var length = 7; length < commit.Sha.Length; length++)
                map.TryAdd(commit.Sha[..length], commit);
        }
        return map;
    }

    private static int IndexOf(RebaseScope scope, string sha)
    {
        for (var i = 0; i < scope.Commits.Count; i++)
            if (string.Equals(scope.Commits[i].Sha, sha, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static Task<RebaseRunResult> Refuse(string reason) => Task.FromResult(RebaseRunResult.Failed(reason));

    private static void TryDeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log.Warn($"could not delete surgery scratch tree {path}", ex); }
    }
}
