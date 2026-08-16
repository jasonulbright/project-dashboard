using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>The five per-repository sources the Alerts page reads from GitHub.</summary>
public enum AlertSource
{
    Dependabot,
    CodeScanning,
    SecretScanning,

    /// <summary>GitHub's issues listing counts pull requests among issues; the true issue count is this minus <see cref="PullRequests"/>.</summary>
    IssuesAndPrs,

    PullRequests,
}

/// <summary>
/// One source's last-known answer for one repository. <see cref="Count"/> null means no answer
/// is held — never zero, which is an answer. <see cref="Unreadable"/> carries why the source
/// refused, in the words the row shows; empty when the count stands.
/// </summary>
public sealed class AlertSourceState
{
    public int? Count { get; set; }
    public string Unreadable { get; set; } = "";
    public string ETag { get; set; } = "";
    public DateTimeOffset FetchedUtc { get; set; }
}

/// <summary>
/// What one refresh did, per source. The four are worded apart on the page because they are
/// different facts: <see cref="Changed"/> and <see cref="Unchanged"/> are answers GitHub gave,
/// <see cref="Refused"/> is GitHub declining to answer, and <see cref="Unanswered"/> is no
/// answer arriving at all — a killed gh, a dead network, a rate limit — after which the held
/// value still describes the LAST answer, not the present.
/// </summary>
public sealed record AlertRefreshOutcome(int Changed, int Unchanged, int Refused, int Unanswered)
{
    public static AlertRefreshOutcome operator +(AlertRefreshOutcome a, AlertRefreshOutcome b) =>
        new(a.Changed + b.Changed, a.Unchanged + b.Unchanged, a.Refused + b.Refused, a.Unanswered + b.Unanswered);

    public static AlertRefreshOutcome Zero => new(0, 0, 0, 0);
}

/// <summary>
/// Reads what is open against each repository — issues, pull requests, Dependabot, code
/// scanning, secret scanning — and holds the answers in a local cache so the alerts view opens
/// from what it last knew.
///
/// Every refresh is a conditional request: the cached ETag rides out as If-None-Match, and a
/// 304 answer costs one round-trip that GitHub does not count against the rate limit. Local
/// state is no proxy for any of this — an issue, a CVE, or a leaked secret arrives server-side
/// with no local commit moving — so the server's own change signal is the only reliable one.
///
/// A refusal is a fact the row states, never a zero: secret scanning needs permissions many
/// tokens lack, and either scanning feature can simply be off for a repository. Each source
/// degrades alone with its reason held, and a later refresh asks again. A rate-limited answer
/// is NOT a refusal — the limit says nothing about the repository — so the held answer stands.
///
/// The cache records which gh identity read it. What a token can see is a property of the
/// account, so answers read as one identity are dropped whole when another takes over rather
/// than being shown as if the new identity had been told them.
/// </summary>
public class AlertsService
{
    private readonly GitHubService _gitHub;
    private readonly object _gate = new();
    private CacheFile _cache;

    public AlertsService(GitHubService gitHub)
    {
        _gitHub = gitHub;
        _cache = LoadCache();
    }

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<AlertSource> Sources =
        [AlertSource.Dependabot, AlertSource.CodeScanning, AlertSource.SecretScanning,
         AlertSource.IssuesAndPrs, AlertSource.PullRequests];

    /// <summary>The held answer, or null when this repository's source was never read.</summary>
    public AlertSourceState? Cached(string repoSlug, AlertSource source)
    {
        lock (_gate)
        {
            return _cache.Repos.TryGetValue(repoSlug, out var sources)
                   && sources.TryGetValue(source, out var state)
                ? Clone(state)
                : null;
        }
    }

    /// <summary>
    /// Names the identity the coming reads run as. A cache read by a different identity is
    /// dropped whole — its counts and refusals describe what THAT account could see. An empty
    /// name means the identity could not be established; the cache is kept, because dropping
    /// held answers over an unreadable auth state would trade known facts for none.
    /// </summary>
    public void EnsureAccount(string account)
    {
        if (account.Length == 0) return;
        lock (_gate)
        {
            if (_cache.Account == account) return;
            _cache = new CacheFile { Account = account };
        }
        SaveCache();
    }

