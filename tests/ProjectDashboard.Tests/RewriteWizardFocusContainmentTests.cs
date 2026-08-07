using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The wizard is a sibling overlay, not a dialog, so nothing about the visual tree stops the
/// keyboard from leaving it. The scrim blocks the mouse only: without a tab cycle on the pane
/// and a disabled surface behind it, Tab walks into the work-area tabs where Space and Enter
/// fire discard, stage, and branch-delete on controls the reader cannot see, and a screen
/// reader announces both surfaces at once.
/// </summary>
public class RewriteWizardFocusContainmentTests
{
    [Fact]
    public void TheWizardPane_CyclesTabAndControlTabWithinItself()
    {
        var xaml = File.ReadAllText(SourceFile("RewriteWizardView.xaml"));
        var pane = Regex.Match(xaml, @"<Border\b[^>]*MaxWidth=""1040""[^>]*>", RegexOptions.Singleline);

        Assert.True(pane.Success, "the wizard's root pane border was not found");
        Assert.Contains(@"KeyboardNavigation.TabNavigation=""Cycle""", pane.Value);
        Assert.Contains(@"KeyboardNavigation.ControlTabNavigation=""Cycle""", pane.Value);
    }

    [Fact]
    public void TheWorkAreaTabs_AreDisabledWhileTheWizardIsUp()
    {
        var xaml = File.ReadAllText(SourceFile("ProjectDetailPage.xaml"));
        var tabs = Regex.Match(xaml, @"<TabControl\b[^>]*>", RegexOptions.Singleline);

        Assert.True(tabs.Success, "the work-area TabControl was not found");
        Assert.Contains(@"IsEnabled=""{Binding RewriteWizardHidden}""", tabs.Value);
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
