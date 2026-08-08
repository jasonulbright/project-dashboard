using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectDashboard.Helpers;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The detail page's markup, loaded for real. Every StaticResource, style key, converter, and
/// x:Static reference in it is resolved at parse time and by nothing the compiler checks — a
/// misspelled brush or a style declared in the wrong scope builds cleanly and throws the first
/// time a reader opens the page.
/// </summary>
[Collection("detail-page-markup")]
public class DetailPageMarkupTests
{
    /// <summary>
    /// One test rather than one per view: an Application and the brushes in its dictionaries
    /// belong to the thread that built them, and a second STA thread cannot read them.
    /// </summary>
    [Fact]
    public void TheDetailPageAndItsOverlays_ResolveEveryResourceTheirMarkupNames()
        => RunSta(() =>
        {
            var page = new ProjectDetailPage(NewViewModel());
            Assert.NotNull(page.Content);
            Assert.NotNull(new TagsView { DataContext = NewViewModel() }.Content);
            Assert.NotNull(new ReflogView { DataContext = NewViewModel() }.Content);

            // Laid out here for the same reason: the pane's own template and list style, on the
            // one thread this Application's brushes belong to.
            SideBySidePane_SplitsEvenlyAndScrollsToTheEndOfALongLine(page);
            ListRows_AreAnnouncedAsTheirContentAndNotAsTheirTypeName(page);
            StatusLines_CarryTheirValueAndAreAnnouncedAsTheyChange(page);
            // Last: it applies a theme, and the assertions above read the brushes in force.
            TheStatusPalette_OutranksTheThemeDictionary();
        });

    /// <summary>
    /// Applying a theme rebuilds the merged dictionaries, so the palette has to be re-appended
    /// after it. A palette that is merged before the theme dictionary resolves to the theme's
    /// own value, and the contrast-corrected colours silently stop being the ones on screen.
    /// </summary>
    private static void TheStatusPalette_OutranksTheThemeDictionary()
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
        ProjectDashboard.Views.Windows.MainWindow.ApplyPalette(Wpf.Ui.Appearance.ApplicationTheme.Light);

