using System.Text.RegularExpressions;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The rollup's shipped surface: where it lives in the shell, how its rows are announced, and the
/// deep-link handoff its actions travel on. XAML compiles to BAML with no runtime API for the
/// attached properties a template declares, so these are asserted against the markup itself.
/// </summary>
public class SafetyPageMarkupTests
{
    private const string Shell = "src/ProjectDashboard/Views/Windows/MainWindow.xaml";
    private const string Page = "src/ProjectDashboard/Views/Pages/SafetyPage.xaml";

    /// <summary>
    /// The footer is where app-level surfaces live. The five main-menu items are filters over one
    /// card grid and all route to the dashboard; this is a different page with a different shape,
    /// and a sixth filter there would claim to be one of them.
    /// </summary>
    [Fact]
    public void TheSafetyItem_IsInTheFooterBesideSettings()
    {
        var shell = RepoSource.Read(Shell);
        var footer = Regex.Match(shell,
            @"<local:AppNavigationView\.FooterMenuItems>(?<body>.*?)</local:AppNavigationView\.FooterMenuItems>",
            RegexOptions.Singleline);

        Assert.True(footer.Success, "the shell no longer declares footer menu items");
        Assert.Contains(@"Content=""Safety""", footer.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("pages:SafetyPage", footer.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// No badge on the nav item. Background nagging is a stated non-goal, and a count on an item
    /// nobody opened is exactly that.
    /// </summary>
    [Fact]
    public void TheSafetyItem_CarriesNoCountBadge()
    {
        var shell = RepoSource.Read(Shell);
        var item = Regex.Match(shell,
            @"<ui:NavigationViewItem\s+Content=""Safety""(?<body>.*?)/>", RegexOptions.Singleline);

        Assert.True(item.Success, "the Safety nav item moved");
        Assert.DoesNotContain("InfoBadge", item.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Binding", item.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A findings list across a large portfolio has the same unbounded growth the card grid does.
    /// Group headings are rows of the same flat list precisely so it can virtualize.
    /// </summary>
    [Fact]
    public void TheFindingsList_IsVirtualized()
    {
        var page = RepoSource.Read(Page);
        Assert.Contains(@"VirtualizingPanel.IsVirtualizing=""True""", page, StringComparison.Ordinal);
        Assert.Contains(@"VirtualizingPanel.VirtualizationMode=""Recycling""", page, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupStyle", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without an automation name the row is announced as its type's name: an item container falls
    /// back to the item's ToString.
    /// </summary>
    [Fact]
    public void EveryRowContainer_IsNamedFromTheModel()
    {
        var markup = MarkupName.Markup(Page);
        var setter = MarkupName.Element(
            markup,
            "//*[local-name()='ListBox']/*[local-name()='ListBox.ItemContainerStyle']"
            + "/*[local-name()='Style']/*[local-name()='Setter'][@Property='AutomationProperties.Name']",
            Page);

        var row = new SafetyRow
        {
            IsGroup = false,
            Title = "worker",
            Line = "No remote configured",
            Detail = "Every commit here exists on this machine only.",
            Severity = SafetySeverity.WorthALook,
        };

        var name = MarkupName.From(setter.GetAttribute("Value"), row);
        Assert.Equal(row.AccessibleName, name);
        Assert.Contains("worker", name, StringComparison.Ordinal);
        Assert.Contains("Worth a look", name, StringComparison.Ordinal);
    }

    /// <summary>The rollup and the tier line are both on the page, and both are addressable by a UIA pass.</summary>
    [Fact]
    public void TheHeader_CarriesBothTheRollupAndWhichTiersRan()
    {
        var page = RepoSource.Read(Page);
        Assert.Contains(@"AutomationProperties.AutomationId=""SafetyRollupText""", page, StringComparison.Ordinal);
        Assert.Contains(@"AutomationProperties.AutomationId=""SafetyTierText""", page, StringComparison.Ordinal);
        Assert.Contains(@"Text=""{Binding RollupText}""", page, StringComparison.Ordinal);
        Assert.Contains(@"Text=""{Binding TierText}""", page, StringComparison.Ordinal);
    }

    /// <summary>An outcome the reader asked for is announced; the running tally beside it is not.</summary>
    [Fact]
    public void TheOutcomeLine_IsAnnouncedAndTheTallyIsNot()
    {
        var page = RepoSource.Read(Page);
        var status = Regex.Match(page,
            @"<TextBlock[^>]*AutomationProperties\.AutomationId=""SafetyStatusText""[^>]*>",
            RegexOptions.Singleline);
        Assert.True(status.Success, "the status line moved");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", status.Value, StringComparison.Ordinal);

        var progress = Regex.Match(page,
            @"<TextBlock[^>]*AutomationProperties\.AutomationId=""SafetyProgressText""[^>]*>",
            RegexOptions.Singleline);
        Assert.True(progress.Success, "the progress line moved");
        Assert.DoesNotContain("LiveSetting", progress.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// The expensive button names its cost before it is pressed, rather than starting a run of
    /// unknown length behind a spinner.
    /// </summary>
    [Fact]
    public void TheExpensiveButton_StatesItsCost()
    {
        var page = RepoSource.Read(Page);
        var button = Regex.Match(page, @"<ui:Button[^>]*Command=""\{Binding CheckAllCommand\}""[^>]*/>",
            RegexOptions.Singleline);
        Assert.True(button.Success, "the deep-check button moved");
        Assert.Contains("minutes", button.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AutomationProperties.Name", button.Value, StringComparison.Ordinal);
    }

    /// <summary>A running check must be stoppable, and the control appears only while one runs.</summary>
    [Fact]
    public void ARunningCheck_OffersACancel()
    {
        var page = RepoSource.Read(Page);
        var cancel = Regex.Match(page, @"<ui:Button[^>]*Command=""\{Binding CancelCheckCommand\}""[^>]*/>",
            RegexOptions.Singleline);
        Assert.True(cancel.Success, "the cancel button moved");
        Assert.Contains("CheckRunning", cancel.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pane travels to the page the navigation is about to build, exactly as the work-area tab
    /// does. The shell must not go looking for the page in the visual tree: a search has to guess
    /// when the page attached, and the retry loop it needs fails silently on the last attempt.
    /// </summary>
    [Fact]
    public void TheShellHandsTheOverlayToThePage_AndDoesNotSearchTheVisualTree()
    {
        var shell = RepoSource.Read("src/ProjectDashboard/Views/Windows/MainWindow.xaml.cs");
        Assert.Contains("ProjectDetailPage.RequestedOverlay = overlay;", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("FindVisualChildren", shell, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rollup's links are wired by the shell, not by the page, because the page is built on
    /// first navigation. Without this the rows are buttons that reach nothing until a reader has
    /// already visited the surface they lead to.
    /// </summary>
    [Fact]
    public void TheShell_WiresBothOfTheRollupsNavigationEvents()
    {
        var shell = RepoSource.Read("src/ProjectDashboard/Views/Windows/MainWindow.xaml.cs");
        Assert.Contains("safetyVm.NavigateToProjectTabRequested +=", shell, StringComparison.Ordinal);
        Assert.Contains("safetyVm.NavigateToProjectOverlayRequested +=", shell, StringComparison.Ordinal);
    }

    /// <summary>
    /// One deep link must not steer a later navigation that asked for no pane, so the page clears
    /// the request as it consumes it.
    /// </summary>
    [Fact]
    public void ThePendingOverlay_IsClearedWhenThePageConsumesIt()
    {
        var page = RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml.cs");
        var consume = Regex.Match(page,
            @"var overlay = RequestedOverlay;\s*RequestedOverlay = null;\s*await ApplyPendingOverlay\(",
            RegexOptions.Singleline);

        Assert.True(consume.Success, "the page does not consume a pending overlay");
    }

    /// <summary>
    /// Every overlay a link can name opens through that pane's own command, so it loads its
    /// contents and states its own refusals rather than being shown empty.
    /// </summary>
    [Fact]
    public void EveryOverlayValue_IsRoutedThroughThatPanesOwnCommand()
    {
        var page = RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml.cs");
        foreach (var overlay in Enum.GetValues<ProjectDashboard.Models.DetailOverlay>())
            Assert.Contains($"DetailOverlay.{overlay} =>", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// Public copy never names a competitor, and it never points at a private document. The rollup
    /// is a page of prose, so its own strings are checked rather than assumed.
    /// </summary>
    [Fact]
    public void TheRollupsCopy_NamesNoPrivateDocument()
    {
        foreach (var text in new[]
                 {
                     RepoSource.Read(Page),
                     RepoSource.Read("src/ProjectDashboard/Services/Safety/SafetyModels.cs"),
                     RepoSource.Read("src/ProjectDashboard/Services/Safety/SafetySurvey.cs"),
                     RepoSource.Read("src/ProjectDashboard/ViewModels/Pages/SafetyViewModel.cs"),
                 })
        {
            Assert.DoesNotContain(".local.md", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PUNCHLIST", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ROADMAP", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
