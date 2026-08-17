using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.ViewModels.Windows;
using ProjectDashboard.Views.Pages;
using ProjectDashboard.Views.Windows;
using System.Windows;
using Wpf.Ui.DependencyInjection;

namespace ProjectDashboard;

public partial class App : Application
{
    /// <summary>
    /// Which failures the process can survive. ShutdownMode is OnMainWindowClose and the main
    /// window is shown by a hosted service, so a failure before that window exists leaves a live
    /// process with no window: nothing to close, nothing to report into, and no exit path. Such a
    /// failure ends the process; a failure after it is reported into the running window instead.
    /// </summary>
    internal sealed class StartupGuard
    {
        private bool _complete;
        private bool _exiting;

        public void MarkComplete() => _complete = true;

        public bool IsFatal => !_complete;

        /// <summary>True for the first caller only, so one failure does not start two exits.</summary>
        public bool TryBeginExit()
        {
            if (_exiting) return false;
            _exiting = true;
            return true;
        }
    }

    private const int StartupFailureExitCode = 1;

    private readonly StartupGuard _startup = new();
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (_startup.IsFatal)
            {
                FailStartup(args.Exception);
                return;
            }

            ProjectDashboard.Services.Log.Error("Unhandled dispatcher exception", args.Exception);
            System.Windows.MessageBox.Show(
                $"Error: {args.Exception.Message}\n\n{args.Exception.StackTrace?[..Math.Min(500, args.Exception.StackTrace?.Length ?? 0)]}",
                "Project Dashboard Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        };

        // Background-task and non-dispatcher failures never reach the handler above —
        // without these they vanish (or kill the process) with no log entry.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ProjectDashboard.Services.Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ProjectDashboard.Services.Log.Error(
                $"Unhandled domain exception (terminating={args.IsTerminating})",
                args.ExceptionObject as Exception);

