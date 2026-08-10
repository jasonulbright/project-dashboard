using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class SafetyPage
{
    public SafetyPage(SafetyViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Page and view model are singletons, and the free tier is derived from a project list
        // every refresh replaces. Recomputing on navigation is what keeps a page revisited after a
        // scan from showing the portfolio as it stood when the page was first built.
        Loaded += (_, _) => viewModel.Rebuild();
    }
}
