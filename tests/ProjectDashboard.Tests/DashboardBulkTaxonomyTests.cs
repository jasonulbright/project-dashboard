using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Setting one metadata field on several cards at once, and the chips the grid draws for the
/// four fields. What is asserted is that the selection is only ever the cards the current filter
/// shows, that a card whose write failed is named in the outcome rather than folded into a
/// tally, and that no card adopts a value the store did not report durable.
/// </summary>
[Collection("app-data-sandbox")]
public class DashboardBulkTaxonomyTests
{
    public DashboardBulkTaxonomyTests() => TestSandbox.ResetDataDir();

    /// <summary>Answers the picker without a window, and refuses writes for named repositories.</summary>
    private sealed class Picking : DashboardViewModel
    {
        private readonly BulkTaxonomyChoice? _choice;

        public Picking(
            ProjectDiscoveryService discovery, SettingsService settings, ProjectWatcherService watcher,
            BulkTaxonomyChoice? choice)
            : base(discovery, null!, settings, new GitHubService(settings), new GitService(), watcher,
                new RepoBusyRegistry(), uiPost: callback => callback())
            => _choice = choice;

        public int Prompts { get; private set; }

        public int LastPromptCount { get; private set; }

        internal override Task<BulkTaxonomyChoice?> PromptForBulkTaxonomyAsync(int cardCount)
        {
            Prompts++;
            LastPromptCount = cardCount;
            return Task.FromResult(_choice);
        }
    }

    /// <summary>Writes through a real store, except for the paths it is told to refuse.</summary>
    private sealed class PartlyRefusingDiscovery : ProjectDiscoveryService
    {
        private readonly ManifestStore _store;
        private readonly string[] _refused;

        public PartlyRefusingDiscovery(SettingsService settings, ManifestStore store, params string[] refused)
            : base(new GitService(), new GitHubService(settings), settings, store)
        {
            _store = store;
            _refused = refused;
        }

        public override Task<bool> SaveManifestAsync(
            string repoPath, ProjectManifest manifest, RepoFingerprint? identity = null, CancellationToken ct = default)
            => _refused.Contains(repoPath, StringComparer.OrdinalIgnoreCase)
                ? Task.FromResult(false)
                : Task.FromResult(_store.Save(repoPath, manifest));
    }

    private static ProjectInfo Card(string name, string category = "Uncategorized") => new()
    {
        DirectoryName = name,
        DisplayName = name,
        FullPath = $@"C:\projects\{name}",
        HasManifest = true,
        Manifest = new ProjectManifest { Category = category },
    };

