using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

public class GitService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Environment for every git call: never prompt for credentials (a windowless app
    /// would hang invisibly), never take optional index locks during reads.
    /// </summary>
    private static readonly Dictionary<string, string> GitEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0"
    };

    /// <summary>
    /// True when the directory is a git checkout. A primary checkout has a .git
    /// DIRECTORY; a linked worktree or submodule has a .git FILE — accept both.
    /// </summary>
    public static bool IsGitRepo(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    public async Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
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

        try
        {
            var logLine = await RunGitAsync(repoPath, ["log", "-1", "--format=%aI|%s"], ct);
            if (!string.IsNullOrWhiteSpace(logLine))
            {
                var parts = logLine.Trim().Split('|', 2);
                if (parts.Length >= 1 && DateTimeOffset.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                    status.LastCommitDate = date;
                if (parts.Length >= 2)
                    status.LastCommitMessage = parts[1];
            }
        }
        catch { /* no commits yet */ }

        try { status.RemoteUrl = (await RunGitAsync(repoPath, ["config", "--get", "remote.origin.url"], ct)).Trim(); }
        catch { /* no remote */ }

        return status;
    }

    public async Task<List<GitCommit>> GetRecentCommitsAsync(string repoPath, int count = 20, CancellationToken ct = default)
    {
        var commits = new List<GitCommit>();

        try
        {
            var output = await RunGitAsync(repoPath, ["log", $"--format=%h|%an|%aI|%s", "-n", count.ToString()], ct);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 4);
                if (parts.Length < 4) continue;

                commits.Add(new GitCommit
                {
                    ShortHash = parts[0],
                    Author = parts[1],
                    Date = DateTimeOffset.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d) ? d : default,
                    Message = parts[3]
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"git log failed for {repoPath}", ex);
        }

        return commits;
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

    private async Task<RepoActivity> DetectActivityAsync(string repoPath, CancellationToken ct)
    {
        // Resolve the real git dir — a linked worktree's .git is a file pointing elsewhere.
        var result = await RunAsync(repoPath, ["rev-parse", "--git-dir"], ct);
        if (!result.Success) return RepoActivity.None;

        var gitDir = result.StdOut.Trim();
        if (!Path.IsPathRooted(gitDir)) gitDir = Path.Combine(repoPath, gitDir);

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

        var commit = await RunAsync(repoPath, ["commit", "-m", commitMessage], ct);
        if (!commit.Success) return $"git commit failed: {commit.FirstError}";

        return null;
    }

    /// <summary>Structured run for callers that need exit codes and stderr (no throw on failure).</summary>
    public async Task<ProcessResult> RunAsync(string repoPath, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        // core.quotepath=false: unicode paths arrive as UTF-8, not octal escapes.
        var full = new List<string> { "-c", "core.quotepath=false" };
        full.AddRange(args);
        return await ProcessRunner.RunAsync(ResolveGitExe(), full, repoPath, timeout ?? Timeout, GitEnvironment, ct);
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
        args.Add(file.Path);
        if (file.OrigPath is not null) args.Add(file.OrigPath);

        var result = await RunAsync(repoPath, args, ct);
        if (!result.Success)
        {
            Log.Warn($"git diff failed for {file.Path}: {result.FirstError}");
            return null;
        }
        return FileDiff.ParseUnified(result.StdOut).FirstOrDefault();
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
        => RunAsync(repoPath, ["add", "--", path], ct);

    public Task<ProcessResult> UnstageAsync(string repoPath, string path, CancellationToken ct = default)
        => RunAsync(repoPath, ["restore", "--staged", "--", path], ct);

    public Task<ProcessResult> StageAllAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["add", "-A"], ct);

    public Task<ProcessResult> UnstageAllAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["restore", "--staged", "."], ct);

    /// <summary>Discards a file's unstaged state: untracked files are deleted, tracked files restored.</summary>
    public Task<ProcessResult> DiscardAsync(string repoPath, WorkingFile file, CancellationToken ct = default)
        => file.IsUntracked
            ? RunAsync(repoPath, ["clean", "-f", "--", file.Path], ct)
            : RunAsync(repoPath, ["restore", "--", file.Path], ct);

    public Task<ProcessResult> CommitAsync(string repoPath, string message, bool amend, CancellationToken ct = default)
    {
        var args = new List<string> { "commit", "-m", message };
        if (amend) args.Add("--amend");
        return RunAsync(repoPath, args, ct, TimeSpan.FromSeconds(30));
    }

    public async Task<string> GetLastCommitMessageAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath, ["log", "-1", "--format=%B"], ct);
        return result.Success ? result.StdOut.TrimEnd() : "";
    }

    // ── Branches ────────────────────────────────────────────────────────────

    public async Task<List<BranchInfo>> GetBranchesAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await RunAsync(repoPath,
            ["for-each-ref", "refs/heads",
             "--format=%(refname:short)|%(HEAD)|%(upstream:short)|%(upstream:track)|%(committerdate:iso8601-strict)"], ct);
        if (!result.Success)
        {
            Log.Warn($"git for-each-ref failed for {repoPath}: {result.FirstError}");
            return [];
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
        return branches;
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

    /// <summary>Fast-forward-only pull: a diverged branch fails loudly instead of creating a surprise merge.</summary>
    public Task<ProcessResult> PullAsync(string repoPath, CancellationToken ct = default)
        => RunAsync(repoPath, ["pull", "--ff-only"], ct, NetworkTimeout);

    /// <summary>Push; sets upstream automatically when the branch has none.</summary>
    public async Task<ProcessResult> PushAsync(string repoPath, CancellationToken ct = default)
    {
        var upstream = await RunAsync(repoPath, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"], ct);
        return upstream.Success
            ? await RunAsync(repoPath, ["push"], ct, NetworkTimeout)
            : await RunAsync(repoPath, ["push", "-u", "origin", "HEAD"], ct, NetworkTimeout);
    }

    // ── Stash ───────────────────────────────────────────────────────────────

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
        var result = await RunAsync(repoPath, ["show", "--no-color", "--format=", hash, "--", filePath], ct);
        if (!result.Success) return null;
        return FileDiff.ParseUnified(result.StdOut).FirstOrDefault();
    }

    // ── Clone ───────────────────────────────────────────────────────────────

    /// <summary>Clones into targetParentDir/<name>. Returns null on success, else a short error.</summary>
    public async Task<string?> CloneAsync(string url, string targetParentDir, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(ResolveGitExe(),
            ["clone", "--", url], targetParentDir, TimeSpan.FromMinutes(15), GitEnvironment, ct);
        return result.Success ? null : result.FirstError;
    }

    /// <summary>Resolve git: known install dirs first (survives a stale Start-Menu PATH), then PATH.</summary>
    private static string ResolveGitExe()
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
