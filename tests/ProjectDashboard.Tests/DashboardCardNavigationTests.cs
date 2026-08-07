using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Xml;

namespace ProjectDashboard.Tests;

/// <summary>
/// Card-grid keyboard traversal, measured rather than described. The probe reads the
/// keyboard-navigation modes out of DashboardPage.xaml and rebuilds the grid's focus
/// structure with them, so a mode changed in markup changes what these tests measure;
/// a focusable control added inside a card can silently capture the arrow keys the
/// cheat sheet promises move between cards, and that is invisible to the view model.
/// WPF needs an STA thread and a real presentation source for focus to move.
/// </summary>
public class DashboardCardNavigationTests
{
    private const string PageXaml = "src/ProjectDashboard/Views/Pages/DashboardPage.xaml";

    [Fact]
    public void ArrowKeys_FromACard_ReachTheNextCard()
    {
        var right = "";
        var down = "";
        RunSta(() =>
        {
            using var probe = BuildProbe(cardCount: 6);
            right = Describe(probe.Cards[0].PredictFocus(FocusNavigationDirection.Right));
            down = Describe(probe.Cards[0].PredictFocus(FocusNavigationDirection.Down));
        });

        Assert.Equal("c1", right);
        Assert.Equal("c2", down);
    }

    [Fact]
    public void Tab_LeavesTheGridAfterTheFocusedCardsOwnActions()
    {
        var path = "";
        RunSta(() =>
        {
            using var probe = BuildProbe(cardCount: 4);
            path = string.Join(">", TabWalk(probe, maxSteps: 20));
        });

        // One card's three quick actions, then out. Tab is how the keyboard crosses
        // the grid, and a stop on every card and every card's buttons puts whatever
        // follows a forty-project grid a hundred and sixty presses away.
        Assert.Equal("fetch0>pull0>push0>afterGrid", path);
    }

    // ── Probe ────────────────────────────────────────────────────────────────

    private sealed class Probe : IDisposable
    {
        public required HwndSource Source { get; init; }
        public required List<Border> Cards { get; init; }
        public required Button AfterGrid { get; init; }

        public void Dispose() => Source.Dispose();
    }

    private const int Width = 700;
    private const int Height = 600;

    /// <summary>
    /// Mirrors the grid's focus structure — scroller, items control, one focusable
    /// Border per card, three action buttons inside each — with the navigation modes
    /// the page declares. Two cards fit per row at this width.
    /// </summary>
    private static Probe BuildProbe(int cardCount)
    {
        var markup = new XmlDocument();
        markup.LoadXml(RepoSource.Read(PageXaml));
        var gridElement = Element(markup, "//*[local-name()='ScrollViewer']/*[local-name()='ItemsControl']");
        var cardElement = Element(markup, "//*[local-name()='DataTemplate']/*[local-name()='Border']");
        var actionsElement = Element(markup,
            "//*[local-name()='StackPanel'][*[local-name()='Button'][@Content='Fetch']]");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var items = new ItemsControl { Name = "cards" };
        KeyboardNavigation.SetTabNavigation(items, TabMode(gridElement));
        KeyboardNavigation.SetDirectionalNavigation(items, DirectionalMode(gridElement));
        items.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));

        var cards = new List<Border>();
        for (var i = 0; i < cardCount; i++)
        {
            var card = new Border
            {
                Name = "c" + i,
                Width = 320,
                MinHeight = 200,
                Margin = new Thickness(8),
                Focusable = true,
            };
            KeyboardNavigation.SetTabNavigation(card, TabMode(cardElement));
            KeyboardNavigation.SetDirectionalNavigation(card, DirectionalMode(cardElement));

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            KeyboardNavigation.SetTabNavigation(actions, TabMode(actionsElement));
            KeyboardNavigation.SetDirectionalNavigation(actions, DirectionalMode(actionsElement));
            foreach (var verb in new[] { "fetch", "pull", "push" })
                actions.Children.Add(new Button { Name = verb + i, Content = verb });

            var body = new Grid();
            body.Children.Add(actions);
            card.Child = body;
            items.Items.Add(card);
            cards.Add(card);
        }

        var scroller = new ScrollViewer { Content = items };
        Grid.SetRow(scroller, 0);
        root.Children.Add(scroller);

        var after = new Button { Name = "afterGrid", Content = "after" };
        Grid.SetRow(after, 1);
        root.Children.Add(after);

        // WS_CLIPCHILDREN only: the probe window stays hidden, and focus is thread-local
        // so it never has to be shown for keyboard navigation to run for real.
        var source = new HwndSource(new HwndSourceParameters("card-grid-probe", Width, Height)
        {
            WindowStyle = 0x02000000,
        })
        {
            RootVisual = root,
        };
        root.Measure(new Size(Width, Height));
        root.Arrange(new Rect(0, 0, Width, Height));
        root.UpdateLayout();
        SetFocus(source.Handle);

        return new Probe { Source = source, Cards = cards, AfterGrid = after };
    }

    /// <summary>Every element Tab reaches, starting from the first card, until it leaves the grid.</summary>
    private static List<string> TabWalk(Probe probe, int maxSteps)
    {
        var visited = new List<string>();
        Assert.True(probe.Cards[0].Focus(), "the probe's first card never took focus");

        var cursor = (FrameworkElement)probe.Cards[0];
        for (var step = 0; step < maxSteps; step++)
        {
            if (!cursor.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
            {
                visited.Add("(tab-refused)");
                break;
            }
            if (Keyboard.FocusedElement is not FrameworkElement next)
            {
                visited.Add("(focus-lost)");
                break;
            }
            visited.Add(Describe(next));
            cursor = next;
            if (ReferenceEquals(next, probe.AfterGrid)) break;
        }
        return visited;
    }

    private static XmlElement Element(XmlDocument markup, string xpath)
    {
        var node = markup.SelectSingleNode(xpath) as XmlElement;
        Assert.True(node is not null, $"markup shape moved; nothing matched {xpath} in {PageXaml}");
        return node!;
    }

    private static KeyboardNavigationMode TabMode(XmlElement element) =>
        Mode(element, "KeyboardNavigation.TabNavigation");

    private static KeyboardNavigationMode DirectionalMode(XmlElement element) =>
        Mode(element, "KeyboardNavigation.DirectionalNavigation");

    private static KeyboardNavigationMode Mode(XmlElement element, string attribute)
    {
        var value = element.GetAttribute(attribute);
        return value.Length == 0 ? KeyboardNavigationMode.Continue : Enum.Parse<KeyboardNavigationMode>(value);
    }

    private static string Describe(DependencyObject? target) => target switch
    {
        null => "(none)",
        FrameworkElement element when element.Name.Length > 0 => element.Name,
        _ => target.GetType().Name,
    };

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

    /// <summary>Focus follows the thread's active window; without it the probe has no keyboard focus to move.</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);
}
