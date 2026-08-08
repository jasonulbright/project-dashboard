using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ProjectDashboard.Helpers;

/// <summary>
/// Key handling every confirmation dialog installs. The keystroke that opens a dialog is
/// still down when the dialog takes focus, and Windows auto-repeat then delivers Enter or
/// Space to whichever button holds focus — on a destructive confirmation, the one that runs
/// the operation. A repeat is never a deliberate answer, so it is dropped wherever it could
/// activate; the first non-repeat press still confirms, Esc still cancels, and a text box in the
/// dialog keeps the repeats it types.
/// </summary>
internal static class DialogKeyGuard
{
    /// <summary>True for a key event that only auto-repeat could have produced on a button.</summary>
    internal static bool IsAutoRepeatActivation(Key key, bool isRepeat) =>
        isRepeat && key is Key.Enter or Key.Space;

    /// <summary>
    /// Whether the dialog drops this repeat. A text box consumes its own repeats as typed
    /// characters, so a held space there is a run of spaces in the box and never an answer;
    /// dropping it leaves a reader who holds the key unable to type one at all.
    /// </summary>
    internal static bool ShouldDrop(Key key, bool isRepeat, object? source) =>
        IsAutoRepeatActivation(key, isRepeat) && source is not TextBoxBase;

    /// <summary>
    /// Drops auto-repeat activations before they reach the focused button. Tunnelling, so it
    /// runs whatever inside the dialog holds focus.
    /// </summary>
    internal static void Install(UIElement dialog) =>
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (ShouldDrop(e.Key, e.IsRepeat, e.OriginalSource)) e.Handled = true;
        };
}
