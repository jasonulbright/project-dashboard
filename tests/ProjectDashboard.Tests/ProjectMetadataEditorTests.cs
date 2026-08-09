using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Overview card's manifest editor: what a Save writes, and what the page says when the
/// write did not land. The store's own durability is covered by <see cref="ManifestStoreTests"/>;
/// what is asserted here is that the page never presents an unsaved edit as stored.
/// </summary>
[Collection("app-data-sandbox")]
public class ProjectMetadataEditorTests
{
    public ProjectMetadataEditorTests() => TestSandbox.ResetDataDir();

    private const string RepoPath = @"C:\projects\metadata-editor";

    /// <summary>Only the manifest store is reached through the save path, so the rest stays null.</summary>
    private static ProjectDiscoveryService RealDiscovery() =>
        new(null!, null!, null!, new ManifestStore());

    private sealed class RefusingDiscoveryService()
        : ProjectDiscoveryService(null!, null!, null!, new ManifestStore())
    {
        public int Calls { get; private set; }

        public override Task<bool> SaveManifestAsync(string repoPath, ProjectManifest manifest,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }

    private static ProjectDetailViewModel VmOn(ProjectDiscoveryService discovery, ProjectManifest manifest)
    {
        var vm = new ProjectDetailViewModel(discovery, new GitService(), null!);
        vm.SetProjectAsync(new ProjectInfo
        {
            DirectoryName = "metadata-editor",
            DisplayName = "metadata-editor",
            FullPath = RepoPath,
            HasManifest = true,
            Manifest = manifest
        }).GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public async Task Save_ThatLanded_AdoptsTheManifestAndSaysSo()
    {
        var vm = VmOn(RealDiscovery(), new ProjectManifest { Category = "Uncategorized" });
        vm.SelectedCategory = "Tools";

        await vm.SaveManifestCommand.ExecuteAsync(null);

        Assert.Equal("Metadata saved.", vm.ManifestStatusText);
        Assert.Equal("Tools", vm.Project!.Manifest.Category);
        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("Tools", stored!.Category);
    }

    /// <summary>
    /// The edit is only in the editor after a failed write. Adopting it onto the project would
    /// make the card read as saved for the rest of the session and revert at the next launch.
    /// </summary>
    [Fact]
    public async Task Save_ThatDidNotReachDisk_SaysSoAndLeavesTheProjectUnchanged()
    {
        var discovery = new RefusingDiscoveryService();
        var vm = VmOn(discovery, new ProjectManifest { Category = "Uncategorized" });
        vm.SelectedCategory = "Tools";

        await vm.SaveManifestCommand.ExecuteAsync(null);

        Assert.Equal(1, discovery.Calls);
        Assert.Equal(ProjectDetailViewModel.ManifestSaveFailed, vm.ManifestStatusText);
        Assert.Equal("Uncategorized", vm.Project!.Manifest.Category);
        // The editor still holds the edit, so a retry has something to write.
        Assert.Equal("Tools", vm.SelectedCategory);
    }

    /// <summary>
    /// Descriptions arrive with legacy repo-root manifests, and nothing but this editor ever
    /// writes one back. Built without it, the replacement manifest a Save persists carries the
    /// type's default of "" and the imported text is gone — from a Save the reader made about
    /// an unrelated field.
    /// </summary>
    [Fact]
    public async Task Save_OfAnUnrelatedField_KeepsTheDescriptionTheProjectAlreadyHad()
    {
        var store = new ManifestStore();
        store.Save(RepoPath, new ProjectManifest { Description = "imported description", Category = "Uncategorized" });
        Assert.True(store.TryGet(RepoPath, out var seeded));

        var vm = VmOn(RealDiscovery(), seeded!);
        Assert.Equal("imported description", vm.ManifestDescription);

        vm.SelectedCategory = "Tools";
        await vm.SaveManifestCommand.ExecuteAsync(null);

        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("imported description", stored!.Description);
        Assert.Equal("Tools", stored.Category);

        // Reload the way discovery does — straight out of the store.
        var reloaded = VmOn(RealDiscovery(), stored);
        Assert.Equal("imported description", reloaded.ManifestDescription);
        Assert.Equal("imported description", reloaded.Project!.Manifest.Description);
    }

    [Fact]
    public async Task Save_WritesAnEditedDescription()
    {
        var vm = VmOn(RealDiscovery(), new ProjectManifest { Description = "old" });

        vm.ManifestDescription = "what this repository is for";
        await vm.SaveManifestCommand.ExecuteAsync(null);

        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("what this repository is for", stored!.Description);
        Assert.Equal("what this repository is for", vm.Project!.Manifest.Description);
        Assert.False(vm.Project.HasIncompleteMetadata);
    }

    [Fact]
    public void TheDescriptionField_IsOnTheManifestCard()
    {
        var markup = RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

        Assert.Contains("{Binding ManifestDescription, UpdateSourceTrigger=PropertyChanged}", markup);
    }

    [Fact]
    public async Task SwitchingProjects_ClearsTheSaveOutcomeOfThePreviousOne()
    {
        var vm = VmOn(RealDiscovery(), new ProjectManifest());
        await vm.SaveManifestCommand.ExecuteAsync(null);
        Assert.Equal("Metadata saved.", vm.ManifestStatusText);

        await vm.SetProjectAsync(new ProjectInfo
        {
            DirectoryName = "other",
            DisplayName = "other",
            FullPath = @"C:\projects\metadata-editor-other"
        });

        Assert.Equal("", vm.ManifestStatusText);
    }
}
