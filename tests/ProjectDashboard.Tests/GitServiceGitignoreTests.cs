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

        Assert.True(await _git.CheckIgnoreAsync(repo.Path, "debug.log"));
        Assert.False(await _git.CheckIgnoreAsync(repo.Path, "notes.txt"));
    }
}
