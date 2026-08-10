using System.Diagnostics;
using System.IO;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services;

/// <summary>A repository the fan-out may search: display name plus its working-tree path.</summary>
public sealed record RepoSearchTarget(string Name, string Path);

/// <summary>
/// One result row. <see cref="Line"/> is 0 for a filename match, where
/// <see cref="Text"/> repeats the path rather than a line of content.
/// <see cref="FileScope"/> is what git says the file is, so a row from build output says so.
/// </summary>
public sealed record RepoSearchHit(
    string RepoName,
    string RepoPath,
    string FilePath,
    int Line,
    string Text,
    SearchFileScope FileScope = SearchFileScope.Tracked)
{
    public bool IsFileNameMatch => Line == 0;

    /// <summary>"repo · path:line" — the row's provenance in one line.</summary>
    public string Location => IsFileNameMatch ? $"{RepoName} · {FilePath}" : $"{RepoName} · {FilePath}:{Line}";

    /// <summary>"untracked" / "ignored" for a row whose file is neither in the index; empty for one that is.</summary>
    public string ScopeLabel => SearchScopeCopy.RowLabel(FileScope);

    /// <summary>The row's provenance with what its file is to git, for the surfaces that name both in one string.</summary>
    public string LocationWithScope =>
        ScopeLabel.Length == 0 ? Location : $"{Location} · {ScopeLabel}";
}

/// <summary>
/// Outcome of one fan-out. <see cref="More"/> counts matches found but not returned
/// (either cap), so the caller can say how much it is hiding instead of implying
/// the returned rows are everything.
///
/// The four repository counts are exclusive and together account for every target: a repository
/// was searched whole, was cut short by its budget, reported an error, or was never readable.
/// Folding them into one number would let a partial answer read as a complete one.
/// </summary>
public sealed record RepoSearchResult(
    IReadOnlyList<RepoSearchHit> Hits,
    int More,
    int ReposSearched,
    int ReposSkipped,
    int ReposTruncated = 0,
    int ReposFailed = 0)
{
    public static readonly RepoSearchResult Empty = new([], 0, 0, 0);

    /// <summary>True when something the fan-out reached did not answer in full.</summary>
    public bool IsPartial => More > 0 || ReposTruncated > 0 || ReposFailed > 0 || ReposSkipped > 0;
}

