using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The dashboard's markup, loaded for real, and read the way a screen reader reads it: through
/// automation peers. A name set on an element WPF builds no peer for reaches nobody, and that is
/// invisible to any assertion made against the markup alone.
/// </summary>
[Collection("shipped-markup")]
public class DashboardPageMarkupTests
{
    /// <summary>
    /// One test rather than one per surface: an Application and the brushes in its dictionaries
    /// belong to the thread that built them, and the page is laid out once for all of them.
    /// </summary>
    [Fact]
    public void TheDashboard_IsReadableWhereTheKeyboardGoes()
        => StaHost.Run(() =>
        {
            var model = NewViewModel();
            var window = new Window
            {
                Content = new DashboardPage(model), Width = 1400, Height = 900, ShowActivated = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                EveryChip_IsAnInvokableElementWithItsName(window);
                EveryCard_NamesTheElementTheKeyboardLandsOn(window);
                // Last: activating a filter is what the chips do, and it empties the grid the
                // assertion above measures.
                Chips_AreActivatedByEnterAndSpace(window, model);
            }
            finally { window.Close(); }
        });

    private const string ChipNamePrefix = "Show ";

    /// <summary>
    /// A chip is a filter a keyboard user lands on. Without a peer it is not in the automation
    /// tree at all — not even the raw view — so a reader is told nothing when focus arrives.
    /// </summary>
    private static void EveryChip_IsAnInvokableElementWithItsName(Window window)
    {
        var chips = Chips(window);
        Assert.Equal(8, chips.Count);

        foreach (var chip in chips)
        {
            var name = AutomationProperties.GetName(chip);
            var peer = UIElementAutomationPeer.CreatePeerForElement(chip);

            Assert.True(peer is not null, $"the chip named '{name}' has no automation peer");
            Assert.Equal(name, peer!.GetName());
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));

            // The chip is a control for the peer's sake and a chip on screen; a restyle that
            // dropped the rounded fill would have bought the peer with the look.
            var fill = Descendants<Border>(chip).First();
            Assert.Equal(new CornerRadius(8), fill.CornerRadius);
            Assert.Equal(new Thickness(12, 8, 12, 8), fill.Padding);
            Assert.NotNull(fill.Background);
        }
    }

    /// <summary>
    /// Both keys, because the chips were reachable by keyboard before they were readable by one
    /// and neither may be traded for the other.
    /// </summary>
    private static void Chips_AreActivatedByEnterAndSpace(Window window, DashboardViewModel model)
    {
        var dirty = Chips(window).Single(c => AutomationProperties.GetName(c).StartsWith(
            "Show dirty projects", StringComparison.Ordinal));

        foreach (var key in new[] { Key.Enter, Key.Space })
        {
            model.ActiveFilter = "all";
            Assert.True(dirty.Focus(), "the chip refused focus");

            Press(dirty, key);
            Assert.Equal("dirty", model.ActiveFilter);
        }
    }

    /// <summary>
    /// The generated container carries the name a reader enumerates the grid by, and the Border
    /// inside it is what owns the click and the Enter and Space bindings — so it is where the
    /// keyboard lands, and a name it does not carry leaves the reader announcing the window.
    /// </summary>
    private static void EveryCard_NamesTheElementTheKeyboardLandsOn(Window window)
    {
        var containers = Descendants<ListBoxItem>(window)
            .Where(item => item.DataContext is ProjectInfo)
            .ToList();
        Assert.Equal(Fixtures.Count, containers.Count);

        foreach (var container in containers)
        {
            var project = (ProjectInfo)container.DataContext;
            Assert.Equal(project.AccessibleName, AutomationProperties.GetName(container));

            var card = Descendants<Border>(container).First(b => b.Focusable);
            var peer = UIElementAutomationPeer.CreatePeerForElement(card);

            Assert.True(peer is not null,
                $"the focused element of the '{project.DisplayName}' card has no automation peer");
            Assert.Equal(project.AccessibleName, peer!.GetName());
        }
    }

    // ── Fixtures and plumbing ────────────────────────────────────────────────

    private static List<Button> Chips(Window window) =>
        Descendants<Button>(window)
            .Where(b => AutomationProperties.GetName(b).StartsWith(ChipNamePrefix, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// A key press delivered as WPF delivers one. Space presses on the way down and clicks on the
    /// way up, so both halves are raised.
    /// </summary>
    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        Assert.True(source is not null, "the target is not in a presentation source; no key would route to it");

        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyUpEvent
        });
    }

    /// <summary>
    /// One project of every kind whose chip is only shown when it has something to count, so all
    /// eight chips are laid out; a collapsed chip would go unmeasured.
    /// </summary>
    private static List<ProjectInfo> Fixtures =>
    [
        new()
        {
            DirectoryName = "app-packager",
            DisplayName = "app-packager",
            IsRemoteOnly = true,
            RemoteSlug = "owner/app-packager",
            HasManifest = true,
            Manifest = new ProjectManifest { Description = "packages apps" },
            GitStatus = new GitStatus { RemoteUrl = "https://github.com/owner/app-packager" }
        },
        new()
        {
            DirectoryName = "trackr",
            DisplayName = "trackr",
            FullPath = @"C:\projects\trackr",
            HasManifest = true,
            Manifest = new ProjectManifest { Description = "tracks", Notes = "TASK: ship it" },
            OpenIssueCount = 2,
            GitStatus = new GitStatus
            {
                Branch = "main",
                IsDirty = true,
                ModifiedCount = 2,
                UntrackedCount = 1,
                AheadBy = 2,
                RemoteUrl = "https://github.com/owner/trackr"
            }
        },
        new()
        {
            DirectoryName = "widgets",
            DisplayName = "widgets",
            FullPath = @"C:\projects\widgets",
            HasManifest = true,
            Manifest = new ProjectManifest { Description = "widgets" },
            GitStatus = new GitStatus
            {
                Branch = "main",
                RemoteUrl = "https://github.com/owner/gadgets"
            }
        },
        new()
        {
            DirectoryName = "sketchpad",
            DisplayName = "sketchpad",
            FullPath = @"C:\projects\sketchpad",
            GitStatus = new GitStatus { Branch = "main" }
        }
    ];

    private static DashboardViewModel NewViewModel()
    {
        var settings = new SettingsService();
        var gitHub = new GitHubService(settings);
        var model = new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            new ProjectWatcherService(),
            new RepoBusyRegistry(),
            uiPost: callback => callback());

        var projects = Fixtures;
        model.Projects = [.. projects];
        model.FilteredProjects = [.. projects];
        return model;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
