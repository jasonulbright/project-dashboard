using System.Diagnostics;

namespace ProjectDashboard.Tests;

/// <summary>
/// Every test that parses shipped markup queues its body onto one STA thread. A body that stops
/// returning holds that thread for the rest of the run, and the wait is what each later body
/// pays: without the poison the run reports one timeout per remaining markup test and names the
/// cause of none of them.
///
/// Exercised on an isolated host: the poison is terminal by design, so the shared host the real
/// markup tests queue onto is never the one wedged here.
/// </summary>
public class StaHostTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void ABodyThatOverrunsItsBudget_FailsItsOwnTestAndPoisonsTheHostForTheRest()
    {
        var release = new ManualResetEventSlim();
        var host = StaHost.Isolated(Budget);
        try
        {
            var wedge = Assert.Throws<TimeoutException>(() => host.Execute(release.Wait, "WedgingBody"));
            Assert.Contains("WedgingBody", wedge.Message);

            var elapsed = Stopwatch.StartNew();
            var later = Assert.Throws<InvalidOperationException>(() => host.Execute(() => { }, "LaterBody"));
            elapsed.Stop();

            // The later caller is told which body wedged the host, not that its own timed out.
            Assert.Equal(StaHost.WedgedMessage("WedgingBody"), later.Message);
            Assert.DoesNotContain("LaterBody", later.Message);
            Assert.True(elapsed.Elapsed < Budget, $"the poisoned host waited {elapsed.Elapsed}");
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// The wedge is what poisons the host — a body that returns, whatever it did, leaves the
    /// host running the ones queued behind it.
    /// </summary>
    [Fact]
    public void ABodyThatThrows_SurfacesItsOwnExceptionAndLeavesTheHostUsable()
    {
        var host = StaHost.Isolated(Budget);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => host.Execute(() => throw new InvalidOperationException("body boom")));
        Assert.Equal("body boom", thrown.Message);

        var ran = 0;
        host.Execute(() => ran++);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void QueuedBodies_RunInTurnOnTheOneThread()
    {
        var host = StaHost.Isolated(Budget);
        var order = new List<int>();
        var threads = new HashSet<int>();

        for (var i = 0; i < 3; i++)
        {
            var n = i;
            host.Execute(() =>
            {
                order.Add(n);
                threads.Add(Environment.CurrentManagedThreadId);
            });
        }

        Assert.Equal([0, 1, 2], order);
        Assert.Single(threads);
    }
}
