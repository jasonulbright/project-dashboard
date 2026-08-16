using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class AlertsPage
{
    public AlertsPage(AlertsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Page and view model are singletons over a project list every scan replaces. Each
        // navigation rebuilds the rows from the cache — on screen at once, no request made.
        // Asking GitHub is the Refresh button's job, and cancelling it is the reader's.
        Loaded += (_, _) => viewModel.Open();
    }
}
