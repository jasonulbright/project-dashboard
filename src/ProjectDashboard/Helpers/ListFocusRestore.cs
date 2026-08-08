using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProjectDashboard.Helpers;

/// <summary>
/// Focus repair for a list whose items were just replaced. A full refresh rebuilds every row,
/// which destroys the container keyboard focus was on; WPF leaves focus on the list itself with
/// no current row, so the next arrow key starts from the top rather than from where the reader
/// was, and a list that left the tree with its rows drops focus out of the page altogether.
/// </summary>
internal static class ListFocusRestore
{
    /// <summary>
    /// Whether the page should take focus back for <paramref name="list"/>. Only for the list
    /// the reader was actually in, and only from where the rebuild left it: on the list itself,
    /// its row destroyed, or out of the page's content entirely when the list went with it.
    /// Focus anywhere else inside the page is somewhere the reader moved to, and taking it
    /// back would fight them.
    /// </summary>
    internal static bool Wanted(ListBox list, ListBox? lastFocused, IInputElement? focused,
        DependencyObject pageRoot) =>
        ReferenceEquals(list, lastFocused)
        && (ReferenceEquals(focused, list) || LeftTheContent(focused, pageRoot));

    /// <summary>
    /// True when focus is on the page root itself, on nothing, or outside the page — none of
    /// them a place the reader chose.
    /// </summary>
    internal static bool LeftTheContent(IInputElement? focused, DependencyObject pageRoot)
    {
        if (focused is not DependencyObject start) return true;

        for (DependencyObject? node = start; node is not null; node = Parent(node))
            if (ReferenceEquals(node, pageRoot)) return ReferenceEquals(start, pageRoot);
        return true;
    }

    /// <summary>
    /// Puts focus on <paramref name="focused"/>'s row, or on the list when that row has no
    /// container. The focused row is not SelectedItem in an extended selection, where that stays
    /// the first row of the selection rather than the row the reader was on.
    /// </summary>
    internal static bool Apply(ListBox list, object? focused) =>
        list.ItemContainerGenerator.ContainerFromItem(focused ?? list.SelectedItem) is ListBoxItem row
            ? row.Focus()
            : list.Focus();

    /// <summary>
    /// Walks out of a popup as well as up a visual tree: a context menu's items have no visual
    /// parent inside the page, and the logical parent is what connects them to it.
    /// </summary>
    private static DependencyObject? Parent(DependencyObject node) =>
        (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null)
        ?? LogicalTreeHelper.GetParent(node);
}
