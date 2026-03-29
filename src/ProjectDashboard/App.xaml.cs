using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectDashboard.Services;
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
            System.Windows.MessageBox.Show(
                $"Error: {args.Exception.Message}\n\n{args.Exception.StackTrace?[..Math.Min(500, args.Exception.StackTrace?.Length ?? 0)]}",
                "Project Dashboard Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            args.Handled = true;
        };

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
                services.AddSingleton<GitService>();
                services.AddSingleton<GitHubService>();
                services.AddSingleton<ProjectDiscoveryService>();

                // Windows
                services.AddSingleton<MainWindow>();
                services.AddSingleton<MainWindowViewModel>();

                // Pages
                services.AddSingleton<DashboardPage>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<ProjectDetailPage>();
                services.AddSingleton<ProjectDetailViewModel>();
                services.AddSingleton<SettingsPage>();
                services.AddSingleton<SettingsViewModel>();

                // Hosted service
                services.AddHostedService<ApplicationHostService>();
            })
            .Build();

        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
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
