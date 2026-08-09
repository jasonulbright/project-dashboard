using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;

namespace ProjectDashboard.Tests;

/// <summary>
/// The one STA thread this process parses shipped markup on. WPF allows one Application per
/// process, and an Application together with the brushes in its merged dictionaries belongs to
/// the thread that built them — so every test that loads a page shares this thread rather than
/// starting one of its own.
///
/// Bodies run one at a time on that thread, and a body that stops returning cannot be taken off
/// it: the thread is inside the call. The first caller to overrun its budget fails, and the host
/// is poisoned so every later caller fails at once naming that body — otherwise one wedge turns
/// into a timeout per remaining test, and the report names dozens of them instead of its cause.
/// A body that is merely slower than the budget poisons the host the same way; nothing on this
/// side of the call can tell the two apart.
/// </summary>
internal sealed class StaHost
{
    /// <summary>The host every markup test queues onto, and the only one that owns an Application.</summary>
    private static readonly StaHost Shared = new(TimeSpan.FromSeconds(60), hostsApplication: true);

    public static void Run(Action body, [CallerMemberName] string caller = "") =>
        Shared.Execute(body, caller);

    /// <summary>
    /// A host of the caller's own, carrying no Application — WPF allows one per process and the
    /// shared host owns it. The poison is terminal, so a test that exercises it must not run on
    /// the host the markup tests share.
    /// </summary>
    internal static StaHost Isolated(TimeSpan budget) => new(budget, hostsApplication: false);

    internal static string WedgedMessage(string caller) =>
        $"STA host wedged by {caller} — that body never returned, so no later body can run on it.";

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Lazy<Thread> _worker;
    private readonly TimeSpan _budget;
    private readonly bool _hostsApplication;

    /// <summary>The first caller whose body overran, or null while the host still runs bodies.</summary>
    private string? _wedgedBy;

    private StaHost(TimeSpan budget, bool hostsApplication)
    {
        _budget = budget;
        _hostsApplication = hostsApplication;
        _worker = new Lazy<Thread>(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private sealed record WorkItem(Action Body, ManualResetEventSlim Done)
    {
        public Exception? Error { get; set; }
    }

    internal void Execute(Action body, [CallerMemberName] string caller = "")
    {
        if (Volatile.Read(ref _wedgedBy) is { } offender)
            throw new InvalidOperationException(WedgedMessage(offender));

        _ = _worker.Value;

        var item = new WorkItem(body, new ManualResetEventSlim());
        _queue.Add(item);
        if (!item.Done.Wait(_budget))
        {
            // First writer wins: the poison names the body that stopped the thread, not
            // whichever later body happened to give up waiting for it first.
            Interlocked.CompareExchange(ref _wedgedBy, caller, null);
            throw new TimeoutException($"STA test body from {caller} did not complete within {_budget}");
        }
        if (item.Error is not null)
            ExceptionDispatchInfo.Capture(item.Error).Throw();
    }

    private Thread Start()
    {
        var ready = new ManualResetEventSlim();
        Exception? startupError = null;

        // A body that wedges must not outlive the run: Execute gives up waiting, and a foreground
        // thread would keep the test host alive after it does.
        var thread = new Thread(() =>
        {
            try
            {
                if (_hostsApplication)
                {
                    var app = Application.Current as ProjectDashboard.App ?? new ProjectDashboard.App();
                    app.InitializeComponent();
                    // The shipped mode shuts the Application down when the first window closes, and a
                    // window opened after that never lays out — so later bodies would read an empty
                    // visual tree rather than fail on what they are checking.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                }
            }
            catch (Exception ex) { startupError = ex; }
            finally { ready.Set(); }

            foreach (var item in _queue.GetConsumingEnumerable())
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
