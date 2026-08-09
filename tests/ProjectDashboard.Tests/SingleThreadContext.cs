namespace ProjectDashboard.Tests;

/// <summary>
/// A single-threaded synchronization context on a dedicated thread, which is what an awaited
/// step sees when the app runs it: every continuation is posted back to the one thread the
/// surface is drawn on. A pool thread can never serve as this thread, so work handed to the pool
/// is provably not running here.
///
/// A test that exercises two callers awaiting ONE task needs this to be faithful. Without a
/// context, both continuations are scheduled to the pool and can resume in parallel — which the
/// app's own dispatcher never does, and which turns a check-then-assign the view models make
/// between awaits into a race that exists nowhere but the test.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "ui-test-pump" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("the pump only accepts posted continuations");

    /// <summary>Runs the body on the pump thread, drains every continuation it posts, and rethrows what it threw.</summary>
    public void Run(Func<Task> body)
    {
        Task work = Task.CompletedTask;
        Post(_ =>
        {
            try
            {
                work = body();
            }
            catch (Exception ex)
            {
                work = Task.FromException(ex);
            }
            // Off the pump so the shutdown signal cannot be queued behind the continuations
            // it is waiting on.
            work.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
        }, null);
        _thread.Join();
        work.GetAwaiter().GetResult();
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            callback(state);
    }

    public void Dispose() => _queue.Dispose();
}