    /// <summary>
    /// Asks GitHub about every source for one repository, conditionally where an answer is
    /// already held. A 304 keeps the held count and refreshes its stamp — the server confirmed
    /// it still stands, which is a fact worth the stamp.
    /// </summary>
    public async Task<AlertRefreshOutcome> RefreshAsync(string repoSlug, CancellationToken ct = default)
    {
        var outcome = AlertRefreshOutcome.Zero;
        foreach (var source in Sources)
        {
            ct.ThrowIfCancellationRequested();
            var held = Cached(repoSlug, source);
            var args = ApiArgs(repoSlug, source, held?.ETag ?? "");
            var run = await _gitHub.RunAsync(args, ct, ReadTimeout);
            var read = ParseApiResponse(run.StdOut, run.StdErr);

            var state = new AlertSourceState { FetchedUtc = DateTimeOffset.UtcNow };
            switch (read.Status)
            {
                case 304 when held is not null:
                    state.Count = held.Count;
                    state.Unreadable = held.Unreadable;
                    state.ETag = held.ETag;
                    outcome += new AlertRefreshOutcome(0, 1, 0, 0);
                    break;
                case 200:
                    state.Count = read.Count;
                    state.ETag = read.ETag;
                    outcome += new AlertRefreshOutcome(1, 0, 0, 0);
                    break;
                case 0:
                case 429:
                case 403 when IsRateLimited(read.Message):
                    // No verdict on the repository was given. The held answer outlives it —
                    // its stamp untouched, because nothing confirmed it just now — and a
                    // repository never read simply stays never read.
                    outcome += new AlertRefreshOutcome(0, 0, 0, 1);
                    continue;
                default:
                    state.Unreadable = Refusal(source, read.Status, read.Message);
                    outcome += new AlertRefreshOutcome(0, 0, 1, 0);
                    break;
            }
            Store(repoSlug, source, state);
        }
        SaveCache();
        return outcome;
    }

    /// <summary>GitHub's rate limit answers 403 with these words; the 403 for a missing scope carries neither.</summary>
    internal static bool IsRateLimited(string message) =>
        message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("abuse", StringComparison.OrdinalIgnoreCase);

    private void Store(string repoSlug, AlertSource source, AlertSourceState state)
    {
        lock (_gate)
        {
            if (!_cache.Repos.TryGetValue(repoSlug, out var sources))
                _cache.Repos[repoSlug] = sources = [];
            sources[source] = state;
        }
    }

    internal static List<string> ApiArgs(string repoSlug, AlertSource source, string etag)
    {
        var path = source switch
        {
            AlertSource.Dependabot => $"repos/{repoSlug}/dependabot/alerts?state=open&per_page=1",
            AlertSource.CodeScanning => $"repos/{repoSlug}/code-scanning/alerts?state=open&per_page=1",
            AlertSource.SecretScanning => $"repos/{repoSlug}/secret-scanning/alerts?state=open&per_page=1",
            AlertSource.IssuesAndPrs => $"repos/{repoSlug}/issues?state=open&per_page=1",
            _ => $"repos/{repoSlug}/pulls?state=open&per_page=1",
        };
        var args = new List<string> { "api", "--include" };
        if (etag.Length > 0)
        {
            args.Add("-H");
            args.Add($"If-None-Match: {etag}");
        }
        args.Add(path);
        return args;
    }

    internal sealed record ApiRead(int Status, string ETag, int Count, string Message);

