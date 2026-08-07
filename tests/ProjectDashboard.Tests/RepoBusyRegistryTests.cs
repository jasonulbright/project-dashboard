using ProjectDashboard.Services.Safety;
using Xunit;

namespace ProjectDashboard.Tests;

public class RepoBusyRegistryTests
{
    private const string Repo = @"C:\projects\demo";

    [Fact]
    public void Acquire_MarksBusy_DisposeReleases()
    {
        var registry = new RepoBusyRegistry();
        Assert.False(registry.IsBusy(Repo));

        var lease = registry.Acquire(Repo);
        Assert.True(registry.IsBusy(Repo));

        lease.Dispose();
        Assert.False(registry.IsBusy(Repo));
    }

    [Fact]
    public void IsBusy_NormalizesPath_CaseAndTrailingSeparator()
    {
        var registry = new RepoBusyRegistry();
        using var _ = registry.Acquire(@"C:\projects\Demo");
        Assert.True(registry.IsBusy(@"c:\projects\demo\"));
    }

    [Fact]
    public void Acquire_WhileBusy_Throws()
    {
        var registry = new RepoBusyRegistry();
        using var _ = registry.Acquire(Repo);
        Assert.Throws<InvalidOperationException>(() => registry.Acquire(Repo));
    }

    [Fact]
    public void TryAcquire_SecondAttempt_ReturnsFalse_ThenSucceedsAfterRelease()
    {
        var registry = new RepoBusyRegistry();
        Assert.True(registry.TryAcquire(Repo, out var first));
        Assert.False(registry.TryAcquire(Repo, out _));

        first.Dispose();
        Assert.True(registry.TryAcquire(Repo, out var third));
        third.Dispose();
    }

    [Fact]
    public void DoubleDispose_DoesNotReleaseALaterAcquirersLease()
    {
        var registry = new RepoBusyRegistry();
        var first = registry.Acquire(Repo);
        first.Dispose();

        var second = registry.Acquire(Repo); // a new holder
        first.Dispose();                      // stale double dispose must be inert
        Assert.True(registry.IsBusy(Repo));
        second.Dispose();
    }

    [Fact]
    public void Changed_FiresOnAcquireAndRelease()
    {
        var registry = new RepoBusyRegistry();
        var events = new List<string>();
        registry.Changed += p => { lock (events) events.Add(p); };

        using (registry.Acquire(Repo)) { }

        lock (events) Assert.Equal(2, events.Count); // acquire + release
    }

    [Fact]
    public async Task ParallelAcquire_ExactlyOneWins()
    {
        var registry = new RepoBusyRegistry();
        var start = new ManualResetEventSlim(false);
        var winners = 0;

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (registry.TryAcquire(Repo, out IDisposable _))
                Interlocked.Increment(ref winners);
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, winners);
        Assert.True(registry.IsBusy(Repo));
    }
}
