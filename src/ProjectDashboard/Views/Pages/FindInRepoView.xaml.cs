using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Views.Pages;

/// <summary>
/// Find in one repository. It holds no state of its own — the term, the scope, the rows, and the
/// refusals live on <see cref="ViewModels.Pages.ProjectDetailViewModel"/> — so the surface cannot
/// drift from the rules the view model enforces.
/// </summary>
public partial class FindInRepoView
{
    public FindInRepoView() => InitializeComponent();

    /// <summary>
    /// Hands the newly checked switch to the view model, which is where the refusal to re-run for a
    /// scope already in force lives — so a switch re-checked by the view model's own write is inert
    /// rather than a second fan-out.
    /// </summary>
    private void FindScope_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string scope }
            && DataContext is ViewModels.Pages.ProjectDetailViewModel viewModel)
            viewModel.SetFindScopeCommand.Execute(scope);
    }

    /// <summary>
    /// Moves keyboard focus into the term box when the pane opens. Without this the focus stays on
    /// the page behind the scrim, where every keystroke would drive controls the scrim covers.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!IsVisible) return;
            if (!FindTermBox.Focus())
                MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }
}
