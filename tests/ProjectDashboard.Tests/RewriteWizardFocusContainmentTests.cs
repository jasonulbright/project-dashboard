using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProjectDashboard.Tests;

/// <summary>
/// The wizard is a sibling overlay, not a dialog, so nothing about the visual tree stops the
/// keyboard from leaving it. The scrim blocks the mouse only: without a cycle on every
/// navigation mode and a disabled surface behind it, Tab and the arrow keys walk onto controls
/// the reader cannot see, where Space and Enter fire discard, stage, branch-delete, and Pull —
/// and a Pull merges the un-rewritten remote history back over a rewrite the same screen is
/// still reporting as verified clean.
/// </summary>
public class RewriteWizardFocusContainmentTests
{
    [Fact]
    public void TheWizardPane_CyclesEveryNavigationModeWithinItself()
    {
        var xaml = File.ReadAllText(SourceFile("RewriteWizardView.xaml"));
        var pane = Regex.Match(xaml, @"<Border\b[^>]*MaxWidth=""1040""[^>]*>", RegexOptions.Singleline);

        Assert.True(pane.Success, "the wizard's root pane border was not found");
        Assert.Contains(@"KeyboardNavigation.TabNavigation=""Cycle""", pane.Value);
        Assert.Contains(@"KeyboardNavigation.ControlTabNavigation=""Cycle""", pane.Value);
        // Tab and Ctrl+Tab are not the only ways out: the arrow keys traverse independently.
        Assert.Contains(@"KeyboardNavigation.DirectionalNavigation=""Cycle""", pane.Value);
    }

    [Fact]
    public void TheWorkAreaTabs_AreDisabledWhileTheWizardIsUp()
    {
        var tabs = Regex.Match(File.ReadAllText(SourceFile("ProjectDetailPage.xaml")),
            @"<TabControl\b[^>]*>", RegexOptions.Singleline);

        Assert.True(tabs.Success, "the work-area TabControl was not found");
        Assert.Contains(@"IsEnabled=""{Binding SafetyOverlayHidden}""", tabs.Value);
    }

    /// <summary>
    /// The header rows sit outside the TabControl, so disabling the tabs alone leaves Fetch,
    /// Pull, Push, the stale-lock retry, and Open in Terminal reachable behind the scrim.
    /// </summary>
    /// <summary>
    /// The backups pane offers a restore that replaces every ref in the repository, so it is a
    /// safety overlay on the same terms as the wizard and needs the same containment.
    /// </summary>
    [Fact]
    public void TheBackupsPane_CyclesEveryNavigationModeWithinItself()
    {
        var xaml = File.ReadAllText(SourceFile("BackupsView.xaml"));
        var pane = Regex.Match(xaml, @"<Border\b[^>]*MaxWidth=""940""[^>]*>", RegexOptions.Singleline);

        Assert.True(pane.Success, "the backups pane's root border was not found");
        Assert.Contains(@"KeyboardNavigation.TabNavigation=""Cycle""", pane.Value);
        Assert.Contains(@"KeyboardNavigation.ControlTabNavigation=""Cycle""", pane.Value);
        Assert.Contains(@"KeyboardNavigation.DirectionalNavigation=""Cycle""", pane.Value);
    }

    [Theory]
    [InlineData("StateBanner")]
    [InlineData("BranchBar")]
    [InlineData("RecoveryBanner")]
    public void ThePageHeaderRows_AreDisabledWhileTheWizardIsUp(string name)
    {
        var row = Regex.Match(File.ReadAllText(SourceFile("ProjectDetailPage.xaml")),
            @"<Border\b[^>]*x:Name=""" + name + @"""[^>]*>", RegexOptions.Singleline);

        Assert.True(row.Success, $"the {name} border was not found");
        Assert.Contains(@"IsEnabled=""{Binding SafetyOverlayHidden}""", row.Value);
    }

    /// <summary>
    /// The detail page is transient over a shared view model, so a navigation away and back
    /// leaves an unrooted pane bound to the same properties as the visible one. A GroupName is an
    /// application-wide table matched by visual root, which every unrooted pane shares, so that
    /// pane's buttons uncheck the visible one's. The write path stays two-way because a selection
    /// made through automation arrives as a property write and raises no click; refusing the
    /// uncheck is the view model's job, which
    /// <see cref="RewriteWizardViewModelTests.ClearingTheChosenOperationFlag_LeavesTheChoiceStanding"/>
    /// covers.
    /// </summary>
    [Fact]
    public void TheWizardChoices_JoinNoApplicationWideRadioGroup()
    {
        var choices = Regex.Matches(File.ReadAllText(SourceFile("RewriteWizardView.xaml")),
            @"<RadioButton\b[^>]*?/>", RegexOptions.Singleline);

        Assert.Equal(9, choices.Count);
        foreach (var choice in choices.Select(match => match.Value))
        {
            Assert.DoesNotContain("GroupName", choice);
            Assert.Contains("Mode=TwoWay", choice);
        }
    }

