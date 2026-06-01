using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class SettingsPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
