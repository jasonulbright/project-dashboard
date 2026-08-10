using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ProjectDashboard.Tests;

/// <summary>
/// The markup facts the history-depth surfaces rest on, none of which a view-model test can
/// reach. Each one is silently breakable: a diff list that stops reporting its selected row
/// leaves every hunk action permanently unavailable, and a pane declared without its visibility
/// binding never appears at all.
/// </summary>
[Collection("shipped-markup")]
public class HistoryDepthViewBindingTests
{
    private static string ViewXaml(string fileName, [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..",
            "src", "ProjectDashboard", "Views", "Pages", fileName));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return File.ReadAllText(path);
    }

    private static string DetailPageXaml() => ViewXaml("ProjectDetailPage.xaml");

    /// <summary>
    /// A scrim stops the mouse and no keystroke. Without a cycle on every navigation mode, Tab
    /// and the arrow keys walk out of a read-only pane onto Discard, Push and Pull behind it.
    /// </summary>
    [Theory]
    [InlineData("FileHistoryView.xaml")]
    [InlineData("CommitGraphView.xaml")]
    public void EachReadOnlyPane_CyclesEveryNavigationModeWithinItself(string fileName)
    {
        var pane = Regex.Match(ViewXaml(fileName), @"<Border\b[^>]*MaxWidth=""1320""[^>]*>", RegexOptions.Singleline);

        Assert.True(pane.Success, $"the root pane border of {fileName} was not found");
        Assert.Contains(@"KeyboardNavigation.TabNavigation=""Cycle""", pane.Value);
        Assert.Contains(@"KeyboardNavigation.ControlTabNavigation=""Cycle""", pane.Value);
        Assert.Contains(@"KeyboardNavigation.DirectionalNavigation=""Cycle""", pane.Value);
    }

    [Theory]
    [InlineData("FileHistoryView.xaml", "CloseFileHistoryCommand")]
    [InlineData("CommitGraphView.xaml", "CloseCommitGraphCommand")]
    public void EachReadOnlyPane_ClosesOnEscape(string fileName, string command)
    {
        Assert.Contains($@"<KeyBinding Key=""Escape"" Command=""{{Binding {command}}}"" />", ViewXaml(fileName));
    }

    /// <summary>
    /// The hunk commands read the selected diff row for the hunk to slice. Without the two-way
    /// selection binding every one of them is blocked on "Select a line inside a hunk first",
    /// whatever the reader clicks.
    /// </summary>
    [Fact]
    public void TheWorkingDiffList_ReportsItsSelectedRowAndItsDoubleClick()
    {
        var declaration = Regex.Match(DetailPageXaml(),
            @"<ListBox x:Name=""WorkingDiffRows""[\s\S]*?>").Value;

        Assert.Contains(@"SelectedItem=""{Binding SelectedDiffLine}""", declaration);
        Assert.Contains(@"MouseDoubleClick=""OnWorkingDiffDoubleClick""", declaration);
        Assert.Contains(@"SelectionChanged=""OnWorkingDiffSelectionChanged""", declaration);
    }

    [Fact]
    public void TheDetailPage_HostsBothReadOnlyPanesWithTheirVisibilityBindings()
    {
        var xaml = DetailPageXaml();

        Assert.Contains(
            @"Visibility=""{Binding FileHistoryVisible, Converter={StaticResource BooleanToVisibilityConverter}}""",
            xaml);
        Assert.Contains(
            @"Visibility=""{Binding CommitGraphVisible, Converter={StaticResource BooleanToVisibilityConverter}}""",
            xaml);
    }

    /// <summary>
    /// Paging is offered only where the list can continue: a button bound to nothing would page
    /// a window that is already at the end of the branch.
    /// </summary>
    [Fact]
    public void TheHistoryList_OffersPagingOnlyWhileOlderCommitsMayExist()
    {
        var declaration = Regex.Match(DetailPageXaml(),
            @"<ui:Button DockPanel.Dock=""Left"" Content=""Load older commits""[\s\S]*?/>").Value;

        Assert.Contains(@"Command=""{Binding LoadOlderCommitsCommand}""", declaration);
        Assert.Contains(
            @"Visibility=""{Binding HistoryHasMore, Converter={StaticResource BooleanToVisibilityConverter}}""",
            declaration);
    }

    /// <summary>
    /// Every graph row reserves the same lane width, and it comes from the pane rather than the
    /// row so a page that opens a new column widens the rows already drawn. That needs the
    /// ancestor's data context from inside an item template; if the path did not resolve, the
    /// lane cell would collapse to nothing and the graph would render blank.
    /// </summary>
    [Fact]
    public void AnItemTemplate_ReadsTheListsDataContextThroughItsAncestor()
    {
        StaHost.Run(() =>
        {
            var host = new LaneWidthHost();
            var list = new ListBox { DataContext = host, ItemsSource = new[] { "row" } };
            // A Rectangle, because the default ListBoxItem template supplies a Border of its own
            // and finding that one instead would assert nothing.
            var cell = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
            cell.SetBinding(FrameworkElement.WidthProperty, new Binding
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBox), 1),
                Path = new PropertyPath($"DataContext.{nameof(LaneWidthHost.GraphLaneColumnWidth)}")
            });
            list.ItemTemplate = new DataTemplate { VisualTree = cell };

            var window = new Window { Content = list, Width = 300, Height = 200, ShowActivated = false };
            window.Show();
            try
            {
                list.UpdateLayout();
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var laneCell = FindLaneCell(container);
                Assert.NotNull(laneCell);
                Assert.Equal(host.GraphLaneColumnWidth, laneCell!.Width);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static System.Windows.Shapes.Rectangle? FindLaneCell(DependencyObject root)
    {
        if (root is System.Windows.Shapes.Rectangle cell) return cell;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindLaneCell(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private sealed class LaneWidthHost
    {
        public double GraphLaneColumnWidth => 48;
    }

}
