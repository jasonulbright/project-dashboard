using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class SettingsPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Page and VM are singletons: the constructor-time snapshot goes stale
        // when another writer touches settings.json, and Save would then revert
        // those writes. Loaded fires on every navigation to the page.
        Loaded += (_, _) => viewModel.LoadSettings();
    }
}
