using Microsoft.Extensions.DependencyInjection;

namespace ProjectDashboard.Services.Surgery;

/// <summary>Composition of the commit-surgery services onto the host's container.</summary>
public static class SurgeryRegistration
{
    /// <summary>
    /// Registers the surgery stack. Every part is a singleton: the driver probes `git --version`
    /// once for the `--empty` spelling git accepts and sweeps its scratch root once, both per
    /// instance, so a fresh instance per action would repeat each of them. The safety rails
    /// (<see cref="Safety.BackupService"/>, <see cref="Safety.RepoBusyRegistry"/>,
    /// <see cref="Safety.RewriteJournal"/>) and <see cref="GitService"/> are the caller's to
    /// register, because the rewrite engine shares those same instances.
    ///
    /// The driver takes a factory: its git-executable and scratch-root parameters are strings
    /// with defaults, not services.
    /// </summary>
    public static IServiceCollection AddCommitSurgery(this IServiceCollection services)
    {
        services.AddSingleton(sp => new RebaseDriver(sp.GetRequiredService<GitService>()));
        services.AddSingleton<CommitSurgery>();
        services.AddSingleton<HistoryEdits>();
        // Explicit factory: the undo this coordinator hands back re-reads what the repository is,
        // and that has to reach the container's own store rather than a private one.
        services.AddSingleton(sp => new SurgeryCoordinator(
            sp.GetRequiredService<Safety.BackupService>(),
            sp.GetRequiredService<Safety.RepoBusyRegistry>(),
            sp.GetRequiredService<GitService>(),
            sp.GetRequiredService<RebaseDriver>(),
            sp.GetRequiredService<CommitSurgery>(),
            sp.GetRequiredService<HistoryEdits>(),
            // Optional by design, and asked for as such: this stack composes onto a container
            // that registered only the rails above, and a required lookup would refuse it.
            history: sp.GetService<Safety.OperationHistory>(),
            manifests: sp.GetService<ManifestStore>()));
        return services;
    }
}
