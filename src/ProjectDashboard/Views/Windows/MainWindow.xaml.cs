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

        WireTopNav();

        // Arrow-key navigation within the nav pane. WPF-UI's Left pane doesn't provide it
        // (each item is a ButtonBase tab stop), so move focus up/down between items ourselves.
        RootNavigation.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is not NavigationViewItem navItem)
                return;
            if (e.Key == Key.Down)
            {
                navItem.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                navItem.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                e.Handled = true;
            }
        };

        // Mouse back button (XButton1) navigates back
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1)
            {
                RootNavigation.GoBack();
                e.Handled = true;
            }
        };

        // Keyboard back navigation: Alt+Left, BrowserBack, or Backspace outside a
        // text field. Without these, going back was mouse-only (XButton1 / on-screen
        // back button) — a dead end for keyboard-only use. Ctrl+K opens the palette.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                TogglePalette();
                e.Handled = true;
                return;
            }
            if (PaletteOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
            {
                ClosePalette();
                e.Handled = true;
                return;
            }

            var inTextBox = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                         or System.Windows.Controls.PasswordBox;
            var altLeft = e.Key == Key.System && e.SystemKey == Key.Left
                       && (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            var back = e.Key == Key.BrowserBack || (e.Key == Key.Back && !inTextBox);
            if (altLeft || back)
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
        RootNavigation.IsPaneOpen = settings.PaneOpen;

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
            s.PaneOpen = RootNavigation.IsPaneOpen;
            settingsService.Save(s);
        };

        WireSidebarProjects();
    }

    private void WireSidebarProjects()
    {
        var dashVm = _serviceProvider.GetRequiredService<DashboardViewModel>();

        // Every refresh REPLACES the Projects collection, so listen for the property
        // change (a CollectionChanged subscription would orphan on the first refresh
        // and the sidebar would never update again).
        dashVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.Projects))
                Dispatcher.Invoke(() => RefreshSidebarProjects(dashVm));
        };

        // Card / palette "open project" selects that project's sidebar item, so
        // navigation lands on the right project rather than the first of its type.
        dashVm.NavigateToProjectRequested += project =>
            Dispatcher.Invoke(() => NavigateToProject(project));

        // The initial load may already have finished before this subscription.
        RefreshSidebarProjects(dashVm);
    }

    private void NavigateToProject(Models.ProjectInfo project)
    {
        DashboardViewModel.SelectedProject = project;

        var item = FindProjectNavItem(project);
        if (item is not null)
            RootNavigation.Navigate(item.Id); // selects THIS item; handler sets the same project
        else
            RootNavigation.Navigate(typeof(ProjectDetailPage)); // not in sidebar (filtered out) — fall back
    }

    private NavigationViewItem? FindProjectNavItem(Models.ProjectInfo project)
    {
        foreach (var item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem { Content: "Projects" } parent)
                foreach (var child in parent.MenuItems)
                    if (child is NavigationViewItem nvi && ReferenceEquals(nvi.Tag, project))
                        return nvi;
        }
        return null;
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
            var proj = project;
            // Status glyph matches the card language (shape only — color in the nav is reserved
            // for selection): cloud-off (no remote) / edit (dirty) / check (synced).
            var statusIcon = new SymbolIcon(
                string.IsNullOrEmpty(project.GitStatus.RemoteUrl) ? SymbolRegular.CloudOff24
                : project.GitStatus.IsDirty ? SymbolRegular.Edit24
                : SymbolRegular.CheckmarkCircle24);

            var navItem = new NavigationViewItem
            {
                Content = project.DisplayName,
                Icon = statusIcon,
                Tag = project,
                TargetPageType = typeof(ProjectDetailPage)
            };

            // TargetPageType navigates AND selects this item (blue indicator + parent Projects
            // highlight). Cache is Disabled, so a fresh ProjectDetailPage loads and reads
            // SelectedProject, which we set here before the page loads.
            navItem.Click += (_, _) => DashboardViewModel.SelectedProject = proj;

            projectsParent.MenuItems.Add(navItem);
        }

        // Register the new nested items with the navigation dictionaries — without
        // this, GoBack() to a project entry throws KeyNotFoundException (the library
        // only rebuilds its lookup when the ROOT collection changes).
        RootNavigation.RegisterDynamicMenuItems();
        // Hidden / Private / Public / Dashboard are handled in OnNavigationSelectionChanged —
        // wiring them here would re-add a handler on every sidebar refresh (a leak).
    }

    /// <summary>
    /// Wire the static top-level items via Click. Click fires reliably; SelectionChanged does NOT
    /// when navigating between items that all target DashboardPage. Called once — no per-refresh
    /// re-wiring (which previously stacked handlers on every sidebar refresh).
    /// </summary>
    private void WireTopNav()
    {
        foreach (var item in RootNavigation.MenuItems)
        {
            if (item is not NavigationViewItem nvi) continue;
            var tag = nvi.Tag?.ToString();
            if (tag is not ("FilterAll" or "FilterPublic" or "FilterPrivate" or "FilterNonLocal" or "HiddenProjects")) continue;

            nvi.Click += (_, _) =>
            {
                // Only set the filter. The item's TargetPageType navigates to DashboardPage AND
                // selects THIS item (the blue indicator follows the click). Do NOT Navigate by
                // page type here — that resolves to the first DashboardPage item (Dashboard) and
                // steals the selection.
                var vm = _serviceProvider.GetRequiredService<DashboardViewModel>();
                if (tag == "HiddenProjects")
                    vm.FilterHiddenCommand.Execute(null);
                else
                    vm.SetFilterCommand.Execute(tag switch
                    {
                        "FilterPublic" => "public",
                        "FilterPrivate" => "private",
                        "FilterNonLocal" => "nonlocal",
                        _ => "all"
                    });
            };
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, RoutedEventArgs e)
    {
        // When a project item is selected, set which project the detail page reads. The item's
        // TargetPageType does the navigation + selection highlight (fresh page, cache Disabled).
        if (sender.SelectedItem is NavigationViewItem selected && selected.Tag is Models.ProjectInfo proj)
            DashboardViewModel.SelectedProject = proj;
    }

    // ── Command palette (Ctrl+K) ─────────────────────────────────────────────

    private List<Models.PaletteItem> _paletteItems = [];

    private void TogglePalette()
    {
        if (PaletteOverlay.Visibility == Visibility.Visible) { ClosePalette(); return; }

        _paletteItems = BuildPaletteItems();
        PaletteSearch.Text = "";
        ApplyPaletteFilter("");
        PaletteOverlay.Visibility = Visibility.Visible;
        PaletteSearch.Focus();
    }

    private void ClosePalette()
    {
        PaletteOverlay.Visibility = Visibility.Collapsed;
    }

    private List<Models.PaletteItem> BuildPaletteItems()
    {
        var vm = _serviceProvider.GetRequiredService<DashboardViewModel>();
        var items = new List<Models.PaletteItem>();

        // Global actions first (stable order; matched by keyword).
        void Action(string title, string keywords, SymbolRegular icon, System.Action run) =>
            items.Add(new Models.PaletteItem
            {
                Title = title,
                Subtitle = "Action",
                Icon = icon,
                SearchText = (title + " " + keywords).ToLowerInvariant(),
                Invoke = run
            });

        Action("Refresh all", "reload sync scan", SymbolRegular.ArrowSync24,
            () => vm.ForceRefreshCommand.Execute(null));
        Action("New project", "create add", SymbolRegular.Add24,
            () => vm.NewProjectCommand.Execute(null));
        Action("Clone repository", "git download get", SymbolRegular.CloudArrowDown24,
            () => vm.CloneRepoCommand.Execute(null));
        Action("Sync all clean repos", "fetch pull push bulk", SymbolRegular.ArrowSyncCircle24,
            () => vm.SyncAllCommand.Execute(null));
        Action("Open Settings", "preferences config theme gh", SymbolRegular.Settings24,
            () => RootNavigation.Navigate(typeof(SettingsPage)));
        Action("Dashboard: all projects", "home filter", SymbolRegular.Home24,
            () => { RootNavigation.Navigate(typeof(DashboardPage)); vm.SetFilterCommand.Execute("all"); });
        Action("Filter: dirty", "uncommitted changes", SymbolRegular.Edit24,
            () => { RootNavigation.Navigate(typeof(DashboardPage)); vm.SetFilterCommand.Execute("dirty"); });
        Action("Filter: public", "visibility", SymbolRegular.Globe24,
            () => { RootNavigation.Navigate(typeof(DashboardPage)); vm.SetFilterCommand.Execute("public"); });
        Action("Filter: private", "visibility", SymbolRegular.LockClosed24,
            () => { RootNavigation.Navigate(typeof(DashboardPage)); vm.SetFilterCommand.Execute("private"); });

        // Then every project — jump straight to its detail (or clone if remote-only).
        foreach (var p in vm.Projects.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var proj = p;
            items.Add(new Models.PaletteItem
            {
                Title = proj.DisplayName,
                Subtitle = proj.IsRemoteOnly ? "Cloud repo" : "Project",
                Icon = proj.IsRemoteOnly ? SymbolRegular.CloudArrowDown24 : SymbolRegular.Folder24,
                SearchText = (proj.DisplayName + " " + proj.DirectoryName + " " + proj.Manifest.Category).ToLowerInvariant(),
                Invoke = () => vm.OpenProjectCommand.Execute(proj)
            });
        }

        return items;
    }

    private void ApplyPaletteFilter(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        var matches = _paletteItems
            .Select(item => (item, score: item.Score(q)))
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.item)
            .Take(50)
            .ToList();

        PaletteList.ItemsSource = matches;
        if (matches.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private void PaletteSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyPaletteFilter(PaletteSearch.Text);

    private void PaletteSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MovePaletteSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                InvokeSelectedPaletteItem();
                e.Handled = true;
                break;
        }
    }

    private void MovePaletteSelection(int delta)
    {
        var count = PaletteList.Items.Count;
        if (count == 0) return;
        var next = PaletteList.SelectedIndex + delta;
        PaletteList.SelectedIndex = Math.Clamp(next, 0, count - 1);
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void InvokeSelectedPaletteItem()
    {
        if (PaletteList.SelectedItem is Models.PaletteItem item)
        {
            ClosePalette();
            // Let the overlay collapse before the action navigates/opens a dialog.
            Dispatcher.BeginInvoke(item.Invoke);
        }
    }

    private void PaletteItem_Invoke(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => InvokeSelectedPaletteItem();

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
