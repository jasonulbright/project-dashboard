using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.ViewModels.Windows;
using ProjectDashboard.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace ProjectDashboard.Views.Windows;

public partial class MainWindow : INavigationWindow
{
    private readonly INavigationService _navigationService;
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _navigationService = navigationService;
        _serviceProvider = serviceProvider;
        DataContext = viewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        RootNavigation.SetServiceProvider(serviceProvider);

        // Mouse back button (XButton1) navigates back
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1)
            {
                RootNavigation.GoBack();
                e.Handled = true;
            }
        };

        // Global mouse wheel fix — WPF-UI NavigationView swallows scroll events.
        // Tunnel the event to the nearest ScrollViewer in the visual tree.
        PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is System.Windows.Controls.ScrollViewer sv && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
                {
                    sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                    e.Handled = true;
                    return;
                }
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }
        };
    }

    private void FluentWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // If screen is 1080p or smaller, maximize
        if (SystemParameters.PrimaryScreenHeight <= 1080)
        {
            WindowState = WindowState.Maximized;
        }

        // Populate sidebar after DashboardViewModel finishes loading (delayed check)
        _ = PopulateSidebarWhenReady();
    }

    private async Task PopulateSidebarWhenReady()
    {
        // Wait for DashboardViewModel to finish loading projects
        var dashVm = _serviceProvider.GetRequiredService<DashboardViewModel>();
        for (int i = 0; i < 60; i++) // up to 30 seconds
        {
            if (dashVm.Projects.Count > 0) break;
            await Task.Delay(500);
        }

        // Find the "Projects" parent NavigationViewItem
        NavigationViewItem? projectsParent = null;
        foreach (var item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem nvi && nvi.Content?.ToString() == "Projects")
            {
                projectsParent = nvi;
                break;
            }
        }

        if (projectsParent is null) return;

        projectsParent.MenuItems.Clear();

        foreach (var project in dashVm.Projects.OrderBy(p => p.DisplayName))
        {
            var item = new NavigationViewItem
            {
                Content = project.DisplayName,
                Icon = new SymbolIcon(project.GitStatus.IsDirty ? SymbolRegular.CircleHalfFill24 : SymbolRegular.CheckmarkCircle24),
                Tag = project,
                TargetPageType = typeof(ProjectDetailPage)
            };

            projectsParent.MenuItems.Add(item);
        }

        // Handle sidebar project clicks via SelectionChanged
        RootNavigation.SelectionChanged += (_, _) =>
        {
            if (RootNavigation.SelectedItem is NavigationViewItem selected && selected.Tag is Models.ProjectInfo proj)
            {
                DashboardViewModel.SelectedProject = proj;
                _navigationService.Navigate(typeof(ProjectDetailPage));
            }
        };
    }

    public INavigationView GetNavigation() => RootNavigation;

    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    public void SetPageService(INavigationViewPageProvider pageService)
    {
        RootNavigation.SetPageProviderService(pageService);
    }

    public void ShowWindow() => Show();

    public void CloseWindow() => Close();

    public void SetServiceProvider(IServiceProvider serviceProvider)
    {
        RootNavigation.SetServiceProvider(serviceProvider);
    }
}
