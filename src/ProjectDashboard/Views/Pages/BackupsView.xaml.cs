using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Views.Pages;

/// <summary>
/// The Backups pane. It holds no state of its own — the list, the gates, and every verdict live
/// on <see cref="ViewModels.Pages.ProjectDetailViewModel"/> — so the surface cannot drift from
/// the rules the view model enforces.
/// </summary>
public partial class BackupsView
{
    public BackupsView() => InitializeComponent();

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
            if (!BackupChoices.Focus())
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
}
