using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class DashboardPage
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
