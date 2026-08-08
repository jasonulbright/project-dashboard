using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ProjectDashboard.Helpers;

namespace ProjectDashboard.Tests;

/// <summary>
/// The two library facts the commit-surgery view layer rests on, neither of which any
/// view-model test can reach: a context menu declared on a list binds to that list's data
/// context, and a WPF-UI button honours an access key in its content. WPF controls require an
/// STA thread; no Application is needed.
/// </summary>
public class SurgeryViewBindingTests
{
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>
    /// The commit list's context menu is the only route to reword, drop, reset, revert and
    /// cherry-pick, and it carries no data context of its own: every command binding on it
    /// resolves against the list's, which is the page's view-model. A menu moved into an item
    /// template would inherit the commit instead and bind to nothing.
    /// </summary>
    [Fact]
    public void AContextMenuOnAList_ResolvesItsBindingsAgainstTheListsDataContext()
    {
        RunSta(() =>
        {
            var viewModel = new MenuHost();
            var list = new ListBox { DataContext = viewModel };
            var item = new MenuItem { Header = "Reword…" };
            item.SetBinding(MenuItem.CommandProperty, new Binding(nameof(MenuHost.Reword)));
            var menu = new ContextMenu();
            menu.Items.Add(item);
            list.ContextMenu = menu;

            var window = new Window { Content = list, Width = 300, Height = 300, ShowActivated = false };
            window.Show();
            try
            {
                menu.PlacementTarget = list;
                menu.IsOpen = true;

                Assert.Same(viewModel, menu.DataContext);
                Assert.Same(viewModel, item.DataContext);
                Assert.Same(viewModel.Reword, item.Command);
            }
            finally
            {
                menu.IsOpen = false;
                window.Close();
            }
        });
    }

    /// <summary>
    /// The planning dialog's buttons carry access keys as underscores in their content. The
    /// WPF-UI template must recognise them, or every one of those labels renders the underscore
    /// literally.
    /// </summary>
    [Fact]
    public void AWpfUiButton_ReadsAnUnderscoreInItsContentAsAnAccessKey()
    {
        RunSta(() =>
        {
            var button = new Wpf.Ui.Controls.Button { Content = "Move _up" };
            var host = new Border { Child = button };
            host.Measure(new Size(400, 200));
            host.Arrange(new Rect(0, 0, 400, 200));

            var presenter = Descendants(button).OfType<ContentPresenter>().FirstOrDefault();
            Assert.NotNull(presenter);
            Assert.True(presenter.RecognizesAccessKey);
            Assert.Single(Descendants(button).OfType<AccessText>());
        });
    }

    /// <summary>
    /// Reword and squash discard a whitespace-only message, so the prompt's Save must be
    /// unavailable while the box holds one. Without the gate the button is live and its click
    /// closes nothing — a control that looks broken rather than refused.
    /// </summary>
    [Fact]
    public void TheCommitMessagePromptsSaveButton_IsGatedOnANonWhitespaceMessage()
    {
        var converter = new HasNonWhitespaceTextConverter();
        Assert.Equal(false, converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert("", typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(" \t\r\n ", typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(true, converter.Convert("a message", typeof(bool), null, CultureInfo.InvariantCulture));

        var declaration = Regex.Match(WindowMarkup("CommitMessagePromptWindow.xaml"), @"<ui:Button x:Name=""SaveButton""[^>]*>").Value;
        Assert.Contains(
            @"IsEnabled=""{Binding Text, ElementName=MessageInput, Converter={StaticResource HasNonWhitespaceTextConverter}}""",
            declaration);
    }

    /// <summary>
    /// A drop or squash mark holds a typed reword aside and restores it when the mark is
    /// lifted. Without a bound marker the row renders identically to one that never carried a
    /// message, so nothing on screen separates "held, and coming back" from "never typed".
    /// The marker's visibility resolves through a converter key the merged WPF-UI dictionary
    /// declares, not this project. Whether that key still resolves is proven at run time by
    /// CommitGraphView and BackupsView, which bind through the same key on load; a key that
    /// stopped resolving fails there, so this test asserts only the binding.
    /// </summary>
    [Fact]
    public void APlanRowHoldingADisplacedMessage_ShowsAMarkerBoundToThatState()
    {
        var marker = Regex.Match(
            WindowMarkup("HistoryPlanWindow.xaml"),
            @"<TextBlock[^>]*Text=""message held""[^>]*/>",
            RegexOptions.Singleline).Value;

        Assert.Contains(
            @"Visibility=""{Binding HasDisplacedMessage, Converter={StaticResource BooleanToVisibilityConverter}}""",
            marker);
        Assert.Contains(@"AutomationProperties.Name=""A new message for this commit is held aside by its mark""", marker);
    }

    private static string WindowMarkup(string fileName, [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..",
            "src", "ProjectDashboard", "Views", "Windows", fileName));
        Assert.True(File.Exists(path), $"window markup not found at {path}");
        return File.ReadAllText(path);
    }

    private sealed class MenuHost
    {
        public ICommand Reword { get; } = new InertCommand();

        private sealed class InertCommand : ICommand
        {
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) { }
        }
    }
}
