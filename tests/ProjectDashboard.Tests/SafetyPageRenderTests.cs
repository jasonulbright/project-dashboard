using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The safety page's markup, loaded and laid out for real. Every StaticResource, DynamicResource,
/// converter and template reference in it is resolved at parse time and by nothing the compiler
/// checks — a misspelled brush or a converter declared in the wrong scope builds cleanly and throws
/// the first time a reader opens the page.
/// </summary>
[Collection("shipped-markup")]
public class SafetyPageRenderTests
{
    /// <summary>
    /// One test rather than one per assertion: an Application and the brushes in its dictionaries
    /// belong to the thread that built them, and the page is laid out once on that thread.
    /// </summary>
    [Fact]
    public void TheSafetyPage_ResolvesItsMarkupAndRendersBothRowShapes()
        => StaHost.Run(() =>
        {
            var page = new SafetyPage(NewViewModel());
            Assert.NotNull(page.Content);

            var window = new Window { Content = page, Width = 1200, Height = 800, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                var list = (ListBox)page.FindName("SafetyRows")!;
                // A virtualizing panel realizes rows against a viewport this window never paints,
                // so nothing would be generated to read a name off.
                VirtualizingPanel.SetIsVirtualizing(list, false);
                list.ItemsSource = new[]
                {
                    new SafetyRow { IsGroup = true, Title = "Interrupted operations", Line = "No repository has one." },
                    new SafetyRow
                    {
                        IsGroup = false,
                        Title = "worker",
                        Line = "No remote configured",
                        Detail = "Every commit here exists on this machine only.",
                        ActionLabel = "Open Remotes",
                        Action = SafetyAction.OpenRemotes,
                        RepoPath = @"C:\projects\worker",
                        Severity = SafetySeverity.WorthALook,
                    },
                };
                list.UpdateLayout();

                var texts = Descendants<TextBlock>(list).Select(t => t.Text).ToList();
                Assert.Contains("Interrupted operations", texts);
                Assert.Contains("No remote configured", texts);
                Assert.Contains("Worth a look", texts);
                Assert.Contains("Every commit here exists on this machine only.", texts);

                // The one control a finding offers, and the group heading offering none.
                var buttons = Descendants<Wpf.Ui.Controls.Button>(list)
                    .Where(b => b.IsVisible)
                    .ToList();
                Assert.Single(buttons, b => (b.Content as string) == "Open Remotes");

                // Each row is announced from the model rather than as its type name.
                var containers = list.Items.Cast<SafetyRow>()
                    .Select(row => (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row))
                    .ToList();
                Assert.All(containers, c => Assert.NotEqual("", AutomationProperties.GetName(c)));
                Assert.Equal(
                    list.Items.Cast<SafetyRow>().Select(r => r.AccessibleName),
                    containers.Select(AutomationProperties.GetName));
            }
            finally { window.Close(); }
        });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// A view model over an empty projects root: the page renders from the dashboard's list, and a
    /// root left at the configured default would send this test's scan across real repositories.
    /// </summary>
    private static SafetyViewModel NewViewModel()
    {
        var root = TestEnv.NewDir("safety-render");
        var settings = new SettingsService();
        settings.Save(new AppSettings
        {
            ProjectsRootPath = root,
            GhPath = Path.Combine(root, "no-such-gh.exe"),
            EnableGitHubDiscovery = false,
            ExcludedDirectories = [],
        });

        var gitHub = new GitHubService(settings);
        var dashboard = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!, settings, gitHub, new GitService(),
            new ProjectWatcherService(), new RepoBusyRegistry(),
            // The render only needs the list as it stands; a callback posted from the first scan
            // would run against a window this body has already closed.
            uiPost: _ => { },
            history: new OperationHistory(TestEnv.NewDir("safety-render-ledger")));

        return new SafetyViewModel(
            dashboard, new RepoBusyRegistry(), settings, new GitService(),
            history: new OperationHistory(TestEnv.NewDir("safety-render-vm-ledger")),
            uiPost: _ => { });
    }
}
