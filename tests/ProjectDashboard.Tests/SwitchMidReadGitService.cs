using ProjectDashboard.Services;

namespace ProjectDashboard.Tests;

/// <summary>
/// Lands a project switch inside a read that is already in flight. The callback runs once, in the
/// first git call made after it is set, so the continuations that follow are the previous
/// project's — which is the only way to reach the code that marks a tab loaded after an await.
/// </summary>
internal sealed class SwitchMidReadGitService : GitService
{
    private Func<Task>? _onNextCall;

    public Func<Task>? OnNextCall
    {
        set => Interlocked.Exchange(ref _onNextCall, value);
    }

    public override async Task<ProcessResult> RunAsync(
        string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _onNextCall, null) is { } callback) await callback();
        return await base.RunAsync(repoPath, args, environment, ct, timeout);
    }
}
