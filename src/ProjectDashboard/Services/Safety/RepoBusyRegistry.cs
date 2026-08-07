using System.IO;

namespace ProjectDashboard.Services.Safety;

/// <summary>
/// App-wide record of repositories under a long, destructive operation (R-08). The
/// watcher refresh, periodic timer, discovery, and Sync All consult it to skip a repo
/// that a rewrite is actively touching, so a background read can never collide with an
/// in-flight swap. One instance is shared as a singleton; all members are thread-safe.
///
/// Keys are the normalized full repo path (lowercased — Windows paths are
/// case-insensitive), so two spellings of one repo share a busy state.
/// </summary>
public sealed class RepoBusyRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);

    /// <summary>Raised after a repo becomes busy or is released, carrying that repo's path.</summary>
    public event Action<string>? Changed;

    /// <summary>
    /// Marks the repo busy and returns a lease whose disposal releases it. Throws
    /// <see cref="InvalidOperationException"/> if the repo is already busy — for callers
    /// that treat a double-acquire as a bug. Use <see cref="TryAcquire"/> to branch instead.
    /// </summary>
    public IDisposable Acquire(string repoPath)
    {
        if (TryAcquire(repoPath, out var lease)) return lease;
        throw new InvalidOperationException($"Repository is already busy: {repoPath}");
    }

    /// <summary>
    /// Attempts to mark the repo busy. Returns false without a lease when another lease is
    /// already held; exactly one of a set of racing callers wins.
    /// </summary>
    public bool TryAcquire(string repoPath, out IDisposable lease)
    {
        var key = Normalize(repoPath);
        lock (_gate)
        {
            if (!_busy.Add(key)) { lease = NoopLease.Instance; return false; }
        }
        RaiseChanged(repoPath);
        lease = new Lease(this, key, repoPath);
        return true;
    }

    public bool IsBusy(string repoPath)
    {
        var key = Normalize(repoPath);
        lock (_gate) return _busy.Contains(key);
    }

    private void Release(string key, string repoPath)
    {
        bool removed;
        lock (_gate) removed = _busy.Remove(key);
        if (removed) RaiseChanged(repoPath);
    }

    private void RaiseChanged(string repoPath)
    {
        // Fire outside the lock: a subscriber that calls back into the registry (or blocks)
        // must not deadlock or stall other acquirers.
        try { Changed?.Invoke(repoPath); }
        catch (Exception ex) { Log.Warn("RepoBusyRegistry subscriber threw", ex); }
    }

    private static string Normalize(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repo path is required.", nameof(repoPath));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath)).ToLowerInvariant();
    }

    private sealed class Lease : IDisposable
    {
        private readonly RepoBusyRegistry _owner;
        private readonly string _key;
        private readonly string _repoPath;
        private int _disposed;

        public Lease(RepoBusyRegistry owner, string key, string repoPath)
        {
            _owner = owner;
            _key = key;
            _repoPath = repoPath;
        }

        public void Dispose()
        {
            // Idempotent: a double dispose must not release a lease a later acquirer now holds.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Release(_key, _repoPath);
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
}
