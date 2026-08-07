using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Views.Windows;

/// <summary>
/// Multi-line commit message entry for reword and squash. Enter inserts a newline — a commit
/// message is a body, not a single line — so Ctrl+Enter is what accepts.
/// </summary>
public partial class CommitMessagePromptWindow
{
    private string? _result;

    public CommitMessagePromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        };
    }

    /// <summary>The entered text, or null when the dialog was cancelled or dismissed.</summary>
    public static Task<string?> ShowAsync(string title, string prompt, string initialText)
    {
        var window = new CommitMessagePromptWindow
        {
            Title = title,
            Owner = Application.Current?.MainWindow
        };
        window.TitleBarControl.Title = title;
        window.PromptText.Text = prompt;
        window.MessageInput.Text = initialText;
        window.ShowDialog();
        return Task.FromResult(window._result);
    }

    private void OnSave(object sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _result = null;
        DialogResult = false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        Accept();
        e.Handled = true;
    }

    private void Accept()
    {
        var text = MessageInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _result = text;
        DialogResult = true;
    }
}
