using System.IO;
using System.Text;
using ProjectDashboard.Models;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.Services;

public class GitService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Environment for every git call in this application: never prompt for credentials (a
    /// windowless app would hang invisibly), never take optional index locks during reads, and
    /// emit messages in one fixed language. Several decisions are made by matching git's own
    /// words — a rejected lease, a held index lock, a commit a rebase emptied — and git
    /// translates those words, so the locale pin is load-bearing, not cosmetic.
    /// The single source — a caller that needs more variables merges onto this rather than
    /// writing its own set, or a git process started somewhere else can still block on a
    /// prompt no window shows or answer in a language no sniff matches.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> NonInteractiveEnvironment =
        new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["LC_ALL"] = "C",
            ["LANGUAGE"] = "C"
        };

    /// <summary>
    /// Field separator for every --format this app parses. A unit separator cannot occur in a ref
    /// name, a reflog subject, or a commit subject, so no value can split a record.
    /// </summary>
    internal const string FieldSeparator = "\u001f";

    /// <summary>
    /// A concrete path as a pathspec that selects only itself. Every path this service passes to
    /// git names one file — it comes from git's own output or from the working tree, never from
    /// somebody writing a pattern — and as a bare pathspec a name holding '*', '?', or a bracket
    /// range also selects the other paths it globs, so a read describes, and a write reverts, a
    /// file the caller never named.
    /// <para>
    /// Per-pathspec magic, not GIT_LITERAL_PATHSPECS: that variable makes git stop parsing
    /// pathspec magic at all, which narrows the rewrite scrub's own :(glob) and :(literal)
    /// pathspecs down to nothing and makes `git check-ignore` exit 128 on every path.
    /// </para>
    /// <para>
    /// Commands that take a pathNAME rather than a pathspec — `blame`, `check-ignore` — reject
    /// this magic and already resolve a name to itself, so those pass their path bare.
    /// </para>
    /// </summary>
    internal static string LiteralPathspec(string path) => ":(literal)" + path;

    /// <summary>
    /// git's own default for a commit message it is handed rather than opening an editor for,
    /// pinned so a repository configured with commit.cleanup=strip cannot rewrite one. `tag -a`
    /// takes strip as its own default and honours no config, so the pin is the only thing
    /// keeping an annotated tag message intact. Under strip every line of the message starting
    /// with the comment character is deleted, which silently drops an issue reference like
    /// "#42 …" and — when that was the whole subject — leaves a commit rejected as empty and a
    /// tag recorded with no message at all. Every commit, amend and annotated tag this app runs
    /// carries the pin.
    /// </summary>
    internal const string MessageCleanupPin = "--cleanup=whitespace";

    /// <summary>
    /// True when the directory is a git checkout. A primary checkout has a .git
    /// DIRECTORY; a linked worktree or submodule has a .git FILE — accept both.
    /// </summary>
    public static bool IsGitRepo(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    /// <summary>
    /// Everything a dashboard card is built from, read in one pass: the repository's status and
    /// its recent-commit window. The window's first row IS the tip, so the summary's last-commit
    /// date and subject are taken from it rather than from a <c>git log</c> of their own — but
    /// only when the walk that produced the window completed. A walk that died partway through
    /// history yields the same empty window a commitless repository does, and passing that on
    /// would report a damaged repository as having no commits.
    /// </summary>
    public async Task<RepoCardState> GetCardStateAsync(string repoPath, int commitCount, CancellationToken ct = default)
    {
        var (walked, commits) = await ReadRecentCommitsAsync(repoPath, commitCount, ct);
        return new RepoCardState(await GetStatusAsync(repoPath, walked ? commits : null, ct), commits);
    }

    public Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken ct = default) =>
        GetStatusAsync(repoPath, recentCommits: null, ct);

    /// <summary>
    /// <paramref name="recentCommits"/> is a window a completed walk of HEAD from the tip read
    /// this repository's log into — the same walk the tip read below performs. Null means no such
    /// window exists and the tip is read here; empty means the completed walk found no commits,
    /// which is what a repository with no commits yet reports and leaves the tip fields blank.
    /// </summary>
    private async Task<GitStatus> GetStatusAsync(
        string repoPath, IReadOnlyList<GitCommit>? recentCommits, CancellationToken ct)
    {
        var status = new GitStatus();

        // One porcelain-v2 read is THE critical signal (works on commitless repos too):
        // dirty state, branch, detached, upstream divergence, and conflicts together.
        var state = await GetWorkingStateAsync(repoPath, ct);
        if (state is null)
        {
            // git missing / stale PATH / broken repo — must NOT masquerade as a clean repo.
            status.HasError = true;
            return status;
        }

        status.IsDirty = state.IsDirty;
        status.UntrackedCount = state.Files.Count(f => f.IsUntracked);
        status.ModifiedCount = state.Files.Count - status.UntrackedCount;
        status.Branch = state.Detached ? "HEAD" : state.Branch;
        status.IsDetached = state.Detached;
        status.HasConflicts = state.HasConflicts;
        status.AheadBy = state.Ahead;
        status.BehindBy = state.Behind;
        status.ActivityLabel = state.Activity switch
        {
            RepoActivity.Merging => "merge",
            RepoActivity.Rebasing => "rebase",
            RepoActivity.CherryPicking => "cherry-pick",
            RepoActivity.Reverting => "revert",
            RepoActivity.Bisecting => "bisect",
            _ => ""
        };

        // Best-effort metadata. A fresh repo with no commits/tags/remote is normal
        // and must not blank out the signals above.
        try { status.LatestTag = (await RunGitAsync(repoPath, ["describe", "--tags", "--abbrev=0"], ct)).Trim(); }
        catch { /* no tags */ }

        if (recentCommits is null)
        {
            try
            {
                var logLine = await RunGitAsync(
                    repoPath, ["log", "-1", "--format=%aI" + FieldSeparator + "%s"], ct);
                if (!string.IsNullOrWhiteSpace(logLine))
                {
                    var parts = logLine.Trim().Split(FieldSeparator, 2);
                    if (parts.Length >= 1 && DateTimeOffset.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var date))
                        status.LastCommitDate = date;
                    if (parts.Length >= 2)
                        status.LastCommitMessage = parts[1];
                }
            }
            catch { /* no commits yet */ }
        }
        else if (recentCommits.Count > 0)
        {
            var tip = recentCommits[0];
            if (tip.Date != default) status.LastCommitDate = tip.Date;
            status.LastCommitMessage = tip.Message;
        }

        // A repo whose only remote has a non-origin name (renamed, single "github"
        // remote) must not read as local: an empty RemoteUrl means cloud-off UI,
        // no gh enrichment, and Sync All skips the repo. The default remote is the
        // first of this list, so it needs no read of its own.
        var remotes = await ReadRemoteUrlsAsync(repoPath, ct);
        if (remotes.Count > 0) status.RemoteUrl = remotes[0].Url;

        return status;
    }

    public async Task<List<GitCommit>> GetRecentCommitsAsync(string repoPath, int count = 20, CancellationToken ct = default) =>
        (await ReadRecentCommitsAsync(repoPath, count, ct)).Commits;

    /// <summary>
    /// The recent-commit window plus whether the walk that produced it ran to completion.
    /// An empty window says nothing on its own: a repository with no commits, a walk that died on
    /// a missing object partway through history, and a window nobody asked for all produce one.
    /// Only a completed walk of a window someone asked for describes what HEAD actually has.
    /// </summary>
    private async Task<(bool Walked, List<GitCommit> Commits)> ReadRecentCommitsAsync(
        string repoPath, int count, CancellationToken ct)
    {
        var commits = new List<GitCommit>();
        if (count <= 0) return (false, commits);

        try
        {
            var output = await RunGitAsync(repoPath, ["log", CommitLogFormat, "-n", count.ToString()], ct);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (ParseCommitLine(line) is { } commit) commits.Add(commit);
        }
        catch (Exception ex)
        {
            Log.Warn($"git log failed for {repoPath}", ex);
            return (false, commits);
        }

        return (true, commits);
    }

    /// <summary>
    /// Full working-tree state in one porcelain-v2 call (+ a git-dir probe for
    /// merge/rebase state). Null when git can't read the repo at all.
    /// </summary>
    public async Task<WorkingState?> GetWorkingStateAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["status", "--porcelain=v2", "--branch"], ct);
        if (!result.Success)
        {
            Log.Warn($"git status v2 failed for {repoPath}: {result.FirstError}");
            return null;
        }
        var state = WorkingState.Parse(result.StdOut);
        state.Activity = await DetectActivityAsync(repoPath, ct);
        return state;
    }

    /// <summary>
    /// Every parentless commit reachable from any ref, sorted. One process, and the only read
    /// the repository fingerprint needs beyond what a card already costs.
    ///
    /// Empty for a repository with no commits, and empty for one git could not read — the caller
    /// treats both as "nothing to identify this by" rather than as an identity of its own, so an
    /// unreadable repository can never match another unreadable one.
    /// </summary>
    public async Task<string[]> GetRootCommitsAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["rev-list", "--max-parents=0", "--all"], ct);
        if (!result.Success)
        {
            Log.Warn($"git rev-list of root commits failed for {repoPath}: {result.FirstError}");
            return [];
        }

        return [.. result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)];
    }

    /// <summary>Real git dir for a checkout — a linked worktree's .git is a file pointing elsewhere. Null when git can't read the repo.</summary>
    public async Task<string?> ResolveGitDirAsync(string repoPath, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var result = await RunAsync(repoPath, ["rev-parse", "--git-dir"], ct, timeout);
        if (!result.Success) return null;

        var gitDir = result.StdOut.Trim();
        if (gitDir.Length == 0) return null;
        return Path.IsPathRooted(gitDir) ? gitDir : Path.Combine(repoPath, gitDir);
    }

    /// <summary>
    /// The git directory of a primary checkout, without launching git: a directory named .git IS
    /// the git directory, which is the same answer `rev-parse --git-dir` gives for that layout.
    /// Null where the answer is not the layout's — a linked worktree or submodule, whose .git is
    /// a file naming a directory elsewhere, and an inherited GIT_DIR, which overrides discovery
    /// altogether. Both fall back to asking git.
    /// </summary>
    private static string? LayoutGitDir(string repoPath)
    {
        if (Environment.GetEnvironmentVariable("GIT_DIR") is { Length: > 0 }) return null;
        var dotGit = Path.Combine(repoPath, ".git");
        return Directory.Exists(dotGit) ? dotGit : null;
    }

    /// <summary>
    /// What the repository is in the middle of, from its git directory alone. Read on its own by
    /// the sequencer driver, which has to know whether a commit it just wrote finished the
    /// sequence before it decides to continue one.
    /// </summary>
    internal async Task<RepoActivity> DetectActivityAsync(string repoPath, CancellationToken ct = default)
    {
        var gitDir = LayoutGitDir(repoPath) ?? await ResolveGitDirAsync(repoPath, ct);
        if (gitDir is null) return RepoActivity.None;

        // Rebase first: a rebase stopped on a conflict has no MERGE_HEAD but does
        // have the rebase state dir, and "rebasing" is the more precise banner.
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return RepoActivity.Rebasing;
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return RepoActivity.Merging;
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return RepoActivity.CherryPicking;
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return RepoActivity.Reverting;
        if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return RepoActivity.Bisecting;
        return RepoActivity.None;
    }

    /// <summary>Init + stage + first commit for New Project. Returns null on success, else a short error.</summary>
    public async Task<string?> InitWithFirstCommitAsync(string repoPath, string commitMessage, CancellationToken ct = default)
    {
        var init = await RunAsync(repoPath, ["init"], ct);
        if (!init.Success) return $"git init failed: {init.FirstError}";

        var add = await RunAsync(repoPath, ["add", "-A"], ct);
        if (!add.Success) return $"git add failed: {add.FirstError}";

        var commit = await RunAsync(repoPath, ["commit", MessageCleanupPin, "-m", commitMessage], ct);
        if (!commit.Success) return $"git commit failed: {commit.FirstError}";

        return null;
    }

    /// <summary>Structured run for callers that need exit codes and stderr (no throw on failure).</summary>
    public Task<ProcessResult> RunAsync(string repoPath, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null) =>
        RunAsync(repoPath, args, null, ct, timeout);

    /// <summary>
    /// Structured run with extra environment variables layered over
    /// <see cref="NonInteractiveEnvironment"/>. For the callers that need a variable of their own
    /// — a sequence editor, a signing override — without restating the non-interactive set or
    /// starting git through some other path.
    /// This overload is the virtual seam for every run whose payload is arguments: all of them
    /// funnel through it, the clone included, so a subclass that overrides it sees each one.
    /// A run whose payload is stdin cannot take this shape and has its own seam,
    /// <see cref="RunWithInputAsync"/>; between them the two cover every git invocation here.
    /// </summary>
    public virtual async Task<ProcessResult> RunAsync(
        string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        // core.quotepath=false: unicode paths arrive as UTF-8, not octal escapes.
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);
        return await ProcessRunner.RunAsync(ResolveGitExe(), full, repoPath, timeout ?? Timeout, MergedEnvironment(environment), ct);
    }

    /// <summary>
    /// Structured run whose payload is stdin rather than arguments — `update-ref --stdin` and its
    /// kind. Same executable resolution and same environment as every other call here.
    /// Virtual for the same reason the argument overload is: a stdin-carrying run moves refs in
    /// one transaction, and a subclass that cannot observe or refuse it sees an incomplete
    /// picture of what this service did to a repository.
    /// </summary>
    public virtual Task<ProcessResult> RunWithInputAsync(
        string repoPath, IEnumerable<string> args, string standardInput,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);
        return ProcessRunner.RunWithInputAsync(
            ResolveGitExe(), full, standardInput, repoPath, timeout ?? Timeout, NonInteractiveEnvironment, ct);
    }

    /// <summary>
    /// Characters retained per stream by <see cref="RunStreamingAsync"/>. A streaming caller
    /// consumes each line as it arrives, so the capture exists only to carry an error message; at
    /// the default budget it would hold a second copy of a whole object listing.
    /// </summary>
    internal const int StreamingCaptureCharBudget = 64 * 1024;

    /// <summary>
    /// Structured run whose stdout is delivered line by line while git is still running, for the
    /// reads whose output is proportional to the object store rather than to a result. The capture
    /// is bounded at <see cref="StreamingCaptureCharBudget"/>, so a caller that keeps only what it
    /// needs from each line never holds the whole listing.
    /// Virtual for the same reason the other two run shapes are: a subclass that cannot observe a
    /// run sees an incomplete picture of what this service did to a repository.
    /// </summary>
    public virtual Task<ProcessResult> RunStreamingAsync(
        string repoPath, IEnumerable<string> args, Action<string> onStdOutLine,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);
        return ProcessRunner.RunStreamingAsync(
            ResolveGitExe(), full, repoPath, timeout ?? Timeout, NonInteractiveEnvironment,
            onStdOutLine, onStdErrLine: null, ct, StreamingCaptureCharBudget);
    }

    /// <summary>
    /// A run whose STDERR is delivered line by line while git works — the stream progress
    /// arrives on. The caller passes `--progress` itself: git only volunteers those lines to a
    /// terminal, and a pipe gets them on request alone.
    /// </summary>
    public virtual Task<ProcessResult> RunWithProgressAsync(
        string repoPath, IEnumerable<string> args, Action<string> onProgressLine,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);
        return ProcessRunner.RunStreamingAsync(
            ResolveGitExe(), full, repoPath, timeout ?? Timeout, NonInteractiveEnvironment,
            onStdOutLine: null, onStdErrLine: onProgressLine, ct, StreamingCaptureCharBudget);
    }

    /// <summary>The non-interactive environment with <paramref name="extra"/> layered on top; the base pair itself is never overridden away.</summary>
    private static IReadOnlyDictionary<string, string> MergedEnvironment(IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is null || extra.Count == 0) return NonInteractiveEnvironment;
        var merged = new Dictionary<string, string>(extra, StringComparer.Ordinal);
        foreach (var (key, value) in NonInteractiveEnvironment)
            merged[key] = value;
        return merged;
    }

    /// <summary>String-result run that throws on non-zero exit (legacy shape for simple reads).</summary>
    private async Task<string> RunGitAsync(string workingDir, IEnumerable<string> args, CancellationToken ct)
    {
        var result = await RunAsync(workingDir, args, ct);
        if (result.TimedOut)
            throw new TimeoutException($"git timed out in {workingDir}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git failed ({result.ExitCode}): {result.FirstError}");
        return result.StdOut;
    }

    // ── Working-tree operations (Changes view) ─────────────────────────────

    /// <summary>Unified diff for one file (staged or unstaged side). Untracked files synthesize an all-added diff.</summary>
    public async Task<FileDiff?> GetFileDiffAsync(string repoPath, WorkingFile file, bool staged, CancellationToken ct = default)
    {
        if (file.IsUntracked)
            return SynthesizeUntrackedDiff(repoPath, file.Path);

        var args = new List<string> { "diff", "--no-color" };
        if (staged) args.Add("--cached");
        args.Add("--");
        args.Add(LiteralPathspec(file.Path));
        if (file.OrigPath is not null) args.Add(LiteralPathspec(file.OrigPath));

        var result = await RunAsync(repoPath, args, ct);
        if (!result.Success)
        {
            Log.Warn($"git diff failed for {file.Path}: {result.FirstError}");
            return null;
        }
        var diff = FileDiff.ParseUnified(result.StdOut).FirstOrDefault();
        if (diff is not null) diff.Truncated = result.Truncated;
        return diff;
    }

    private static FileDiff? SynthesizeUntrackedDiff(string repoPath, string relPath)
    {
        try
        {
            var full = Path.Combine(repoPath, relPath);
            var info = new FileInfo(full);
            if (!info.Exists) return null;

            var diff = new FileDiff { Path = relPath };
            if (info.Length > 512 * 1024)
            {
                diff.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = $"(new file, {info.Length / 1024} KB — too large to preview)" });
                return diff;
            }
            var content = File.ReadAllText(full);
            if (content.Contains('\0')) { diff.IsBinary = true; return diff; }

            var lines = content.Split('\n');
            diff.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = $"@@ new file: {lines.Length} lines @@" });
            for (var i = 0; i < lines.Length; i++)
                diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = lines[i].TrimEnd('\r'), NewNumber = (i + 1).ToString() });
            return diff;
        }
        catch (Exception ex)
        {
            Log.Warn($"untracked preview failed for {relPath}", ex);
            return null;
        }
    }

    public Task<ProcessResult> StageAsync(string repoPath, string path, CancellationToken ct = default)
        => RunAsync(repoPath, ["add", "--", LiteralPathspec(path)], ct);

    public Task<ProcessResult> UnstageAsync(string repoPath, string path, CancellationToken ct = default)
        => RunAsync(repoPath, ["restore", "--staged", "--", LiteralPathspec(path)], ct);

    public Task<ProcessResult> StageAllAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["add", "-A"], ct);

    public Task<ProcessResult> UnstageAllAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["restore", "--staged", "."], ct);

    /// <summary>Discards a file's unstaged state: untracked files are deleted, tracked files restored.</summary>
    public Task<ProcessResult> DiscardAsync(string repoPath, WorkingFile file, CancellationToken ct = default)
        => file.IsUntracked
            ? RunAsync(repoPath, ["clean", "-f", "--", LiteralPathspec(file.Path)], ct)
            : RunAsync(repoPath, ["restore", "--", LiteralPathspec(file.Path)], ct);

    public Task<ProcessResult> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        => RunPerBatchAsync(repoPath, ["add", "--"], paths, ct);

    public Task<ProcessResult> UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        => RunPerBatchAsync(repoPath, ["restore", "--staged", "--"], paths, ct);

    /// <summary>
    /// Discards several files at once. Tracked paths are restored before any untracked one is
    /// deleted: a failed restore then stops the run with nothing removed from disk.
    /// </summary>
    public async Task<ProcessResult> DiscardAsync(string repoPath, IReadOnlyList<WorkingFile> files,
        CancellationToken ct = default)
    {
        var tracked = files.Where(f => !f.IsUntracked).Select(f => f.Path).ToList();
        var untracked = files.Where(f => f.IsUntracked).Select(f => f.Path).ToList();

        var result = NothingToDo;
        if (tracked.Count > 0)
        {
            result = await RunPerBatchAsync(repoPath, ["restore", "--"], tracked, ct);
            if (!result.Success) return result;
        }
        if (untracked.Count > 0)
            result = await RunPerBatchAsync(repoPath, ["clean", "-f", "--"], untracked, ct);
        return result;
    }

    /// <summary>An operation asked to touch no paths at all: nothing ran and nothing failed.</summary>
    private static readonly ProcessResult NothingToDo = new(0, "", "", TimedOut: false);

    /// <summary>
    /// Runs <paramref name="prefix"/> once per pathspec batch. Windows composes an argument
    /// list into one command line with a hard length limit, so a selection of a few hundred
    /// paths sent as one run fails outright with nothing staged. A batch that fails stops the
    /// run: a later batch would pile a second change on top of an outcome nobody has seen yet.
    /// </summary>
    private async Task<ProcessResult> RunPerBatchAsync(string repoPath, IReadOnlyList<string> prefix,
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = NothingToDo;
        foreach (var batch in PathspecBatches(paths))
        {
            result = await RunAsync(repoPath, [.. prefix, .. batch], ct);
            if (!result.Success) return result;
        }
        return result;
    }

    /// <summary>Command-line budget for one run's pathspecs, well inside the Windows limit.</summary>
    private const int PathspecBudget = 24000;

    internal static List<List<string>> PathspecBatches(IEnumerable<string> paths, int budget = PathspecBudget)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var length = 0;

        foreach (var path in paths)
        {
            var spec = LiteralPathspec(path);
            // A single pathspec over the budget still gets its own run: git's own limit is
            // what refuses it, rather than this silently dropping the path.
            if (current.Count > 0 && length + spec.Length + 1 > budget)
            {
                batches.Add(current);
                current = [];
                length = 0;
            }
            current.Add(spec);
            length += spec.Length + 1;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>Budget for a commit that writes no signature: local work, waiting on nothing.</summary>
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Budget for a run that signs the object it writes. The signer can put a passphrase prompt on
    /// screen for a reader to answer, and killing it at the unsigned budget aborts the write
    /// mid-authorisation.
    /// </summary>
    private static readonly TimeSpan SignedObjectTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether <paramref name="repoPath"/> is configured to sign the commits it creates. An
    /// unreadable or unset value reads as off, which is git's own default; a repository that does
    /// sign always has the setting to say so.
    /// </summary>
    public virtual async Task<bool> SignsCommitsAsync(string repoPath, CancellationToken ct = default)
    {
        var configured = await RunAsync(repoPath, ["config", "--type=bool", "--get", "commit.gpgsign"], ct);
        return configured.Success && configured.StdOut.Trim() == "true";
    }

    /// <summary>
    /// Whether <paramref name="repoPath"/> is configured to sign the tags it creates. Read
    /// separately from <see cref="SignsCommitsAsync"/> because git's two settings are
    /// independent: a repository can sign one kind of object and not the other.
    /// </summary>
    public virtual async Task<bool> SignsTagsAsync(string repoPath, CancellationToken ct = default)
    {
        var configured = await RunAsync(repoPath, ["config", "--type=bool", "--get", "tag.gpgsign"], ct);
        return configured.Success && configured.StdOut.Trim() == "true";
    }

    /// <summary>
    /// The configured <c>gpg.format</c>, or empty when it is unset or unreadable. git's own
    /// default there is openpgp, but reporting that as the repository's setting would name a
    /// value nothing read.
    /// </summary>
    public virtual async Task<string> GetSigningFormatAsync(string repoPath, CancellationToken ct = default)
    {
        var configured = await RunAsync(repoPath, ["config", "--get", "gpg.format"], ct);
        return configured.Success ? configured.StdOut.Trim() : "";
    }

    /// <summary>
    /// The everyday commit. <see cref="SigningChoice.ProceedUnsigned"/> is the only thing that
    /// turns signing off — never this layer's own initiative — and it does so for the one run
    /// rather than by writing the repository's configuration.
    /// </summary>
    public Task<ProcessResult> CommitAsync(string repoPath, string message, bool amend,
        SigningChoice signing = SigningChoice.NotChosen, CancellationToken ct = default)
    {
        var args = new List<string>();
        if (signing == SigningChoice.ProceedUnsigned) args.AddRange(["-c", "commit.gpgsign=false"]);
        args.AddRange(["commit", MessageCleanupPin, "-m", message]);
        if (amend) args.Add("--amend");
        return RunAsync(repoPath, args, ct,
            signing == SigningChoice.KeepSigning ? SignedObjectTimeout : CommitTimeout);
    }

    /// <summary>
    /// Matches a commit or tag whose signature git could not produce: the openpgp signer's
    /// failure, git's two ssh missing-key wordings, the tag signer's failure, and the fatal git
    /// dies with once a commit's signature is absent.
    /// <para>
    /// Every token here must be a phrase only git's own signing machinery emits. A commit runs
    /// arbitrary pre-commit and commit-msg hooks whose stderr lands in this same text, so a token
    /// loose enough to appear in ordinary prose — the bare word "signing", which a hook rejecting
    /// a commit for a missing signing acknowledgement prints — classifies that hook's refusal as a
    /// key failure and offers an unsigned retry that reruns the same hook and fails identically.
    /// That is the acceptance rule for any token added later: exclusivity, not coverage. A genuine
    /// signing wording no token matches falls to the generic failure path, which shows git's own
    /// stderr and misdirects nobody.
    /// </para>
    /// </summary>
    public static bool IsSigningFailure(ProcessResult result) =>
        !result.Success
        && (result.StdErr + "\n" + result.StdOut) is var text
        && (text.Contains("gpg failed to sign", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign the data", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign the tag", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable to sign the", StringComparison.OrdinalIgnoreCase)
            || text.Contains("user.signingkey", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gpg.ssh.defaultkeycommand", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to write commit object", StringComparison.OrdinalIgnoreCase));

    public async Task<string> GetLastCommitMessageAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["log", "-1", "--format=%B"], ct);
        return result.Success ? result.StdOut.TrimEnd() : "";
    }

    /// <summary>Matches git's "Unable to create '…/index.lock': File exists" failure shape.</summary>
    public static bool IsIndexLockConflict(ProcessResult result) =>
        !result.Success
        && (result.StdErr + "\n" + result.StdOut) is var text
        && text.Contains("index.lock", StringComparison.OrdinalIgnoreCase)
        && text.Contains("File exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes an orphaned index.lock left behind by a killed git process, which
    /// otherwise blocks every later stage/unstage/commit. git never cleans it up
    /// itself. Heuristic, not proof — process enumeration is unreliable (another
    /// process's CWD/args are not dependably readable), so staleness is judged by
    /// age plus a re-check: the lock must be older than <paramref name="minAge"/>
    /// (default 2 minutes) and still present with identical creation/write stamps
    /// and length after <paramref name="recheckDelay"/> (default 500 ms), proving
    /// no live git replaced it in the window. False-positive bound: only a single
    /// live git operation holding one lock file continuously past the age
    /// threshold can be misjudged; index writes complete in seconds even on very
    /// large repositories, and the worst case is that op failing its final
    /// rename — an error, not repository corruption. Returns true only when a
    /// lock file was deleted.
    /// </summary>
    public virtual async Task<bool> TryCleanStaleLockAsync(string repoPath, TimeSpan? minAge = null,
        TimeSpan? recheckDelay = null, CancellationToken ct = default)
    {
        var gitDir = await ResolveGitDirAsync(repoPath, ct);
        if (gitDir is null) return false;

        var lockPath = Path.Combine(gitDir, "index.lock");
        try
        {
            var info = new FileInfo(lockPath);
            if (!info.Exists) return false;
            var seen = (info.CreationTimeUtc, info.LastWriteTimeUtc, info.Length);
            if (DateTime.UtcNow - info.CreationTimeUtc < (minAge ?? TimeSpan.FromMinutes(2)))
                return false;

            await Task.Delay(recheckDelay ?? TimeSpan.FromMilliseconds(500), ct);

            info.Refresh();
            if (!info.Exists || (info.CreationTimeUtc, info.LastWriteTimeUtc, info.Length) != seen)
                return false;

            File.Delete(lockPath);
            Log.Warn($"Removed stale index.lock in {gitDir}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"stale-lock cleanup failed for {repoPath}", ex);
            return false;
        }
    }

    // ── Branches ────────────────────────────────────────────────────────────

    public async Task<List<BranchInfo>> GetBranchesAsync(string repoPath, CancellationToken ct = default) =>
        (await GetBranchesResultAsync(repoPath, ct)).Branches;

    /// <summary>
    /// The same read as <see cref="GetBranchesAsync"/>, keeping the failure rather than folding it
    /// into an empty list. A caller reporting on branches it did not read has to be able to say so.
    /// </summary>
    public async Task<BranchesResult> GetBranchesResultAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath,
            ["for-each-ref", "refs/heads",
             "--format=%(refname:short)|%(HEAD)|%(upstream:short)|%(upstream:track)|%(committerdate:iso8601-strict)"], ct);
        if (!result.Success)
        {
            Log.Warn($"git for-each-ref failed for {repoPath}: {result.FirstError}");
            return new BranchesResult([], HasError: true, result.FirstError);
        }

        var branches = new List<BranchInfo>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('|');
            if (parts.Length < 5) continue;

            var track = parts[3];
            int ahead = 0, behind = 0;
            var gone = track.Contains("gone", StringComparison.OrdinalIgnoreCase);
            foreach (var seg in track.Trim('[', ']').Split(','))
            {
                var s = seg.Trim();
                if (s.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(s[6..], out var a)) ahead = a;
                else if (s.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(s[7..], out var b)) behind = b;
            }

            branches.Add(new BranchInfo
            {
                Name = parts[0],
                IsCurrent = parts[1] == "*",
                Upstream = parts[2],
                UpstreamGone = gone,
                Ahead = ahead,
                Behind = behind,
                LastCommit = DateTimeOffset.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : null
            });
        }
        return new BranchesResult(branches);
    }

    public Task<ProcessResult> CreateBranchAsync(string repoPath, string name, CancellationToken ct = default)
        => RunAsync(repoPath, ["switch", "-c", name], ct);

    public Task<ProcessResult> SwitchBranchAsync(string repoPath, string name, CancellationToken ct = default)
        => RunAsync(repoPath, ["switch", name], ct);

    /// <summary>Safe delete (-d): refuses when unmerged; the error is surfaced, not forced.</summary>
    public Task<ProcessResult> DeleteBranchAsync(string repoPath, string name, CancellationToken ct = default)
        => RunAsync(repoPath, ["branch", "-d", name], ct);

    // ── Remote sync (long timeouts; progress lands on the drained stderr) ──

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);

    public Task<ProcessResult> FetchAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["fetch", "--prune"], ct, NetworkTimeout);

    /// <summary>
    /// Fetch that narrates. `--progress` forces the counting/receiving lines git otherwise
    /// emits only to a terminal; they arrive on stderr, CR-terminated mid-redraw, and the
    /// runner delivers each redraw as its own callback line.
    /// </summary>
    public Task<ProcessResult> FetchProgressAsync(
        string repoPath, Action<string> onProgress, CancellationToken ct = default)
        => RunWithProgressAsync(repoPath, ["fetch", "--prune", "--progress"], onProgress, ct, NetworkTimeout);

    /// <summary>Fast-forward-only pull: a diverged branch fails loudly instead of creating a surprise merge.</summary>
    public Task<ProcessResult> PullAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["pull", "--ff-only"], ct, NetworkTimeout);

    public Task<ProcessResult> PullProgressAsync(
        string repoPath, Action<string> onProgress, CancellationToken ct = default)
        => RunWithProgressAsync(repoPath, ["pull", "--ff-only", "--progress"], onProgress, ct, NetworkTimeout);

    /// <summary>Push; sets upstream automatically when the branch has none.</summary>
    public async Task<ProcessResult> PushAsync(string repoPath, CancellationToken ct = default)
    {
        var upstream = await RunAsync(repoPath, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct);
        if (upstream.Success)
            return await RunAsync(repoPath, ["push"], ct, NetworkTimeout);

        // No upstream yet: set one on the repo's actual remote — not a hardcoded "origin"
        // (renamed/single-remote setups don't necessarily have one called origin).
        var remote = await ResolveDefaultRemoteAsync(repoPath, ct);
        if (remote is null)
            return new ProcessResult(-1, "", "no remote configured to push to", TimedOut: false);
        return await RunAsync(repoPath, ["push", "-u", remote, "HEAD"], ct, NetworkTimeout);
    }

    public async Task<ProcessResult> PushProgressAsync(
        string repoPath, Action<string> onProgress, CancellationToken ct = default)
    {
        var upstream = await RunAsync(repoPath, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct);
        if (upstream.Success)
            return await RunWithProgressAsync(repoPath, ["push", "--progress"], onProgress, ct, NetworkTimeout);

        var remote = await ResolveDefaultRemoteAsync(repoPath, ct);
        if (remote is null)
            return new ProcessResult(-1, "", "no remote configured to push to", TimedOut: false);
        return await RunWithProgressAsync(
            repoPath, ["push", "--progress", "-u", remote, "HEAD"], onProgress, ct, NetworkTimeout);
    }

    /// <summary>
    /// The remote a repo's remote-dependent operations target: the first remote
    /// whose remote.&lt;name&gt;.url is set, origin preferred; null when no remote
    /// has a URL. Single authority for remote resolution — any operation needing
    /// a remote name (status, push, and future remote-mutating commands)
    /// resolves through here, because a second resolution site reintroduces
    /// origin-hardcoding that misreads renamed-remote repos as local.
    /// </summary>
    public async Task<string?> ResolveDefaultRemoteAsync(string repoPath, CancellationToken ct = default)
    {
        var remotes = await ReadRemoteUrlsAsync(repoPath, ct);
        return remotes.Count > 0 ? remotes[0].Name : null;
    }

    /// <summary>
    /// Every remote that has a URL, in one config read, ordered the way
    /// <see cref="ResolveDefaultRemoteAsync"/> picks: origin first, then by name.
    /// <para>
    /// A remote with no URL is absent, not empty: `git remote` also lists fetch-only stanzas
    /// (remote.&lt;name&gt;.fetch with no url), and a URL-less remote can be neither fetched nor
    /// pushed, so it must not shadow a later remote that has one. A remote whose URL is set more
    /// than once keeps the last value, which is what `git config --get` answers with.
    /// </para>
    /// <para>
    /// `config --get-regexp`, not `remote get-url`: get-url's legacy name-as-URL fallback exits 0
    /// and echoes the bare remote name when remote.&lt;name&gt;.url is unset, which would surface
    /// as a URL. The pattern anchors a literal ".url" tail, so remote.&lt;name&gt;.pushurl does
    /// not match and a remote whose own name ends in ".url" still parses — the name is everything
    /// between the first "remote." and the last ".url".
    /// </para>
    /// </summary>
    private async Task<List<(string Name, string Url)>> ReadRemoteUrlsAsync(string repoPath, CancellationToken ct)
    {
        var result = await RunAsync(repoPath, ["config", "--get-regexp", @"^remote\..*\.url$"], ct);
        if (!result.Success) return [];

        const string prefix = "remote.";
        const string suffix = ".url";
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var key = line[..space];
            var url = line[(space + 1)..].Trim();
            if (url.Length == 0) continue;
            if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                !key.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var name = key[prefix.Length..^suffix.Length];
            if (name.Length == 0) continue;
            byName[name] = url;
        }

        return [.. byName
            .OrderBy(e => e.Key == "origin" ? 0 : 1)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => (e.Key, e.Value))];
    }

    // ── Stash ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How many entries the stash stack holds, or null when git could not answer. Distinct from
    /// <see cref="GetStashesAsync"/>, which reports a failed read as an empty list: a caller about
    /// to destroy the stack needs "there are none" told apart from "this could not be read".
    ///
    /// The probe reads refs/stash's reflog directly, because that IS the stash stack. git reports
    /// an absent refs/stash as an unknown revision, which is the one failure that means zero.
    /// </summary>
    public async Task<int?> CountStashEntriesAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["reflog", "show", "--format=%gd", "refs/stash"], ct);
        if (result.Success)
            return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        var text = result.StdErr + result.StdOut;
        if (!result.TimedOut && text.Contains("unknown revision", StringComparison.OrdinalIgnoreCase))
            return 0;

        Log.Warn($"could not read the stash stack for {repoPath}: {result.FirstError}");
        return null;
    }

    public async Task<List<StashEntry>> GetStashesAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["stash", "list", "--format=%gd|%ci|%gs"], ct);
        if (!result.Success) return [];

        var stashes = new List<StashEntry>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('|', 3);
            if (parts.Length < 3) continue;
            stashes.Add(new StashEntry
            {
                Ref = parts[0],
                Date = DateTimeOffset.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : null,
                Subject = parts[2]
            });
        }
        return stashes;
    }

    public Task<ProcessResult> StashApplyAsync(string repoPath, string stashRef, CancellationToken ct = default)
        => RunAsync(repoPath, ["stash", "apply", stashRef], ct, TimeSpan.FromSeconds(30));

    public Task<ProcessResult> StashPopAsync(string repoPath, string stashRef, CancellationToken ct = default)
        => RunAsync(repoPath, ["stash", "pop", stashRef], ct, TimeSpan.FromSeconds(30));

    public Task<ProcessResult> StashDropAsync(string repoPath, string stashRef, CancellationToken ct = default)
        => RunAsync(repoPath, ["stash", "drop", stashRef], ct);

    // ── History detail ──────────────────────────────────────────────────────

    public async Task<List<CommitFile>> GetCommitFilesAsync(string repoPath, string hash, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["show", "--name-status", "--format=", hash], ct);
        if (!result.Success) return [];

        var files = new List<CommitFile>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 2) continue;
            // Renames: "R100 <tab> old <tab> new" — show the new path.
            files.Add(new CommitFile { Status = parts[0], Path = parts[^1] });
        }
        return files;
    }

    public async Task<FileDiff?> GetCommitFileDiffAsync(string repoPath, string hash, string filePath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath,
            ["show", "--no-color", "--format=", hash, "--", LiteralPathspec(filePath)], ct);
        if (!result.Success) return null;
        var diff = FileDiff.ParseUnified(result.StdOut).FirstOrDefault();
        if (diff is not null) diff.Truncated = result.Truncated;
        return diff;
    }

    public async Task<TagsResult> GetTagsAsync(string repoPath, CancellationToken ct = default)
    {
        // %(objecttype) is "tag" for an annotated tag, "commit" for a lightweight one, and the
        // "*" atoms are the dereferenced form — populated for an annotated tag, empty for a
        // lightweight one whose ref already names the commit. So every fact about the target
        // commit is read from the "*" atom when annotated and the plain atom when not.
        // %(creatordate) is the tagger date on a tag object and the commit date on a commit.
        // Separated by the unit separator: a commit subject may contain a tab.
        var format = string.Join(FieldSeparator,
            "%(refname:short)", "%(objecttype)", "%(objectname)", "%(*objectname)",
            "%(taggerdate:iso8601-strict)", "%(*creatordate:iso8601-strict)", "%(creatordate:iso8601-strict)",
            "%(contents:subject)", "%(*contents:subject)");
        var result = await RunAsync(repoPath, ["for-each-ref", "refs/tags", "--format=" + format], ct);
        if (!result.Success)
        {
            Log.Warn($"git for-each-ref tags failed for {repoPath}: {result.FirstError}");
            return new TagsResult([], true, ReadFailureText(result, Timeout));
        }

        var tags = new List<TagInfo>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.TrimEnd('\r').Split(FieldSeparator);
            if (f.Length < 9) continue;
            var annotated = f[1] == "tag";
            tags.Add(new TagInfo
            {
                Name = f[0],
                IsAnnotated = annotated,
                TargetSha = annotated ? f[3] : f[2],
                Subject = annotated ? f[7] : null,
                TaggerDate = annotated ? ParseIsoStrict(f[4]) : null,
                TargetSubject = annotated ? f[8] : f[7],
                TargetDate = ParseIsoStrict(annotated ? f[5] : f[6])
            });
        }
        return new TagsResult(tags);
    }

    private static DateTimeOffset? ParseIsoStrict(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// Creates a tag: annotated when <paramref name="message"/> is non-null, else lightweight.
    /// <para>
    /// `tag.gpgsign` reaches BOTH shapes, not only the annotated one: with it on, git treats a
    /// bare `git tag <name>` as `git tag -s <name>`, so a lightweight request becomes a signed tag
    /// object and fails outright when no message accompanies it. Only
    /// <see cref="SigningChoice.ProceedUnsigned"/> turns that off, for the one run, and a
    /// lightweight tag is genuinely lightweight again under it.
    /// </para>
    /// </summary>
    public Task<ProcessResult> CreateTagAsync(string repoPath, string name, string? message = null,
        string? targetCommit = null, SigningChoice signing = SigningChoice.NotChosen,
        CancellationToken ct = default)
    {
        var args = new List<string>();
        if (signing == SigningChoice.ProceedUnsigned) args.AddRange(["-c", "tag.gpgsign=false"]);
        args.Add("tag");
        if (message is not null) { args.Add("-a"); args.Add(MessageCleanupPin); args.Add("-m"); args.Add(message); }
        args.Add(name);
        if (!string.IsNullOrEmpty(targetCommit)) args.Add(targetCommit);
        return RunAsync(repoPath, args, ct, signing == SigningChoice.KeepSigning ? SignedObjectTimeout : null);
    }

    public Task<ProcessResult> DeleteTagAsync(string repoPath, string name, CancellationToken ct = default)
        => RunAsync(repoPath, ["tag", "-d", name], ct);

    public Task<ProcessResult> PushTagAsync(string repoPath, string remote, string name, CancellationToken ct = default)
        => RunAsync(repoPath, ["push", remote, $"refs/tags/{name}"], ct, NetworkTimeout);

    public Task<ProcessResult> PushAllTagsAsync(string repoPath, string remote, CancellationToken ct = default)
        => RunAsync(repoPath, ["push", remote, "--tags"], ct, NetworkTimeout);

    public async Task<RemotesResult> GetRemotesAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["remote", "-v"], ct);
        if (!result.Success)
        {
            Log.Warn($"git remote -v failed for {repoPath}: {result.FirstError}");
            return new RemotesResult([], true, ReadFailureText(result, Timeout));
        }

        // Lines are "<name>\t<url> (fetch)" and "<name>\t<url> (push)"; fetch and push
        // URLs can differ, so both variants merge into one entry per name. Insertion
        // order is preserved so the listing matches `git remote`.
        var byName = new Dictionary<string, (string fetch, string push)>();
        var order = new List<string>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var name = line[..tab];
            var rest = line[(tab + 1)..];
            var sp = rest.LastIndexOf(' ');
            if (sp < 0) continue;
            var url = rest[..sp];
            var kind = rest[(sp + 1)..].Trim('(', ')');
            if (!byName.TryGetValue(name, out var cur)) { cur = ("", ""); order.Add(name); }
            byName[name] = kind == "push" ? (cur.fetch, url) : (url, cur.push);
        }

        var remotes = new List<RemoteEntry>();
        foreach (var name in order)
        {
            var (fetch, push) = byName[name];
            remotes.Add(new RemoteEntry { Name = name, FetchUrl = fetch, PushUrl = push.Length > 0 ? push : fetch });
        }
        return new RemotesResult(remotes);
    }

    public Task<ProcessResult> AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct = default)
        => RunAsync(repoPath, ["remote", "add", name, url], ct);

    public Task<ProcessResult> RemoveRemoteAsync(string repoPath, string name, CancellationToken ct = default)
        => RunAsync(repoPath, ["remote", "remove", name], ct);

    public Task<ProcessResult> RenameRemoteAsync(string repoPath, string oldName, string newName, CancellationToken ct = default)
        => RunAsync(repoPath, ["remote", "rename", oldName, newName], ct);

    public Task<ProcessResult> SetRemoteUrlAsync(string repoPath, string name, string url, CancellationToken ct = default)
        => RunAsync(repoPath, ["remote", "set-url", name, url], ct);

    public Task<ProcessResult> RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken ct = default)
        => RunAsync(repoPath, ["branch", "-m", oldName, newName], ct);

    /// <summary>Remote-tracking branches (refs/remotes) for a checkout picker; the symbolic "&lt;remote&gt;/HEAD" pointer is dropped.</summary>
    public async Task<RemoteBranchesResult> GetRemoteBranchesAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["for-each-ref", "refs/remotes", "--format=%(refname:short)"], ct);
        if (!result.Success)
        {
            Log.Warn($"git for-each-ref remotes failed for {repoPath}: {result.FirstError}");
            return new RemoteBranchesResult([], true, ReadFailureText(result, Timeout));
        }
        return new RemoteBranchesResult(result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.TrimEnd('\r'))
            .Where(r => r.Length > 0 && !r.EndsWith("/HEAD", StringComparison.Ordinal))
            .ToList());
    }

    /// <summary>
    /// Creates a local tracking branch for a remote-tracking ref. The local name defaults to the
    /// ref with its leading "&lt;remote&gt;/" stripped; a caller that offers that name for editing
    /// passes what was typed instead, so the branch created is the one the surface showed.
    /// </summary>
    public Task<ProcessResult> CheckoutRemoteBranchAsync(string repoPath, string remoteBranch,
        string? localName = null, CancellationToken ct = default)
    {
        var slash = remoteBranch.IndexOf('/');
        var local = string.IsNullOrWhiteSpace(localName)
            ? slash >= 0 ? remoteBranch[(slash + 1)..] : remoteBranch
            : localName.Trim();
        return RunAsync(repoPath, ["switch", "-c", local, "--track", remoteBranch], ct);
    }

    /// <summary>Deletes a branch on the remote (destructive; the UI confirms and gates this).</summary>
    public Task<ProcessResult> DeleteRemoteBranchAsync(string repoPath, string remote, string branch, CancellationToken ct = default)
        => RunAsync(repoPath, ["push", remote, "--delete", branch], ct, NetworkTimeout);

    public Task<ProcessResult> PruneRemoteAsync(string repoPath, string remote, CancellationToken ct = default)
        => RunAsync(repoPath, ["remote", "prune", remote], ct, NetworkTimeout);

    /// <summary>
    /// The remote-tracking refs a prune would drop, read without dropping any. Contacts the
    /// remote: staleness is what this repository holds that the remote no longer publishes, so
    /// an unreachable remote yields no answer rather than an empty one.
    /// </summary>
    public async Task<PrunePreview> PruneRemoteDryRunAsync(string repoPath, string remote,
        CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["remote", "prune", remote, "--dry-run"], ct, NetworkTimeout);
        if (!result.Success)
        {
            Log.Warn($"git remote prune --dry-run failed for {repoPath}: {result.FirstError}");
            return new PrunePreview([], true, ReadFailureText(result, NetworkTimeout));
        }
        return new PrunePreview(ParsePrunableRefs(result.StdOut));
    }

    /// <summary>
    /// Refs out of a prune dry run. Each stale one is announced as " * [would prune] &lt;ref&gt;";
    /// the header lines naming the remote and its URL carry no ref, and a marker with nothing
    /// after it names none either.
    /// </summary>
    internal static List<string> ParsePrunableRefs(string stdOut)
    {
        const string marker = "[would prune]";
        var refs = new List<string>();
        foreach (var raw in stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var name = line[(at + marker.Length)..].Trim();
            if (name.Length > 0) refs.Add(name);
        }
        return refs;
    }

    /// <summary>Points a local branch at a remote-tracking ref (`&lt;remote&gt;/&lt;branch&gt;`); no network runs.</summary>
    public Task<ProcessResult> SetUpstreamAsync(string repoPath, string branch, string upstream,
        CancellationToken ct = default)
        => RunAsync(repoPath, ["branch", $"--set-upstream-to={upstream}", branch], ct);

    /// <summary>Drops a branch's upstream. The remote-tracking ref itself stays; only the link is removed.</summary>
    public Task<ProcessResult> UnsetUpstreamAsync(string repoPath, string branch, CancellationToken ct = default)
        => RunAsync(repoPath, ["branch", "--unset-upstream", branch], ct);

    /// <summary>
    /// How far <paramref name="reference"/> stands from <paramref name="baseRef"/>: commits it has
    /// that the base does not (Ahead) and commits the base has that it does not (Behind). Null when
    /// a ref is unknown or the counts do not parse — a count that was never measured must not be
    /// reported as zero. Two histories with no common commit do measure: the symmetric difference
    /// is then each side's whole history, and rev-list returns those counts.
    /// </summary>
    public async Task<RefComparison?> CompareRefsAsync(string repoPath, string reference, string baseRef,
        CancellationToken ct = default)
    {
        if (reference.Length == 0 || baseRef.Length == 0) return null;
        // Left side of the range is the base, so the left count is what the reference is behind by.
        var result = await RunAsync(repoPath,
            ["rev-list", "--left-right", "--count", $"{baseRef}...{reference}", "--"], ct);
        if (!result.Success) return null;

        var fields = result.StdOut.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !int.TryParse(fields[0], out var behind) || !int.TryParse(fields[1], out var ahead))
            return null;
        return new RefComparison(ahead, behind);
    }

    // ── Reflog ──────────────────────────────────────────────────────────────────

    /// <summary>Repacking a large repository outruns every other budget here, so maintenance gets its own.</summary>
    private static readonly TimeSpan MaintenanceTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// One ref's reflog, newest first. <paramref name="reference"/> is passed to git as written
    /// ("HEAD", or a branch name), and each entry's index selector is derived from its position —
    /// which is what git's own <c>@{n}</c> form means. <c>--date=iso-strict</c> makes the selector
    /// atom carry the moment the entry was written; the index form is rebuilt here rather than
    /// asked for, because git emits one or the other and the timestamp is the part it cannot
    /// reconstruct. A ref with no reflog is an empty list, not a failure.
    /// </summary>
    public async Task<List<ReflogEntry>> GetReflogAsync(
        string repoPath, string reference, int limit = 200, CancellationToken ct = default)
    {
        var format = string.Join(FieldSeparator, "%gD", "%gs", "%H", "%cI");
        var result = await RunAsync(repoPath,
            ["reflog", "show", "--date=iso-strict", "--format=" + format, "-n", limit.ToString(), reference], ct);
        if (!result.Success)
        {
            // A ref that has never moved has no reflog, which git reports as a failure; so does a
            // ref that does not exist. Neither is an error the reader can act on.
            Log.Warn($"git reflog show {reference} failed for {repoPath}: {result.FirstError}");
            return [];
        }

        var entries = new List<ReflogEntry>();
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split(FieldSeparator);
            if (parts.Length < 4) continue;
            var (action, subject) = SplitReflogSubject(parts[1]);
            entries.Add(new ReflogEntry(
                $"{reference}@{{{entries.Count}}}",
                action,
                subject,
                parts[2],
                ParseReflogStamp(parts[0])));
        }
        return entries;
    }

    /// <summary>
    /// The action and its subject. A reflog subject is "&lt;action&gt;: &lt;detail&gt;", except for the
    /// entry a fresh clone or an older git writes with no action at all, which is named rather
    /// than shown as a blank row.
    /// </summary>
    internal static (string Action, string Subject) SplitReflogSubject(string reflogSubject)
    {
        var text = reflogSubject.Trim();
        if (text.Length == 0) return ("(no action recorded)", "");
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon <= 0 ? (text, "") : (text[..colon], text[(colon + 2)..]);
    }

    /// <summary>
    /// The moment inside a date-form selector — "main@{2026-08-07T22:37:30-04:00}". Null when the
    /// selector is not in that form, so a row shows no date rather than a fabricated one.
    /// </summary>
    internal static DateTimeOffset? ParseReflogStamp(string dateSelector)
    {
        var open = dateSelector.IndexOf("@{", StringComparison.Ordinal);
        if (open < 0 || !dateSelector.EndsWith('}')) return null;
        var inner = dateSelector[(open + 2)..^1];
        return DateTimeOffset.TryParse(inner, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var when) ? when : null;
    }

    /// <summary>
    /// Creates a branch at an explicit start point and switches to it. --no-track: the start point
    /// is a raw object id from a reflog row, so there is nothing for the new branch to follow.
    /// </summary>
    public Task<ProcessResult> CreateBranchAtAsync(
        string repoPath, string name, string startPoint, CancellationToken ct = default)
        => RunAsync(repoPath, ["switch", "-c", name, "--no-track", startPoint], ct);

    /// <summary>
    /// Whether git would accept <paramref name="name"/> as a branch name. A leading dash is
    /// refused here rather than handed to git, which would read it as an option.
    /// </summary>
    public async Task<bool> IsValidBranchNameAsync(string repoPath, string name, CancellationToken ct = default)
    {
        if (name.Length == 0 || name.StartsWith('-')) return false;
        return (await RunAsync(repoPath, ["check-ref-format", "--branch", name], ct)).Success;
    }

    /// <summary>
    /// Whether git would accept <paramref name="name"/> as a tag name. Checked as the full ref it
    /// becomes: --branch resolves @{...} shorthand against branches and would answer for a ref
    /// that is not the one being created.
    /// </summary>
    public async Task<bool> IsValidTagNameAsync(string repoPath, string name, CancellationToken ct = default)
    {
        if (name.Length == 0 || name.StartsWith('-')) return false;
        return (await RunAsync(repoPath, ["check-ref-format", "refs/tags/" + name], ct)).Success;
    }

    /// <summary>
    /// Whether git would accept <paramref name="name"/> as a remote name, checked against the
    /// remote-tracking ref namespace the remote would own. A name containing a slash is refused
    /// here: git accepts it, and the resulting refs/remotes path is then ambiguous with a
    /// branch of another remote.
    /// </summary>
    public async Task<bool> IsValidRemoteNameAsync(string repoPath, string name, CancellationToken ct = default)
    {
        if (name.Length == 0 || name.StartsWith('-') || name.Contains('/')) return false;
        return (await RunAsync(repoPath, ["check-ref-format", $"refs/remotes/{name}/HEAD"], ct)).Success;
    }

    /// <summary>
    /// Whether the text can be handed to git as a remote URL at all. Only the shapes that would
    /// misfire rather than fail are refused: a leading dash git reads as an option, and embedded
    /// whitespace or control characters, which no URL or path form carries. Everything else is
    /// git's to accept or reject.
    /// </summary>
    public static bool IsPlausibleRemoteUrl(string url)
    {
        if (url.Length == 0 || url.StartsWith('-')) return false;
        foreach (var c in url)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        return true;
    }

    // ── Object-store maintenance ────────────────────────────────────────────────

    /// <summary>
    /// Expires every reflog entry immediately, on all refs. This is what makes a replaced history
    /// unreachable: the swap's own ref moves leave the pre-rewrite tips in the reflogs, and while
    /// they are there nothing prunes the objects behind them.
    /// </summary>
    public Task<ProcessResult> ExpireReflogsAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all"],
            ct, MaintenanceTimeout);

    /// <summary>
    /// Repacks and prunes with no grace period. Without --prune=now the default two-week window
    /// keeps every just-unreferenced object on disk, so the reclaim would not happen.
    /// </summary>
    public Task<ProcessResult> GarbageCollectAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["gc", "--prune=now", "--quiet"], ct, MaintenanceTimeout);

    /// <summary>
    /// The object store's size as git measures it. Null when git could not read the repository,
    /// so a caller reports "not measured" rather than a zero it never observed.
    /// </summary>
    public async Task<RepoObjectCounts?> CountObjectsAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["count-objects", "-v"], ct, TimeSpan.FromMinutes(2));
        if (!result.Success) return null;

        var fields = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (long.TryParse(line[(colon + 1)..].Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                fields[line[..colon]] = value;
        }

        return new RepoObjectCounts(
            (int)fields.GetValueOrDefault("count"),
            fields.GetValueOrDefault("size"),
            (int)fields.GetValueOrDefault("in-pack"),
            fields.GetValueOrDefault("size-pack"));
    }

    /// <summary>
    /// Budget for the two per-path reads. Both are proportional to the file rather than to the
    /// repository — `log --follow` re-runs rename detection at every commit it walks, and
    /// `blame --porcelain` emits a record per line — so a large file exceeds the default read
    /// budget while git is still working correctly.
    /// </summary>
    private static readonly TimeSpan PathReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Commit history for one file, following it across renames.</summary>
    public async Task<FileHistoryResult> GetFileHistoryAsync(string repoPath, string filePath, int limit = 50, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath,
            ["log", "--follow", CommitLogFormat, "-n", limit.ToString(), "--", LiteralPathspec(filePath)],
            ct, PathReadTimeout);
        if (!result.Success)
        {
            Log.Warn($"git log --follow failed for {filePath} in {repoPath}: {result.FirstError}");
            return new FileHistoryResult([], true, ReadFailureText(result));
        }

        var commits = new List<GitCommit>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (ParseCommitLine(line) is { } commit) commits.Add(commit);
        return new FileHistoryResult(commits);
    }

    public async Task<BlameResult> GetBlameAsync(string repoPath, string filePath, CancellationToken ct = default)
    {
        // Pathname, not pathspec: `blame` rejects pathspec magic and resolves a name to itself.
        var result = await RunAsync(repoPath, ["blame", "--porcelain", "--", filePath], ct, PathReadTimeout);
        if (!result.Success)
        {
            Log.Warn($"git blame failed for {filePath} in {repoPath}: {result.FirstError}");
            return new BlameResult([], true, ReadFailureText(result));
        }
        return new BlameResult(ParseBlamePorcelain(result.StdOut), Truncated: result.Truncated);
    }

    /// <summary>
    /// What to tell a reader about a read that did not run, in the terms a surface states. A
    /// timeout carries no stderr, so <see cref="ProcessResult.FirstError"/> alone would report it
    /// as a bare exit code; the caller's own budget names it instead.
    /// </summary>
    internal static string ReadFailureText(ProcessResult result, TimeSpan? budget = null) =>
        result.TimedOut
            ? $"the read timed out after {(budget ?? PathReadTimeout).TotalSeconds:0} seconds"
            : result.FirstError;

    /// <summary>
    /// Parses `git blame --porcelain`. Each source line is a header
    /// "&lt;sha&gt; &lt;orig&gt; &lt;final&gt; [&lt;count&gt;]" followed — the FIRST time a commit
    /// appears — by its metadata block ending at "filename", then a TAB-prefixed content
    /// line. Later lines of the same commit repeat only the header, so per-sha metadata is
    /// cached and reused. A "boundary" line in a commit's block marks a walk boundary.
    /// </summary>
    internal static List<BlameLine> ParseBlamePorcelain(string porcelain)
    {
        var lines = new List<BlameLine>();
        var meta = new Dictionary<string, (string author, DateTimeOffset? date, bool boundary)>();

        var sha = "";
        var finalLine = 0;
        var author = "";
        long epoch = 0;
        var tz = "";
        var boundary = false;

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line[0] == '\t')
            {
                var m = meta.TryGetValue(sha, out var cached) ? cached : (author, ToBlameDate(epoch, tz), boundary);
                lines.Add(new BlameLine
                {
                    Sha = sha,
                    Author = m.Item1,
                    Date = m.Item2,
                    LineNumber = finalLine,
                    Text = line[1..],
                    IsBoundary = m.Item3
                });
                continue;
            }

            var sp = line.IndexOf(' ');
            var key = sp < 0 ? line : line[..sp];

            if (key.Length == 40 && IsHex(key))
            {
                sha = key;
                var fields = line.Split(' ');
                if (fields.Length >= 3 && int.TryParse(fields[2], out var fl)) finalLine = fl;
                if (!meta.ContainsKey(sha)) { author = ""; epoch = 0; tz = ""; boundary = false; }
                continue;
            }

            switch (key)
            {
                case "author": author = sp < 0 ? "" : line[(sp + 1)..]; break;
                case "author-time": long.TryParse(sp < 0 ? "" : line[(sp + 1)..], out epoch); break;
                case "author-tz": tz = sp < 0 ? "" : line[(sp + 1)..]; break;
                case "boundary": boundary = true; break;
                case "filename":
                    if (!meta.ContainsKey(sha)) meta[sha] = (author, ToBlameDate(epoch, tz), boundary);
                    break;
            }
        }
        return lines;
    }

    private static DateTimeOffset? ToBlameDate(long epoch, string tz)
    {
        if (epoch <= 0) return null;
        var utc = DateTimeOffset.FromUnixTimeSeconds(epoch);
        // tz is "+HHMM" / "-HHMM" — apply it so the displayed time is the author's local time.
        if (tz.Length == 5 && (tz[0] == '+' || tz[0] == '-')
            && int.TryParse(tz.AsSpan(1, 2), out var h) && int.TryParse(tz.AsSpan(3, 2), out var mm))
        {
            var offset = new TimeSpan(h, mm, 0);
            if (tz[0] == '-') offset = -offset;
            try { return utc.ToOffset(offset); } catch { return utc; }
        }
        return utc;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// The one log format behind every <see cref="GitCommit"/>. The full sha leads and
    /// the abbreviation follows it, so a commit carries an unambiguous revision as well
    /// as the short form the lists display. Fields are separated by <see cref="FieldSeparator"/>,
    /// which no author name and no subject can contain — a printable delimiter splits any field
    /// carrying one, and every field after it shifts, so the subject shown against a commit
    /// belongs to no commit at all.
    /// </summary>
    private const string CommitLogFormat =
        "--format=%H" + FieldSeparator + "%h" + FieldSeparator + "%an" + FieldSeparator +
        "%aI" + FieldSeparator + "%s";

    /// <summary>Null when a line carries fewer fields than the format emits.</summary>
    private static GitCommit? ParseCommitLine(string line)
    {
        var parts = line.Split(FieldSeparator, 5);
        if (parts.Length < 5) return null;
        return new GitCommit
        {
            Hash = parts[0],
            ShortHash = parts[1],
            Author = parts[2],
            Date = DateTimeOffset.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : default,
            Message = parts[4]
        };
    }

    /// <summary>
    /// One page of history from `--skip`/`-n`, optionally filtered by message, author,
    /// path, and date range (all combine). Reads count+1 commits so the extra row's
    /// presence is a cheap HasMore signal; it is trimmed before returning.
    /// </summary>
    public async Task<CommitPage> GetCommitsPagedAsync(string repoPath, int skip, int count,
        CommitFilter? filter = null, CancellationToken ct = default)
    {
        var args = new List<string> { "log", CommitLogFormat, $"--skip={skip}", $"-n{count + 1}" };
        string? path = null;
        if (filter is not null)
        {
            if (!string.IsNullOrEmpty(filter.MessageGrep)) args.Add("--grep=" + filter.MessageGrep);
            if (!string.IsNullOrEmpty(filter.Author)) args.Add("--author=" + filter.Author);
            if (filter.Since is { } since) args.Add("--since=" + since.ToString("o"));
            if (filter.Until is { } until) args.Add("--until=" + until.ToString("o"));
            path = string.IsNullOrEmpty(filter.Path) ? null : filter.Path;
        }
        if (path is not null) { args.Add("--"); args.Add(LiteralPathspec(path)); }

        var result = await RunAsync(repoPath, args, ct);
        if (!result.Success)
        {
            Log.Warn($"git log paged failed for {repoPath}: {result.FirstError}");
            return new CommitPage();
        }

        var all = new List<GitCommit>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (ParseCommitLine(line) is { } commit) all.Add(commit);

        var hasMore = all.Count > count;
        return new CommitPage
        {
            Commits = hasMore ? all.GetRange(0, count) : all,
            HasMore = hasMore
        };
    }

    public Task<ProcessResult> StageHunkAsync(string repoPath, string patchText, CancellationToken ct = default)
        => ApplyPatchAsync(repoPath, patchText, ["apply", "--cached"], ct);

    public Task<ProcessResult> UnstageHunkAsync(string repoPath, string patchText, CancellationToken ct = default)
        => ApplyPatchAsync(repoPath, patchText, ["apply", "--cached", "--reverse"], ct);

    public Task<ProcessResult> DiscardHunkAsync(string repoPath, string patchText, CancellationToken ct = default)
        => ApplyPatchAsync(repoPath, patchText, ["apply", "--reverse"], ct);

    private async Task<ProcessResult> ApplyPatchAsync(string repoPath, string patchText,
        IReadOnlyList<string> applyArgs, CancellationToken ct)
    {
        // git apply reads a patch FILE (ProcessRunner has no stdin); paths inside the
        // patch resolve against the repo working dir, not the patch file's location.
        // The patch already carries its own line endings (CRLF preserved from the source
        // diff) and git apply is strict about the trailing newline, so the bytes are
        // written verbatim with no newline translation.
        var tmp = Path.Combine(Path.GetTempPath(), $"pd-hunk-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(tmp, patchText, new UTF8Encoding(false), ct);
            var args = new List<string>(applyArgs) { tmp };
            return await RunAsync(repoPath, args, ct);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>Raw `git diff` for one file, bytes untouched (CRLF preserved) so a hunk can be sliced out faithfully. Null on failure.</summary>
    public async Task<RawFileDiff?> GetFileDiffRawAsync(string repoPath, string filePath, bool staged, CancellationToken ct = default)
    {
        var args = new List<string> { "diff", "--no-color" };
        if (staged) args.Add("--cached");
        args.Add("--");
        args.Add(LiteralPathspec(filePath));
        var result = await RunAsync(repoPath, args, ct);
        if (!result.Success)
        {
            Log.Warn($"git diff (raw) failed for {filePath} in {repoPath}: {result.FirstError}");
            return null;
        }
        return new RawFileDiff(result.StdOut, result.Truncated);
    }

    /// <summary>
    /// Slices a single-hunk patch for <paramref name="filePath"/> out of RAW `git diff` output,
    /// byte-for-byte. The only patch builder: a patch reconstructed from a parsed
    /// <see cref="FileDiff"/> cannot be byte-faithful, because the model discards the CR
    /// of a CRLF line and cannot tell the "\ No newline at end of file" marker from a
    /// context line whose own content begins with a backslash — either one produces a
    /// patch `git apply` rejects or, worse, applies with the wrong bytes.
    ///
    /// The index counts hunks WITHIN that file's own "diff --git" section, as
    /// <see cref="FileDiff.ParseUnified"/> counts them, so a text carrying more than one file
    /// cannot leave the row on screen and the slice naming different hunks. Null when the text
    /// has no section for the path, or that section has no hunk at <paramref name="hunkIndex"/>.
    /// Body lines never start with "@@" (they carry a +/-/space prefix), so a column-0 "@@"
    /// unambiguously marks a hunk header.
    /// </summary>
    public static string? ExtractHunkPatch(string rawFileDiff, string filePath, int hunkIndex)
    {
        if (string.IsNullOrEmpty(rawFileDiff) || string.IsNullOrEmpty(filePath)) return null;
        // Split on '\n' only: each element keeps its trailing '\r' for a CRLF diff.
        var lines = rawFileDiff.Split('\n');
        if (!TryFindFileSection(lines, filePath, out var from, out var to)) return null;

        var headers = new List<int>();
        for (var i = from; i < to; i++)
            if (lines[i].StartsWith("@@", StringComparison.Ordinal)) headers.Add(i);
        if (hunkIndex < 0 || hunkIndex >= headers.Count) return null;

        var start = headers[hunkIndex];
        var end = hunkIndex + 1 < headers.Count ? headers[hunkIndex + 1] : to;

        var sb = new StringBuilder();
        for (var i = from; i < headers[0]; i++)  // preamble: diff --git / index / --- / +++
            sb.Append(lines[i]).Append('\n');
        for (var i = start; i < end; i++)
        {
            // A diff ending in '\n' yields a trailing empty split element; don't emit it
            // as a spurious blank line.
            if (i == lines.Length - 1 && lines[i].Length == 0) break;
            sb.Append(lines[i]).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bounds of the one section describing <paramref name="filePath"/>, as [from, to). False
    /// when no section names it — a slice from the wrong section applies somebody else's change,
    /// so an unrecognized rendering (git C-quotes a path holding a quote, a backslash, or a
    /// control byte even with core.quotepath off) refuses rather than guesses.
    /// </summary>
    private static bool TryFindFileSection(string[] lines, string filePath, out int from, out int to)
    {
        from = to = 0;
        var open = -1;
        for (var i = 0; i <= lines.Length; i++)
        {
            var starts = i < lines.Length && IsSectionStart(lines[i]);
            if (i < lines.Length && !starts) continue;
            if (open >= 0 && SectionPath(lines, open, i) == filePath)
            {
                from = open;
                to = i;
                return true;
            }
            open = starts ? i : -1;
        }
        return false;
    }

    private static bool IsSectionStart(string line) =>
        line.StartsWith("diff --git ", StringComparison.Ordinal) ||
        line.StartsWith("diff --cc ", StringComparison.Ordinal) ||
        line.StartsWith("diff --combined ", StringComparison.Ordinal);

    /// <summary>
    /// The path a section describes, derived exactly as <see cref="FileDiff.ParseUnified"/>
    /// derives it: seeded from the "diff --git" header so a mode-only change still names its
    /// file, then overridden by the rename and "+++" headers, which appear only before the
    /// section's first hunk.
    /// </summary>
    private static string SectionPath(string[] lines, int from, int to)
    {
        var header = lines[from].TrimEnd('\r');
        if (!header.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            var sp = header.IndexOf(' ', 8);
            return sp > 0 ? header[(sp + 1)..].Trim() : "";
        }

        var path = FileDiff.PathFromDiffGit(header);
        string? oldPath = null;
        for (var i = from + 1; i < to; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal)) break;
            if (line.StartsWith("rename from ", StringComparison.Ordinal))
                oldPath = line["rename from ".Length..];
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                path = line["rename to ".Length..];
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var p = line[4..];
                if (p != "/dev/null") oldPath = FileDiff.StripPrefix(p);
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var p = line[4..];
                path = p == "/dev/null" ? oldPath ?? "" : FileDiff.StripPrefix(p);
            }
        }
        return path;
    }

    public Task<ProcessResult> StashPushAsync(string repoPath, string? message = null,
        bool includeUntracked = false, CancellationToken ct = default)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("-u");
        if (!string.IsNullOrEmpty(message)) { args.Add("-m"); args.Add(message); }
        return RunAsync(repoPath, args, ct, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The stashed change as a parsed diff, or null when the read failed. An empty list means a
    /// stash that recorded no file change; reported as the same value, a failed read would offer
    /// a reader an empty preview of work that is still in the stash.
    /// </summary>
    public async Task<List<FileDiff>?> GetStashDiffAsync(string repoPath, string stashRef, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["stash", "show", "-p", "--no-color", stashRef], ct);
        if (!result.Success)
        {
            Log.Warn($"git stash show failed for {stashRef} in {repoPath}: {result.FirstError}");
            return null;
        }
        var diffs = FileDiff.ParseUnified(result.StdOut);
        // The budget cuts one read covering every file, so the prefix is short of the whole
        // stash — not only of the file its last row fell in.
        foreach (var diff in diffs) diff.Truncated = result.Truncated;
        return diffs;
    }

    public async Task<List<WorktreeEntry>> GetWorktreesAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["worktree", "list", "--porcelain"], ct);
        if (!result.Success)
        {
            Log.Warn($"git worktree list failed for {repoPath}: {result.FirstError}");
            return [];
        }
        return ParseWorktreePorcelain(result.StdOut);
    }

    /// <summary>
    /// Parses `git worktree list --porcelain`: blank-line-separated blocks of
    /// "worktree &lt;path&gt;", "HEAD &lt;sha&gt;", and one of "branch &lt;ref&gt;" / "detached" /
    /// "bare", plus optional "locked" and "prunable &lt;reason&gt;" lines.
    /// <para>
    /// The first block is the main worktree — git lists it first from every checkout, including
    /// from a linked worktree — and that position is the only thing in the listing that
    /// identifies it. Nothing else may be removed from the record, so it is read here rather
    /// than re-derived by a caller comparing paths.
    /// </para>
    /// </summary>
    internal static List<WorktreeEntry> ParseWorktreePorcelain(string porcelain)
    {
        var entries = new List<WorktreeEntry>();
        string? path = null, head = null, branch = null;
        var prunableReason = "";
        bool bare = false, detached = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is null) return;
            entries.Add(new WorktreeEntry
            {
                Path = path,
                HeadSha = head ?? "",
                Branch = branch,
                IsBare = bare,
                IsDetached = detached,
                IsLocked = locked,
                IsMain = entries.Count == 0,
                IsPrunable = prunable,
                PrunableReason = prunableReason
            });
            path = head = branch = null;
            prunableReason = "";
            bare = detached = locked = prunable = false;
        }

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }

            var sp = line.IndexOf(' ');
            var key = sp < 0 ? line : line[..sp];
            var val = sp < 0 ? "" : line[(sp + 1)..];
            switch (key)
            {
                case "worktree": Flush(); path = val; break;
                case "HEAD": head = val; break;
                case "branch": branch = val.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? val["refs/heads/".Length..] : val; break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "locked": locked = true; break;
                case "prunable": prunable = true; prunableReason = val; break;
            }
        }
        Flush();
        return entries;
    }

    public Task<ProcessResult> AddWorktreeAsync(string repoPath, string path, string? branch = null, CancellationToken ct = default)
    {
        var args = new List<string> { "worktree", "add" };
        if (!string.IsNullOrEmpty(branch)) { args.Add("-b"); args.Add(branch); }
        args.Add(path);
        return RunAsync(repoPath, args, ct, TimeSpan.FromSeconds(30));
    }

    public Task<ProcessResult> RemoveWorktreeAsync(string repoPath, string path, CancellationToken ct = default)
        => RunAsync(repoPath, ["worktree", "remove", path], ct);

    /// <summary>
    /// Drops the administrative entries whose working trees are gone. Only entries git already
    /// reports as prunable are affected; a worktree still on disk is untouched.
    /// </summary>
    public Task<ProcessResult> PruneWorktreesAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["worktree", "prune"], ct);

    /// <summary>Root .gitignore contents, or null when absent.</summary>
    public async Task<string?> GetGitignoreAsync(string repoPath, CancellationToken ct = default)
    {
        var p = Path.Combine(repoPath, ".gitignore");
        if (!File.Exists(p)) return null;
        return await File.ReadAllTextAsync(p, ct);
    }

    /// <summary>Overwrites the root .gitignore (a plain file write — no git command runs).</summary>
    public Task SaveGitignoreAsync(string repoPath, string content, CancellationToken ct = default)
        => File.WriteAllTextAsync(Path.Combine(repoPath, ".gitignore"), content, ct);

    /// <summary>
    /// Appends a pattern to the root .gitignore unless it already appears as a whole-line entry,
    /// and reports whether anything was written. The dedupe leaves no trace in the file, so a
    /// caller that read only the absence of an exception would report a rule it did not add.
    /// </summary>
    public Task<bool> AppendIgnoreEntryAsync(string repoPath, string pattern, CancellationToken ct = default)
        => AppendIgnoreLineAsync(Path.Combine(repoPath, ".gitignore"), pattern, ct);

    /// <summary>
    /// The same append-if-absent against the global excludes file, whose parent directory may not
    /// exist yet — git's default location is only created when something writes there.
    /// </summary>
    public Task<bool> AppendGlobalIgnoreEntryAsync(string excludesPath, string pattern, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(excludesPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return AppendIgnoreLineAsync(excludesPath, pattern, ct);
    }

    private static async Task<bool> AppendIgnoreLineAsync(string path, string pattern, CancellationToken ct)
    {
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        var target = NormalizeIgnoreLine(pattern);
        if (existing.Split('\n').Any(l => NormalizeIgnoreLine(l.TrimEnd('\r')) == target)) return false;

        var sb = new StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith('\n')) sb.Append('\n');
        sb.Append(target).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        return true;
    }

    /// <summary>
    /// Where global ignore rules live: core.excludesFile when configured, with a leading "~"
    /// expanded the way git expands it, otherwise git's own default of
    /// $XDG_CONFIG_HOME/git/ignore falling back to ~/.config/git/ignore. The default is returned
    /// whether or not the file exists — git reads it without any configuration, so a rule
    /// appended there takes effect with nothing else written. Null when git could not answer
    /// whether the setting exists, which is not the same as it being unset.
    /// </summary>
    public virtual async Task<string?> GetGlobalExcludesPathAsync(string repoPath, CancellationToken ct = default)
    {
        var read = await RunAsync(repoPath, ["config", "--global", "--get", "core.excludesFile"], ct,
            TimeSpan.FromSeconds(10));
        if (read.TimedOut) return null;
        var configured = read.StdOut.Trim();
        if (read.Success && configured.Length > 0) return ExpandTilde(configured);
        // Exit 1 with nothing on stderr is `--get` reporting the key unset; anything else is git
        // failing to answer, and a default returned then could shadow a configured location.
        if (!read.Success && read.StdErr.Trim().Length > 0) return null;

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(configHome, "git", "ignore");
    }

    private static string ExpandTilde(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;

    /// <summary>
    /// A .gitignore line matching exactly one path and nothing else. The leading slash anchors the
    /// entry to the repository root, without which a bare name also matches every namesake in
    /// every subdirectory; '*', '?' and '[' are escaped, without which a path holding one is read
    /// as a pattern and matches files other than the one it was written for — or misses it.
    /// The separator is normalized because .gitignore is matched against the forward-slash paths
    /// git records, never the platform's.
    /// </summary>
    public static string IgnoreLineForPath(string path) =>
        "/" + EscapeIgnoreLine(path.Replace('\\', '/').TrimStart('/'));

    /// <summary>A .gitignore line matching every file with one extension, with or without its leading dot.</summary>
    public static string IgnoreLineForExtension(string extension) =>
        "*." + EscapeIgnoreLine(extension.TrimStart('.'));

    /// <summary>
    /// A line matching every file carrying one name, in any directory. Unanchored by intent — the
    /// global excludes file has no repository root to anchor to, and the name is the rule.
    /// </summary>
    public static string IgnoreLineForName(string fileName) => EscapeIgnoreLine(fileName);

    private static string EscapeIgnoreLine(string value) => EscapeTrailingWhitespace(EscapeIgnoreGlob(value));

    private static string EscapeIgnoreGlob(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '*' or '?' or '[') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// git drops trailing whitespace from a .gitignore line unless a backslash quotes it, so a
    /// name ending in one needs every character of that run escaped — unescaped, the rule becomes
    /// one for the trimmed name, which misses the file it was written for and catches another.
    /// </summary>
    private static string EscapeTrailingWhitespace(string value)
    {
        var end = value.Length;
        while (end > 0 && char.IsWhiteSpace(value[end - 1])) end--;
        if (end == value.Length) return value;

        var sb = new StringBuilder(value[..end]);
        for (var i = end; i < value.Length; i++) sb.Append('\\').Append(value[i]);
        return sb.ToString();
    }

    /// <summary>
    /// One .gitignore line as git reads it: leading whitespace and unescaped trailing whitespace
    /// are not part of the pattern. Trailing whitespace a backslash quotes is, so trimming it
    /// would turn a rule for a name ending in a space into a rule for a different name.
    /// </summary>
    private static string NormalizeIgnoreLine(string line)
    {
        var trimmed = line.TrimStart();
        var end = trimmed.Length;
        while (end > 0 && char.IsWhiteSpace(trimmed[end - 1]) && !IsBackslashEscaped(trimmed, end - 1)) end--;
        return trimmed[..end];
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> is quoted. An odd run of backslashes
    /// before it escapes it; an even run is escaped backslashes and leaves it bare.
    /// </summary>
    private static bool IsBackslashEscaped(string value, int index)
    {
        var backslashes = 0;
        for (var i = index - 1; i >= 0 && value[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>
    /// What git says about one path and the ignore rules. `check-ignore` exits 0 when the path is
    /// ignored and 1 when it is not, and anything else — 128 for a path outside the repository, a
    /// kill on timeout — is an error rather than a "no". Exit 1 is also what a tracked path gets
    /// even when a rule matches it, because check-ignore consults the index, so trackedness is
    /// read separately and carried alongside the answer.
    /// </summary>
    public async Task<IgnoreAnswer> CheckIgnoreAsync(string repoPath, string path, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["check-ignore", "-q", "--", path], ct);
        if (result.TimedOut) return new IgnoreAnswer(IgnoreState.Unknown, false, "the check timed out");
        if (result.ExitCode == 0) return new IgnoreAnswer(IgnoreState.Ignored, false, "");
        if (result.ExitCode != 1) return new IgnoreAnswer(IgnoreState.Unknown, false, result.FirstError);

        return new IgnoreAnswer(IgnoreState.NotIgnored, await IsTrackedAsync(repoPath, path, ct), "");
    }

    /// <summary>
    /// Whether the index holds this exact path. `ls-files` exits 0 either way and prints every
    /// index entry the pathspec covers, which for a directory is the files UNDER it — so any
    /// output at all would report a directory containing tracked files as itself tracked. Only
    /// an entry equal to the path answers the question that was asked. -z returns a path holding
    /// a quote, a backslash, or a non-ASCII byte byte-exact instead of C-quoting it.
    /// </summary>
    public async Task<bool> IsTrackedAsync(string repoPath, string path, CancellationToken ct = default)
    {
        // The index records forward slashes, and so does every entry ls-files prints; a path
        // typed with the platform separator has to be asked for in the form git holds it.
        var wanted = path.Replace('\\', '/');
        var result = await RunAsync(repoPath, ["ls-files", "-z", "--", LiteralPathspec(wanted)], ct);
        if (!result.Success) return false;
        return result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => string.Equals(entry, wanted, StringComparison.Ordinal));
    }

    // ── Clone ───────────────────────────────────────────────────────────────

    /// <summary>Clones into targetParentDir/<name>. Returns null on success, else a short error.</summary>
    public async Task<string?> CloneAsync(string url, string targetParentDir, CancellationToken ct = default,
        TimeSpan? timeout = null, Action<string>? onProgress = null)
    {
        var repoName = GitRemote.RepoNameFromUrl(url);
        var target = repoName.Length > 0 ? Path.Combine(targetParentDir, repoName) : null;
        var existedBefore = target is not null && Directory.Exists(target);

        var result = onProgress is null
            ? await RunAsync(targetParentDir, ["clone", "--", url], null, ct, timeout ?? TimeSpan.FromMinutes(15))
            : await RunWithProgressAsync(
                targetParentDir, ["clone", "--progress", "--", url], onProgress, ct, timeout ?? TimeSpan.FromMinutes(15));
        if (result.Success) return null;

        // A failed or timeout-killed clone can leave a partial target directory:
        // the next attempt then dies on the exists-guard and discovery reads the
        // remnant as a broken repo. Remove it only when this clone created it —
        // a pre-existing directory is never deleted.
        if (target is not null && !existedBefore && Directory.Exists(target)
            && IsSafeCloneCleanupTarget(target, targetParentDir, existedBefore))
        {
            try
            {
                ForceDeleteDirectory(target);
                return $"{result.FirstError} — removed the partial clone at {target}";
            }
            catch (Exception ex)
            {
                Log.Warn($"could not remove partial clone at {target}", ex);
                return $"{result.FirstError} — a partial clone remains at {target}; delete it before retrying";
            }
        }
        return result.FirstError;
    }

    /// <summary>
    /// True only when a failed clone's target directory may be deleted: the clone
    /// itself created it (it did not exist beforehand) and it normalizes to a
    /// DIRECT child of the parent directory the clone ran in. Anything else — a
    /// pre-existing directory, the parent itself, a traversal that escapes it —
    /// is kept.
    /// </summary>
    public static bool IsSafeCloneCleanupTarget(string targetDir, string parentDir, bool existedBeforeClone)
    {
        if (existedBeforeClone) return false;
        if (string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(parentDir)) return false;
        try
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDir));
            var targetParent = Path.GetDirectoryName(target);
            return targetParent is not null
                && string.Equals(targetParent, parent, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target, parent, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Recursive delete that first clears the read-only bit git sets on object files (Directory.Delete refuses read-only entries).</summary>
    private static void ForceDeleteDirectory(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Resolve git: known install dirs first (survives a stale Start-Menu PATH), then PATH.
    /// Internal so a caller that must name the same binary in a command line of its own — a
    /// rebase todo's exec line — runs the executable this service starts, not a second one.
    /// </summary>
    internal static string ResolveGitExe()
    {
        string[] known =
        [
            Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files", "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)", "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("LocalAppData") ?? "", "Programs", "Git", "cmd", "git.exe"),
        ];
        foreach (var p in known)
            if (p.Length > 0 && File.Exists(p)) return p;
        return "git"; // last resort: PATH
    }
}
