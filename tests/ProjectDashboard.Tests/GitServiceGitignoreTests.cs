using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>.gitignore read/save/append and check-ignore (L-10).</summary>
public class GitServiceGitignoreTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task GetGitignore_Absent_IsNull()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-absent");
        Assert.Null(await _git.GetGitignoreAsync(repo.Path));
    }

    [Fact]
    public async Task SaveThenGet_RoundTrips()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-save");
        await _git.SaveGitignoreAsync(repo.Path, "bin/\nobj/\n");
        Assert.Equal("bin/\nobj/\n", await _git.GetGitignoreAsync(repo.Path));
    }

    [Fact]
    public async Task AppendIgnoreEntry_AddsOnceAndIsIdempotent()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-append");
        await _git.SaveGitignoreAsync(repo.Path, "bin/\n");

        await _git.AppendIgnoreEntryAsync(repo.Path, "*.log");
        await _git.AppendIgnoreEntryAsync(repo.Path, "*.log");   // already present — no-op

        var content = await _git.GetGitignoreAsync(repo.Path);
        var occurrences = content!.Split('\n').Count(l => l.Trim() == "*.log");
        Assert.Equal(1, occurrences);
        Assert.StartsWith("bin/\n", content);
    }

    [Fact]
    public async Task AppendIgnoreEntry_CreatesFileWhenAbsent()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-create");
        await _git.AppendIgnoreEntryAsync(repo.Path, "node_modules/");
        Assert.Equal("node_modules/\n", await _git.GetGitignoreAsync(repo.Path));
    }

    [Fact]
    public async Task CheckIgnore_ReflectsPatterns()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-check");
        await _git.SaveGitignoreAsync(repo.Path, "*.log\n");

        Assert.Equal(new IgnoreAnswer(IgnoreState.Ignored, false, ""),
            await _git.CheckIgnoreAsync(repo.Path, "debug.log"));
        Assert.Equal(new IgnoreAnswer(IgnoreState.NotIgnored, false, ""),
            await _git.CheckIgnoreAsync(repo.Path, "notes.txt"));
    }

    /// <summary>
    /// check-ignore consults the index, so a tracked path exits 1 — "not ignored" — even while a
    /// rule matches it. Trackedness is what separates that from a path no rule matches.
    /// </summary>
    [Fact]
    public async Task CheckIgnore_ATrackedPathIsReportedNotIgnoredAndTracked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-check-tracked");
        repo.WriteFile("kept.log", "x\n");
        await repo.GitAsync("add", "--force", "--", "kept.log");
        await repo.CommitAllAsync("track a log");
        await _git.SaveGitignoreAsync(repo.Path, "*.log\n");

        var answer = await _git.CheckIgnoreAsync(repo.Path, "kept.log");

        Assert.Equal(IgnoreState.NotIgnored, answer.State);
        Assert.True(answer.Tracked);
        Assert.True(await _git.IsTrackedAsync(repo.Path, "kept.log"));
        Assert.False(await _git.IsTrackedAsync(repo.Path, "never-added.log"));
    }

    /// <summary>
    /// `ls-files` prints every index entry a pathspec covers, so a directory pathspec prints the
    /// files UNDER it. Treating any output as a hit made a directory holding tracked files read
    /// as tracked itself, and the probe then told the reader the index outranks the ignore rules
    /// for a path the index does not hold.
    /// </summary>
    [Fact]
    public async Task IsTracked_ADirectoryHoldingTrackedFiles_IsNotItselfTracked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-tracked-dir");
        repo.WriteFile("lib/inner.txt", "x\n");
        await repo.GitAsync("add", "--", "lib/inner.txt");
        await repo.CommitAllAsync("track a file under a directory");

        Assert.True(await _git.IsTrackedAsync(repo.Path, "lib/inner.txt"));
        Assert.False(await _git.IsTrackedAsync(repo.Path, "lib"));
        Assert.False(await _git.IsTrackedAsync(repo.Path, "lib/"));

        // The probe reports what the index holds, so the directory gets the plain answer.
        var answer = await _git.CheckIgnoreAsync(repo.Path, "lib");
        Assert.Equal(IgnoreState.NotIgnored, answer.State);
        Assert.False(answer.Tracked);
    }

    /// <summary>A path typed with the platform separator asks about the entry git records.</summary>
    [Fact]
    public async Task IsTracked_AcceptsTheWindowsSeparatorForAPathTheIndexHolds()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-tracked-sep");
        repo.WriteFile("lib/inner.txt", "x\n");
        await repo.GitAsync("add", "--", "lib/inner.txt");
        await repo.CommitAllAsync("track a file under a directory");

        Assert.True(await _git.IsTrackedAsync(repo.Path, @"lib\inner.txt"));
    }

    /// <summary>Exit 128 is git refusing the question; answering "not ignored" would invent an answer.</summary>
    [Fact]
    public async Task CheckIgnore_APathGitRefuses_IsUnknownRatherThanNotIgnored()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("ignore-check-outside");
        await _git.SaveGitignoreAsync(repo.Path, "*.log\n");

        var answer = await _git.CheckIgnoreAsync(repo.Path, "../outside.log");

        Assert.Equal(IgnoreState.Unknown, answer.State);
        Assert.NotEqual("", answer.Error);
    }
}
