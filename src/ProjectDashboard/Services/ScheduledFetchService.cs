using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using ProjectDashboard.Models;
using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services;

/// <summary>One repository a tick may fetch: where it is, and the remote its host budget keys on.</summary>
public sealed record FetchCandidate(string RepoPath, string RemoteUrl);

/// <summary>
/// What one tick did, for a status line that never presents a skipped repository as a fetched
/// one. <see cref="Offline"/> true means the tick spawned nothing at all.
/// </summary>
public sealed record FetchTickReport(
    int Fetched, int Failed, int Skipped, bool Offline, DateTimeOffset CompletedUtc);

/// <summary>
/// Fetches each repository's remote-tracking refs on a schedule, so ahead/behind counts stop
/// aging silently between manual syncs. `git fetch --prune` is the ONLY verb this service runs:
/// read-only on the remote, writes only refs/remotes/* and FETCH_HEAD locally, touches no
/// working tree, index, local branch, or stash, and never pushes.
///
/// Each fetch runs under the repository lease and releases it BEFORE the caller is told to
/// refresh the card — a refresh under the lease reads a repository the registry says is
/// untouchable. A repository already under lease is skipped this tick, not queued: the next
/// tick reads fresh eligibility rather than replaying a stale plan.
///
/// Failures are classified. An authentication refusal or a repository that no longer exists is
/// not transient: that repository is parked with its reason until the app restarts or the
/// feature is toggled, because retrying a bad credential on a timer forever is how a background
/// feature becomes noise. Anything else backs its HOST off exponentially (1 → 60 minutes,
/// jittered), so one unreachable host cannot make every tick spawn a timeout per repository.
/// </summary>
public sealed class ScheduledFetchService : IDisposable
{
    private readonly GitService _git;
    private readonly RepoBusyRegistry _busy;
    private readonly object _gate = new();

    private readonly Dictionary<string, DateTimeOffset> _lastFetched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _hostBackoff = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _parked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Overridable network gate; false skips the whole tick with no processes spawned.</summary>
    internal Func<bool> NetworkAvailable { get; set; } = NetworkInterface.GetIsNetworkAvailable;

    /// <summary>Raised after a fetch's lease is released, naming the repository whose card is stale.</summary>
    public event Action<string>? RepoFetched;

    /// <summary>One line for the Settings page: what the last tick did, or that none has run.</summary>
    public string StatusLine { get; private set; } = "No background fetch has run yet.";

    /// <summary>Fetches one host may start inside a single minute before the rest defer a tick.</summary>
    internal const int HostBudgetPerMinute = 10;

    private static readonly TimeSpan BackoffCeiling = TimeSpan.FromMinutes(60);

    public ScheduledFetchService(GitService git, RepoBusyRegistry busy)
    {
        _git = git;
        _busy = busy;
        LoadState();
        // Reconnect is the one signal that makes every backoff stale at once; waiting the
        // ceiling out after a resume would read as the feature being broken for an hour.
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void Dispose() => NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        lock (_gate) _hostBackoff.Clear();
    }

    /// <summary>Why a card's counts may be stale or frozen, for the card's own affordance. Empty when neither.</summary>
    public string DescribeRepo(string repoPath)
    {
        lock (_gate)
        {
            if (_parked.TryGetValue(repoPath, out var reason))
                return $"Background fetch is parked for this repository: {reason}";
            if (_lastFetched.TryGetValue(repoPath, out var at))
                return $"Counts are as of the last fetch, {at.ToLocalTime():HH:mm}.";
        }
        return "";
    }

    /// <summary>A successful fetch from any surface makes this repository's counts current now.</summary>
    public void RecordManualFetch(string repoPath)
    {
        lock (_gate)
        {
            _lastFetched[repoPath] = DateTimeOffset.UtcNow;
            _parked.Remove(repoPath);
        }
        SaveState();
    }

    /// <summary>Toggling the feature is the user acting; parked repositories get a fresh verdict.</summary>
    public void ClearParked()
    {
        lock (_gate) _parked.Clear();
    }

