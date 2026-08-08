using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProjectDashboard.Helpers;

namespace ProjectDashboard.Tests;

/// <summary>
/// Focus restore across a full refresh (X-08), on a live focus scope. The replica carries the
/// page's shape — a focusable root holding a list whose items are replaced wholesale — because
/// the loss is a property of that arrangement, not of what the list contains.
/// </summary>
public class ListFocusRestoreTests
{
    private sealed record Row(string Path);

    /// <summary>
    /// The library fact the restore answers, and the proof the test below is not vacuous:
    /// replacing a list's items destroys the container focus was on and leaves focus on the
    /// list itself. The row is gone, so the next arrow key starts from the top rather than
    /// from where the reader was.
    /// </summary>
    [Fact]
    public void ReplacingAListsItems_LosesTheRowFocusWasOn()
    {
        RunSta(() =>
        {
            var (root, list) = ShowReplica();
            FocusRow(list, 1);
            Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);

            Rebuild(list);

            Assert.Same(list, Keyboard.FocusedElement);
            Assert.True(ListFocusRestore.Wanted(list, list, Keyboard.FocusedElement, root));
        });
    }

    /// <summary>Focus on the page root, on nothing, or outside the page is equally a loss.</summary>
    [Fact]
    public void FocusOffThePageContent_ReadsAsALoss()
    {
        RunSta(() =>
        {
            var (root, list) = ShowReplica();

            Assert.True(ListFocusRestore.LeftTheContent(null, root));
            Assert.True(ListFocusRestore.LeftTheContent(root, root));
            Assert.True(ListFocusRestore.LeftTheContent(new Button(), root));
            Assert.False(ListFocusRestore.LeftTheContent(list, root));
        });
    }

    [Fact]
    public void TheRestore_PutsFocusBackOnTheReselectedRow()
    {
        RunSta(() =>
        {
            var (root, list) = ShowReplica();
            FocusRow(list, 1);
            Rebuild(list);

            Assert.True(ListFocusRestore.Wanted(list, list, Keyboard.FocusedElement, root));
            Assert.True(ListFocusRestore.Apply(list, list.SelectedItem));

            Assert.True(list.IsKeyboardFocusWithin);
            Assert.Same(list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem),
                Keyboard.FocusedElement);
        });
    }

    /// <summary>
    /// In an extended selection the reader's row is the one they last arrowed onto, and
    /// SelectedItem is the first row of the selection — so focus taken back to SelectedItem
    /// lands somewhere the reader never was, and the next arrow key walks from there.
    /// </summary>
    [Fact]
    public void TheRestore_PrefersTheFocusedRowOverTheFirstOfASelection()
    {
        RunSta(() =>
        {
            var (_, list) = ShowReplica();
            list.SelectionMode = SelectionMode.Extended;
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[2]);
            list.UpdateLayout();
            var focused = list.Items[2];

            Assert.NotSame(focused, list.SelectedItem);
            Assert.True(ListFocusRestore.Apply(list, focused));

            Assert.Same(list.ItemContainerGenerator.ContainerFromItem(focused), Keyboard.FocusedElement);
        });
    }

    /// <summary>
    /// A reader who moved on — into the commit box, say — keeps the focus they chose. The
    /// restore only repairs a loss, and a loss is focus landing outside the page's content.
    /// </summary>
    [Fact]
    public void FocusTheReaderMovedElsewhereInThePage_IsLeftAlone()
    {
        RunSta(() =>
        {
            var (root, list) = ShowReplica();
            var box = (TextBox)((Panel)root).Children[1];
            FocusRow(list, 1);
            Rebuild(list);
            box.Focus();

            Assert.False(ListFocusRestore.Wanted(list, list, Keyboard.FocusedElement, root));
        });
    }

    /// <summary>A rebuild of a list the reader was never in takes no focus from where they are.</summary>
    [Fact]
    public void AListTheReaderWasNeverIn_IsNotGivenFocus()
    {
        RunSta(() =>
        {
            var (root, list) = ShowReplica();
            Rebuild(list);

            Assert.False(ListFocusRestore.Wanted(list, lastFocused: null, Keyboard.FocusedElement, root));
        });
    }

    /// <summary>A list left with no selection still takes focus back, on the list itself.</summary>
    [Fact]
    public void AnEmptiedList_TakesFocusBackOnItself()
    {
        RunSta(() =>
        {
            var (_, list) = ShowReplica();
            FocusRow(list, 1);
            list.ItemsSource = new List<Row>();
            list.UpdateLayout();

            Assert.True(ListFocusRestore.Apply(list, null));
            Assert.True(list.IsKeyboardFocused);
        });
    }

    /// <summary>
    /// Root shaped like the page: focusable itself, with a list and one other control. The window
    /// is shown unactivated and closed with the body: keyboard focus is a desktop-wide resource,
    /// and a window that takes activation pulls it out of whatever another test class is holding
    /// on its own thread.
    /// </summary>
    private static (FrameworkElement Root, ListBox List) ShowReplica()
    {
        var list = new ListBox
        {
            ItemsSource = new List<Row> { new("a.txt"), new("b.txt"), new("c.txt") },
            Height = 200
        };
        var root = new StackPanel { Focusable = true };
        root.Children.Add(list);
        root.Children.Add(new TextBox { Width = 200, Height = 24 });

        var window = new Window { Content = root, Width = 400, Height = 400, ShowActivated = false };
        _windows.Value!.Add(window);
        window.Show();
        root.UpdateLayout();
        return (root, list);
    }

    /// <summary>Windows the running body opened, closed when it ends however it ends.</summary>
    private static readonly ThreadLocal<List<Window>> _windows = new(() => []);

    private static void FocusRow(ListBox list, int index)
    {
        list.SelectedIndex = index;
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        Assert.True(row.Focus(), "the replica's row did not take focus");
    }

    /// <summary>What a full refresh does: fresh instances for every row, then re-select by path.</summary>
    private static void Rebuild(ListBox list)
    {
        var wanted = (list.SelectedItem as Row)?.Path;
        var rebuilt = new List<Row> { new("a.txt"), new("b.txt"), new("c.txt") };
        list.ItemsSource = rebuilt;
        list.SelectedItem = rebuilt.FirstOrDefault(r => r.Path == wanted);
        list.UpdateLayout();
    }

    /// <summary>WPF focus needs an STA thread; no Application is needed for a bare replica.</summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally
            {
                foreach (var window in _windows.Value!) window.Close();
                _windows.Value!.Clear();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test body did not complete");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
