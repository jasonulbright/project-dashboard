using System.Windows;
using ProjectDashboard.ViewModels.Windows;

namespace ProjectDashboard.Views.Windows;

/// <summary>
/// Column, path-mode, and filter choices for a portfolio export, over a live preview. Nothing
/// here writes a file: the accepted view model is handed back, and the caller runs the same
/// destination-and-write path the export always had.
/// </summary>
public partial class ExportDialogWindow
{
    private bool _accepted;

    private ExportDialogWindow(ExportDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>True when the reader chose to export; the choices stay on the view model.</summary>
    public static Task<bool> ShowAsync(ExportDialogViewModel viewModel)
    {
        var window = new ExportDialogWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        return Task.FromResult(window._accepted);
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _accepted = false;
        DialogResult = false;
    }
}
