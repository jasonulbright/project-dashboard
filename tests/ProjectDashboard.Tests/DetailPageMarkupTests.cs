using System.Runtime.ExceptionServices;
using System.Windows;
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
        });

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