    private static SettingsService SavedSettings()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { SettingsSchemaVersion = 1, Taxonomy = Taxonomy.Seed() });
        return settings;
    }

    private static Picking Grid(
        SettingsService settings, ProjectWatcherService watcher, ProjectDiscoveryService discovery,
        DashboardViewModel.BulkTaxonomyChoice? choice, params ProjectInfo[] cards)
    {
        var grid = new Picking(discovery, settings, watcher, choice);
        // Seeded the way a scan does: assigning the list alone leaves the chips, the category
        // filter, and the visible set unbuilt, which is not the state the grid is ever in.
        grid.UpdateProjectList([.. cards]);
        return grid;
    }

    // ── The chips ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryCard_DrawsItsFourChipsFromTheReadersLists()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var store = new ManifestStore();
        var card = Card("alpha", "Web");
        card.Manifest.Status = "maintenance";
        card.Manifest.ValidationSchedule = "none";
        card.Manifest.ProjectType = "library";

        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, store), null, card);

        Assert.Equal(TaxonomyPalette.Warn, card.StatusBadge.Color);
        Assert.True(card.CategoryBadge.Visible);
        Assert.Equal("library", card.TypeBadge.Text);
        // The chip a trigger keyed on the literal "none" used to collapse.
        Assert.False(card.ScheduleBadge.Visible);
        Assert.Equal(0, grid.Prompts);
    }

    [Fact]
    public void AStoredValueNoListHolds_IsDrawnAsItselfAndMarked()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var card = Card("alpha", "Imported");

        Grid(settings, watcher, new PartlyRefusingDiscovery(settings, new ManifestStore()), null, card);

        Assert.Equal("Imported", card.CategoryBadge.Text);
        Assert.True(card.CategoryBadge.OffList);
    }

    // ── The selection ───────────────────────────────────────────────────────

    [Fact]
    public void ACardTheFilterNoLongerShows_LosesItsTick()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var alpha = Card("alpha");
        var beta = Card("beta");
        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, new ManifestStore()), null, alpha, beta);

        grid.IsSelectionMode = true;
        grid.SelectAllVisibleCommand.Execute(null);
        Assert.Equal(2, grid.SelectedCount);

        grid.SearchText = "alpha";

        Assert.Equal(1, grid.SelectedCount);
        Assert.True(alpha.IsSelected);
        Assert.False(beta.IsSelected);
    }

    [Fact]
    public void ACardWithNoWorkingTree_IsNeverTicked()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var cloud = new ProjectInfo
        {
            DirectoryName = "beta", DisplayName = "beta", FullPath = "",
            IsRemoteOnly = true, RemoteSlug = "o/beta",
        };
        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, new ManifestStore()), null, Card("alpha"), cloud);

        grid.IsSelectionMode = true;
        grid.SelectAllVisibleCommand.Execute(null);

        Assert.Equal(1, grid.SelectedCount);
        Assert.False(cloud.IsSelected);
    }

    [Fact]
    public void LeavingSelectionMode_ClearsTheSelection()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var alpha = Card("alpha");
        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, new ManifestStore()), null, alpha);

        grid.IsSelectionMode = true;
        grid.SelectAllVisibleCommand.Execute(null);
        grid.ToggleSelectionModeCommand.Execute(null);

        Assert.False(alpha.IsSelected);
        Assert.Equal(0, grid.SelectedCount);
    }

    // ── The write ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingTicked_OffersNoDialogAndSaysWhy()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, new ManifestStore()), null, Card("alpha"));

        await grid.BulkSetTaxonomyCommand.ExecuteAsync(null);

        Assert.Equal(0, grid.Prompts);
        Assert.Equal(DashboardViewModel.NothingSelectedNotice, grid.OpStatusText);
    }

    [Fact]
    public async Task ACancelledDialog_WritesNothing()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var store = new ManifestStore();
        var alpha = Card("alpha");
        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, store), null, alpha);

        grid.IsSelectionMode = true;
        grid.SelectAllVisibleCommand.Execute(null);
        await grid.BulkSetTaxonomyCommand.ExecuteAsync(null);

        Assert.Equal(1, grid.Prompts);
        Assert.False(store.TryGet(alpha.FullPath, out _));
    }

    [Fact]
    public async Task EveryTickedCard_IsWrittenAndReported()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var store = new ManifestStore();
        var alpha = Card("alpha");
        var beta = Card("beta");
        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, store),
            new DashboardViewModel.BulkTaxonomyChoice(TaxonomyField.Category, "Web"), alpha, beta);

        grid.IsSelectionMode = true;
        grid.SelectAllVisibleCommand.Execute(null);
        await grid.BulkSetTaxonomyCommand.ExecuteAsync(null);

        Assert.Equal(2, grid.LastPromptCount);
        Assert.Equal("Web", alpha.Manifest.Category);
        Assert.Equal("Web", beta.Manifest.Category);
        Assert.True(store.TryGet(beta.FullPath, out var stored));
        Assert.Equal("Web", stored!.Category);
        Assert.Contains("Set category to \"Web\" on 2 projects — 2 succeeded.", grid.OpStatusText);
        // The chips follow the write; a card still tinted for the old value would be lying.
        Assert.Equal("Web", alpha.CategoryBadge.Text);
    }

    /// <summary>
    /// A refused write must leave the card reading exactly what the store holds, and the outcome
    /// must name it — a tally with one silent failure is a partial apply presented as a whole one.
    /// </summary>
    [Fact]
    public async Task ARefusedWrite_IsNamedAndLeavesThatCardAlone()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var store = new ManifestStore();
        var alpha = Card("alpha");
        var beta = Card("beta");
        var discovery = new PartlyRefusingDiscovery(settings, store, beta.FullPath);
        var grid = Grid(settings, watcher, discovery,
            new DashboardViewModel.BulkTaxonomyChoice(TaxonomyField.Category, "Web"), alpha, beta);

        grid.IsSelectionMode = true;
        grid.SelectAllVisibleCommand.Execute(null);
        await grid.BulkSetTaxonomyCommand.ExecuteAsync(null);

        Assert.Equal("Web", alpha.Manifest.Category);
        Assert.Equal("Uncategorized", beta.Manifest.Category);
        Assert.Contains("1 succeeded, 1 failed: beta (the metadata file could not be written)", grid.OpStatusText);
    }

    /// <summary>
    /// The cascade rewrites the index, not the models the grid is holding. Without the carry the
    /// card would keep showing the old value, marked as one no list holds, until the next scan —
    /// which is worse than not renaming at all, because it reads as data the reader has lost.
    /// </summary>
    [Fact]
    public void ARenameInSettings_ReachesTheCardsAlreadyOnScreen()
    {
        var settings = SavedSettings();
        using var watcher = new ProjectWatcherService();
        var store = new ManifestStore();
        var alpha = Card("alpha", "MECM");
        store.Save(alpha.FullPath, alpha.Manifest);

        var grid = Grid(settings, watcher, new PartlyRefusingDiscovery(settings, store), null, alpha);
        var page = new SettingsViewModel(settings, null!, grid, manifests: store);

        page.TaxonomyLists.Single(l => l.Field == TaxonomyField.Category)
            .Rows.Single(r => r.Name == "MECM").Name = "SCCM";
        page.SaveTaxonomyCommand.Execute(null);

        Assert.Equal("SCCM", alpha.Manifest.Category);
        Assert.Equal("SCCM", alpha.CategoryBadge.Text);
        Assert.False(alpha.CategoryBadge.OffList);
        Assert.Contains("SCCM", grid.Categories);
    }

    [Fact]
    public void TheOutcome_ListsTheFirstFailuresAndCountsTheRest()
    {
        var many = Enumerable.Range(1, 7).Select(i => ($"repo{i}", "busy")).ToList();

        var line = DashboardViewModel.DescribeBulkSet(TaxonomyField.Status, "archived", 9, 2, many);

        Assert.Contains("Set status to \"archived\" on 9 projects — 2 succeeded, 7 failed:", line);
        Assert.Contains("repo5 (busy)", line);
        Assert.DoesNotContain("repo6", line);
        Assert.Contains("and 2 more", line);
    }
}
