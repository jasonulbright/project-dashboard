using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectDashboard.Views.Pages;
using ProjectDashboard.Views.Windows;

namespace ProjectDashboard.Services;

public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Before any page reads the lists: a value only the stored records hold has to be in them
        // by the time a card decides whether to draw it as one the reader knows about.
        TaxonomyMigration.Run(
            serviceProvider.GetRequiredService<SettingsService>(),
            serviceProvider.GetRequiredService<ManifestStore>());

        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        var navigationWindow = mainWindow as INavigationWindow;

        navigationWindow?.ShowWindow();
        navigationWindow?.Navigate(typeof(DashboardPage));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
