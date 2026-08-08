using System.IO;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// Submodules of a superproject: listing with status, and the init/update/sync/deinit
/// operations. Every destructive path is gated by an explicit request flag, so no
/// argument combination discards a submodule checkout by default.
/// </summary>
public sealed class SubmoduleService
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    private readonly GitService _git;

    public SubmoduleService(GitService git) => _git = git;

    /// <summary>
    /// Every submodule the superproject knows about, in .gitmodules order followed by any
    /// gitlink recorded in the index without a declaration. Two fixed reads cover the
    /// superproject (.gitmodules and the index); each INITIALIZED submodule costs one
    /// further status read for its HEAD, branch, and dirty state. Nested submodules are
    /// reported, never entered.
    /// </summary>
    public async Task<List<SubmoduleEntry>> GetSubmodulesAsync(string repoPath, CancellationToken ct = default)
    {
        var declared = await ReadGitmodulesAsync(repoPath, ct);
        var recorded = await ReadGitlinksAsync(repoPath, ct);

        var order = new List<string>();
        foreach (var d in declared) order.Add(d.Path);
        foreach (var path in recorded.Keys)
            if (!order.Contains(path)) order.Add(path);

        var byPath = declared.ToDictionary(d => d.Path);
        var entries = new List<SubmoduleEntry>();
        foreach (var path in order)
        {
            byPath.TryGetValue(path, out var decl);
            recorded.TryGetValue(path, out var link);

            var full = ResolveInsideRepo(repoPath, path);
            var dotGit = full is null ? null : Path.Combine(full, ".git");
            var gitDir = dotGit is null ? SubmoduleGitDir.None
                : File.Exists(dotGit) ? SubmoduleGitDir.Linked
                : Directory.Exists(dotGit) ? SubmoduleGitDir.Embedded
                : SubmoduleGitDir.None;
            var treeExists = full is not null && Directory.Exists(full);
            var initialized = treeExists && gitDir != SubmoduleGitDir.None;

            var currentSha = "";
            string? branch = null;
            var detached = false;
            var modified = false;
            var untracked = false;

            if (initialized)
            {
                // Read the submodule as its own repository. Running git in the directory is
                // only safe once a .git entry is confirmed there: in an uninitialized (empty)
                // submodule directory git walks UP and answers for the SUPERPROJECT, which
                // would report the superproject's HEAD as the submodule's.
                var status = await _git.RunAsync(full!, ["status", "--porcelain=v2", "--branch"], ct, ReadTimeout);
                if (status.Success)
                {
                    var state = WorkingState.Parse(status.StdOut);
                    currentSha = state.NoCommitsYet ? "" : state.Oid;
                    detached = state.Detached;
                    branch = state.Detached || state.Branch.Length == 0 ? null : state.Branch;
                    modified = state.Files.Any(f => !f.IsUntracked);
                    untracked = state.Files.Any(f => f.IsUntracked);
                }
                else
                {
                    Log.Warn($"git status failed inside submodule {path} of {repoPath}: {status.FirstError}");
                }
            }

            entries.Add(new SubmoduleEntry
            {
                Name = decl?.Name ?? path,
                Path = path,
                Url = decl?.Url ?? "",
                TrackedBranch = decl?.Branch,
                DeclaredInGitmodules = decl is not null,
                RecordedInIndex = link is not null,
                RecordedSha = link?.Sha ?? "",
                IsConflicted = link?.Conflicted ?? false,
                CurrentSha = currentSha,
                CheckedOutBranch = branch,
                IsDetached = detached,
                WorkingTreeExists = treeExists,
                IsInitialized = initialized,
                GitDir = gitDir,
                HasModifiedContent = modified,
                HasUntrackedContent = untracked,
                HasNestedSubmodules = initialized && File.Exists(Path.Combine(full!, ".gitmodules"))
            });
        }
        return entries;
    }

    /// <summary>
    /// Commits the submodule checkout has beyond the recorded sha (Ahead) and commits the
    /// recorded sha has beyond the checkout (Behind). Null when the submodule is not
    /// initialized, either sha is unknown, or the recorded commit is absent from the
    /// submodule's object store — a shallow or never-fetched clone cannot answer.
    /// </summary>
    public async Task<SubmoduleDivergence?> GetDivergenceAsync(string repoPath, SubmoduleEntry entry,
        CancellationToken ct = default)
    {
        if (!entry.IsInitialized || !IsSha(entry.RecordedSha) || !IsSha(entry.CurrentSha)) return null;
        if (entry.RecordedSha == entry.CurrentSha) return new SubmoduleDivergence(0, 0);

        var full = ResolveInsideRepo(repoPath, entry.Path);
        if (full is null) return null;

        var result = await _git.RunAsync(full,
            ["rev-list", "--left-right", "--count", $"{entry.RecordedSha}...{entry.CurrentSha}", "--"], ct, ReadTimeout);
        if (!result.Success) return null;

        var fields = result.StdOut.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !int.TryParse(fields[0], out var behind) || !int.TryParse(fields[1], out var ahead))
            return null;
        return new SubmoduleDivergence(ahead, behind);
    }

    /// <summary>Registers submodules in .git/config (`git submodule init`) without cloning them.</summary>
    public Task<ProcessResult> InitAsync(string repoPath, string? path = null, CancellationToken ct = default)
    {
        if (RejectBlankPath(path) is { } refusal) return Task.FromResult(refusal);

        var args = new List<string> { "submodule", "init" };
        if (path is not null) { args.Add("--"); args.Add(path); }
        return _git.RunAsync(repoPath, args, ct, WriteTimeout);
    }

    /// <summary>
    /// `git submodule update`. Clones and checks out the recorded commit; --force is
    /// refused unless the request also confirms the discard it performs.
    /// </summary>
    public Task<ProcessResult> UpdateAsync(string repoPath, SubmoduleUpdateRequest request,
        CancellationToken ct = default)
    {
        if (RejectBlankPath(request.Path) is { } refusal) return Task.FromResult(refusal);
        if (request.Force && !request.ConfirmDiscard)
            return Task.FromResult(Refuse(
                "submodule update --force resets the submodule checkout and discards local commits there; " +
                "set ConfirmDiscard to allow it"));
        if (request.Depth is { } depth && depth < 1)
            return Task.FromResult(Refuse($"submodule update --depth must be at least 1, got {depth}"));

        var args = new List<string> { "submodule", "update" };
        if (request.Init) args.Add("--init");
        if (request.Recursive) args.Add("--recursive");
        if (request.Depth is { } d) { args.Add("--depth"); args.Add(d.ToString()); }
        if (request.Force) args.Add("--force");
        if (request.Path is not null) { args.Add("--"); args.Add(request.Path); }
        return _git.RunAsync(repoPath, args, ct, NetworkTimeout);
    }

    /// <summary>Rewrites each submodule's configured URL from .gitmodules (`git submodule sync`).</summary>
    public Task<ProcessResult> SyncAsync(string repoPath, string? path = null, bool recursive = false,
        CancellationToken ct = default)
    {
        if (RejectBlankPath(path) is { } refusal) return Task.FromResult(refusal);

        var args = new List<string> { "submodule", "sync" };
        if (recursive) args.Add("--recursive");
        if (path is not null) { args.Add("--"); args.Add(path); }
        return _git.RunAsync(repoPath, args, ct, WriteTimeout);
    }

    /// <summary>
    /// `git submodule deinit`. Empties the submodule working tree and unregisters it, so
    /// the call is refused — before any process starts — unless the request confirms the
    /// discard. A null path means --all; a blank one is refused rather than widened to it.
    /// </summary>
    public Task<ProcessResult> DeinitAsync(string repoPath, SubmoduleDeinitRequest request,
        CancellationToken ct = default)
    {
        if (RejectBlankPath(request.Path) is { } refusal) return Task.FromResult(refusal);
        if (!request.ConfirmDiscard)
            return Task.FromResult(Refuse(
                "submodule deinit empties the submodule working tree; set ConfirmDiscard to allow it"));

        var args = new List<string> { "submodule", "deinit" };
        if (request.Force) args.Add("--force");
        if (request.Path is null) args.Add("--all");
        else { args.Add("--"); args.Add(request.Path); }
        return _git.RunAsync(repoPath, args, ct, WriteTimeout);
    }

    /// <summary>
    /// A whitespace-only path is a caller bug, and the null-means-every-submodule
    /// convention would silently turn it into a repo-wide operation.
    /// </summary>
    private static ProcessResult? RejectBlankPath(string? path) =>
        path is not null && path.Trim().Length == 0
            ? Refuse("a submodule path must be a real path; pass null to target every submodule")
            : null;

    private static ProcessResult Refuse(string message) => new(-1, "", message, TimedOut: false);

    /// <summary>
    /// Absolute path of a submodule's working tree, or null when the declared path does not
    /// land strictly inside the superproject. .gitmodules is repository content and can be
    /// hand-written: an absolute or "../"-escaping path would otherwise make the listing
    /// stat, and run git in, a directory outside the repository being inspected.
    /// </summary>
    internal static string? ResolveInsideRepo(string repoPath, string relativePath)
    {
        if (relativePath.Length == 0) return null;
        try
        {
            var root = Path.GetFullPath(repoPath);
            // A drive root ("C:\") is its own terminator: appending a separator to it
            // would build "C:\\" and no descendant would match the prefix.
            var prefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            var full = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
            return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSha(string value)
    {
        if (value.Length is < 4 or > 64) return false;
        foreach (var c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    internal sealed record Declaration(string Name, string Path, string Url, string? Branch);

    private async Task<List<Declaration>> ReadGitmodulesAsync(string repoPath, CancellationToken ct)
    {
        // -z, not the line format: a submodule path or URL may contain spaces or a
        // trailing CR, and NUL-separated records keep every value byte-exact.
        var result = await _git.RunAsync(repoPath, ["config", "-f", ".gitmodules", "-z", "--list"], ct, ReadTimeout);
        // A superproject with no .gitmodules exits non-zero; that is "no submodules declared".
        return result.Success ? ParseGitmodulesConfig(result.StdOut) : [];
    }

    /// <summary>
    /// Parses `git config -z --list`: NUL-separated records, each "key\nvalue" (a valueless
    /// key has no newline). Only submodule.&lt;name&gt;.{path,url,branch} is kept; the name is
    /// the text between the first and last dot, so a name containing dots or slashes
    /// survives. Declaration order is preserved because .gitmodules order is the order the
    /// UI lists submodules in.
    /// </summary>
    internal static List<Declaration> ParseGitmodulesConfig(string configZ)
    {
        var paths = new List<string>();
        var byName = new Dictionary<string, (string Path, string Url, string? Branch)>();
        var order = new List<string>();

        foreach (var record in configZ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = record.IndexOf('\n');
            if (nl < 0) continue;
            var key = record[..nl];
            var value = record[(nl + 1)..];

            if (!key.StartsWith("submodule.", StringComparison.Ordinal)) continue;
            var lastDot = key.LastIndexOf('.');
            if (lastDot < "submodule.".Length) continue;
            var name = key["submodule.".Length..lastDot];
            if (name.Length == 0) continue;

            if (!byName.TryGetValue(name, out var cur)) { cur = ("", "", null); order.Add(name); }
            byName[name] = key[(lastDot + 1)..] switch
            {
                "path" => (value, cur.Url, cur.Branch),
                "url" => (cur.Path, value, cur.Branch),
                "branch" => (cur.Path, cur.Url, value),
                _ => cur
            };
        }

        var declarations = new List<Declaration>();
        foreach (var name in order)
        {
            var (path, url, branch) = byName[name];
            path = NormalizeDeclaredPath(path);
            // A section with no path declares nothing checkoutable; two sections claiming
            // one path would collapse the listing, so the first declaration wins.
            if (path.Length == 0 || paths.Contains(path)) continue;
            paths.Add(path);
            declarations.Add(new Declaration(name, path, url, branch));
        }
        return declarations;
    }

    /// <summary>
    /// The form git records a gitlink under: forward slashes, no "." components, no empty
    /// components. .gitmodules is hand-writable, so "./lib", "lib/", "lib\", ".//lib", and
    /// "a/./b" must collapse onto the paths the index calls "lib" and "a/b"; without a
    /// common form the declaration/index union lists one submodule several times or invents
    /// an uninitialized row for a path the index already holds.
    /// </summary>
    internal static string NormalizeDeclaredPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts.Where(p => p != "."));
    }

    /// <summary>One gitlink as the superproject index holds it.</summary>
    /// <param name="Sha">Stage 0, or stage 2 while the gitlink is unmerged; empty when neither exists.</param>
    /// <param name="Conflicted">The index holds unmerged stages for this path.</param>
    internal sealed record Gitlink(string Sha, bool Conflicted);

    private async Task<Dictionary<string, Gitlink>> ReadGitlinksAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, ["ls-files", "-z", "--stage"], ct, ReadTimeout);
        if (!result.Success)
        {
            Log.Warn($"git ls-files --stage failed for {repoPath}: {result.FirstError}");
            return [];
        }
        return ParseGitlinks(result.StdOut);
    }

    /// <summary>
    /// Picks the gitlinks out of `git ls-files -z --stage`: NUL-separated
    /// "&lt;mode&gt; &lt;sha&gt; &lt;stage&gt;\t&lt;path&gt;" records, gitlinks being mode 160000. NUL
    /// termination is what makes a path containing a space or a tab unambiguous.
    /// <para>
    /// A path is emitted once per index stage. A merged path has stage 0 alone; an
    /// unmerged one has no stage 0 and instead stages 1 (base), 2 (ours), and 3 (theirs)
    /// — so taking the last record would record THEIR commit as the superproject's, and
    /// a checkout sitting on ours would read as diverged. Stage 0 wins, stage 2 is the
    /// fallback, and any stage above 0 marks the path conflicted.
    /// </para>
    /// </summary>
    internal static Dictionary<string, Gitlink> ParseGitlinks(string lsFilesZ)
    {
        var links = new Dictionary<string, Gitlink>();
        foreach (var record in lsFilesZ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 3 || meta[0] != "160000") continue;

            var path = record[(tab + 1)..];
            links.TryGetValue(path, out var current);
            var sha = meta[2] switch
            {
                "0" => meta[1],
                "2" when current is null || current.Sha.Length == 0 => meta[1],
                _ => current?.Sha ?? ""
            };
            links[path] = new Gitlink(sha, (current?.Conflicted ?? false) || meta[2] != "0");
        }
        return links;
    }
}
