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
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Prevent unhandled exceptions from crashing the app
        DispatcherUnhandledException += (_, args) =>
        {
            ProjectDashboard.Services.Log.Error("Unhandled dispatcher exception", args.Exception);
            System.Windows.MessageBox.Show(
                $"Error: {args.Exception.Message}\n\n{args.Exception.StackTrace?[..Math.Min(500, args.Exception.StackTrace?.Length ?? 0)]}",
                "Project Dashboard Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            args.Handled = true;
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

        _host = Host.CreateDefaultBuilder()
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
                services.AddSingleton<ProjectDiscoveryService>();
                services.AddSingleton<ProjectWatcherService>();

                // Safety rails: shared singletons for the destructive stages.
                services.AddSingleton<Services.Safety.RepoBusyRegistry>();
                services.AddSingleton<Services.Safety.RewriteJournal>();
                services.AddSingleton<Services.Safety.BackupService>();
                services.AddSingleton<Services.Safety.RewriteRecoveryService>();

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
                    sp.GetRequiredService<Services.Safety.RewriteJournal>()));
                services.AddSingleton<IRewriteSessionFactory>(sp =>
                    new CoordinatorRewriteSessionFactory(sp.GetRequiredService<Services.Rewrite.RewriteCoordinator>()));

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
                    sp.GetRequiredService<Services.Safety.RepoBusyRegistry>())
                {
                    Surgery = sp.GetRequiredService<SurgeryCoordinator>()
                });
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<SettingsViewModel>();

                // Hosted services run in registration order: crash-recovery detection must
                // complete before ApplicationHostService shows the interactive window.
                services.AddHostedService(sp => sp.GetRequiredService<Services.Safety.RewriteRecoveryService>());
                services.AddHostedService<ApplicationHostService>();
            })
            .Build();

        // ApplicationHostService shows the window and navigates to the dashboard.
        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
