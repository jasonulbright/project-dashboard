using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class ProjectDetailPage
{
    private readonly ProjectDetailViewModel _viewModel;

    public ProjectDetailPage(ProjectDetailViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        var project = DashboardViewModel.SelectedProject;
        if (project is not null)
        {
            _viewModel.SetProject(project);
        }
    }
}
