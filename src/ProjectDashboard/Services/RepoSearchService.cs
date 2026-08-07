using System.Diagnostics;
using System.IO;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services;

/// <summary>A repository the fan-out may search: display name plus its working-tree path.</summary>
public sealed record RepoSearchTarget(string Name, string Path);

/// <summary>
/// One result row. <see cref="Line"/> is 0 for a filename match, where
/// <see cref="Text"/> repeats the path rather than a line of content.
/// </summary>
public sealed record RepoSearchHit(string RepoName, string RepoPath, string FilePath, int Line, string Text)
{
    public bool IsFileNameMatch => Line == 0;

    /// <summary>"repo · path:line" — the row's provenance in one line.</summary>
    public string Location => IsFileNameMatch ? $"{RepoName} · {FilePath}" : $"{RepoName} · {FilePath}:{Line}";
}

/// <summary>
/// Outcome of one fan-out. <see cref="More"/> counts matches found but not returned
/// (either cap), so the caller can say how much it is hiding instead of implying
/// the returned rows are everything.
/// </summary>
public sealed record RepoSearchResult(
    IReadOnlyList<RepoSearchHit> Hits,
    int More,
    int ReposSearched,
    int ReposSkipped)
{
    public static readonly RepoSearchResult Empty = new([], 0, 0, 0);
}

/// <summary>
/// Content and filename search across every discovered repository, one `git grep`
/// plus one `git ls-files` per repo. Bounded on every axis a repo set can grow in:
/// concurrency, per-repo wall clock, hits per repo, and hits overall. Every call is
/// cancellable — the palette cancels the previous fan-out on each keystroke, and an
/// abandoned search must stop spawning git rather than run to completion unseen.
/// </summary>
public sealed class RepoSearchService(GitService gitService, RepoBusyRegistry busyRegistry)
{
    public const int MaxConcurrency = 6;
    public const int MaxHitsPerRepo = 5;
    public const int MaxHitsTotal = 60;
    public const int MinTermLength = 2;
    public static readonly TimeSpan PerRepoTimeout = TimeSpan.FromSeconds(4);

    public async Task<RepoSearchResult> SearchAsync(
        string term, IReadOnlyList<RepoSearchTarget> targets, CancellationToken ct = default)
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
                perRepo[index] = await SearchRepoAsync(term, target, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable repo must not fault the whole fan-out; it counts as skipped.
                Log.Warn($"search skipped {target.Path}", ex);
                Interlocked.Increment(ref skipped);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        var hits = new List<RepoSearchHit>();
        var more = 0;
        var searched = 0;
        foreach (var matches in perRepo)
        {
            if (matches is null) continue;
            searched++;
            more += matches.Suppressed;
            foreach (var hit in matches.Hits)
            {
                if (hits.Count < MaxHitsTotal) hits.Add(hit);
                else more++;
            }
        }

        return new RepoSearchResult(hits, more, searched, skipped);
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

    private sealed record RepoMatches(List<RepoSearchHit> Hits, int Suppressed);

    private async Task<RepoMatches> SearchRepoAsync(string term, RepoSearchTarget target, CancellationToken ct)
    {
        var hits = new List<RepoSearchHit>();
        var suppressed = 0;

        void Add(RepoSearchHit hit)
        {
            if (hits.Count < MaxHitsPerRepo) hits.Add(hit);
            else suppressed++;
        }

        // One budget spans both invocations. A timeout each makes the real per-repo
        // ceiling twice PerRepoTimeout, and the fan-out's worst case twice what the
        // concurrency cap implies.
        var budget = Stopwatch.StartNew();

        // Filename matches lead: a path hit names the thing the user is looking for,
        // where a content hit is one line out of a file.
        var files = await gitService.RunAsync(target.Path, ["ls-files", "-z"], ct, PerRepoTimeout);
        if (files.Success)
        {
            foreach (var path in files.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (path.Contains(term, StringComparison.OrdinalIgnoreCase))
                    Add(new RepoSearchHit(target.Name, target.Path, path, 0, path));
        }

        var remaining = PerRepoTimeout - budget.Elapsed;
        if (remaining <= TimeSpan.Zero) return new RepoMatches(hits, suppressed);

        // -m 1 caps git's own work at one line per file; -I skips binaries. Exit 1 means
        // "no matches", which is an outcome, not a failure.
        var grep = await gitService.RunAsync(
            target.Path,
            ["grep", "--no-color", "-I", "-n", "-i", "-F", "-m", "1", "-e", term],
            ct,
            remaining);

        if (grep.ExitCode == 0 && !grep.TimedOut)
        {
            foreach (var line in grep.StdOut.Split('\n'))
            {
                var parsed = ParseGrepLine(line);
                if (parsed is null) continue;
                Add(new RepoSearchHit(target.Name, target.Path, parsed.Value.Path, parsed.Value.Line, parsed.Value.Text));
            }
        }

        return new RepoMatches(hits, suppressed);
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