    /// <summary>
    /// Reads a `gh api --include` answer: the status line, the ETag, and the open count.
    /// The count asks for one item per page, so the last-page number in the Link header IS the
    /// count; no Link with an empty array is zero, no Link with one item is one. gh exits
    /// nonzero for a 304, so the status line decides and the exit code never does. Status 0
    /// means no HTTP answer was found at all — gh itself failed to run or to reach the host.
    /// </summary>
    internal static ApiRead ParseApiResponse(string stdout, string stderr)
    {
        var text = stdout.Length > 0 ? stdout : stderr;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var status = 0;
        var etag = "";
        var count = 0;
        var bodyAt = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i == 0)
            {
                var parts = line.Split(' ');
                if (parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
                    && int.TryParse(parts[1], out var parsed))
                    status = parsed;
                continue;
            }
            if (line.Length == 0) { bodyAt = i + 1; break; }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("ETag", StringComparison.OrdinalIgnoreCase))
                etag = value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value;
            else if (name.Equals("Link", StringComparison.OrdinalIgnoreCase))
                count = LastPageNumber(value);
        }

        var body = bodyAt >= 0 && bodyAt < lines.Length
            ? string.Join("\n", lines[bodyAt..]).Trim()
            : "";
        if (status == 200 && count == 0)
            count = body.Length > 0 && body != "[]" ? 1 : 0;
        return new ApiRead(status, etag, count, MessageOf(body, stderr));
    }

    /// <summary>The page number the rel="last" link names; zero when the header names none.</summary>
    internal static int LastPageNumber(string link)
    {
        foreach (var part in link.Split(','))
        {
            if (!part.Contains("rel=\"last\"", StringComparison.Ordinal)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(part, @"[?&]page=(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var page)) return page;
        }
        return 0;
    }

    private static string MessageOf(string body, string stderr)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? "";
        }
        catch (JsonException) { }
        return stderr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
    }

    /// <summary>
    /// The row's words for a source that answered with a refusal. 404 is the feature being off
    /// or unseen as often as the repository missing, and a non-rate-limit 403 is the token, so
    /// each names the likely cause and none of them claims zero alerts.
    /// </summary>
    internal static string Refusal(AlertSource source, int status, string message)
    {
        var name = source switch
        {
            AlertSource.Dependabot => "Dependabot alerts",
            AlertSource.CodeScanning => "code scanning",
            AlertSource.SecretScanning => "secret scanning",
            AlertSource.IssuesAndPrs => "open issues",
            _ => "open pull requests",
        };
        return status switch
        {
            403 => $"Couldn't read {name}: the signed-in account or its token lacks the permission (HTTP 403"
                   + (message.Length > 0 ? $": {message})" : ")"),
            404 => $"Couldn't read {name}: not enabled for this repository, or not visible to this account (HTTP 404)",
            _ => $"Couldn't read {name}: HTTP {status}" + (message.Length > 0 ? $": {message}" : ""),
        };
    }

    // ── Cache — machine-local, reconstructible, owned by one gh identity ────

    private sealed class CacheFile
    {
        public string Account { get; set; } = "";
        public Dictionary<string, Dictionary<AlertSource, AlertSourceState>> Repos { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string CachePath => Path.Combine(AppPaths.LocalDir, "alerts-cache.json");

    private static CacheFile LoadCache()
    {
        try
        {
            return DurableJsonFile.Read<CacheFile>(CachePath, JsonOptions) ?? new CacheFile();
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {CachePath}; every repository counts as never read", ex);
            return new CacheFile();
        }
    }

    private void SaveCache()
    {
        try
        {
            CacheFile snapshot;
            lock (_gate)
                snapshot = new CacheFile
                {
                    Account = _cache.Account,
                    Repos = _cache.Repos.ToDictionary(
                        r => r.Key,
                        r => r.Value.ToDictionary(s => s.Key, s => Clone(s.Value)),
                        StringComparer.OrdinalIgnoreCase),
                };
            DurableJsonFile.Write(CachePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write {CachePath}; alert answers reset on next launch", ex);
        }
    }

    private static AlertSourceState Clone(AlertSourceState state) => new()
    {
        Count = state.Count,
        Unreadable = state.Unreadable,
        ETag = state.ETag,
        FetchedUtc = state.FetchedUtc,
    };
}
