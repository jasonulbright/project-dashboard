using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Recovery paths for artifacts a killed git process leaves behind: orphaned
/// index.lock files and partially-created clone targets.
/// </summary>
public class GitServiceRecoveryTests
{
    private readonly GitService _git = new();

    private static readonly TimeSpan FastRecheck = TimeSpan.FromMilliseconds(50);

    private static void BackdateLock(string lockPath)
    {
        var old = DateTime.UtcNow.AddMinutes(-10);
        File.SetCreationTimeUtc(lockPath, old);
        File.SetLastWriteTimeUtc(lockPath, old);
    }

    [Fact]
    public async Task TryCleanStaleLock_RemovesABackdatedLock()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("stale-lock");
        var lockPath = System.IO.Path.Combine(repo.Path, ".git", "index.lock");
        File.WriteAllText(lockPath, "");
        BackdateLock(lockPath);

        var removed = await _git.TryCleanStaleLockAsync(repo.Path, recheckDelay: FastRecheck);

        Assert.True(removed);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task TryCleanStaleLock_KeepsAFreshLock()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("fresh-lock");
        var lockPath = System.IO.Path.Combine(repo.Path, ".git", "index.lock");
        File.WriteAllText(lockPath, "");

        var removed = await _git.TryCleanStaleLockAsync(repo.Path, recheckDelay: FastRecheck);

        Assert.False(removed);
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public async Task TryCleanStaleLock_NoLock_ReturnsFalse()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("no-lock");
        Assert.False(await _git.TryCleanStaleLockAsync(repo.Path, recheckDelay: FastRecheck));
    }

    [Fact]
    public async Task TryCleanStaleLock_ResolvesThroughAWorktreeGitFile()
    {
        using var main = await TempRepo.CreateWithCommitAsync("wt-main");
        var wtPath = System.IO.Path.Combine(TestEnv.NewDir("wt-parent"), "wt");
        await main.GitAsync("worktree", "add", wtPath, "-b", "wt-branch");

        // A linked worktree's .git is a FILE; its private git dir (and lock)
        // lives under the main repo's .git/worktrees/<name>.
        Assert.True(File.Exists(System.IO.Path.Combine(wtPath, ".git")));
        var lockPath = System.IO.Path.Combine(main.Path, ".git", "worktrees", "wt", "index.lock");
        File.WriteAllText(lockPath, "");
        BackdateLock(lockPath);

        var removed = await _git.TryCleanStaleLockAsync(wtPath, recheckDelay: FastRecheck);

        Assert.True(removed);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void IsIndexLockConflict_MatchesGitsLockMessageOnly()
    {
        var lockFail = new ProcessResult(128, "",
            "fatal: Unable to create 'C:/repo/.git/index.lock': File exists.\n\nAnother git process seems to be running.",
            TimedOut: false);
        Assert.True(GitService.IsIndexLockConflict(lockFail));

        Assert.False(GitService.IsIndexLockConflict(new ProcessResult(0, "ok", "", TimedOut: false)));
        Assert.False(GitService.IsIndexLockConflict(new ProcessResult(1, "", "fatal: pathspec 'x' did not match", TimedOut: false)));
        // Success with lock-like text must not count as a conflict.
        Assert.False(GitService.IsIndexLockConflict(new ProcessResult(0, "index.lock File exists", "", TimedOut: false)));
    }

    [Fact]
    public void IsSafeCloneCleanupTarget_AllowsOnlyAFreshDirectChild()
    {
        var parent = TestEnv.NewDir("safe-parent");
        var child = System.IO.Path.Combine(parent, "repo");

        Assert.True(GitService.IsSafeCloneCleanupTarget(child, parent, existedBeforeClone: false));

        // A directory that predates the clone is never deleted.
        Assert.False(GitService.IsSafeCloneCleanupTarget(child, parent, existedBeforeClone: true));
        // The parent itself, a grandchild, and traversal escapes are all refused.
        Assert.False(GitService.IsSafeCloneCleanupTarget(parent, parent, existedBeforeClone: false));
        Assert.False(GitService.IsSafeCloneCleanupTarget(System.IO.Path.Combine(child, "nested"), parent, existedBeforeClone: false));
        Assert.False(GitService.IsSafeCloneCleanupTarget(System.IO.Path.Combine(parent, "..", "elsewhere"), parent, existedBeforeClone: false));
        Assert.False(GitService.IsSafeCloneCleanupTarget(System.IO.Path.Combine(parent, "repo", "..", "..", "escape"), parent, existedBeforeClone: false));
        Assert.False(GitService.IsSafeCloneCleanupTarget("", parent, existedBeforeClone: false));
        Assert.False(GitService.IsSafeCloneCleanupTarget(child, "", existedBeforeClone: false));
    }

    [Fact]
    public async Task Clone_NonexistentSource_FailsAndLeavesNoTarget()
    {
        var parent = TestEnv.NewDir("clone-missing");
        var url = System.IO.Path.Combine(TestEnv.Root, "no-such-source");

        var error = await _git.CloneAsync(url, parent);

        Assert.NotNull(error);
        Assert.False(Directory.Exists(System.IO.Path.Combine(parent, "no-such-source")));
    }

    [Fact]
    public async Task Clone_KilledByTimeout_LeavesNoPartialTarget()
    {
        using var source = await TempRepo.CreateWithCommitAsync("clone-src");
        using var bare = await TempRepo.CreateBareFromAsync(source);
        var parent = TestEnv.NewDir("clone-killed");

        var error = await _git.CloneAsync(bare.FileUrl, parent, timeout: TimeSpan.FromMilliseconds(50));

        // Whether the kill landed before or after git created the directory, the
        // retry precondition is the same: an error and no leftover target.
        Assert.NotNull(error);
        Assert.False(Directory.Exists(System.IO.Path.Combine(parent, "remote")));
    }

    [Fact]
    public async Task Clone_PreexistingTarget_IsNeverDeleted()
    {
        using var source = await TempRepo.CreateWithCommitAsync("clone-pre-src");
        using var bare = await TempRepo.CreateBareFromAsync(source);
        var parent = TestEnv.NewDir("clone-pre");
        var target = System.IO.Path.Combine(parent, "remote");
        Directory.CreateDirectory(target);
        File.WriteAllText(System.IO.Path.Combine(target, "keep.txt"), "precious\n");

        var error = await _git.CloneAsync(bare.FileUrl, parent);

        Assert.NotNull(error);
        Assert.True(File.Exists(System.IO.Path.Combine(target, "keep.txt")));
    }
}
