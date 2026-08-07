using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// A disposable git repository under the per-run fixture root. Dispose deletes the
/// tree (tolerating locked files); nothing is ever created outside %TEMP%\pd-fixtures.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    public string Path { get; }

    private TempRepo(string path) => Path = path;

    /// <summary>file:// form of the repo path, for exercising the URL-based code paths.</summary>
    public string FileUrl => new Uri(Path).AbsoluteUri;

    public static TempRepo CreateEmptyDir(string prefix = "repo") => new(TestEnv.NewDir(prefix));

    /// <summary>Repo on branch main with one committed file.txt.</summary>
    public static async Task<TempRepo> CreateWithCommitAsync(string prefix = "repo")
    {
        var repo = CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("file.txt", "line one\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "initial commit");
        return repo;
    }

    /// <summary>Bare clone of an existing repo, usable as a file:// origin.</summary>
    public static async Task<TempRepo> CreateBareFromAsync(TempRepo source, string prefix = "origin")
    {
        var bare = new TempRepo(System.IO.Path.Combine(TestEnv.NewDir(prefix), "remote.git"));
        await Git.RunAsync(TestEnv.Root, "clone", "--bare", source.Path, bare.Path);
        return bare;
    }

    /// <summary>Working clone of a bare origin via its file:// URL (upstream tracking set by clone).</summary>
    public static async Task<TempRepo> CloneFromAsync(TempRepo bare, string prefix = "clone")
    {
        var parent = TestEnv.NewDir(prefix);
        await Git.RunAsync(parent, "clone", bare.FileUrl, "work");
        return new TempRepo(System.IO.Path.Combine(parent, "work"));
    }

    public void WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string ReadFile(string relativePath) =>
        File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    public bool FileExists(string relativePath) =>
        File.Exists(System.IO.Path.Combine(Path, relativePath));

    /// <summary>Runs git in this repo, throwing on failure (fixture setup must not fail silently).</summary>
    public Task<string> GitAsync(params string[] args) => Git.RunAsync(Path, args);

    /// <summary>Commits all pending changes with the given subject.</summary>
    public async Task CommitAllAsync(string subject)
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-m", subject);
    }

    public async Task<int> CommitCountAsync() =>
        int.Parse((await GitAsync("rev-list", "--count", "HEAD")).Trim());

    public async Task<string> HeadShaAsync() => (await GitAsync("rev-parse", "HEAD")).Trim();

    public async Task<string> HeadSubjectAsync() => (await GitAsync("log", "-1", "--format=%s")).Trim();

    public void Dispose() => TestEnv.TryDeleteTree(Path);
}

/// <summary>Direct git runner for fixture setup, independent of the service under test.</summary>
internal static class Git
{
    public static async Task<string> RunAsync(string workDir, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workDir, TimeSpan.FromSeconds(60));
        if (!result.Success)
            throw new InvalidOperationException(
                $"fixture git {string.Join(' ', args)} failed in {workDir}: {result.FirstError}");
        return result.StdOut;
    }

    /// <summary>Runs git expecting failure to be possible (e.g. a conflicting merge); returns the result.</summary>
    public static Task<ProcessResult> TryRunAsync(string workDir, params string[] args) =>
        ProcessRunner.RunAsync("git", args, workDir, TimeSpan.FromSeconds(60));
}
