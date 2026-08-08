using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ProjectDashboard.Helpers;

namespace ProjectDashboard.Tests;

/// <summary>
/// Auto-repeat must never answer a confirmation. The keystroke that opens a destructive
/// dialog is still down when the dialog takes focus, and the primary button is both focused
/// and styled Danger — so one held Enter confirms a discard the reader never read.
/// </summary>
public class DialogKeyGuardTests
{
    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void AHeldActivationKey_IsDropped(Key key) =>
        Assert.True(DialogKeyGuard.IsAutoRepeatActivation(key, isRepeat: true));

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void AFirstPressOfAnActivationKey_StillAnswers(Key key) =>
        Assert.False(DialogKeyGuard.IsAutoRepeatActivation(key, isRepeat: false));

    /// <summary>
    /// Only activation repeats are dropped: a held Esc still cancels, and a held arrow or
    /// Tab still moves focus, so the dialog stays usable from the keyboard alone.
    /// </summary>
    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Left)]
    [InlineData(Key.Down)]
    public void AHeldNonActivationKey_IsUntouched(Key key)
    {
        Assert.False(DialogKeyGuard.IsAutoRepeatActivation(key, isRepeat: true));
        Assert.False(DialogKeyGuard.IsAutoRepeatActivation(key, isRepeat: false));
    }

    /// <summary>
    /// The predicate decides nothing on its own: a dialog that never installs it answers a
    /// held key exactly as before. IsRepeat comes from the raw input stream and cannot be
    /// synthesized, so the wiring is asserted at the source.
    /// </summary>
    [Theory]
    [InlineData("ViewModels/Pages/ProjectDetailViewModel.Work.cs")]
    [InlineData("Views/Windows/TextPromptWindow.xaml.cs")]
    public void EveryConfirmationSurface_InstallsTheGuard(string relativePath) =>
        Assert.Contains("DialogKeyGuard.Install(", File.ReadAllText(SourceFile(relativePath)));

    private static string SourceFile(string relativePath, [CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", relativePath));
        Assert.True(File.Exists(path), $"source not found at {path}");
        return path;
    }
}
