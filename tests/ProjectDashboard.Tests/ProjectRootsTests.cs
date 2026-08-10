using System.Security.AccessControl;
using System.Security.Principal;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The root list as a shape on disk: what a settings file written before it becomes, and what
/// the singular compatibility field means once the list exists. A migration that loses an
/// exclusion, or a singular edit that is silently discarded, is a settings wipe the user is
/// never told about.
/// </summary>
[Collection("app-data-sandbox")]
public class ProjectRootMigrationTests
{
    public ProjectRootMigrationTests() => TestSandbox.ResetDataDir();

    [Fact]
    public void ASettingsFileWithOnlyTheSingularRoot_BecomesOneRootCarryingItsExclusions()
    {
        File.WriteAllText(AppPaths.SettingsFile,
            """{"ProjectsRootPath": "C:\\legacy", "ExcludedDirectories": ["Internal", "games"]}""");

        var loaded = new SettingsService().Load();

        var root = Assert.Single(loaded.ProjectRoots);
        Assert.Equal(@"C:\legacy", root.Path);
        Assert.Equal(["Internal", "games"], root.ExcludedDirectories);
        Assert.True(root.Enabled);
        Assert.Equal(ProjectRootSettings.MinDepth, root.MaxDepth);

        // The old key still round-trips, and the default write target is the migrated root.
        Assert.Equal(@"C:\legacy", loaded.ProjectsRootPath);
        Assert.Equal(@"C:\legacy", loaded.DefaultRootPath);
    }

    [Fact]
    public void SavingAMigratedFile_KeepsTheSingularRootPointingAtTheFirstEnabledRoot()
    {
        var service = new SettingsService();
        service.Save(new AppSettings
        {
            ProjectRoots =
            [
                new ProjectRoot { Path = @"C:\one", Enabled = false },
                new ProjectRoot { Path = @"D:\two", ExcludedDirectories = ["vendor"] },
            ],
        });

        var loaded = service.Load();

        Assert.Equal(@"D:\two", loaded.ProjectsRootPath);
        Assert.Equal(["vendor"], loaded.ExcludedDirectories);
        Assert.Equal(2, loaded.ProjectRoots.Length);
    }

    /// <summary>
    /// The singular fields stay a live surface. Every caller that still load-mutates them — an
    /// external editor among them — would otherwise have its edit dropped on save.
    /// </summary>
    [Fact]
    public void AnEditToTheSingularRoot_IsAdoptedIntoTheFirstRoot()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\before" });

        var moved = service.Load();
        moved.ProjectsRootPath = @"C:\after";
        service.Save(moved);

