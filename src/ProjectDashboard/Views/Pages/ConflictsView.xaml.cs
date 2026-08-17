using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Views.Pages;

/// <summary>
/// The conflict panel. It holds no state of its own — the file list, the stage preview, the
/// refusals and the two sequencer buttons all live on
/// <see cref="ViewModels.Pages.ProjectDetailViewModel"/> — so the surface cannot drift from the
/// rules the view model enforces.
/// </summary>
public partial class ConflictsView
{
    public ConflictsView() => InitializeComponent();

    /// <summary>
    /// Moves keyboard focus into the pane when it opens. Without this the focus stays on the page
    /// behind the scrim, where arrow keys and Tab would drive controls the scrim covers.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!IsVisible) return;
            if (!ConflictRowList.Focus())
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
}
