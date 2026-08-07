using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

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