        var loaded = service.Load();
        Assert.Equal(@"C:\after", Assert.Single(loaded.ProjectRoots).Path);
        Assert.Equal(@"C:\after", loaded.ProjectsRootPath);
    }

    [Fact]
    public void AnEditToTheSingularExclusions_IsAdoptedIntoTheFirstRoot()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\root", ExcludedDirectories = ["alpha"] });

        var edited = service.Load();
        edited.ExcludedDirectories = ["alpha", "beta"];
        service.Save(edited);

        Assert.Equal(["alpha", "beta"], Assert.Single(service.Load().ProjectRoots).ExcludedDirectories);
    }

    /// <summary>The richer surface wins: a root-list edit is not undone by the mirror it leaves stale.</summary>
    [Fact]
    public void ARootListEdit_OutranksTheStaleSingularFieldsCarriedWithIt()
    {
        var service = new SettingsService();
        service.Save(new AppSettings { ProjectsRootPath = @"C:\one" });

        var edited = service.Load();
        edited.ProjectRoots = [new ProjectRoot { Path = @"D:\two" }];
        // Left pointing at the old root, as any caller that only knows the list would leave it.
        service.Save(edited);

        var loaded = service.Load();
        Assert.Equal(@"D:\two", Assert.Single(loaded.ProjectRoots).Path);
        Assert.Equal(@"D:\two", loaded.ProjectsRootPath);
    }

    [Fact]
    public void DepthIsClampedOnLoad_SoNoFileCanAskForAnUnboundedWalk()
    {
        var service = new SettingsService();
        service.Save(new AppSettings
        {
            ProjectRoots = [new ProjectRoot { Path = @"C:\root", MaxDepth = 99 }],
        });

        Assert.Equal(ProjectRootSettings.MaxDepth, Assert.Single(service.Load().ProjectRoots).MaxDepth);
    }

    [Fact]
    public void ARootListedTwice_CollapsesToOne()
    {
        var settings = new AppSettings
        {
            ProjectRoots =
            [
                new ProjectRoot { Path = @"C:\root" },
                new ProjectRoot { Path = @"C:\root\" },
            ],
        };

        ProjectRootSettings.Migrate(settings);

        Assert.Equal(@"C:\root", Assert.Single(settings.ProjectRoots).Path);
    }

    [Fact]
    public void ADefaultRootThatIsNoLongerListed_FallsBackToTheFirstEnabledRoot()
    {
        var settings = new AppSettings
        {
            DefaultRootPath = @"E:\removed",
            ProjectRoots = [new ProjectRoot { Path = @"C:\one", Enabled = false }, new ProjectRoot { Path = @"D:\two" }],
        };

        ProjectRootSettings.Migrate(settings);

        Assert.Equal(@"D:\two", settings.DefaultRootPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\one")]
    public void AWriteTargetThatIsUnusable_IsRefusedWithAReasonRatherThanGuessed(string defaultRoot)
    {
        var settings = new AppSettings
        {
            ProjectsRootPath = "",
            DefaultRootPath = defaultRoot,
            ProjectRoots = defaultRoot.Length == 0 ? [] : [new ProjectRoot { Path = defaultRoot, Enabled = false }],
        };

        Assert.Equal("", ProjectRootSettings.WriteTarget(settings));
        Assert.NotNull(ProjectRootSettings.WriteTargetRefusal(settings));
    }
}

/// <summary>
/// Discovery over more than one root. The union, the ordering, and — the part that has to be
/// right — a root that cannot be read reporting itself instead of quietly contributing nothing.
/// </summary>
[Collection("app-data-sandbox")]
public class MultipleRootDiscoveryTests
{
    public MultipleRootDiscoveryTests() => TestSandbox.ResetDataDir();

    [Fact]
    public async Task RepositoriesInEveryRoot_AreAllDiscovered_AndCarryTheRootTheyCameFrom()
    {
        var first = TestEnv.NewDir("roots-first");
        var second = TestEnv.NewDir("roots-second");
        await InitRepoAsync(first, "alpha");
        await InitRepoAsync(second, "bravo");

        var results = await ScanAsync(first, second);

        Assert.Equal(["alpha", "bravo"], results.Select(p => p.DirectoryName).Order());
        Assert.Equal(RepoPaths.Normalize(first), results.Single(p => p.DirectoryName == "alpha").RootPath);
        Assert.Equal(RepoPaths.Normalize(second), results.Single(p => p.DirectoryName == "bravo").RootPath);
    }

    /// <summary>
    /// Two roots can each hold a "tabkit". They are two repositories, they get two cards, and
    /// nothing that keys on the folder name may merge them.
    /// </summary>
    [Fact]
    public async Task ASameNamedRepositoryInTwoRoots_ProducesTwoCardsWithDistinctPaths()
    {
        var first = TestEnv.NewDir("dup-first");
        var second = TestEnv.NewDir("dup-second");
        await InitRepoAsync(first, "tabkit");
        await InitRepoAsync(second, "tabkit");

        var results = await ScanAsync(first, second);

        Assert.Equal(2, results.Count);
        Assert.All(results, p => Assert.Equal("tabkit", p.DirectoryName));
        Assert.Equal(2, results.Select(p => RepoPaths.Normalize(p.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Pins are path-keyed, so pinning one leaves the other alone.
        var pinned = DashboardOrdering.KeySet([results[0].FullPath]);
        Assert.True(DashboardOrdering.IsPinned(results[0], pinned));
        Assert.False(DashboardOrdering.IsPinned(results[1], pinned));
    }

    /// <summary>A root nested inside another is one place, not two, and produces one card.</summary>
    [Fact]
    public async Task ARootListedInsideAnotherRoot_DoesNotDoubleTheCards()
    {
        var outer = TestEnv.NewDir("nested-root-outer");
        var inner = Path.Combine(outer, "group");
        Directory.CreateDirectory(inner);
        await InitRepoAsync(inner, "site");

        var results = await ScanAsync(inner, outer);

        Assert.Equal("site", Assert.Single(results).DirectoryName);
    }

    [Fact]
    public async Task AMissingRoot_ReportsItselfAndLeavesTheOtherRootsRepositoriesOnTheGrid()
    {
        var present = TestEnv.NewDir("mixed-present");
        var missing = Path.Combine(TestEnv.Root, "mixed-missing-" + Guid.NewGuid().ToString("N")[..8]);
        await InitRepoAsync(present, "alpha");

        var service = NewService(present, missing);
        var results = await service.ForceRefreshAllAsync();

        Assert.Equal("alpha", Assert.Single(results).DirectoryName);

        var reported = service.LastRootStatuses;
        Assert.Equal(RootAvailability.Available, reported.Single(s => RepoPaths.Equal(s.Path, present)).Availability);
        Assert.Equal(RootAvailability.Missing, reported.Single(s => RepoPaths.Equal(s.Path, missing)).Availability);

        var banner = DashboardEmptyState.DescribeUnavailableRoots(reported);
        Assert.NotNull(banner);
        Assert.Contains(RepoPaths.Normalize(missing), banner);
    }

    [Fact]
    public async Task ADisabledRoot_IsNotScannedAndIsNotReportedAsAFailure()
    {
        var scanned = TestEnv.NewDir("disabled-scanned");
        var off = TestEnv.NewDir("disabled-off");
        await InitRepoAsync(scanned, "alpha");
        await InitRepoAsync(off, "bravo");

        var settings = new SettingsService();
        settings.Save(BaseSettings(
            new ProjectRoot { Path = scanned },
            new ProjectRoot { Path = off, Enabled = false }));

        var service = NewService(settings);
        var results = await service.ForceRefreshAllAsync();

        Assert.Equal("alpha", Assert.Single(results).DirectoryName);
        Assert.Equal(RootAvailability.Disabled, service.LastRootStatuses.Single(s => RepoPaths.Equal(s.Path, off)).Availability);
        Assert.Null(DashboardEmptyState.DescribeUnavailableRoots(service.LastRootStatuses));
    }

    /// <summary>
    /// A root that exists and refuses to be enumerated is a third state. Reported as empty it
    /// would look like a folder the user emptied, and the repositories in it would vanish with
    /// no explanation.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRoot_IsReportedAsUnreadableRatherThanEmpty()
    {
        var readable = TestEnv.NewDir("acl-readable");
        var denied = TestEnv.NewDir("acl-denied");
        await InitRepoAsync(readable, "alpha");
        await InitRepoAsync(denied, "bravo");

        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(denied);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);

        try
        {
            var service = NewService(readable, denied);
            var results = await service.ForceRefreshAllAsync();

            Assert.Equal("alpha", Assert.Single(results).DirectoryName);
            Assert.Equal(RootAvailability.Unreadable,
                service.LastRootStatuses.Single(s => RepoPaths.Equal(s.Path, denied)).Availability);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task PerRootExclusions_HideOnlyUnderTheRootTheyBelongTo()
    {
        var first = TestEnv.NewDir("excl-first");
        var second = TestEnv.NewDir("excl-second");
        await InitRepoAsync(first, "internal");
        await InitRepoAsync(second, "internal");

        var settings = new SettingsService();
        settings.Save(BaseSettings(
            new ProjectRoot { Path = first, ExcludedDirectories = ["internal"] },
            new ProjectRoot { Path = second }));

        var results = await NewService(settings).ForceRefreshAllAsync();

        var kept = Assert.Single(results);
        Assert.Equal(RepoPaths.Normalize(second), kept.RootPath);
    }

    private static async Task InitRepoAsync(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        await Git.RunAsync(path, "init", "-b", "main");
    }

    private static AppSettings BaseSettings(params ProjectRoot[] roots) => new()
    {
        ProjectRoots = roots,
        // gh pointed at a nonexistent executable: discovery stays local and spawns no network.
        GhPath = Path.Combine(TestEnv.Root, "no-such-gh.exe"),
        EnableGitHubDiscovery = false,
        RefreshIntervalSeconds = 7200,
    };

    private static Task<List<ProjectInfo>> ScanAsync(params string[] roots)
    {
        return NewService(roots).ForceRefreshAllAsync();
    }

    private static ProjectDiscoveryService NewService(params string[] roots)
    {
        var settings = new SettingsService();
        settings.Save(BaseSettings([.. roots.Select(r => new ProjectRoot { Path = r })]));
        return NewService(settings);
    }

    private static ProjectDiscoveryService NewService(SettingsService settings) =>
        new(new GitService(), new GitHubService(settings), settings, new ManifestStore());
}