    /// <summary>
    /// The containment itself, on a live focus scope. The replica carries the page's structure —
    /// an enabled-or-not header row beside a pane that cycles its navigation modes — because the
    /// escape is a property of that arrangement, not of the wizard's contents.
    /// </summary>
    [Theory]
    [InlineData(FocusNavigationDirection.Up)]
    [InlineData(FocusNavigationDirection.Down)]
    [InlineData(FocusNavigationDirection.Left)]
    [InlineData(FocusNavigationDirection.Right)]
    public void ArrowKeysFromInsideThePane_NeverReachTheHeaderBehindTheScrim(FocusNavigationDirection direction)
    {
        RunSta(() =>
        {
            var (root, header, first, last) = BuildReplica(wizardVisible: true, contained: true);
            new System.Windows.Window { Content = root, Width = 800, Height = 600 }.Show();

            foreach (var start in new[] { first, last })
            {
                Assert.True(start.Focus(), "the replica's pane control did not take focus");
                start.MoveFocus(new TraversalRequest(direction));
                var landed = Keyboard.FocusedElement as DependencyObject;
                Assert.False(IsInside(landed, header),
                    $"{direction} from the pane landed on the header behind the scrim");
            }
        });
    }

    /// <summary>
    /// The library fact the containment answers, and the proof the test above is not vacuous:
    /// a tab cycle alone does not bound arrow traversal, so an overlay over an enabled header
    /// leaks focus onto it on the first arrow key.
    /// </summary>
    [Fact]
    public void WithoutTheContainment_AnArrowKeyLeavesThePaneOntoTheHeader()
    {
        RunSta(() =>
        {
            var (root, header, first, _) = BuildReplica(wizardVisible: true, contained: false);
            new System.Windows.Window { Content = root, Width = 800, Height = 600 }.Show();

            Assert.True(first.Focus());
            first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));

            Assert.True(IsInside(Keyboard.FocusedElement as DependencyObject, header),
                "the unbounded arrangement was expected to leak focus onto the header");
        });
    }

    /// <summary>The containment is conditional: closing the wizard must hand the header back.</summary>
    [Fact]
    public void WithTheWizardClosed_TheHeaderIsReachableAgain()
    {
        RunSta(() =>
        {
            var (root, header, _, _) = BuildReplica(wizardVisible: false, contained: true);
            new System.Windows.Window { Content = root, Width = 800, Height = 600 }.Show();

            Assert.True(header.IsEnabled);
            Assert.True(((Button)header.Children[0]).Focus());
        });
    }

    /// <summary>
    /// The page's arrangement: a header row of controls and an overlay pane above it.
    /// <paramref name="contained"/> applies the two halves of the containment together — the
    /// header's SafetyOverlayHidden binding, resolved to the value that binding would produce,
    /// and the pane's navigation-mode cycles — because either half alone still leaks.
    /// </summary>
    private static (Grid Root, StackPanel Header, Button First, Button Last) BuildReplica(
        bool wizardVisible, bool contained)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = !(wizardVisible && contained),
        };
        header.Children.Add(new Button { Content = "Pull", Width = 80, Height = 24 });
        header.Children.Add(new Button { Content = "Push", Width = 80, Height = 24 });

        var paneContent = new StackPanel();
        var first = new Button { Content = "Replace text", Width = 200, Height = 24 };
        var last = new Button { Content = "Execute", Width = 200, Height = 24 };
        paneContent.Children.Add(first);
        paneContent.Children.Add(new Button { Content = "Next", Width = 200, Height = 24 });
        paneContent.Children.Add(last);

        var pane = new Border
        {
            Child = paneContent,
            Margin = new Thickness(24, 120, 24, 24),
            Visibility = wizardVisible ? Visibility.Visible : Visibility.Collapsed,
        };
        KeyboardNavigation.SetTabNavigation(pane, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(pane, KeyboardNavigationMode.Cycle);
        if (contained)
            KeyboardNavigation.SetDirectionalNavigation(pane, KeyboardNavigationMode.Cycle);

        // The page's shape: the header owns its own row and the pane is an overlay spanning
        // every row, so the header sits above the pane and behind its scrim.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(pane, 0);
        Grid.SetRowSpan(pane, 2);
        root.Children.Add(header);
        root.Children.Add(pane);
        return (root, header, first, last);
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (var node = element; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, container)) return true;
        return false;
    }

    /// <summary>WPF focus traversal needs an STA thread; no Application is needed.</summary>
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

    private static string SourceFile(string name, [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..",
            "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
