using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class AlertsPage
{
    public AlertsPage(AlertsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Page and view model are singletons over a project list every scan replaces. Each
        // navigation rebuilds the rows from the cache — on screen at once — and starts one
        // conditional pass; a revisit mid-pass keeps the running one.
        Loaded += (_, _) => _ = viewModel.OpenAsync();
    }
}
