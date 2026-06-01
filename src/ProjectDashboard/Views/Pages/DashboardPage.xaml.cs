using System.Windows.Controls;
using System.Windows.Input;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class DashboardPage
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // Keyboard activation for the summary-bar filter chips: Enter/Space invokes the focused
    // chip's existing left-click command, so they work without a mouse.
    private void SummaryBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        if (Keyboard.FocusedElement is not System.Windows.Controls.Border border) return;

        var mouseBinding = border.InputBindings.OfType<MouseBinding>().FirstOrDefault();
        if (mouseBinding?.Command?.CanExecute(mouseBinding.CommandParameter) == true)
        {
            mouseBinding.Command.Execute(mouseBinding.CommandParameter);
            e.Handled = true;
        }
    }
}
