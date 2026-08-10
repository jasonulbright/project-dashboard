using System.IO;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// A repository that moves, renamed or carried to another projects folder, against real fixture
/// repositories. The refusal arms are here for the same reason the adoption arms are: the pass is
/// only worth having if it declines every shape it cannot be sure about.
/// </summary>
[Collection("app-data-sandbox")]
public class ManifestAdoptionTests : IDisposable
{
    private readonly string _fixtures = TestEnv.NewDir("adoption");
    private readonly string _rootOne;
    private readonly string _rootTwo;

    public ManifestAdoptionTests()
    {
        TestSandbox.ResetDataDir();
        _rootOne = Path.Combine(_fixtures, "one");
        _rootTwo = Path.Combine(_fixtures, "two");
        Directory.CreateDirectory(_rootOne);
        Directory.CreateDirectory(_rootTwo);
        SaveRoots(_rootOne, _rootTwo);
    }

    public void Dispose() => TestEnv.TryDeleteTree(_fixtures);

    private void SaveRoots(params string[] roots) =>
        new SettingsService().Save(new AppSettings
        {
            ProjectRoots = [.. roots.Select(r => new ProjectRoot { Path = r })],
            GhPath = Path.Combine(_fixtures, "no-such-gh.exe"),
            RefreshIntervalSeconds = 7200,
        });

    private static (ProjectDiscoveryService Discovery, ManifestStore Store) NewService(
        RepoBusyRegistry? busy = null, SettingsService? settingsService = null)
    {
        var settings = settingsService ?? new SettingsService();
        var store = new ManifestStore();
        return (new ProjectDiscoveryService(
            new GitService(), new GitHubService(settings), settings, store, busy), store);
    }

    private static async Task<string> NewRepoAsync(string parent, string name, bool commit = true)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        await Git.RunAsync(path, "init", "-b", "main");
        if (!commit) return path;

