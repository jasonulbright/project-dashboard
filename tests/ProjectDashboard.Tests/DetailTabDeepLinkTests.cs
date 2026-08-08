using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using ProjectDashboard.Models;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A dashboard card opens a project straight at one work-area surface. The page is built
/// by the navigation, so the shell cannot address it: the tab is handed over as pending
/// state the page applies as it loads. The shell must not go looking for the tab host in
/// the visual tree instead — a search has to guess when the page has attached, and the
/// retry loop it needs fails silently on the last attempt, leaving the reader on Overview
/// with no indication the deep link was dropped.
/// </summary>
public class DetailTabDeepLinkTests
{
    [Fact]
    public void EveryDetailTab_IsFoundByTagInTheWorkAreaAsTheMarkupOrdersIt()
    {
        var tags = WorkAreaTags();

        foreach (var tab in Enum.GetValues<DetailTab>())
        {
            var index = ProjectDetailTabs.IndexOfTab(tags, tab);
            Assert.NotNull(index);
            Assert.Equal(tab, tags[index.Value]);
        }
    }

    [Fact]
    public void TheShellHandsTheTabToThePage_AndDoesNotSearchTheVisualTree()
    {
        var shell = File.ReadAllText(SourceFile("Views", "Windows", "MainWindow.xaml.cs"));

        Assert.Contains("ProjectDetailPage.RequestedTab = tab;", shell);
        Assert.DoesNotContain("TrySelectDetailTab", shell);
        Assert.DoesNotContain("FindVisualChildren", shell);
    }

    /// <summary>
    /// One deep link must not steer a later navigation that asked for no tab. The page
    /// clears the request as it consumes it, so the handoff cannot outlive its own load.
    /// </summary>
    [Fact]
    public void ThePendingTab_IsClearedWhenThePageConsumesIt()
    {
        var page = File.ReadAllText(SourceFile("Views", "Pages", "ProjectDetailPage.xaml.cs"));
        var consume = Regex.Match(page,
            @"var requested = RequestedTab;\s*RequestedTab = null;.*?" +
            @"ApplyPendingTab\(WorkTabs, requested, LoadDataForActiveTab\);", RegexOptions.Singleline);

        Assert.True(consume.Success, "the page does not consume a pending tab");
    }

    /// <summary>
    /// Selecting the deep-linked tab raises SelectionChanged, and that handler is what
    /// loads a lazy surface. A second load from the page's own post-navigation step spawns
    /// a duplicate gh/git read of the same surface, and the reply replaces the collection
    /// the first one filled — dropping whatever the reader had selected in it.
    /// </summary>
    [Theory]
    [InlineData(null, DetailTab.Overview)]
    [InlineData(DetailTab.PullRequests, DetailTab.PullRequests)]
    [InlineData(DetailTab.Releases, DetailTab.Releases)]
    // The deep link names the tab already selected: nothing moves, so the page's own
    // load is the only one there is.
    [InlineData(DetailTab.Overview, DetailTab.Overview)]
    public void ADeepLinkedTab_LoadsItsSurfaceExactlyOnce(DetailTab? requested, DetailTab expected)
    {
        RunSta(() =>
        {
            var loads = new List<DetailTab>();
            var tabs = WorkArea(loads);

            ProjectDetailPage.ApplyPendingTab(tabs, requested, () => Load(tabs, loads));

            Assert.Equal([expected], loads);
        });
    }

    /// <summary>The page's tab host: one tab per surface, in the order the markup declares.</summary>
    private static TabControl WorkArea(List<DetailTab> loads)
    {
        var tabs = new TabControl();
        foreach (var tab in Enum.GetValues<DetailTab>()) tabs.Items.Add(new TabItem { Tag = tab });
        tabs.SelectedIndex = 0;
        // The page's own handler, which is why a selection that moves already loads.
        tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, tabs)) Load(tabs, loads);
        };
        return tabs;
    }

    private static void Load(TabControl tabs, List<DetailTab> loads)
    {
        if (tabs.SelectedItem is TabItem { Tag: DetailTab tab }) loads.Add(tab);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Tab tags in the order the markup declares them — the order the page hosts.</summary>
    private static List<DetailTab?> WorkAreaTags()
    {
        var xaml = File.ReadAllText(SourceFile("Views", "Pages", "ProjectDetailPage.xaml"));
        var tags = Regex.Matches(xaml, @"<TabItem\b[^>]*Tag=""\{x:Static models:DetailTab\.(?<tab>\w+)\}""")
            .Select(m => (DetailTab?)Enum.Parse<DetailTab>(m.Groups["tab"].Value))
            .ToList();

        Assert.Equal(Enum.GetValues<DetailTab>().Length, tags.Count);
        return tags;
    }

    private static string SourceFile(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(
            [Path.GetDirectoryName((string)CallerFile())!, "..", "..", "src", "ProjectDashboard", .. parts]));
        Assert.True(File.Exists(path), $"source not found at {path}");
        return path;
    }

    private static string CallerFile([CallerFilePath] string testFile = "") => testFile;
}