    public async Task<FetchTickReport> RunTickAsync(
        IReadOnlyList<FetchCandidate> candidates, TimeSpan interval, CancellationToken ct = default)
    {
        if (!NetworkAvailable())
        {
            StatusLine = $"Last tick {DateTimeOffset.Now:HH:mm}: offline — nothing was fetched.";
            return new FetchTickReport(0, 0, candidates.Count, Offline: true, DateTimeOffset.UtcNow);
        }

        var fetched = 0;
        var failed = 0;
        var skipped = 0;
        var hostStarts = new Dictionary<string, (DateTimeOffset WindowStart, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var host = GitRemote.Parse(candidate.RemoteUrl)?.Host ?? "(local)";
            var now = DateTimeOffset.UtcNow;

            lock (_gate)
            {
                if (_parked.ContainsKey(candidate.RepoPath)) { skipped++; continue; }
                if (_hostBackoff.TryGetValue(host, out var backoff) && now < backoff.RetryAt) { skipped++; continue; }
                if (_lastFetched.TryGetValue(candidate.RepoPath, out var last) && now - last < interval) { skipped++; continue; }
            }

            // The minute budget defers, never parks: the deferred repositories are simply still
            // due next tick.
            var window = hostStarts.TryGetValue(host, out var w) ? w : (WindowStart: now, Count: 0);
            if (now - window.WindowStart < TimeSpan.FromMinutes(1) && window.Count >= HostBudgetPerMinute)
            {
                skipped++;
                continue;
            }
            if (now - window.WindowStart >= TimeSpan.FromMinutes(1)) window = (now, 0);
            hostStarts[host] = (window.WindowStart, window.Count + 1);

            if (!_busy.TryAcquire(candidate.RepoPath, out var lease)) { skipped++; continue; }

            ProcessResult result;
            try
            {
                result = await _git.FetchAsync(candidate.RepoPath, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new ProcessResult(-1, "", ex.Message, TimedOut: false);
            }
            finally
            {
                lease.Dispose();
            }

            if (result.Success)
            {
                fetched++;
                lock (_gate)
                {
                    _lastFetched[candidate.RepoPath] = DateTimeOffset.UtcNow;
                    _hostBackoff.Remove(host);
                }
                RepoFetched?.Invoke(candidate.RepoPath);
            }
            else
            {
                failed++;
                RecordFailure(candidate.RepoPath, host, result);
            }
        }

        SaveState();
        var completed = DateTimeOffset.UtcNow;
        var backingOff = CountBackingOff(completed);
        StatusLine =
            $"Last background fetch {completed.ToLocalTime():HH:mm}: {fetched} fetched, {failed} failed, {skipped} skipped."
            + (backingOff > 0 ? $" {backingOff} host(s) backing off." : "");
        return new FetchTickReport(fetched, failed, skipped, Offline: false, completed);
    }

    private int CountBackingOff(DateTimeOffset now)
    {
        lock (_gate) return _hostBackoff.Count(b => b.Value.RetryAt > now);
    }

    private void RecordFailure(string repoPath, string host, ProcessResult result)
    {
        var reason = NonTransientReason(result);
        lock (_gate)
        {
            if (reason is not null)
            {
                _parked[repoPath] = reason;
                return;
            }
            var failures = _hostBackoff.TryGetValue(host, out var prior) ? prior.Failures + 1 : 1;
            var minutes = Math.Min(BackoffCeiling.TotalMinutes, Math.Pow(2, failures - 1));
            // Jitter keeps a portfolio's worth of repositories on one host from re-arriving as
            // one burst when the backoff lapses.
            var delay = TimeSpan.FromMinutes(minutes * (0.75 + Random.Shared.NextDouble() * 0.5));
            _hostBackoff[host] = (failures, DateTimeOffset.UtcNow + delay);
        }
    }

    /// <summary>
    /// A refusal that no retry can change without the user acting: bad or absent credentials, or
    /// a repository the remote no longer has. A kill on timeout is transient — the question went
    /// unanswered, which is the host-backoff case, not a verdict on the repository.
    /// </summary>
    internal static string? NonTransientReason(ProcessResult result)
    {
        if (result.TimedOut) return null;
        var text = result.StdErr;
        if (text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not read Password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("403", StringComparison.Ordinal))
            return "the remote refused this machine's credentials";
        if (text.Contains("Repository not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase))
            return "the remote repository was not found";
        return null;
    }

    // ── Persistence — machine-local bookkeeping, reconstructible ────────────

    private sealed class FetchState
    {
        public Dictionary<string, DateTimeOffset> LastFetched { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StatePath => Path.Combine(AppPaths.LocalDir, "scheduled-fetch.json");

    private void LoadState()
    {
        try
        {
            var state = DurableJsonFile.Read<FetchState>(StatePath, JsonOptions);
            if (state is null) return;
            lock (_gate)
                foreach (var (path, at) in state.LastFetched) _lastFetched[path] = at;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read {StatePath}; every repository counts as never fetched", ex);
        }
    }

    private void SaveState()
    {
        try
        {
            FetchState state;
            lock (_gate) state = new FetchState { LastFetched = new(_lastFetched, StringComparer.OrdinalIgnoreCase) };
            DurableJsonFile.Write(StatePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write {StatePath}; staleness stamps reset on next launch", ex);
        }
    }
}