        var secondary = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Assert.Equal(Color.FromRgb(0x5C, 0x5C, 0x5C), secondary.Color);
    }

    /// <summary>
    /// An item container with no <see cref="AutomationProperties.Name"/> is announced as the
    /// item's ToString, which for every model bound here is the type's own name. A working-file
    /// row carries a second split: its status column is drawn as one letter, which is read out as
    /// that letter and carries none of the state, so the row's name spells the state out.
    /// </summary>
    private static void ListRows_AreAnnouncedAsTheirContentAndNotAsTheirTypeName(ProjectDetailPage page)
    {
        AssertRowNames(
            new ListBox
            {
                // An explicit style keeps the themed implicit one, whose template needs a
                // render pass this window never takes, off the list.
                Style = new Style(typeof(ListBox)),
                ItemTemplate = (DataTemplate)page.Resources["WorkingFileTemplate"],
                ItemContainerStyle = (Style)page.Resources["WorkingFileRow"],
                ItemsSource = new[]
                {
                    new WorkingFile { Path = "src/a.txt", WorktreeStatus = 'M' },
                    new WorkingFile { Path = "src/b.txt", IsUntracked = true }
                }
            },
            ["modified src/a.txt", "untracked src/b.txt"],
            ["M", "U"]);

        AssertRowNames(
            new ListBox
            {
                Style = new Style(typeof(ListBox)),
                ItemTemplate = (DataTemplate)page.Resources["StagedFileTemplate"],
                ItemContainerStyle = (Style)page.Resources["StagedFileRow"],
                ItemsSource = new[]
                {
                    new WorkingFile { Path = "src/a.txt", IndexStatus = 'A' },
                    new WorkingFile { Path = "src/b.txt", IndexStatus = 'M' }
                }
            },
            ["added src/a.txt", "modified src/b.txt"],
            ["A", "M"]);

        AssertRowNames(
            new ListBox
            {
                Style = (Style)page.Resources["DiffListStyle"],
                ItemsSource = new[]
                {
                    new DiffLine { Kind = DiffLineKind.Removed, Text = "gone", OldNumber = "2" },
                    new DiffLine { Kind = DiffLineKind.Added, Text = "fresh", NewNumber = "2" }
                }
            },
            ["Removed line 2: gone", "Added line 2: fresh"]);
    }

    /// <summary>
    /// A name on a TextBlock REPLACES its text for a reader, so a status line that carries one
    /// has to carry its value inside it; and an outcome a reader is never told about is an
    /// outcome the app did not report.
    /// </summary>
    private static void StatusLines_CarryTheirValueAndAreAnnouncedAsTheyChange(ProjectDetailPage page)
    {
        var model = (ProjectDetailViewModel)page.DataContext;
        model.SurgeryStatusText = "Reordered 3 commits.";

        var window = new Window { Content = page, Width = 1400, Height = 900, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();

            // A TabControl realizes only the selected tab, and the history-edit status line
            // lives on the History tab.
            var tabs = (TabControl)page.FindName("WorkTabs")!;
            tabs.SelectedIndex = (int)DetailTab.History;
            window.UpdateLayout();

            var line = Descendants<TextBlock>(window)
                .Single(t => t.Text == "Reordered 3 commits.");
            Assert.Contains("Reordered 3 commits.", AutomationProperties.GetName(line));

            var live = Descendants<TextBlock>(window)
                .Count(t => new FrameworkElementAutomationPeer(t).GetLiveSetting() != AutomationLiveSetting.Off);
            Assert.True(live >= 3, $"only {live} status lines are announced as they change");
        }
        finally { window.Close(); }
    }

    /// <param name="statusColumn">
    /// The leading column each row draws, when the row has one. Asserted alongside the names so a
    /// name that spells its status out cannot have been bought by respelling the column on screen.
    /// </param>
    private static void AssertRowNames(ListBox list, string[] expected, string[]? statusColumn = null)
    {
        var window = new Window { Content = list, Width = 600, Height = 400, ShowActivated = false };
        try
        {
            // A virtualizing panel realizes rows against a viewport this window never paints,
            // so nothing would be generated to read a name off.
            VirtualizingPanel.SetIsVirtualizing(list, false);
            window.Show();
            window.UpdateLayout();

            var rows = Descendants<ListBoxItem>(list).ToList();
            Assert.Equal(expected, rows.Select(AutomationProperties.GetName).ToArray());

            if (statusColumn is not null)
                Assert.Equal(statusColumn,
                    rows.Select(row => Descendants<TextBlock>(row).First().Text).ToArray());
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The two-column pane at real widths, with its shipped template and list style. A long line
    /// has to stay reachable — the pane scrolls horizontally past the viewport — while both halves
    /// stay equal, which is what the row layout owes a reader comparing two columns.
    /// </summary>
    private static void SideBySidePane_SplitsEvenlyAndScrollsToTheEndOfALongLine(ProjectDetailPage page)
    {
        var rows = SideBySideDiff.Build([
            new DiffLine { Kind = DiffLineKind.HunkHeader, Text = "@@ -1,2 +1,2 @@", HunkIndex = 0 },
            new DiffLine { Kind = DiffLineKind.Context, Text = "short", OldNumber = "1", NewNumber = "1" },
            new DiffLine { Kind = DiffLineKind.Removed, Text = new string('x', 400), OldNumber = "2" },
            new DiffLine { Kind = DiffLineKind.Added, Text = "tiny", NewNumber = "2" }
        ]);

        var list = new ListBox
        {
            Style = (Style)page.Resources["SideBySideListStyle"],
            ItemsSource = rows
        };
        var window = new Window { Content = list, Width = 420, Height = 300, ShowActivated = false };
        try
        {
            window.Show();
            list.UpdateLayout();

            var scroll = FirstChild<ScrollViewer>(list);
            Assert.NotNull(scroll);
            Assert.True(scroll.ExtentWidth > scroll.ViewportWidth,
                "a line wider than the pane left nothing to scroll to");

            Assert.Equal(rows.Count, Descendants<DiffRowPanel>(list).Count());

            // Header rows collapse both cells; the split is about the rows that carry lines.
            var panels = Descendants<DiffRowPanel>(list)
                .Where(p => Cell(p, DiffRowCell.NewText).IsVisible)
                .ToList();

            Assert.Equal(rows.Count(r => !r.IsHeader), panels.Count);
            Assert.Single(panels
                .Select(p => Cell(p, DiffRowCell.NewText).TransformToAncestor(list).Transform(default).X)
                .Distinct());
            foreach (var panel in panels)
                Assert.Equal(Cell(panel, DiffRowCell.OldText).ActualWidth,
                    Cell(panel, DiffRowCell.NewText).ActualWidth, 3);
        }
        finally { window.Close(); }
    }

    private static FrameworkElement Cell(DiffRowPanel panel, DiffRowCell cell) =>
        panel.Children.OfType<FrameworkElement>().Single(c => DiffRowPanel.GetCell(c) == cell);

    private static T? FirstChild<T>(DependencyObject root) where T : DependencyObject =>
        Descendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static ProjectDetailViewModel NewViewModel() =>
        new(null!, new GitService(), null!);

    /// <summary>
    /// WPF needs an STA thread, and the page's markup reaches app-level resources, so the
    /// Application and its merged dictionaries have to exist before anything is parsed.
    /// </summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current as ProjectDashboard.App ?? new ProjectDashboard.App();
                app.InitializeComponent();
                // The shipped mode shuts the Application down when the first window closes, and
                // a window opened after that never lays out — so later assertions would read an
                // empty visual tree rather than fail on what they are checking.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                action();
            }
            catch (Exception ex) { error = ex; }
        });
        // A body that wedges must not outlive the run: the Join below gives up, and a foreground
        // thread would keep the test host alive after it does.
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}

/// <summary>
/// One Application per process is a WPF invariant, and each of these tests creates one on its
/// own thread — serializing them keeps two from racing to be it.
/// </summary>
[CollectionDefinition("detail-page-markup", DisableParallelization = true)]
public sealed class DetailPageMarkupCollection;
