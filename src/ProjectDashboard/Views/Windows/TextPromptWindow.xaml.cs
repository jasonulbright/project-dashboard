using System.Windows;

namespace ProjectDashboard.Views.Windows;

/// <summary>
/// Single-line typed confirmation for outward-facing repository changes. The value the
/// caller compares against is never shown as a default: an action confirmed by pressing
/// Enter on a pre-filled box is not a typed confirmation.
///
/// A whitespace-only entry cannot match any repository name, so Confirm stays disabled
/// while the box holds one.
/// </summary>
public partial class TextPromptWindow
{
    private string? _result;

    public TextPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ValueInput.Focus();
    }

    /// <summary>The entered text, or null when the dialog was cancelled or dismissed.</summary>
    public static Task<string?> ShowAsync(string title, string prompt, string confirmLabel)
    {
        var window = new TextPromptWindow
        {
            Title = title,
            Owner = Application.Current?.MainWindow
        };
        window.TitleBarControl.Title = title;
        window.PromptText.Text = prompt;
        window.ConfirmButton.Content = confirmLabel;
        window.ShowDialog();
        return Task.FromResult(window._result);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var text = ValueInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _result = text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = null;
        DialogResult = false;
    }
}