        if (AppPaths.StartupNotice is { } notice)
        {
            ProjectDashboard.Services.Log.Warn(notice);
            System.Windows.MessageBox.Show(
                notice,
                "Project Dashboard",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        try
        {
            _host = BuildHost();
            // ApplicationHostService shows the window and navigates to the dashboard.
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            FailStartup(ex);
            return;
        }

        _startup.MarkComplete();
    }

    /// <summary>
    /// Reports a failure the process cannot run past and ends it with a non-zero exit code.
    /// Handled-and-continue is not available here: the window that would carry the report does
    /// not exist, and the shutdown mode that would end the process waits on that window.
    /// </summary>
    private void FailStartup(Exception ex)
    {
        if (!_startup.TryBeginExit()) return;

        ProjectDashboard.Services.Log.Error("Startup failed", ex);
        System.Windows.MessageBox.Show(
            $"Project Dashboard could not start.\n\n{ex.Message}",
            "Project Dashboard",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        Shutdown(StartupFailureExitCode);
    }

    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // WPF-UI page provider (resolves pages from DI for NavigationView)
                services.AddNavigationViewPageProvider();

                // Services
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<ISnackbarService, SnackbarService>();
                services.AddSingleton<IContentDialogService, ContentDialogService>();
                services.AddSingleton<SettingsService>();
                services.AddSingleton<ManifestStore>();
                services.AddSingleton<GitService>();
                services.AddSingleton<GitHubService>();
                // Explicit factory: the scan skips reading a repository under a lease, and that
                // rule must not rest on the container guessing an optional parameter.
                services.AddSingleton(sp => new ProjectDiscoveryService(
                    sp.GetRequiredService<GitService>(),
                    sp.GetRequiredService<GitHubService>(),
                    sp.GetRequiredService<SettingsService>(),
                    sp.GetRequiredService<ManifestStore>(),
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>()));
                services.AddSingleton<ProjectWatcherService>();
                services.AddSingleton<ProjectTemplateService>();
                services.AddSingleton<SubmoduleService>();

                // Safety rails: shared singletons for the destructive stages.
                services.AddSingleton<Services.Safety.RepoBusyRegistry>();
                services.AddSingleton<ScheduledFetchService>();
                services.AddSingleton<Services.Safety.RewriteJournal>();
                // The durable record of what was attempted, separate from the journal's
                // "what is pending now". Every writer shares one instance so appends against
                // one repository serialize on one lock.
                services.AddSingleton<Services.Safety.OperationHistory>();
                services.AddSingleton<Services.Safety.BackupService>();
                services.AddSingleton<Services.Safety.RewriteRecoveryService>();

                // Reads one public release document and compares it with this build. Registered
                // as a singleton so the dashboard reads the same answer the check published.
                services.AddSingleton<Services.Update.UpdateCheckService>();

                // Rewrite engine. Singletons because the coordinator's collaborators cache
                // probes (git executable, settings) and the busy registry must be the one
                // registry every destructive surface contends on. Explicit factories because
                // both constructors carry optional parameters the container will not fill.
                services.AddSingleton(sp => new Services.Rewrite.SwapService(sp.GetRequiredService<GitService>()));
                services.AddSingleton(sp => new Services.Rewrite.RewriteCoordinator(
                    sp.GetRequiredService<Services.Safety.BackupService>(),
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>(),
                    sp.GetRequiredService<GitService>(),
                    sp.GetRequiredService<Services.Rewrite.SwapService>(),
                    sp.GetRequiredService<Services.Safety.RewriteJournal>(),
                    history: sp.GetRequiredService<Services.Safety.OperationHistory>(),
                    manifests: sp.GetRequiredService<ManifestStore>()));
                services.AddSingleton<IRewriteSessionFactory>(sp =>
                    new CoordinatorRewriteSessionFactory(sp.GetRequiredService<Services.Rewrite.RewriteCoordinator>()));

                // Publishing a rewrite and reclaiming what it replaced. Both contend for the same
                // repository lease as the rewrite itself, so both take the container's registry.
                services.AddSingleton(sp => new Services.Rewrite.ForcePushService(
                    sp.GetRequiredService<GitService>(),
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>(),
                    sp.GetRequiredService<Services.Safety.OperationHistory>()));
                services.AddSingleton(sp => new Services.Safety.DeepCleanService(
                    sp.GetRequiredService<GitService>(),
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>(),
                    sp.GetRequiredService<Services.Safety.RewriteJournal>(),
                    sp.GetRequiredService<Services.Safety.OperationHistory>()));

                // Commit surgery, over the rails registered above.
                services.AddCommitSurgery();

                // Windows
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainWindowViewModel>();

                // Pages
                services.AddSingleton<DashboardPage>();
                services.AddSingleton<DashboardViewModel>();
                services.AddTransient<ProjectDetailPage>();
                // The registry and session factory must be the container's own instances:
                // the constructor's null-fallbacks would give the page a private registry
                // that shares no state with the coordinators' leases.
                services.AddSingleton<ProjectDetailViewModel>(sp => new ProjectDetailViewModel(
                    sp.GetRequiredService<ProjectDiscoveryService>(),
                    sp.GetRequiredService<GitService>(),
                    sp.GetRequiredService<GitHubService>(),
                    sp.GetRequiredService<IRewriteSessionFactory>(),
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>(),
                    sp.GetRequiredService<SettingsService>(),
                    sp.GetRequiredService<Services.Safety.BackupService>(),
                    sp.GetRequiredService<Services.Safety.RewriteRecoveryService>(),
                    sp.GetRequiredService<Services.Rewrite.ForcePushService>(),
                    sp.GetRequiredService<Services.Safety.DeepCleanService>(),
                    sp.GetRequiredService<SubmoduleService>(),
                    sp.GetRequiredService<ProjectWatcherService>(),
                    history: sp.GetRequiredService<Services.Safety.OperationHistory>())
                {
                    Surgery = sp.GetRequiredService<SurgeryCoordinator>(),
                    Conflicts = sp.GetRequiredService<ConflictResolver>(),
                    // The container's own driver: whether a stopped rebase may be continued is
                    // decided from the scratch trees that driver keeps, and a private instance
                    // would answer from a root nothing writes to.
                    Rebase = sp.GetRequiredService<RebaseDriver>()
                });
                services.AddSingleton<SettingsPage>();
                // Explicit factory: the metadata section shares the container's own store, or a
                // record forgotten here would still be live in the index every scan reads.
                services.AddSingleton(sp => new SettingsViewModel(
                    sp.GetRequiredService<SettingsService>(),
                    sp.GetRequiredService<GitHubService>(),
                    sp.GetRequiredService<DashboardViewModel>(),
                    sp.GetRequiredService<Services.Update.UpdateCheckService>(),
                    sp.GetRequiredService<ManifestStore>(),
                    sp.GetRequiredService<ProjectDiscoveryService>(),
                    sp.GetRequiredService<Services.Safety.BackupService>(),
                    sp.GetRequiredService<ScheduledFetchService>()));

                // The rollup reads the dashboard's own project list rather than scanning again,
                // and shares the container's busy registry, backup store and ledger — a private
                // registry would let a portfolio check read a repository a rewrite is holding.
                services.AddSingleton<AlertsService>();
                services.AddSingleton<AlertsPage>();
                services.AddSingleton<AlertsViewModel>();
                services.AddSingleton<SafetyPage>();
                services.AddSingleton(sp => new SafetyViewModel(
                    sp.GetRequiredService<DashboardViewModel>(),
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>(),
                    sp.GetRequiredService<SettingsService>(),
                    sp.GetRequiredService<GitService>(),
                    sp.GetRequiredService<Services.Safety.BackupService>(),
                    sp.GetRequiredService<Services.Safety.RewriteRecoveryService>(),
                    sp.GetRequiredService<Services.Safety.OperationHistory>(),
                    sp.GetRequiredService<ProjectDiscoveryService>()));

                // Hosted services run in registration order: crash-recovery detection must
                // complete before ApplicationHostService shows the interactive window.
                services.AddHostedService(sp => sp.GetRequiredService<Services.Safety.RewriteRecoveryService>());
                services.AddHostedService<ApplicationHostService>();
                // Last: the update check is the opposite of crash recovery — it gates nothing,
                // so it starts once the window is already up and is cancelled at shutdown.
                services.AddHostedService(sp => sp.GetRequiredService<Services.Update.UpdateCheckService>());
            })
            .Build();

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        if (_host is not null)
        {
            // A host that failed to start still holds whatever its earlier hosted services
            // acquired, so the stop runs on that path too.
            try { await _host.StopAsync(); }
            catch (Exception ex) { ProjectDashboard.Services.Log.Warn("host stop failed", ex); }
            _host.Dispose();
        }
    }
}
