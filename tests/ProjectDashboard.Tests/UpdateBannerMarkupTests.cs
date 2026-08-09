using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The update notice as a reader meets it: laid out from shipped markup and read through
/// automation peers. The notice is the whole user-facing half of the feature — a banner that
/// laid out but announced nothing, or whose actions carried no peer, would leave a
/// keyboard-only reader with an update they cannot see or act on, and no assertion against
/// the view model can see that.
/// </summary>
[Collection("shipped-markup")]
public class UpdateBannerMarkupTests
{
    [Fact]
    public void TheUpdateNotice_IsReadableAndItsActionsAreReachable()
        => StaHost.Run(() =>
        {
            var model = NewViewModel();
            model.UpdateBannerText = "Project Dashboard v9.0.0 is available — this build is v2.0.1.0.";
            model.UpdateBannerVisible = true;

            var window = new Window
            {
                Content = new DashboardPage(model), Width = 1400, Height = 900, ShowActivated = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var notice = Descendants<TextBlock>(window).Single(t =>
                    AutomationProperties.GetAutomationId(t) == "DashboardUpdateBannerText");

                Assert.True(notice.IsVisible, "the update notice is not laid out when it is visible");
                Assert.Equal(model.UpdateBannerText, notice.Text);

                // A notice that arrives while the reader is on the page has to announce itself:
                // the check completes long after the page was read.
                Assert.Equal(AutomationLiveSetting.Polite,
                    new FrameworkElementAutomationPeer(notice).GetLiveSetting());

                var view = NamedButton(window, "View the release on GitHub");
                var dismiss = NamedButton(window, "Dismiss the update notice");

                Assert.Same(model.OpenUpdateReleaseCommand, view.Command);
                Assert.Same(model.DismissUpdateBannerCommand, dismiss.Command);

                // Both actions have to reach a reader: an element WPF builds no peer for is
                // absent from the automation tree, so focus arriving on it announces nothing.
                foreach (var action in new[] { view, dismiss })
                {
                    var peer = UIElementAutomationPeer.CreatePeerForElement(action);
                    Assert.True(peer is not null,
                        $"the '{AutomationProperties.GetName(action)}' action has no automation peer");
                    Assert.Equal(AutomationProperties.GetName(action), peer!.GetName());
                    Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                }

                // Dismiss is the half of the pair that is safe to press: the other opens a
                // browser. The key press is the reader's own path, and it must reach the
                // command rather than only the binding.
                Assert.True(dismiss.Focus(), "the dismiss action refused focus");
                Press(dismiss, Key.Space);

                Assert.False(model.UpdateBannerVisible);
                window.UpdateLayout();
                Assert.False(notice.IsVisible);
            }
            finally { window.Close(); }
        });

    /// <summary>
    /// A key press delivered as WPF delivers one, and synchronously: the host runs bodies on
    /// its own loop rather than a dispatcher one, so an action posted to the queue — which is
    /// what <c>IInvokeProvider.Invoke</c> does by contract — would never run at all.
    /// Space presses on the way down and clicks on the way up, so both halves are raised.
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

    private static System.Windows.Controls.Button NamedButton(Window window, string name) =>
        Descendants<System.Windows.Controls.Button>(window)
            .Single(b => AutomationProperties.GetName(b) == name);

    private static DashboardViewModel NewViewModel()
    {
        var settings = new SettingsService();
        var gitHub = new GitHubService(settings);
        return new DashboardViewModel(
            new ProjectDiscoveryService(new GitService(), gitHub, settings, new ManifestStore()),
            navigationService: null!,
            settings,
            gitHub,
            new GitService(),
            new ProjectWatcherService(),
            new RepoBusyRegistry(),
            uiPost: callback => callback());
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
