using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Windows;

namespace ProjectDashboard.Tests;

/// <summary>
/// The one STA thread this process parses shipped markup on. WPF allows one Application per
/// process, and an Application together with the brushes in its merged dictionaries belongs to
/// the thread that built them — so every test that loads a page shares this thread rather than
/// starting one of its own.
/// </summary>
internal static class StaHost
{
    private static readonly BlockingCollection<WorkItem> Queue = new();
    private static readonly Lazy<Thread> Worker = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record WorkItem(Action Body, ManualResetEventSlim Done)
    {
        public Exception? Error { get; set; }
    }

    public static void Run(Action body)
    {
        _ = Worker.Value;

        var item = new WorkItem(body, new ManualResetEventSlim());
        Queue.Add(item);
        if (!item.Done.Wait(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("STA test body did not complete");
        if (item.Error is not null)
            ExceptionDispatchInfo.Capture(item.Error).Throw();
    }

    private static Thread Start()
    {
        var ready = new ManualResetEventSlim();
        Exception? startupError = null;

        // A body that wedges must not outlive the run: Run gives up waiting, and a foreground
        // thread would keep the test host alive after it does.
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current as ProjectDashboard.App ?? new ProjectDashboard.App();
                app.InitializeComponent();
                // The shipped mode shuts the Application down when the first window closes, and a
                // window opened after that never lays out — so later bodies would read an empty
                // visual tree rather than fail on what they are checking.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            catch (Exception ex) { startupError = ex; }
            finally { ready.Set(); }

            foreach (var item in Queue.GetConsumingEnumerable())
            {
                try { item.Body(); }
                catch (Exception ex) { item.Error = ex; }
                finally { item.Done.Set(); }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (startupError is not null)
            ExceptionDispatchInfo.Capture(startupError).Throw();
        return thread;
    }
}

/// <summary>
/// Tests that load shipped markup share one Application on one thread, so they run serially
/// rather than racing to be it.
/// </summary>
[CollectionDefinition("shipped-markup", DisableParallelization = true)]
public sealed class ShippedMarkupCollection;
