namespace ProjectDashboard.Tests;

/// <summary>
/// A disposable git repository under %TEMP%\pd-fixtures\surgery-&lt;random&gt; for the history
/// editing and commit surgery tests. Inherits the suite's isolated git config from TestEnv,
/// so machine settings (default branch, signing, autocrlf) cannot change outcomes.
/// </summary>
internal sealed class SurgeryRepo : IDisposable
{
    public string Path { get; }

    private SurgeryRepo(string path) => Path = path;

    /// <summary>Repo on main whose commits are named by <paramref name="subjects"/>; each adds its own file.</summary>
    public static async Task<SurgeryRepo> CreateAsync(params string[] subjects)
    {
        var dir = System.IO.Path.Combine(TestEnv.Root, "surgery-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var repo = new SurgeryRepo(dir);
        await repo.GitAsync("init", "-q", "-b", "main");
        foreach (var subject in subjects)
        {
            repo.Write(subject + ".txt", subject + " content\n");
            await repo.CommitAllAsync(subject);
        }
        return repo;
    }

    public void Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    public bool Exists(string relativePath) => File.Exists(System.IO.Path.Combine(Path, relativePath));

    public Task<string> GitAsync(params string[] args) => Git.RunAsync(Path, args);

    public async Task CommitAllAsync(string subject)
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", subject);
    }

    /// <summary>Commit subjects newest first.</summary>
    public async Task<List<string>> SubjectsAsync() =>
        Lines(await GitAsync("log", "--format=%s"));

    /// <summary>Commit ids newest first.</summary>
    public async Task<List<string>> ShasAsync() =>
        Lines(await GitAsync("log", "--format=%H"));

    /// <summary>The last <paramref name="depth"/> commit ids, oldest first — the order a rebase todo uses.</summary>
    public async Task<List<string>> RangeShasAsync(int depth) =>
        Lines(await GitAsync("log", "--reverse", "-n", depth.ToString(), "--format=%H"));

    public async Task<string> HeadAsync() => (await GitAsync("rev-parse", "HEAD")).Trim();

    public async Task<string> TreeAsync(string rev = "HEAD") => (await GitAsync("rev-parse", rev + "^{tree}")).Trim();

    public async Task<string> ShowAsync(string rev, string path) => await GitAsync("show", rev + ":" + path);

    public async Task<string> MessageAsync(string rev) => (await GitAsync("log", "-1", "--format=%B", rev)).Trim();

    public async Task<string> StatusAsync() => (await GitAsync("status", "--porcelain")).Trim();

    /// <summary>for-each-ref layout plus HEAD — the canonical "the refs match exactly" signal.</summary>
    public async Task<string> RefStateAsync()
    {
        var refs = (await GitAsync("for-each-ref", "--format=%(objectname) %(refname)")).Trim();
        string head;
        try { head = "ref " + (await GitAsync("symbolic-ref", "HEAD")).Trim(); }
        catch (InvalidOperationException) { head = "detached " + (await GitAsync("rev-parse", "HEAD")).Trim(); }
        return head + "\n" + refs;
    }

    /// <summary>True while a rebase state directory survives — the signal that an abort did not clean up.</summary>
    public bool RebaseInProgress =>
        Directory.Exists(System.IO.Path.Combine(Path, ".git", "rebase-merge")) ||
        Directory.Exists(System.IO.Path.Combine(Path, ".git", "rebase-apply"));

    /// <summary>Refs, HEAD, working-tree status, and the rebase state dir in one comparable snapshot.</summary>
    public async Task<string> FullStateAsync() =>
        await RefStateAsync() + "\nstatus:\n" + await StatusAsync() + "\nrebasing:" + RebaseInProgress;

    private static List<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public void Dispose() => TestEnv.TryDeleteTree(Path);
}
