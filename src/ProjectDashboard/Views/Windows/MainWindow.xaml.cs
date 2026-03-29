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
                // VisualTreeHelper.GetParent throws on non-Visual elements (Run, Span, etc.)
                // Use LogicalTreeHelper as fallback for document elements
                try
                {
                    element = element is System.Windows.Media.Visual
                        ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                        : LogicalTreeHelper.GetParent(element);
                }
                catch { break; }
            }
        };
    }

    private void FluentWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Restore window state
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        var settings = settingsService.Load();
        if (settings.WindowLeft >= 0 && settings.WindowTop >= 0)
        {
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;

        // Save on close
        Closing += (_, _) =>
        {
            var s = settingsService.Load();
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            settingsService.Save(s);
        };

        _ = PopulateSidebarWhenReady();
    }

    private async Task PopulateSidebarWhenReady()
    {
        var dashVm = _serviceProvider.GetRequiredService<DashboardViewModel>();

        // Wait for initial load
        for (int i = 0; i < 60; i++)
        {
            if (dashVm.Projects.Count > 0) break;
            await Task.Delay(500);
        }

        RefreshSidebarProjects(dashVm);

        // Re-populate when projects collection changes (after refresh)
        dashVm.Projects.CollectionChanged += (_, _) =>
        {
            Dispatcher.Invoke(() => RefreshSidebarProjects(dashVm));
        };
    }

    private void RefreshSidebarProjects(DashboardViewModel dashVm)
    {
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
            var navItem = new NavigationViewItem
            {
                Content = project.DisplayName,
                Icon = new SymbolIcon(project.GitStatus.IsDirty ? SymbolRegular.CircleHalfFill24 : SymbolRegular.CheckmarkCircle24),
                Tag = project,
                TargetPageType = typeof(ProjectDetailPage)
            };

            projectsParent.MenuItems.Add(navItem);
        }
    }

    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.NavigationView navigationView)
            return;

        if (navigationView.SelectedItem is NavigationViewItem selected && selected.Tag is Models.ProjectInfo proj)
        {
            DashboardViewModel.SelectedProject = proj;
        }
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
