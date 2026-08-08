using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Views.Pages;

/// <summary>
/// The push pane. It holds no state of its own — the plan, the lease values, and the typed gate
/// live on <see cref="ViewModels.Pages.ProjectDetailViewModel"/> — so the surface cannot drift
/// from the rules the view model enforces.
/// </summary>
public partial class ForcePushView
{
    public ForcePushView() => InitializeComponent();

    /// <summary>
    /// Moves keyboard focus into the pane when it opens. Without this the focus stays where it
    /// was — on the page, or on the rewrite wizard's result screen this pane can open over — where
    /// arrow keys and Tab would drive controls the scrim covers.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!IsVisible) return;
            if (!PlanRows.Focus())
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
}
