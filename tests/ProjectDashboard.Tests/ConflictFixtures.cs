namespace ProjectDashboard.Tests;

/// <summary>
/// Repositories left in a real conflicted state by git itself, one builder per sequencer. Nothing
/// here writes an index by hand: what the panel reads has to be what git records, or a refusal
/// proven against a hand-built shape proves nothing about the shape git produces.
/// </summary>
internal static class ConflictFixtures
{
    /// <summary>
    /// A stopped merge whose only conflict is one both-modified text file. `main` holds "ours",
    /// the merged branch holds "theirs", and the ancestor holds "base".
    /// </summary>
    public static async Task<TempRepo> MergeAsync(string prefix = "conflict-merge")
    {
        var repo = await SeedAsync(prefix);
        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("file.txt", "theirs\n");
        await repo.CommitAllAsync("side change");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "ours\n");
        await repo.CommitAllAsync("main change");
        await TryGitAsync(repo, "merge", "side");
        return repo;
    }

    /// <summary>
    /// A stopped merge carrying one shape of each kind the panel has to tell apart: both-modified
    /// text, both-added text, a both-modified binary, and a file they deleted and we modified.
    /// </summary>
    public static async Task<TempRepo> RichMergeAsync(string prefix = "conflict-rich")
    {
        var repo = await SeedAsync(prefix);
        repo.WriteFile("doomed.txt", "base\n");
        WriteBytes(repo, "pic.bin", [0, 1, 2, 0, 3]);
        await repo.CommitAllAsync("second base");

        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("file.txt", "theirs\n");
        repo.WriteFile("added.txt", "theirs added\n");
        WriteBytes(repo, "pic.bin", [9, 9, 0, 9]);
        File.Delete(Path.Combine(repo.Path, "doomed.txt"));
        await repo.CommitAllAsync("side change");

        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "ours\n");
        repo.WriteFile("added.txt", "ours added\n");
        repo.WriteFile("doomed.txt", "ours kept it\n");
        WriteBytes(repo, "pic.bin", [7, 7, 0, 7]);
        await repo.CommitAllAsync("main change");

        await TryGitAsync(repo, "merge", "side");
        return repo;
    }

    /// <summary>A rebase stopped on a conflicting replay, with `topic` checked out.</summary>
    public static async Task<TempRepo> RebaseStopAsync(string prefix = "conflict-rebase")
    {
        var repo = await SeedAsync(prefix);
        await repo.GitAsync("switch", "-c", "topic");
        repo.WriteFile("file.txt", "topic\n");
        await repo.CommitAllAsync("topic change");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "main\n");
        await repo.CommitAllAsync("main change");
        await repo.GitAsync("switch", "topic");
        await TryGitAsync(repo, "rebase", "main");
        return repo;
    }

    /// <summary>A cherry-pick stopped on a conflicting commit.</summary>
    public static async Task<TempRepo> CherryPickStopAsync(string prefix = "conflict-pick")
    {
        var repo = await SeedAsync(prefix);
        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("file.txt", "theirs\n");
        await repo.CommitAllAsync("side change");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "ours\n");
        await repo.CommitAllAsync("main change");
        await TryGitAsync(repo, "cherry-pick", "side");
        return repo;
    }

    /// <summary>A revert stopped on a conflict with a later commit.</summary>
    public static async Task<TempRepo> RevertStopAsync(string prefix = "conflict-revert")
    {
        var repo = await SeedAsync(prefix);
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("second");
        var target = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        repo.WriteFile("file.txt", "third\n");
        await repo.CommitAllAsync("third");
        await TryGitAsync(repo, "revert", "--no-edit", target);
        return repo;
    }

    /// <summary>
    /// A stopped merge in a repository that raised `conflict-marker-size`, so the markers git
    /// writes are longer than its own default.
    /// </summary>
    public static async Task<TempRepo> WideMarkerMergeAsync(int size = 32, string prefix = "conflict-wide")
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile(".gitattributes", $"*.txt conflict-marker-size={size}\n");
        repo.WriteFile("file.txt", "base\n");
        await repo.CommitAllAsync("base");
        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("file.txt", "theirs\n");
        await repo.CommitAllAsync("side change");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "ours\n");
        await repo.CommitAllAsync("main change");
        await TryGitAsync(repo, "merge", "side");
        return repo;
    }

    /// <summary>
    /// A cherry-pick of TWO commits stopped on the first one, so a continue has a queued pick
    /// behind it — the state a hand-made commit strands.
    /// </summary>
    public static async Task<TempRepo> MultiPickStopAsync(string prefix = "conflict-multipick")
    {
        var repo = await SeedAsync(prefix);
        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("file.txt", "theirs\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("-c", "user.name=Original Author", "-c", "user.email=original@elsewhere.invalid",
            "commit", "-m", "first pick");
        repo.WriteFile("second.txt", "second pick\n");
        await repo.CommitAllAsync("second pick");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "ours\n");
        await repo.CommitAllAsync("main change");
        await TryGitAsync(repo, "cherry-pick", "side~1", "side");
        return repo;
    }

    /// <summary>
    /// A cherry-pick of two commits that conflicts on BOTH of them, so continuing past the first
    /// conflict stops the sequence again on the second.
    /// </summary>
    public static async Task<TempRepo> TwoStopPickAsync(string prefix = "conflict-twostop")
    {
        var repo = await SeedAsync(prefix);
        repo.WriteFile("second.txt", "base\n");
        await repo.CommitAllAsync("second base");

        await repo.GitAsync("switch", "-c", "side");
        repo.WriteFile("file.txt", "theirs\n");
        await repo.CommitAllAsync("first pick");
        repo.WriteFile("second.txt", "theirs\n");
        await repo.CommitAllAsync("second pick");

        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "ours\n");
        repo.WriteFile("second.txt", "ours\n");
        await repo.CommitAllAsync("main change");

        await TryGitAsync(repo, "cherry-pick", "side~1", "side");
        return repo;
    }

    /// <summary>A rebase stopped on a commit written by somebody other than the committer.</summary>
    public static async Task<TempRepo> RebaseStopWithAuthorAsync(string prefix = "conflict-author")
    {
        var repo = await SeedAsync(prefix);
        await repo.GitAsync("switch", "-c", "topic");
        repo.WriteFile("file.txt", "topic\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("-c", "user.name=Original Author", "-c", "user.email=original@elsewhere.invalid",
            "commit", "-m", "topic change");
        await repo.GitAsync("switch", "main");
        repo.WriteFile("file.txt", "main\n");
        await repo.CommitAllAsync("main change");
        await repo.GitAsync("switch", "topic");
        await TryGitAsync(repo, "rebase", "main");
        return repo;
    }

    /// <summary>A repository in the middle of a bisect, which this surface never drives.</summary>
    public static async Task<TempRepo> BisectAsync(string prefix = "conflict-bisect")
    {
        var repo = await SeedAsync(prefix);
        repo.WriteFile("file.txt", "second\n");
        await repo.CommitAllAsync("second");
        repo.WriteFile("file.txt", "third\n");
        await repo.CommitAllAsync("third");
        await repo.GitAsync("bisect", "start");
        await repo.GitAsync("bisect", "bad");
        return repo;
    }

    private static async Task<TempRepo> SeedAsync(string prefix)
    {
        var repo = TempRepo.CreateEmptyDir(prefix);
        await repo.GitAsync("init", "-b", "main");
        repo.WriteFile("file.txt", "base\n");
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-m", "base");
        return repo;
    }

    private static void WriteBytes(TempRepo repo, string relativePath, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(repo.Path, relativePath), bytes);

    /// <summary>Runs a git command whose non-zero exit is the conflict this fixture exists to create.</summary>
    private static Task TryGitAsync(TempRepo repo, params string[] args) =>
        Git.TryRunAsync(repo.Path, args);
}
