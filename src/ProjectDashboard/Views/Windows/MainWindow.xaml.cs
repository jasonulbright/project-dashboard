using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        var settings = settingsService.Load();
        RestoreSavedPlacement(settings);
        RootNavigation.IsPaneOpen = settings.PaneOpen;

        Closing += (_, _) =>
        {
            var s = settingsService.Load();
            s.WindowMaximized = WindowState == WindowState.Maximized;
            // A maximized window's rect describes the monitor, not the placement to
            // come back to, so the stored one stands.
            if (WindowState == WindowState.Normal && DeviceRect() is { } rect)
                s.WindowDeviceRect = new SavedWindowRect(
                    (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);
            s.PaneOpen = RootNavigation.IsPaneOpen;
            settingsService.Save(s);
        };

        WireSidebarProjects();
    }

    // ── Saved-position restore ───────────────────────────────────────────────

    private const double MinVisibleWidth = 100;
    private const double MinVisibleHeight = 50;

    /// <summary>A screen rectangle in device pixels: a monitor work area or a window rect.</summary>
    public readonly record struct ScreenRect(double Left, double Top, double Width, double Height);

    /// <summary>Startup geometry: a rect already corrected onto a live monitor, and the state to apply after it.</summary>
    public readonly record struct RestoredPlacement(ScreenRect Rect, bool Maximized);

    /// <summary>
    /// Startup placement for the saved window state, in device pixels. The clamp runs
    /// whatever the saved state: a window that comes back maximized carries the saved
    /// rect as its restore bounds, Windows does not validate those against the current
    /// monitors, and an unvalidated one sends Restore to a monitor that may be gone.
    /// Applying the rect before the maximize is what puts the corrected rectangle
    /// there. <paramref name="current"/> — the window's own rect — supplies whatever
    /// the settings do not hold.
    /// </summary>
    public static RestoredPlacement SavedPlacement(
        AppSettings settings, double dpiScale, ScreenRect current, IReadOnlyList<ScreenRect> screens)
    {
        var rect = SavedRect(settings, dpiScale) ?? current;
        if (!double.IsFinite(rect.Width) || rect.Width <= 0) rect = rect with { Width = current.Width };
        if (!double.IsFinite(rect.Height) || rect.Height <= 0) rect = rect with { Height = current.Height };

        if (ClampToMonitors(rect.Left, rect.Top, rect.Width, rect.Height, screens) is { } placed)
            rect = rect with { Left = placed.Left, Top = placed.Top };

        return new RestoredPlacement(rect, settings.WindowMaximized);
    }

    /// <summary>
    /// The saved rect in device pixels, or null when nothing usable is stored. A rect
    /// carried in the legacy DIP fields was written in the closing monitor's scale,
    /// which is not recorded: the starting monitor's scale is the only reading
    /// available for it, and the clamp bounds how far that reading can be wrong.
    /// </summary>
    private static ScreenRect? SavedRect(AppSettings settings, double dpiScale)
    {
        if (settings.WindowDeviceRect is { } saved)
            return new ScreenRect(saved.Left, saved.Top, saved.Width, saved.Height);
        if (settings.WindowLeft == -1 && settings.WindowTop == -1) return null;
        if (!double.IsFinite(settings.WindowLeft) || !double.IsFinite(settings.WindowTop)) return null;

        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        return new ScreenRect(
            settings.WindowLeft * scale, settings.WindowTop * scale,
            settings.WindowWidth * scale, settings.WindowHeight * scale);
    }

    /// <summary>
    /// Placement policy for a saved window position, in device pixels against the
    /// rectangles of real monitors. Multi-monitor coordinates are signed — a monitor
    /// left of or above the primary yields valid negative positions, so no sign check
    /// can distinguish "off-screen". A position is kept verbatim when at least 100×50 px
    /// of the window rect lies inside SOME monitor (enough to grab with the mouse);
    /// otherwise it moves to whichever monitor costs the least movement to restore that
    /// minimum, so a position saved on a since-undocked monitor lands back in view.
    /// Validating against the monitors rather than their bounding box is what excludes
    /// the dead zones an L-shaped arrangement leaves inside that box. Null (non-finite
    /// position, or no monitors) means do not move; the caller keeps what it has.
    /// </summary>
    public static (double Left, double Top)? ClampToMonitors(
        double left, double top, double width, double height, IReadOnlyList<ScreenRect> screens)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || screens.Count == 0)
            return null;
        // A degenerate size still yields a usable clamp: treat the window as
        // minimum-visibility sized, which forces it fully into view.
        if (!double.IsFinite(width) || width < MinVisibleWidth) width = MinVisibleWidth;
        if (!double.IsFinite(height) || height < MinVisibleHeight) height = MinVisibleHeight;

        foreach (var screen in screens)
            if (Overlap(left, width, screen.Left, screen.Width) >= MinVisibleWidth
                && Overlap(top, height, screen.Top, screen.Height) >= MinVisibleHeight)
                return (left, top);

        (double Left, double Top)? nearest = null;
        var shortestMove = double.PositiveInfinity;
        foreach (var screen in screens)
        {
            var candidate = (
                Left: ClampAxis(left, width, screen.Left, screen.Width, MinVisibleWidth),
                Top: ClampAxis(top, height, screen.Top, screen.Height, MinVisibleHeight));
            var dx = candidate.Left - left;
            var dy = candidate.Top - top;
            var move = dx * dx + dy * dy;
            if (move < shortestMove) (shortestMove, nearest) = (move, candidate);
        }
        return nearest;
    }

    /// <summary>Length shared by [start, start+size] and the screen's span; negative when disjoint.</summary>
    private static double Overlap(double start, double size, double screenStart, double screenExtent) =>
        Math.Min(start + size, screenStart + screenExtent) - Math.Max(start, screenStart);

    /// <summary>
    /// Positions already showing at least minVisible on the axis lie inside
    /// [min, max] and pass through unchanged; anything else moves to the nearer
    /// bound. A screen narrower than minVisible degenerates to its start.
    /// </summary>
    private static double ClampAxis(double position, double size, double screenStart, double screenExtent, double minVisible)
    {
        var min = screenStart + minVisible - size;
        var max = screenStart + screenExtent - minVisible;
        return min <= max ? Math.Clamp(position, min, max) : screenStart;
    }

    /// <summary>
    /// Applies the saved placement through the HWND. Window.Left/Top are per-monitor
    /// DIPs under this app's PerMonitorV2 manifest, so a rect saved on one monitor and
    /// re-applied through them lands scaled by the ratio of the two monitors' DPIs —
    /// on a 200% primary beside a 100% secondary a window saved at device x=4000
    /// returns at 8000, off every screen. Save, restore and clamp all stay in device
    /// pixels here, so no scale enters any of them.
    /// </summary>
    private void RestoreSavedPlacement(AppSettings settings)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var current = DeviceRect();
        var placement = SavedPlacement(settings, VisualTreeHelper.GetDpi(this).DpiScaleX,
            current ?? new ScreenRect(0, 0, ActualWidth, ActualHeight), WorkAreas());

        if (current is { } rect && rect != placement.Rect)
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)placement.Rect.Left, (int)placement.Rect.Top,
                (int)placement.Rect.Width, (int)placement.Rect.Height,
                SwpNoZOrder | SwpNoActivate);

        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>The window's own rect in device pixels; null before its HWND exists.</summary>
    private ScreenRect? DeviceRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return null;
        return new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>
    /// Work area (taskbar excluded) of every attached monitor, in device pixels.
    /// Falls back to the virtual-screen bounding box when enumeration yields nothing,
    /// so a failure here is only as coarse as a bounding-box clamp, never a refusal
    /// to place the window at all.
    /// </summary>
    private static IReadOnlyList<ScreenRect> WorkAreas()
    {
        var screens = new List<ScreenRect>();
        MonitorEnumProc collect = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info))
                screens.Add(new ScreenRect(info.rcWork.Left, info.rcWork.Top,
                    info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        if (screens.Count > 0) return screens;

        return [new ScreenRect(
            GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen))];
    }

    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77,
                      SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr clip, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

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
            navItem.Icon = new SymbolIcon(StatusGlyph(project.GitStatus));
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
    /// Sidebar status glyph, in the card's shape language (shape only — color in the nav
    /// is reserved for selection). Uncommitted work outranks a missing remote: the dirty
    /// state is the one the reader can act on, and a repo can be both.
    /// </summary>
    public static SymbolRegular StatusGlyph(Models.GitStatus status) =>
        status.IsDirty ? SymbolRegular.Edit24
        : string.IsNullOrEmpty(status.RemoteUrl) ? SymbolRegular.CloudOff24
        : SymbolRegular.CheckmarkCircle24;

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
        // A fresh palette opens on its first row; the re-filter below otherwise carries
        // the previous session's highlight forward.
        PaletteList.SelectedItem = null;
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

        var selected = PaletteSelection.AfterRefilter(PaletteList.SelectedItem as Models.PaletteItem, matches);

        var view = new ListCollectionView(matches);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Models.PaletteItem.Group)));
        PaletteList.ItemsSource = view;
        PaletteList.SelectedItem = selected;
        if (selected is not null) PaletteList.ScrollIntoView(selected);
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

    private void CloseShortcuts()
    {
        ShortcutOverlay.Visibility = Visibility.Collapsed;
        // Focus stays on the now-hidden Close button otherwise, and the next Enter or
        // Space re-fires it.
        RootNavigation.Focus();
    }

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

/// <summary>
/// Which row a re-filtered palette lands on. The repository fan-out behind the palette
/// returns long after the keystroke that started it and rebuilds the list under a user
/// who may have arrowed down since: re-selecting the top row every time means Enter
/// opens a row nobody chose. A highlighted row that survives the re-filter keeps the
/// selection.
/// </summary>
public static class PaletteSelection
{
    public static PaletteItem? AfterRefilter(PaletteItem? highlighted, IReadOnlyList<PaletteItem> matches)
    {
        if (matches.Count == 0) return null;
        return highlighted is not null && matches.Contains(highlighted) ? highlighted : matches[0];
    }
}
