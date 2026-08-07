using Microsoft.Extensions.DependencyInjection;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.Services.Surgery;

namespace ProjectDashboard.Tests;

/// <summary>
/// The composition the app performs at startup, over the same rails it registers. Nothing here
/// touches a repository: resolving is the whole subject.
/// </summary>
public class SurgeryRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<GitService>();
        services.AddSingleton<RepoBusyRegistry>();
        services.AddSingleton<RewriteJournal>();
        services.AddSingleton<BackupService>();
        services.AddCommitSurgery();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    [Fact]
    public void TheSurgeryStack_ResolvesFromTheRailsTheAppRegisters()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<RebaseDriver>());
        Assert.NotNull(provider.GetRequiredService<CommitSurgery>());
        Assert.NotNull(provider.GetRequiredService<HistoryEdits>());
        Assert.NotNull(provider.GetRequiredService<SurgeryCoordinator>());
    }

    [Fact]
    public void EveryPartOfTheStack_IsOneSharedInstance()
    {
        using var provider = BuildProvider();

        // The driver holds the probed `--empty` spelling and the once-per-instance scratch
        // sweep, so a second instance repeats a `git --version` launch and a directory walk.
        Assert.Same(provider.GetRequiredService<RebaseDriver>(), provider.GetRequiredService<RebaseDriver>());
        Assert.Same(provider.GetRequiredService<CommitSurgery>(), provider.GetRequiredService<CommitSurgery>());
        Assert.Same(provider.GetRequiredService<HistoryEdits>(), provider.GetRequiredService<HistoryEdits>());
        Assert.Same(provider.GetRequiredService<SurgeryCoordinator>(), provider.GetRequiredService<SurgeryCoordinator>());
    }

    /// <summary>
    /// The coordinator's driver, surgery and edits parameters are optional, so a container that
    /// failed to supply them would silently hand back a coordinator holding private instances —
    /// the busy lease would still work while the driver's probe and sweep ran twice.
    /// </summary>
    [Fact]
    public void TheCoordinator_TakesTheRegisteredDriverRatherThanConstructingItsOwn()
    {
        using var provider = BuildProvider();
        var driver = provider.GetRequiredService<RebaseDriver>();

        var coordinator = provider.GetRequiredService<SurgeryCoordinator>();

        var field = typeof(SurgeryCoordinator).GetField("_driver",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.Same(driver, field!.GetValue(coordinator));
    }
}
