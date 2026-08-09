using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// A disposable git repository under %TEMP%\pd-fixtures\rails-&lt;random&gt; for the
/// safety-rail tests. Uses the same isolated git config the rest of the suite pins
/// via TestEnv (global config, English messages, file protocol). Dispose deletes the
/// tree, clearing the read-only bit git sets on object files.
/// </summary>
internal sealed class RailsRepo : IDisposable
{
    public string Path { get; }

    private RailsRepo(string path) => Path = path;

    public static async Task<RailsRepo> CreateAsync(string prefix = "rails")
    {
        var dir = System.IO.Path.Combine(TestEnv.Root, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var repo = new RailsRepo(dir);
        await repo.GitAsync("init", "-b", "main");
        repo.Write("file.txt", "one\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "initial");
        return repo;
    }

    /// <summary>
    /// A clone of a bare origin, with refs/remotes/origin/HEAD set the way `git clone` sets it.
    /// That symref and its target are what a ref transaction naming both is rejected over, so a
    /// rail exercised only against <see cref="CreateAsync"/> — which configures no remote — never
    /// meets the layout almost every real repository has.
    /// </summary>
    public static async Task<RailsRepo> CreateClonedAsync(string prefix = "rails-clone")
    {
        var stem = System.IO.Path.Combine(TestEnv.Root, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        var origin = stem + "-origin.git";
        var seed = stem + "-seed";
        Directory.CreateDirectory(origin);
        Directory.CreateDirectory(seed);
        await Git.RunAsync(origin, "init", "--bare", "-b", "main");

        var seedRepo = new RailsRepo(seed);
        await seedRepo.GitAsync("init", "-b", "main");
        seedRepo.Write("file.txt", "one\n");
        await seedRepo.GitAsync("add", "-A");
        await seedRepo.GitAsync("commit", "-m", "initial");
        await seedRepo.GitAsync("push", origin.Replace('\\', '/'), "main");
        TestEnv.TryDeleteTree(seed);

        var clone = stem;
        await Git.RunAsync(TestEnv.Root, "clone", origin.Replace('\\', '/'), clone);
        var repo = new RailsRepo(clone) { _origin = origin };
        // The rail this fixture exists for only exists when the symref does.
        var refs = await repo.GitAsync("for-each-ref", "--format=%(refname) %(symref)");
        if (!refs.Contains("refs/remotes/origin/HEAD refs/remotes/origin/main", StringComparison.Ordinal))
            throw new InvalidOperationException($"clone fixture has no origin/HEAD symref:\n{refs}");
        return repo;
    }

    private string? _origin;

    public void Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public async Task CommitAllAsync(string subject)
    {
        await GitAsync("add", "-A");
        await GitAsync("commit", "-m", subject);
    }

    public Task<string> GitAsync(params string[] args) => Git.RunAsync(Path, args);

    /// <summary>for-each-ref layout plus HEAD — the canonical signal for "the refs match exactly".</summary>
    public async Task<string> RefStateAsync()
    {
        var refs = (await GitAsync("for-each-ref", "--format=%(objectname) %(refname)")).Trim();
        string head;
        try { head = "ref " + (await GitAsync("symbolic-ref", "HEAD")).Trim(); }
        catch (InvalidOperationException) { head = "detached " + (await GitAsync("rev-parse", "HEAD")).Trim(); }
        return head + "\n" + refs;
    }

    public void Dispose()
    {
        TestEnv.TryDeleteTree(Path);
        if (_origin is not null) TestEnv.TryDeleteTree(_origin);
    }
}
