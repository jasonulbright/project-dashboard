using System.Windows;
using System.Windows.Input;

namespace ProjectDashboard.Helpers;

/// <summary>
/// Key handling every confirmation dialog installs. The keystroke that opens a dialog is
/// still down when the dialog takes focus, and Windows auto-repeat then delivers Enter or
/// Space to whichever button holds focus — on a destructive confirmation, the one that runs
/// the operation. A repeat is never a deliberate answer, so it is dropped; the first
/// non-repeat press still confirms, and Esc still cancels.
/// </summary>
internal static class DialogKeyGuard
{
    /// <summary>True for a key event that only auto-repeat could have produced on a button.</summary>
    internal static bool IsAutoRepeatActivation(Key key, bool isRepeat) =>
        isRepeat && key is Key.Enter or Key.Space;

    /// <summary>
    /// Drops auto-repeat activations before they reach the focused button. Tunnelling, so it
    /// runs whatever inside the dialog holds focus.
    /// </summary>
    internal static void Install(UIElement dialog) =>
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (IsAutoRepeatActivation(e.Key, e.IsRepeat)) e.Handled = true;
        };
}
