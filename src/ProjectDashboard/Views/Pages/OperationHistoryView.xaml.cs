using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Views.Pages;

/// <summary>
/// The operation-history overlay. It holds no state of its own — the records, the filter, and the
/// cross-links live on <see cref="ViewModels.Pages.ProjectDetailViewModel"/> — so the surface
/// cannot drift from what the ledger actually says.
/// </summary>
public partial class OperationHistoryView
{
    public OperationHistoryView() => InitializeComponent();

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
            if (!OperationRows.Focus())
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
}
