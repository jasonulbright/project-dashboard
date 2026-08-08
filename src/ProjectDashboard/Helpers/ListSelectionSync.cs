using System.Windows.Controls;

namespace ProjectDashboard.Helpers;

/// <summary>
/// The two directions of one file selection. WPF owns the selection a reader makes with the
/// mouse and the keyboard; the view model owns the one it writes back — after a refresh, and
/// when a selection on one side clears the other side's. Suppression is per list, not global:
/// a push into one list writes the OTHER list's selection through the view model, and that
/// write has to land, or rows stay highlighted against a view model holding nothing and the
/// buttons refuse against a selection the reader can see.
/// </summary>
internal sealed class ListSelectionSync
{
    /// <summary>The list currently being written to, whose own notifications are its echo.</summary>
    private ListBox? _writing;

    /// <summary>
    /// Reports a list's own selection to the view model. Suppressed only while that same list is
    /// being written to, where the notification is the write's echo rather than a reader's choice.
    /// </summary>
    internal void Push(ListBox list, Action push) => Guarded(list, push);

    /// <summary>
    /// Writes a selection onto a list, unless that list is the one whose push is in flight —
    /// which is the reader's own selection, and re-writing it would collapse a multi-selection.
    /// </summary>
    internal void Restore(ListBox list, IReadOnlyList<object> wanted) =>
        Guarded(list, () =>
        {
            list.SelectedItems.Clear();
            foreach (var item in wanted) list.SelectedItems.Add(item);
        });

    /// <summary>
    /// Runs one direction with <paramref name="list"/> marked as the list being written to. The
    /// previous mark is restored rather than cleared: a push on one list runs a restore on the
    /// other inside it, and the push's own suppression has to outlive that.
    /// </summary>
    private void Guarded(ListBox list, Action action)
    {
        if (ReferenceEquals(_writing, list)) return;
        var outer = _writing;
        _writing = list;
        try { action(); }
        finally { _writing = outer; }
    }
}
