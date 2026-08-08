using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
            @"if \(RequestedTab is \{ \} requested\)\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);

        Assert.True(consume.Success, "the page does not consume a pending tab");
        Assert.Contains("RequestedTab = null;", consume.Groups["body"].Value);
        Assert.Contains("SelectTab(requested);", consume.Groups["body"].Value);
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