        File.WriteAllText(Path.Combine(path, "file.txt"), "one\n");
        await Git.RunAsync(path, "add", "-A");
        await Git.RunAsync(path, "commit", "-m", "initial commit");
        return path;
    }

    private static void Move(string from, string to) => Directory.Move(from, to);

    // ── Adoption ───────────────────────────────────────────────────

    [Fact]
    public async Task ARepositoryCarriedToAnotherProjectsFolder_KeepsItsMetadata()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService();
        store.Save(origin, new ProjectManifest { Description = "a tab manager", Category = "Tools", Notes = "hand typed" });
        await discovery.ForceRefreshAllAsync();

        var moved = Path.Combine(_rootTwo, "tabkit");
        Move(origin, moved);

        var projects = await discovery.ForceRefreshAllAsync();

        var adoption = Assert.Single(discovery.LastManifestReport.Adoptions);
        Assert.Equal(origin, adoption.FromPath);
        Assert.Equal(moved, adoption.ToPath);

        // The record moved, and the card it now belongs to carries it in the same scan.
        Assert.False(store.TryGet(origin, out _));
        Assert.True(store.TryGet(moved, out var manifest));
        Assert.Equal("a tab manager", manifest!.Description);
        Assert.Equal("hand typed", manifest.Notes);

        var card = Assert.Single(projects, p => RepoPaths.Equal(p.FullPath, moved));
        Assert.True(card.HasManifest);
        Assert.Equal("a tab manager", card.Manifest.Description);
    }

    [Fact]
    public async Task ARepositoryRenamedInPlace_KeepsItsMetadata()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService();
        store.Save(origin, new ProjectManifest { Description = "a tab manager" });
        await discovery.ForceRefreshAllAsync();

        var renamed = Path.Combine(_rootOne, "tab-kit");
        Move(origin, renamed);

        await discovery.ForceRefreshAllAsync();

        Assert.Equal(renamed, Assert.Single(discovery.LastManifestReport.Adoptions).ToPath);
        Assert.True(store.TryGet(renamed, out var manifest));
        Assert.Equal("a tab manager", manifest!.Description);
    }

    /// <summary>
    /// A pin is keyed on the path too. Left behind, the glyph clears on a repository that moved
    /// and the stale entry can never be removed by unpinning the card that is now on screen.
    /// </summary>
    [Fact]
    public async Task APinnedRepositoryThatMoved_IsStillPinned_AndTheWriteDoesNotAskForAnotherScan()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var settings = new SettingsService();
        var (discovery, store) = NewService(settingsService: settings);
        store.Save(origin, new ProjectManifest { Description = "a tab manager" });
        await discovery.ForceRefreshAllAsync();

        var current = settings.Load();
        current.PinnedProjectPaths = [origin];
        settings.Save(current);

        var moved = Path.Combine(_rootTwo, "tabkit");
        Move(origin, moved);

        var changes = new List<SettingsChange>();
        settings.Changed += changes.Add;

        await discovery.ForceRefreshAllAsync();

        Assert.Equal([moved], new SettingsService().Load().PinnedProjectPaths);

        // A pin edit that asked for a re-scan would make every adopting scan start another one.
        var pinWrite = Assert.Single(changes);
        Assert.False(SettingsDelta.RediscoveryRequired(pinWrite));
        Assert.True(SettingsDelta.ViewPreferencesChanged(pinWrite));
    }

    // ── Refusals ───────────────────────────────────────────────────

    /// <summary>
    /// Two clones of one upstream share their root commit and their remote. Adopting onto either
    /// is a coin flip with the reader's notes on it.
    /// </summary>
    [Fact]
    public async Task ARecordMatchingTwoClones_AdoptsNeitherAndSaysSo()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService();
        store.Save(origin, new ProjectManifest { Description = "a tab manager" });
        await discovery.ForceRefreshAllAsync();

        await Git.RunAsync(_rootTwo, "clone", origin, "tabkit-a");
        await Git.RunAsync(_rootTwo, "clone", origin, "tabkit-b");
        TestEnv.TryDeleteTree(origin);

        await discovery.ForceRefreshAllAsync();

        var report = discovery.LastManifestReport;
        Assert.Empty(report.Adoptions);
        var refusal = Assert.Single(report.Refusals);
        Assert.Equal(ManifestRefusalReason.SeveralRepositoriesMatch, refusal.Reason);

        // Every record involved is exactly where it was.
        Assert.True(store.TryGet(origin, out var kept));
        Assert.Equal("a tab manager", kept!.Description);
        Assert.False(store.TryGet(Path.Combine(_rootTwo, "tabkit-a"), out _));
        Assert.False(store.TryGet(Path.Combine(_rootTwo, "tabkit-b"), out _));
    }

    [Fact]
    public async Task ACloneThatAlreadyHasItsOwnMetadata_IsNotOverwritten()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService();
        store.Save(origin, new ProjectManifest { Description = "the original" });
        await discovery.ForceRefreshAllAsync();

        await Git.RunAsync(_rootTwo, "clone", origin, "tabkit");
        var clone = Path.Combine(_rootTwo, "tabkit");
        store.Save(clone, new ProjectManifest { Description = "the clone" });
        TestEnv.TryDeleteTree(origin);

        await discovery.ForceRefreshAllAsync();

        var refusal = Assert.Single(discovery.LastManifestReport.Refusals);
        Assert.Equal(ManifestRefusalReason.TargetAlreadyHasMetadata, refusal.Reason);

        Assert.True(store.TryGet(clone, out var untouched));
        Assert.Equal("the clone", untouched!.Description);
        Assert.True(store.TryGet(origin, out var original));
        Assert.Equal("the original", original!.Description);
    }

    /// <summary>
    /// A repository with no commits and no remote carries nothing that identifies it. Matching it
    /// on its folder name would turn every same-named folder into a wrong adoption.
    /// </summary>
    [Fact]
    public async Task ARepositoryWithNothingToIdentifyIt_IsNotAdoptedOnItsNameAlone()
    {
        var origin = await NewRepoAsync(_rootOne, "fresh", commit: false);
        var (discovery, store) = NewService();
        store.Save(origin, new ProjectManifest { Description = "nothing committed yet" });
        await discovery.ForceRefreshAllAsync();

        TestEnv.TryDeleteTree(origin);
        await NewRepoAsync(_rootTwo, "fresh", commit: false);

        await discovery.ForceRefreshAllAsync();

        Assert.Empty(discovery.LastManifestReport.Adoptions);
        Assert.Empty(discovery.LastManifestReport.Refusals);
        Assert.True(store.TryGet(origin, out _));
        Assert.False(store.TryGet(Path.Combine(_rootTwo, "fresh"), out _));
    }

    /// <summary>
    /// A repository whose refs are mid-swap is not read at all. Its identity is therefore unknown
    /// this pass, and an unknown identity can never be the one repository a record matches.
    /// </summary>
    [Fact]
    public async Task ARepositoryUnderAnOperationsLease_IsNotReadAndNotAdoptedOnto()
    {
        var busy = new RepoBusyRegistry();
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService(busy);
        store.Save(origin, new ProjectManifest { Description = "a tab manager" });
        await discovery.ForceRefreshAllAsync();

        var moved = Path.Combine(_rootTwo, "tabkit");
        Move(origin, moved);

        using (busy.Acquire(moved))
        {
            await discovery.ForceRefreshAllAsync();

            Assert.Empty(discovery.LastManifestReport.Adoptions);
            Assert.DoesNotContain(moved, discovery.LastFingerprints.Keys);
            Assert.True(store.TryGet(origin, out _));
        }

        // Released, the next scan places it.
        await discovery.ForceRefreshAllAsync();
        Assert.Equal(moved, Assert.Single(discovery.LastManifestReport.Adoptions).ToPath);
    }

    // ── Records with no repository ─────────────────────────────────

    /// <summary>
    /// An unplugged drive makes every path under it vanish at once. Counting those as gone would
    /// put a reader's whole portfolio in the forget list the moment a share drops.
    /// </summary>
    [Fact]
    public async Task ARecordUnderAFolderThatIsNotThere_IsNotCalledGone()
    {
        var kept = await NewRepoAsync(_rootOne, "alpha");
        var offline = await NewRepoAsync(_rootTwo, "beta");
        var (discovery, store) = NewService();
        store.Save(kept, new ProjectManifest { Description = "alpha" });
        store.Save(offline, new ProjectManifest { Description = "beta" });
        await discovery.ForceRefreshAllAsync();

        // The drive goes away: the configured folder, and every path under it, stops answering.
        var unplugged = _rootTwo + "-unplugged";
        Move(_rootTwo, unplugged);

        await discovery.ForceRefreshAllAsync();

        Assert.False(Directory.Exists(offline));
        Assert.Equal(
            RootAvailability.Missing,
            Assert.Single(discovery.LastRootStatuses, s => RepoPaths.Equal(s.Path, _rootTwo)).Availability);

        Assert.Empty(discovery.LastManifestReport.Orphans);
        Assert.True(store.TryGet(offline, out var survived));
        Assert.Equal("beta", survived!.Description);
        Assert.True(store.TryGet(kept, out _));

        // Plugged back in, the record is still the one attached to that repository.
        Move(unplugged, _rootTwo);
        await discovery.ForceRefreshAllAsync();

        Assert.Empty(discovery.LastManifestReport.Adoptions);
        Assert.True(store.TryGet(offline, out var reconnected));
        Assert.Equal("beta", reconnected!.Description);
    }

    [Fact]
    public async Task ARecordWhoseRepositoryIsGone_IsListedAndKeptUntilItIsForgotten()
    {
        var gone = await NewRepoAsync(_rootOne, "alpha");
        var (discovery, store) = NewService();
        store.Save(gone, new ProjectManifest { Description = "alpha" });
        await discovery.ForceRefreshAllAsync();

        TestEnv.TryDeleteTree(gone);
        await discovery.ForceRefreshAllAsync();

        var orphan = Assert.Single(discovery.LastManifestReport.Orphans);
        Assert.Equal(gone, orphan.Path);
        Assert.Equal("alpha", orphan.Description);

        // Retained across a reload: nothing deletes a record on a schedule.
        Assert.True(new ManifestStore().TryGet(gone, out _));

        Assert.True(store.Forget([gone]));
        Assert.False(new ManifestStore().TryGet(gone, out _));
    }

    /// <summary>
    /// Metadata typed between two scans is recognisable straight away. The scan already read what
    /// the repository is, so recording it with the new record costs nothing and closes the window
    /// where a folder that moves first would strand it.
    /// </summary>
    [Fact]
    public async Task MetadataTypedAfterAScan_IsRecognisableBeforeTheNextOne()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService();
        await discovery.ForceRefreshAllAsync();

        Assert.True(await discovery.SaveManifestAsync(origin, new ProjectManifest { Description = "typed just now" }));

        var moved = Path.Combine(_rootTwo, "tabkit");
        Move(origin, moved);
        await discovery.ForceRefreshAllAsync();

        Assert.Equal(moved, Assert.Single(discovery.LastManifestReport.Adoptions).ToPath);
        Assert.True(store.TryGet(moved, out var manifest));
        Assert.Equal("typed just now", manifest!.Description);
    }

    /// <summary>
    /// A page opened before a scan re-keyed its record holds the path the record moved off. The
    /// edit has to reach the record, not a fresh empty one at the vacated folder — the reader is
    /// told the save landed either way, and only one of those is true.
    /// </summary>
    [Fact]
    public async Task AnEditSavedAgainstThePathAPageWasOpenedOn_ReachesTheRecordThatMoved()
    {
        var origin = await NewRepoAsync(_rootOne, "tabkit");
        var (discovery, store) = NewService();
        store.Save(origin, new ProjectManifest { Description = "a tab manager", Category = "Tools" });

        // The card a detail page would be holding, read before the move.
        var opened = Assert.Single(await discovery.ForceRefreshAllAsync(), p => RepoPaths.Equal(p.FullPath, origin));
        Assert.NotNull(opened.Fingerprint);

        var moved = Path.Combine(_rootTwo, "tabkit");
        Move(origin, moved);
        await discovery.ForceRefreshAllAsync();
        Assert.Single(discovery.LastManifestReport.Adoptions);

        // The page saves what it has, against the path it was opened on.
        Assert.True(await discovery.SaveManifestAsync(
            opened.FullPath,
            new ProjectManifest { Description = "edited while it moved", Category = "Tools" },
            opened.Fingerprint));

        var reloaded = new ManifestStore();
        Assert.True(reloaded.TryGet(moved, out var record));
        Assert.Equal("edited while it moved", record!.Description);
        Assert.False(reloaded.TryGet(origin, out _));
        Assert.Single(reloaded.Snapshot());
    }

    [Fact]
    public async Task AScanRecordsWhatEachRepositoryIs_SoTheNextOneCanRecogniseIt()
    {
        var repo = await NewRepoAsync(_rootOne, "alpha");
        var (discovery, store) = NewService();
        store.Save(repo, new ProjectManifest { Description = "alpha" });

        await discovery.ForceRefreshAllAsync();

        var entry = store.Snapshot()[RepoPaths.Normalize(repo)];
        Assert.NotNull(entry.Fingerprint);
        Assert.Single(entry.Fingerprint!.RootCommitOids);
        Assert.NotNull(entry.LastSeenUtc);
    }
}
