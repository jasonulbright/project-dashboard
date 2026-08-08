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
