using ProjectDashboard;

namespace ProjectDashboard.Tests;

/// <summary>
/// The application shuts down when its main window closes, and that window is shown by a hosted
/// service during startup. A failure before it exists therefore has no window to report into and
/// nothing whose closing would end the process — marking it handled leaves a live, windowless
/// process no user can see or quit. The guard is what separates that failure from one the
/// running app can survive.
/// </summary>
public class StartupGuardTests
{
    [Fact]
    public void BeforeStartupCompletes_AFailureIsFatal()
        => Assert.True(new App.StartupGuard().IsFatal);

    [Fact]
    public void OnceStartupCompletes_AFailureIsNotFatal()
    {
        var guard = new App.StartupGuard();
        guard.MarkComplete();

        Assert.False(guard.IsFatal);
    }

    [Fact]
    public void OnlyTheFirstFailure_BeginsTheExit()
    {
        var guard = new App.StartupGuard();

        Assert.True(guard.TryBeginExit());
        Assert.False(guard.TryBeginExit());
        Assert.False(guard.TryBeginExit());
    }

    /// <summary>
    /// The premise the guard exists for. A shutdown mode that did not wait on the main window
    /// would end the process on its own and the guard would be answering a question nobody asks.
    /// </summary>
    [Fact]
    public void TheApplication_ShutsDownWhenItsMainWindowCloses()
        => Assert.Contains(
            "ShutdownMode=\"OnMainWindowClose\"",
            RepoSource.Read("src/ProjectDashboard/App.xaml"));
}
