using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Busy-gate and generation semantics of the detail page against real fixture
/// repos. Long-running git ops are made deterministic with a pre-commit hook
/// that blocks until a sentinel file appears, so a project switch can be
/// interleaved at an exact point of an in-flight operation.
/// </summary>
public class ProjectDetailViewModelTests
{
    /// <summary>
    /// The discovery service is only reached through SaveManifestCommand and the
    /// GitHub service only behind a non-empty GitHubSlug (fixture repos have no
    /// remote), so both nulls keep the tests to local git operations.
    /// </summary>
    private static ProjectDetailViewModel NewVm() => new(null!, new GitService(), null!);

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    /// <summary>Blocks `git commit` in the pre-commit hook until the sentinel file exists (self-caps at ~20 s).</summary>
    private static void InstallBlockingPreCommitHook(TempRepo repo, string sentinelPath)
    {
        var hookDir = System.IO.Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hookDir);
        var sentinel = sentinelPath.Replace('\\', '/');
        File.WriteAllText(System.IO.Path.Combine(hookDir, "pre-commit"),
            $"#!/bin/sh\nn=0\nwhile [ ! -f '{sentinel}' ]; do\n  n=$((n+1))\n  [ $n -gt 200 ] && exit 1\n  sleep 0.1\ndone\nexit 0\n");
    }

    private static async Task<TempRepo> RepoWithStagedChangeAsync(string prefix, string file, string content)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile(file, content);
        await repo.GitAsync("add", "-A");
        return repo;
    }

    [Fact]
    public async Task SwitchMidCommit_StaleFinallyDoesNotReleaseTheNewProjectsGate()
    {
        using var repoA = await RepoWithStagedChangeAsync("gate-a", "a.txt", "a\n");
        using var repoB = await RepoWithStagedChangeAsync("gate-b", "b.txt", "b\n");
        var sentinelDir = TestEnv.NewDir("sentinels");
        var sentinelA = System.IO.Path.Combine(sentinelDir, "release-a");
        var sentinelB = System.IO.Path.Combine(sentinelDir, "release-b");
        InstallBlockingPreCommitHook(repoA, sentinelA);
        InstallBlockingPreCommitHook(repoB, sentinelB);

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.StagedFiles = [new WorkingFile { Path = "a.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "slow commit on A";

        var commitA = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); // A holds the gate; git is blocked in the hook

        // Switch to B mid-commit: the reset opens the gate for B's own work.
        await vm.SetProjectAsync(ProjectFor(repoB));
        Assert.False(vm.IsBusy);
        vm.StagedFiles = [new WorkingFile { Path = "b.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "draft typed on B";

        var commitB = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); // B holds the gate now

        // A finishes while B is still mid-flight. Its finally must not release
        // B's gate, its status must not surface, and its success continuation
        // must not clear B's message.
        File.WriteAllText(sentinelA, "go");
        await commitA;
        Assert.True(vm.IsBusy);
        Assert.Equal("Commit…", vm.SyncStatusText);
        Assert.Equal("draft typed on B", vm.CommitMessage);

        // B completes normally: only its own finally releases the gate.
        File.WriteAllText(sentinelB, "go");
        await commitB;
        Assert.False(vm.IsBusy);
        Assert.Equal("Commit done.", vm.SyncStatusText);
        Assert.Equal("", vm.CommitMessage);

        // The stale guard suppresses UI writes only — both commits really landed.
        Assert.Equal(2, await repoA.CommitCountAsync());
        Assert.Equal(2, await repoB.CommitCountAsync());
        Assert.Equal("draft typed on B", await repoB.HeadSubjectAsync());
    }

    [Fact]
    public async Task SwitchMidCommit_StaleCompletionTouchesNothingOnAnIdleNewProject()
    {
        using var repoA = await RepoWithStagedChangeAsync("idle-a", "a.txt", "a\n");
        using var repoB = await TempRepo.CreateWithCommitAsync("idle-b");
        var sentinelA = System.IO.Path.Combine(TestEnv.NewDir("sentinels"), "release-a");
        InstallBlockingPreCommitHook(repoA, sentinelA);

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repoA));
        vm.StagedFiles = [new WorkingFile { Path = "a.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "slow commit on A";

        var commitA = vm.CommitCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        await vm.SetProjectAsync(ProjectFor(repoB));
        vm.CommitMessage = "draft typed on B";

        File.WriteAllText(sentinelA, "go");
        await commitA;

        // No op runs on B, so the gate stays open, the status stays reset, and
        // the draft survives A's completion.
        Assert.False(vm.IsBusy);
        Assert.Equal("", vm.SyncStatusText);
        Assert.Equal("draft typed on B", vm.CommitMessage);
        Assert.Equal(2, await repoA.CommitCountAsync());
    }

    [Fact]
    public async Task Commit_WithoutSwitch_ClearsMessageAndReloadsCommits()
    {
        using var repo = await RepoWithStagedChangeAsync("plain", "work.txt", "work\n");

        var vm = NewVm();
        await vm.SetProjectAsync(ProjectFor(repo));
        vm.StagedFiles = [new WorkingFile { Path = "work.txt", IndexStatus = 'A' }];
        vm.CommitMessage = "second commit";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.IsBusy);
        Assert.Equal("Commit done.", vm.SyncStatusText);
        Assert.Equal("", vm.CommitMessage);
        Assert.Equal(2, vm.Commits.Count);
        Assert.Equal("second commit", await repo.HeadSubjectAsync());
    }
}