/// <summary>
/// Content and filename search across a set of repositories, driven by git and nothing else:
/// `git ls-files` for the filename half, `git grep` for the content half, and the scope switches
/// are git's own flags. Bounded on every axis a repo set can grow in: concurrency, per-repo wall
/// clock, hits per repo, and hits overall. Every call is cancellable — the palette cancels the
/// previous fan-out on each keystroke, and an abandoned search must stop spawning git rather than
/// run to completion unseen.
///
/// Scope is a parameter, never a remembered setting. The widest scope reads ignored files, which
/// on a built repository is mostly compiler output, so it takes a shorter budget and a lower hit
/// cap than the tracked default and every row it returns is labelled with what its file is.
///
/// git does not descend into a directory that holds its own repository, at any scope: a nested
/// repository's matches come from its own invocation as its own target, never through its parent.
/// </summary>
/// <param name="perRepoTimeout">
/// Overrides <see cref="PerRepoTimeout"/>. The shipped value stands; this exists so a test that
/// asserts what a search FOUND is not racing it, and so a test that asserts what the budget DOES
/// can spend one small enough to be certain rather than one it waits out.
/// </param>
/// <param name="widePerRepoTimeout">Overrides <see cref="WidePerRepoTimeout"/>, on the same terms.</param>
public sealed class RepoSearchService(
    GitService gitService,
    RepoBusyRegistry busyRegistry,
    TimeSpan? perRepoTimeout = null,
    TimeSpan? widePerRepoTimeout = null)
{
    public const int MaxConcurrency = 6;
    public const int MaxHitsPerRepo = 5;

    /// <summary>The per-repo cap under <see cref="SearchContentScope.Everything"/>, where most files are build output.</summary>
    public const int WideMaxHitsPerRepo = 3;

    public const int MaxHitsTotal = 60;
    public const int MinTermLength = 2;
    public static readonly TimeSpan PerRepoTimeout = TimeSpan.FromSeconds(4);

    /// <summary>The per-repo budget under <see cref="SearchContentScope.Everything"/>, which walks ignored trees.</summary>
    public static readonly TimeSpan WidePerRepoTimeout = TimeSpan.FromSeconds(2);

    public static int HitsPerRepoFor(SearchContentScope scope) =>
        scope == SearchContentScope.Everything ? WideMaxHitsPerRepo : MaxHitsPerRepo;

    /// <summary>The shipped budget for a scope, whatever this instance was built with.</summary>
    public static TimeSpan TimeoutFor(SearchContentScope scope) =>
        scope == SearchContentScope.Everything ? WidePerRepoTimeout : PerRepoTimeout;

    /// <summary>The budget this instance spends on one repository — the shipped one unless overridden.</summary>
    private TimeSpan BudgetFor(SearchContentScope scope) =>
        scope == SearchContentScope.Everything
            ? widePerRepoTimeout ?? WidePerRepoTimeout
            : perRepoTimeout ?? PerRepoTimeout;

    /// <summary>The index, which is every scope's tracked half.</summary>
    internal static readonly string[] TrackedListArgs = ["ls-files", "-z"];

    /// <summary>Working-tree files git neither tracks nor ignores.</summary>
    internal static readonly string[] UntrackedListArgs = ["ls-files", "-z", "--others", "--exclude-standard"];

    /// <summary>Every untracked file, the ignored ones included; ignored is this set less the one above.</summary>
    internal static readonly string[] IgnoredListArgs = ["ls-files", "-z", "--others"];

    /// <summary>
    /// -I skips binaries and -m 1 caps git's own work at one line per file; both matter more, not
    /// less, as the scope widens. The scope switches are the two leading flags and nothing else.
    /// </summary>
    internal static string[] GrepArgs(SearchContentScope scope, string term) => scope switch
    {
        SearchContentScope.WithUntracked =>
            ["grep", "--untracked", "--no-color", "-I", "-n", "-i", "-F", "-m", "1", "-e", term],
        SearchContentScope.Everything =>
            ["grep", "--untracked", "--no-exclude-standard", "--no-color", "-I", "-n", "-i", "-F", "-m", "1", "-e", term],
        _ => ["grep", "--no-color", "-I", "-n", "-i", "-F", "-m", "1", "-e", term],
    };

    /// <summary>
    /// The whole fan-out runs off the caller's thread. Target triage touches the disk
    /// before the first await, and one target under a disconnected UNC root blocks on
    /// the SMB timeout — on the dispatcher that is the window frozen mid-keystroke.
    /// </summary>
    public Task<RepoSearchResult> SearchAsync(
        string term, IReadOnlyList<RepoSearchTarget> targets, SearchScope scope, CancellationToken ct = default)
        => Task.Run(() => SearchCoreAsync(term, targets, scope, ct), ct);

    private async Task<RepoSearchResult> SearchCoreAsync(
        string term, IReadOnlyList<RepoSearchTarget> targets, SearchScope scope, CancellationToken ct)
    {
        term = term.Trim();
        if (term.Length < MinTermLength || targets.Count == 0) return RepoSearchResult.Empty;

        var perRepo = new RepoMatches[targets.Count];
        var skipped = 0;
        using var semaphore = new SemaphoreSlim(MaxConcurrency);

        await Task.WhenAll(targets.Select(async (target, index) =>
        {
            if (!IsSearchable(target.Path))
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            await semaphore.WaitAsync(ct);
            try
            {
                perRepo[index] = await SearchRepoAsync(term, target, scope.Content, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable repo must not fault the whole fan-out; it counts as a failure of
                // its own rather than as a skip, which is reserved for a repo never read at all.
                Log.Warn($"search failed in {target.Path}", ex);
                perRepo[index] = RepoMatches.Failure;
            }
            finally
            {
                semaphore.Release();
            }
        }));

        var hits = new List<RepoSearchHit>();
        var more = 0;
        var searched = 0;
        var truncated = 0;
        var failed = 0;
        foreach (var matches in perRepo)
        {
            if (matches is null) continue;
            if (matches.Failed) failed++;
            else if (matches.Truncated) truncated++;
            else searched++;

            more += matches.Suppressed;
            foreach (var hit in matches.Hits)
            {
                if (hits.Count < MaxHitsTotal) hits.Add(hit);
                else more++;
            }
        }

        return new RepoSearchResult(hits, more, searched, skipped, truncated, failed);
    }

    /// <summary>
    /// A repo is searchable when it is a real local checkout nobody is rewriting.
    /// A bare repo and a vanished path both fail <see cref="GitService.IsGitRepo"/>
    /// (neither has a .git entry inside a working tree).
    /// </summary>
    private bool IsSearchable(string repoPath) =>
        !string.IsNullOrWhiteSpace(repoPath)
        && Directory.Exists(repoPath)
        && GitService.IsGitRepo(repoPath)
        && !busyRegistry.IsBusy(repoPath);

    /// <summary>
    /// One repository's answer. <see cref="Failed"/> and <see cref="Truncated"/> are exclusive in
    /// the count, failure first: a repository whose reads errored says so rather than reporting
    /// the partial answer that came back before the error as merely cut short.
    /// </summary>
    private sealed record RepoMatches(List<RepoSearchHit> Hits, int Suppressed, bool Truncated, bool Failed)
    {
        public static readonly RepoMatches Failure = new([], 0, false, true);
    }

    /// <summary>
    /// Holds a repository's candidate hits at the per-repo cap without holding every match. Rows
    /// are banked by how much they are worth: a tracked source file before an untracked one before
    /// an ignored one, and a filename match before a line of content. The cap is spent on the
    /// former, which is what keeps a built repository's obj/ tree from filling it.
    /// </summary>
    private sealed class BankedHits(int cap)
    {
        private readonly List<RepoSearchHit>[] _ranks =
            [.. Enumerable.Range(0, 6).Select(_ => new List<RepoSearchHit>())];

        private int _overflow;

        private static int Rank(RepoSearchHit hit) => ((int)hit.FileScope * 2) + (hit.IsFileNameMatch ? 0 : 1);

        public void Add(RepoSearchHit hit)
        {
            var bank = _ranks[Rank(hit)];
            if (bank.Count < cap) bank.Add(hit);
            else _overflow++;
        }

        public (List<RepoSearchHit> Hits, int Suppressed) Take()
        {
            var kept = new List<RepoSearchHit>(cap);
            var dropped = _overflow;
            foreach (var hit in _ranks.SelectMany(bank => bank))
            {
                if (kept.Count < cap) kept.Add(hit);
                else dropped++;
            }
            return (kept, dropped);
        }
    }

    /// <summary>
    /// One repository, under one content scope. Up to four git reads: the index, the untracked
    /// listing, the ignored listing, and the grep — each one only when the scope needs it.
    ///
    /// One budget spans all of them. A timeout each would make the real per-repo ceiling four times
    /// the budget, and the fan-out's worst case four times what the concurrency cap implies. A read
    /// the budget cuts off leaves the repository reported as truncated rather than as searched.
    /// </summary>
    private async Task<RepoMatches> SearchRepoAsync(
        string term, RepoSearchTarget target, SearchContentScope scope, CancellationToken ct)
    {
        var timeout = BudgetFor(scope);
        var banked = new BankedHits(HitsPerRepoFor(scope));
        var budget = Stopwatch.StartNew();
        var truncated = false;

        async Task<string[]?> ListAsync(string[] args)
        {
            var remaining = timeout - budget.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                truncated = true;
                return null;
            }

            var run = await gitService.RunAsync(target.Path, args, ct, remaining);
            if (run.TimedOut)
            {
                truncated = true;
                return null;
            }
            if (!run.Success) return null;
            if (run.Truncated) truncated = true;
            return run.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        var tracked = await ListAsync(TrackedListArgs);
        if (tracked is null && !truncated) return RepoMatches.Failure;
        tracked ??= [];

        string[] untracked = [];
        var ignored = new List<string>();

        if (scope != SearchContentScope.Tracked && !truncated)
        {
            var others = await ListAsync(UntrackedListArgs);
            if (others is null && !truncated) return RepoMatches.Failure;
            untracked = others ?? [];
        }

        var trackedPaths = new HashSet<string>(tracked, StringComparer.Ordinal);
        var untrackedPaths = new HashSet<string>(untracked, StringComparer.Ordinal);

        if (scope == SearchContentScope.Everything && !truncated)
        {
            // Ignored is what the unfiltered listing holds and the filtered one does not. Two
            // listings rather than a check-ignore per hit: the difference is exact, needs no
            // second guess about a path git already classified, and costs one process either way.
            var everything = await ListAsync(IgnoredListArgs);
            if (everything is null && !truncated) return RepoMatches.Failure;
            foreach (var path in everything ?? [])
                if (!untrackedPaths.Contains(path)) ignored.Add(path);
        }

        // Filename matches lead within their own scope: a path hit names the thing the user is
        // looking for, where a content hit is one line out of a file. Listing order is git's.
        void AddNameMatches(IReadOnlyList<string> paths, SearchFileScope fileScope)
        {
            foreach (var path in paths)
                if (path.Contains(term, StringComparison.OrdinalIgnoreCase))
                    banked.Add(new RepoSearchHit(target.Name, target.Path, path, 0, path, fileScope));
        }

        AddNameMatches(tracked, SearchFileScope.Tracked);
        AddNameMatches(untracked, SearchFileScope.Untracked);
        AddNameMatches(ignored, SearchFileScope.Ignored);

        var failed = false;
        var grepRemaining = timeout - budget.Elapsed;
        // A listing cut short is a prefix, and a prefix cannot say what a path outside it is. Under
        // every scope but the narrowest the label is read from those listings, so a content pass
        // over an incomplete one would label tracked source as ignored. The repository already
        // reports as cut short; it does not also get to guess.
        var canLabel = scope == SearchContentScope.Tracked || !truncated;
        if (grepRemaining <= TimeSpan.Zero || !canLabel)
        {
            truncated = true;
        }
        else
        {
            var grep = await gitService.RunAsync(target.Path, GrepArgs(scope, term), ct, grepRemaining);
            if (grep.TimedOut) truncated = true;
            // Exit 1 is "no matches", which is an outcome; anything above it is git refusing.
            else if (grep.ExitCode > 1) failed = true;
            else
            {
                if (grep.Truncated) truncated = true;
                foreach (var line in grep.StdOut.Split('\n'))
                {
                    var parsed = ParseGrepLine(line);
                    if (parsed is null) continue;
                    banked.Add(new RepoSearchHit(
                        target.Name, target.Path, parsed.Value.Path, parsed.Value.Line, parsed.Value.Text,
                        ClassifyPath(parsed.Value.Path, trackedPaths, untrackedPaths, scope)));
                }
            }
        }

        var (hits, suppressed) = banked.Take();
        return new RepoMatches(hits, suppressed, truncated, failed);
    }

    /// <summary>
    /// What git says a grepped path is. Under the narrowest scope git read the index and nothing
    /// else, so every path it returned is tracked by construction. Above it, a path in neither
    /// listing is one the listings could not account for, and under the widest scope the honest
    /// reading of that is ignored — the tracked and untracked sets are both explicit, and only the
    /// ignored set is derived.
    ///
    /// The listings are separate reads and git offers no single call that returns all three sets
    /// atomically, so a file staged or ignored between them carries the earlier read's label for
    /// the length of one search. The label is drawn on the row and nothing keys off it; the next
    /// search takes fresh listings.
    /// </summary>
    private static SearchFileScope ClassifyPath(
        string path, HashSet<string> tracked, HashSet<string> untracked, SearchContentScope scope)
    {
        if (scope == SearchContentScope.Tracked) return SearchFileScope.Tracked;
        if (tracked.Contains(path)) return SearchFileScope.Tracked;
        if (untracked.Contains(path)) return SearchFileScope.Untracked;
        return scope == SearchContentScope.Everything ? SearchFileScope.Ignored : SearchFileScope.Untracked;
    }

    /// <summary>
    /// Splits one `git grep -n` line into path, line number, and text. A path may itself
    /// contain a colon, so the split is the first colon FOLLOWED BY digits and a colon,
    /// not the first colon. Returns null for a line that isn't in that shape.
    /// </summary>
    public static (string Path, int Line, string Text)? ParseGrepLine(string raw)
    {
        var line = raw.TrimEnd('\r');
        if (line.Length == 0) return null;

        for (var i = line.IndexOf(':'); i >= 0; i = line.IndexOf(':', i + 1))
        {
            var digits = i + 1;
            while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
            if (digits == i + 1 || digits >= line.Length || line[digits] != ':') continue;
            if (!int.TryParse(line.AsSpan(i + 1, digits - i - 1), out var number)) continue;
            return (line[..i], number, line[(digits + 1)..].Trim());
        }
        return null;
    }
}
