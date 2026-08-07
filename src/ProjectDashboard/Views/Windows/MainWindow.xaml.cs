using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ProjectDashboard.Models;
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

        ShortcutGroups.ItemsSource = ShortcutTable.Groups;

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
            if (e.Key == Key.Escape)
            {
                if (ShortcutOverlay.Visibility == Visibility.Visible)
                {
                    CloseShortcuts();
                    e.Handled = true;
                    return;
                }
                if (PaletteOverlay.Visibility == Visibility.Visible)
                {
                    ClosePalette();
                    e.Handled = true;
                    return;
                }
            }

            var inTextBox = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                         or System.Windows.Controls.PasswordBox;

            // Shift+/ is a literal "?" everywhere except inside a text field, where it
            // is a character the user is typing.
            if (e.Key == Key.OemQuestion && (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                && !inTextBox && PaletteOverlay.Visibility != Visibility.Visible)
            {
                ToggleShortcuts();
                e.Handled = true;
                return;
            }

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
        // -1/-1 is the never-saved default; geometry cannot distinguish it from
        // a real position, so it short-circuits to OS placement, not the clamp.
        if (!(settings.WindowLeft == -1 && settings.WindowTop == -1))
        {
            var position = ClampToVirtualScreen(
                settings.WindowLeft, settings.WindowTop, settings.WindowWidth, settings.WindowHeight,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (position.HasValue)
            {
                Left = position.Value.Left;
                Top = position.Value.Top;
            }
        }
        // Assigning a non-finite or non-positive length throws in WPF; a damaged
        // settings file must not take the window down at startup.
        if (double.IsFinite(settings.WindowWidth) && settings.WindowWidth > 0)
            Width = settings.WindowWidth;
        if (double.IsFinite(settings.WindowHeight) && settings.WindowHeight > 0)
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

    /// <summary>
    /// Placement policy for a saved window position. Multi-monitor coordinates
    /// are signed — a monitor left of or above the primary yields valid negative
    /// positions, so no sign check can distinguish "off-screen". A position is
    /// kept verbatim when at least 100×50 px of the window rect lies inside the
    /// virtual screen (enough to grab with the mouse); otherwise each axis
    /// clamps to the nearest value restoring that minimum, so a position saved
    /// on a since-undocked monitor lands back in view. Null (non-finite input)
    /// means do not restore; the caller keeps default placement.
    /// </summary>
    public static (double Left, double Top)? ClampToVirtualScreen(
        double left, double top, double width, double height,
        double screenLeft, double screenTop, double screenWidth, double screenHeight)
    {
        const double minVisibleWidth = 100;
        const double minVisibleHeight = 50;

        if (!double.IsFinite(left) || !double.IsFinite(top))
            return null;
        // A degenerate size still yields a usable clamp: treat the window as
        // minimum-visibility sized, which forces it fully into view.
        if (!double.IsFinite(width) || width < minVisibleWidth) width = minVisibleWidth;
        if (!double.IsFinite(height) || height < minVisibleHeight) height = minVisibleHeight;

        return (
            ClampAxis(left, width, screenLeft, screenWidth, minVisibleWidth),
            ClampAxis(top, height, screenTop, screenHeight, minVisibleHeight));
    }

    /// <summary>
    /// Positions already showing at least minVisible on the axis lie inside
    /// [min, max] and pass through unchanged; anything else moves to the nearer
    /// bound. A virtual screen narrower than minVisible degenerates to its start.
    /// </summary>
    private static double ClampAxis(double position, double size, double screenStart, double screenExtent, double minVisible)
    {
        var min = screenStart + minVisible - size;
        var max = screenStart + screenExtent - minVisible;
        return min <= max ? Math.Clamp(position, min, max) : screenStart;
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

        dashVm.NavigateToProjectTabRequested += (project, tab) =>
            Dispatcher.Invoke(() => NavigateToProjectTab(project, tab));

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

    // Sidebar project items pooled by repo path and mutated in place. The navigation
    // dictionaries key by per-instance item Id and only ever add entries, so fresh
    // instances each refresh grow them without bound (each stale entry pins a full
    // ProjectInfo graph), while removing stale entries breaks journal resolution for
    // GoBack targets. Stable instances keep the dictionaries bounded by distinct
    // projects seen, not by refresh count.
    private readonly Dictionary<string, NavigationViewItem> _projectNavPool = new(StringComparer.OrdinalIgnoreCase);

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

        // Local projects only: a remote-only card has no repo path, so the detail
        // page this item targets would save its manifest under an empty key and
        // the edits vanish on restart. Cloud repos clone via the card or palette.
        foreach (var project in dashVm.Projects.Where(p => !p.IsRemoteOnly).OrderBy(p => p.DisplayName))
        {
            if (!_projectNavPool.TryGetValue(project.FullPath, out var navItem))
            {
                navItem = new NavigationViewItem { TargetPageType = typeof(ProjectDetailPage) };

                // TargetPageType navigates AND selects this item (blue indicator + parent
                // Projects highlight). Cache is Disabled, so a fresh ProjectDetailPage loads
                // and reads SelectedProject. Tag is read at click time — the pooled item
                // outlives any single ProjectInfo instance, so a captured one goes stale.
                navItem.Click += (s, _) =>
                {
                    if (s is NavigationViewItem { Tag: Models.ProjectInfo p })
                        DashboardViewModel.SelectedProject = p;
                };
                _projectNavPool[project.FullPath] = navItem;
            }

            navItem.Content = project.DisplayName;
            // Status glyph matches the card language (shape only — color in the nav is reserved
            // for selection): cloud-off (no remote) / edit (dirty) / check (synced).
            navItem.Icon = new SymbolIcon(
                string.IsNullOrEmpty(project.GitStatus.RemoteUrl) ? SymbolRegular.CloudOff24
                : project.GitStatus.IsDirty ? SymbolRegular.Edit24
                : SymbolRegular.CheckmarkCircle24);
            navItem.Tag = project;

            projectsParent.MenuItems.Add(navItem);
        }

        // Register the nested items with the navigation dictionaries — without
        // this, GoBack() to a project entry throws KeyNotFoundException (the library
        // only rebuilds its lookup when the ROOT collection changes). Pooled items
        // re-register as no-ops, so the call stays idempotent across refreshes.
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

    private const string ActionsGroup = "Actions";
    private const string ProjectsGroup = "Projects";
    private const string VerbsGroup = "Project actions";
    private const string SearchGroup = "In files";

    private static int GroupRank(string group) => group switch
    {
        ProjectsGroup => 0,
        VerbsGroup => 1,
        SearchGroup => 2,
        _ => 3,
    };

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
        CancelRepoSearch();
        // Keyboard focus stays on the now-hidden search box otherwise, and every
        // single-key gesture (? for the cheat sheet, Backspace for back) is then read
        // as typing into a text field and swallowed.
        RootNavigation.Focus();
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
                Group = ActionsGroup,
                Bias = 20,
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
        Action("Keyboard shortcuts", "help hotkeys cheat sheet bindings", SymbolRegular.Keyboard24,
            ToggleShortcuts);
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
            var haystack = (proj.DisplayName + " " + proj.DirectoryName + " " + proj.Manifest.Category).ToLowerInvariant();
            items.Add(new Models.PaletteItem
            {
                Title = proj.DisplayName,
                Subtitle = proj.IsRemoteOnly ? "Cloud repo" : "Project",
                Icon = proj.IsRemoteOnly ? SymbolRegular.CloudArrowDown24 : SymbolRegular.Folder24,
                Group = ProjectsGroup,
                // The jump outranks that project's own verbs, which match the same text.
                Bias = 40,
                SearchText = haystack,
                Invoke = () => vm.OpenProjectCommand.Execute(proj)
            });

            // Verbs need a working tree; a cloud card has none until it is cloned.
            if (proj.IsRemoteOnly || proj.FullPath.Length == 0) continue;

            void Verb(string title, string keywords, SymbolRegular icon, System.Action run) =>
                items.Add(new Models.PaletteItem
                {
                    Title = title,
                    Subtitle = proj.DirectoryName,
                    Icon = icon,
                    Group = VerbsGroup,
                    AllowFuzzy = false,
                    SearchText = (title + " " + haystack + " " + keywords).ToLowerInvariant(),
                    Invoke = run
                });

            Verb($"Fetch {proj.DisplayName}", "git remote refs", SymbolRegular.ArrowDownload24,
                () => vm.FetchProjectCommand.Execute(proj));
            Verb($"Pull {proj.DisplayName}", "git fast-forward merge", SymbolRegular.ArrowSync24,
                () => vm.PullProjectCommand.Execute(proj));
            Verb($"Push {proj.DisplayName}", "git upload publish", SymbolRegular.ArrowUpload24,
                () => vm.PushProjectCommand.Execute(proj));
            Verb($"Open folder — {proj.DisplayName}", "explorer directory reveal", SymbolRegular.FolderOpen24,
                () => vm.OpenFolderCommand.Execute(proj));
            Verb($"Open terminal — {proj.DisplayName}", "shell console prompt", SymbolRegular.Window24,
                () => vm.OpenTerminalCommand.Execute(proj));
            Verb($"Copy path — {proj.DisplayName}", "clipboard location", SymbolRegular.Copy24,
                () => vm.CopyPathCommand.Execute(proj));
            Verb($"Changes — {proj.DisplayName}", "diff staged uncommitted", SymbolRegular.Edit24,
                () => vm.OpenChangesTabCommand.Execute(proj));
        }

        return items;
    }

    private void ApplyPaletteFilter(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        var matches = _paletteItems
            .Select(item => (item, score: item.Score(q)))
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score + x.item.Bias)
            .ThenBy(x => x.item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.item)
            .Take(50)
            .ToList();

        // File hits carry git's own ordering and are appended whole; they are only
        // shown while they still describe the text in the box.
        if (_searchRows.Count > 0 && string.Equals(_searchRowsQuery, q, StringComparison.Ordinal))
            matches.AddRange(_searchRows);

        // Grouping renders groups in first-appearance order, so without a fixed rank
        // the section order changes with every query. OrderBy is stable, so each
        // group keeps its own relevance order.
        matches = [.. matches.OrderBy(item => GroupRank(item.Group))];

        var view = new ListCollectionView(matches);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Models.PaletteItem.Group)));
        PaletteList.ItemsSource = view;
        if (matches.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private void PaletteSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyPaletteFilter(PaletteSearch.Text);
        ScheduleRepoSearch();
    }

    // ── Global file search behind the palette (X-12) ─────────────────────────

    private CancellationTokenSource? _repoSearchCts;
    private DispatcherTimer? _repoSearchDebounce;
    private List<Models.PaletteItem> _searchRows = [];
    private string _searchRowsQuery = "";

    /// <summary>
    /// Drops any in-flight fan-out and its results. Every keystroke calls this: an
    /// abandoned search must stop spawning git rather than finish unseen and then
    /// overwrite the list the user is now looking at.
    /// </summary>
    private void CancelRepoSearch()
    {
        _repoSearchDebounce?.Stop();
        _repoSearchCts?.Cancel();
        _repoSearchCts = null;
        _searchRows = [];
        _searchRowsQuery = "";
    }

    private void ScheduleRepoSearch()
    {
        CancelRepoSearch();
        if (PaletteSearch.Text.Trim().Length < RepoSearchService.MinTermLength) return;

        _repoSearchDebounce ??= CreateSearchDebounce();
        _repoSearchDebounce.Start();
    }

    private DispatcherTimer CreateSearchDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RunRepoSearchAsync(PaletteSearch.Text.Trim().ToLowerInvariant());
        };
        return timer;
    }

    private async Task RunRepoSearchAsync(string term)
    {
        if (term.Length < RepoSearchService.MinTermLength) return;

        var cts = new CancellationTokenSource();
        _repoSearchCts = cts;
        try
        {
            var vm = _serviceProvider.GetRequiredService<DashboardViewModel>();
            var result = await vm.SearchAllReposAsync(term, cts.Token);

            // A newer keystroke replaced this fan-out while it ran; its rows are stale.
            if (!ReferenceEquals(_repoSearchCts, cts)) return;

            _searchRows = BuildSearchRows(vm, result);
            _searchRowsQuery = term;
            if (PaletteOverlay.Visibility == Visibility.Visible)
                ApplyPaletteFilter(PaletteSearch.Text);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("repository search failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_repoSearchCts, cts)) _repoSearchCts = null;
            cts.Dispose();
        }
    }

    private static List<Models.PaletteItem> BuildSearchRows(DashboardViewModel vm, RepoSearchResult result)
    {
        var rows = new List<Models.PaletteItem>();
        foreach (var hit in result.Hits)
        {
            var project = vm.FindByPath(hit.RepoPath);
            rows.Add(new Models.PaletteItem
            {
                Title = hit.IsFileNameMatch ? hit.FilePath : hit.Text,
                Subtitle = hit.Location,
                Icon = hit.IsFileNameMatch ? SymbolRegular.Document24 : SymbolRegular.Code24,
                Group = SearchGroup,
                Invoke = project is null ? () => { } : () => vm.OpenProjectCommand.Execute(project)
            });
        }

        if (result.More > 0)
        {
            rows.Add(new Models.PaletteItem
            {
                Title = $"{result.More} more matches — narrow the search",
                Subtitle = $"{result.ReposSearched} repos searched",
                Icon = SymbolRegular.MoreHorizontal24,
                Group = SearchGroup
            });
        }

        return rows;
    }

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

    // ── Shortcut cheat sheet (X-13) ──────────────────────────────────────────

    private void ToggleShortcuts()
    {
        if (ShortcutOverlay.Visibility == Visibility.Visible) { CloseShortcuts(); return; }
        ClosePalette();
        ShortcutOverlay.Visibility = Visibility.Visible;
        // Focus lands inside the overlay so Tab and Esc address it, not the page behind.
        ShortcutCloseButton.Focus();
    }

    private void CloseShortcuts() => ShortcutOverlay.Visibility = Visibility.Collapsed;

    private void ShortcutClose_Click(object sender, RoutedEventArgs e) => CloseShortcuts();

    // ── Detail-tab deep links (X-11) ─────────────────────────────────────────

    private void NavigateToProjectTab(Models.ProjectInfo project, DetailTab tab)
    {
        NavigateToProject(project);
        TrySelectDetailTab(tab, attemptsLeft: 8);
    }

    /// <summary>
    /// The detail page publishes no tab-selection API, so the shell selects the tab
    /// through the visual tree. Navigate() does not synchronously attach the page's
    /// content, and the attempt count is what covers that: a single dispatcher pass
    /// silently lands on the previous page.
    /// </summary>
    private void TrySelectDetailTab(DetailTab tab, int attemptsLeft)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (SelectDetailTab(tab)) return;
            if (attemptsLeft > 1) TrySelectDetailTab(tab, attemptsLeft - 1);
            else Log.Warn($"could not select detail tab {tab}: no visible tab host found");
        }));
    }

    private bool SelectDetailTab(DetailTab tab)
    {
        foreach (var control in FindVisualChildren<System.Windows.Controls.TabControl>(RootNavigation))
        {
            if (!control.IsVisible) continue;
            foreach (var item in control.Items)
            {
                // The DetailTab tag identifies the detail page's work-area host; any
                // other TabControl in the tree carries different tags.
                if (item is System.Windows.Controls.TabItem { Tag: DetailTab tag } tabItem && tag == tab)
                {
                    control.SelectedItem = tabItem;
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
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
