using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectDashboard.Views.Pages;
using ProjectDashboard.Views.Windows;

namespace ProjectDashboard.Services;

public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        var navigationWindow = mainWindow as INavigationWindow;

        navigationWindow?.ShowWindow();
        navigationWindow?.Navigate(typeof(DashboardPage));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
