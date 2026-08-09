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

    /// <summary>Refuses every write, and records what it was asked to write it to.</summary>
    private sealed class RefusingDiscoveryService()
        : ProjectDiscoveryService(null!, null!, null!, new ManifestStore())
    {
        public List<(string RepoPath, ProjectManifest Manifest)> Attempts { get; } = [];

        public int Calls => Attempts.Count;

        public override Task<bool> SaveManifestAsync(string repoPath, ProjectManifest manifest,
            CancellationToken ct = default)
        {
            Attempts.Add((repoPath, manifest));
            return Task.FromResult(false);
        }
    }

    private static ProjectInfo ProjectNamed(string folder, ProjectManifest manifest) => new()
    {
        DirectoryName = folder,
        DisplayName = folder,
        FullPath = $@"C:\projects\{folder}",
        HasManifest = true,
        Manifest = manifest
    };

    private static async Task<ProjectDetailViewModel> VmOnAsync(ProjectDiscoveryService discovery,
        ProjectManifest manifest)
    {
        var vm = new ProjectDetailViewModel(discovery, new GitService(), null!);
        await vm.SetProjectAsync(ProjectNamed("metadata-editor", manifest));
        return vm;
    }

    [Fact]
    public async Task Save_ThatLanded_AdoptsTheManifestAndSaysSo()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest { Category = "Uncategorized" });
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
        var vm = await VmOnAsync(discovery, new ProjectManifest { Category = "Uncategorized" });
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

        var vm = await VmOnAsync(RealDiscovery(), seeded!);
        Assert.Equal("imported description", vm.ManifestDescription);

        vm.SelectedCategory = "Tools";
        await vm.SaveManifestCommand.ExecuteAsync(null);

        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("imported description", stored!.Description);
        Assert.Equal("Tools", stored.Category);

        // Reload the way discovery does — straight out of the store.
        var reloaded = await VmOnAsync(RealDiscovery(), stored);
        Assert.Equal("imported description", reloaded.ManifestDescription);
        Assert.Equal("imported description", reloaded.Project!.Manifest.Description);
    }

    [Fact]
    public async Task Save_WritesAnEditedDescription()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest { Description = "old" });

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

    // ── Notes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing else on the page persists notes: before the write moved onto the close, the text
    /// lived in the editor until the next project load overwrote it from the stored manifest.
    /// </summary>
    [Fact]
    public async Task LeavingTheNotesEditor_WritesWhatWasTyped()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest());

        await vm.ToggleEditNotesCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditingNotes);
        vm.Notes = "TASK: wire the stash preview\n";

        await vm.ToggleEditNotesCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditingNotes);
        Assert.Equal("Notes saved.", vm.NotesStatusText);
        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("TASK: wire the stash preview\n", stored!.Notes);
        Assert.Equal(1, vm.Project!.TaskCount);
    }

    /// <summary>
    /// A close that swallowed the failure would drop the typed text with the editor, and the
    /// reader would find the old notes back on the next load with nothing having said why.
    /// </summary>
    [Fact]
    public async Task LeavingTheNotesEditor_WhenTheWriteFails_StaysOpenOverTheTextAndSaysSo()
    {
        var vm = await VmOnAsync(new RefusingDiscoveryService(), new ProjectManifest { Notes = "old\n" });

        await vm.ToggleEditNotesCommand.ExecuteAsync(null);
        vm.Notes = "BUG: not saved yet\n";

        await vm.ToggleEditNotesCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditingNotes);
        Assert.Equal("BUG: not saved yet\n", vm.Notes);
        Assert.Equal(ProjectDetailViewModel.NotesSaveFailed, vm.NotesStatusText);
        Assert.Equal("old\n", vm.Project!.Manifest.Notes);
    }

    /// <summary>
    /// The metadata card has its own Save. Closing the notes editor must not carry a metadata
    /// edit nobody sanctioned into the store with it.
    /// </summary>
    [Fact]
    public async Task LeavingTheNotesEditor_LeavesUnsavedMetadataEditsWhereTheyAre()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest { Category = "Uncategorized", Description = "kept" });

        vm.SelectedCategory = "Tools";
        await vm.ToggleEditNotesCommand.ExecuteAsync(null);
        vm.Notes = "PLAN: something\n";
        await vm.ToggleEditNotesCommand.ExecuteAsync(null);

        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("PLAN: something\n", stored!.Notes);
        Assert.Equal("Uncategorized", stored.Category);
        Assert.Equal("kept", stored.Description);
        // The pending metadata edit is still in the editor for its own Save.
        Assert.Equal("Tools", vm.SelectedCategory);
    }

    /// <summary>
    /// Every navigation back to the page re-applies the project already open. Re-reading the
    /// stored notes over an open editor discards exactly the text the close is there to save.
    /// </summary>
    [Fact]
    public async Task ReloadingTheSameProject_LeavesAnOpenNotesEditorAlone()
    {
        var project = new ProjectInfo
        {
            DirectoryName = "metadata-editor",
            DisplayName = "metadata-editor",
            FullPath = RepoPath,
            HasManifest = true,
            Manifest = new ProjectManifest { Notes = "stored\n" }
        };
        var vm = new ProjectDetailViewModel(RealDiscovery(), new GitService(), null!);
        await vm.SetProjectAsync(project);

        await vm.ToggleEditNotesCommand.ExecuteAsync(null);
        vm.Notes = "TASK: half typed\n";

        await vm.SetProjectAsync(project);

        Assert.True(vm.IsEditingNotes);
        Assert.Equal("TASK: half typed\n", vm.Notes);
    }

    /// <summary>
    /// Clicking another project card is the ordinary way out of the notes editor, and it used to
    /// take the typed text with it — no write, no notice. The text belongs to the project being
    /// left, so it is written there before the swap, and the incoming project still reads its own.
    /// </summary>
    [Fact]
    public async Task SwitchingProjectsWithTheEditorOpen_SavesToTheOutgoingProjectFirst()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest { Notes = "first\n" });
        var outgoing = vm.Project!;
        await vm.ToggleEditNotesCommand.ExecuteAsync(null);
        vm.Notes = "TASK: typed just before the click\n";

        await vm.SetProjectAsync(ProjectNamed("metadata-editor-other",
            new ProjectManifest { Notes = "second\n" }));

        // Written to the repository it was typed against, not the one that took the screen.
        Assert.True(new ManifestStore().TryGet(RepoPath, out var stored));
        Assert.Equal("TASK: typed just before the click\n", stored!.Notes);
        Assert.Equal("TASK: typed just before the click\n", outgoing.Manifest.Notes);
        Assert.False(new ManifestStore().TryGet(@"C:\projects\metadata-editor-other", out _));

        // The switch completed onto the incoming project, which reads its own notes.
        Assert.False(vm.IsEditingNotes);
        Assert.Equal("second\n", vm.Notes);
        Assert.Equal("", vm.NotesStatusText);
    }

    /// <summary>
    /// A failed write must not trap the reader on the page — losing navigation is worse than
    /// losing the text — but it must not be silent either. The notice names the project the
    /// notes belonged to, since it is read on the page of the one switched to.
    /// </summary>
    [Fact]
    public async Task SwitchingProjects_WhenTheNotesWriteFails_StillSwitchesAndSaysWhichProject()
    {
        var discovery = new RefusingDiscoveryService();
        var vm = await VmOnAsync(discovery, new ProjectManifest { Notes = "first\n" });
        await vm.ToggleEditNotesCommand.ExecuteAsync(null);
        vm.Notes = "BUG: about to be lost\n";
        discovery.Attempts.Clear();

        await vm.SetProjectAsync(ProjectNamed("metadata-editor-other",
            new ProjectManifest { Notes = "second\n" }));

        // The write was attempted, against the outgoing repository and with the pending text.
        var attempt = Assert.Single(discovery.Attempts);
        Assert.Equal(RepoPath, attempt.RepoPath);
        Assert.Equal("BUG: about to be lost\n", attempt.Manifest.Notes);

        // The switch went through anyway, and the failure is on screen naming the project.
        Assert.Equal("metadata-editor-other", vm.Project!.DirectoryName);
        Assert.False(vm.IsEditingNotes);
        Assert.Equal("second\n", vm.Notes);
        Assert.Equal(ProjectDetailViewModel.NotesLeftUnsaved("metadata-editor"), vm.NotesStatusText);
    }

    /// <summary>A switch with the editor closed writes nothing — there is no pending text.</summary>
    [Fact]
    public async Task SwitchingProjectsWithTheEditorClosed_WritesNothing()
    {
        var discovery = new RefusingDiscoveryService();
        var vm = await VmOnAsync(discovery, new ProjectManifest { Notes = "first\n" });
        discovery.Attempts.Clear();

        await vm.SetProjectAsync(ProjectNamed("metadata-editor-other",
            new ProjectManifest { Notes = "second\n" }));

        Assert.Empty(discovery.Attempts);
        Assert.Equal("", vm.NotesStatusText);
        Assert.Equal("second\n", vm.Notes);
    }

    [Fact]
    public async Task TheNotesButton_NamesWhatItDoesInEitherState()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest());

        Assert.Equal("Edit", vm.NotesEditLabel);
        Assert.Equal("Edit the project notes", vm.NotesEditName);

        vm.IsEditingNotes = true;

        Assert.Equal("Save", vm.NotesEditLabel);
        Assert.Equal("Save the project notes", vm.NotesEditName);
    }

    [Fact]
    public void TheNotesOutcome_IsRenderedBesideTheEditor()
    {
        var markup = RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

        Assert.Contains("{Binding NotesStatusText}", markup);
        Assert.Contains(@"Content=""{Binding NotesEditLabel}""", markup);
    }

    [Fact]
    public async Task SwitchingProjects_ClearsTheSaveOutcomeOfThePreviousOne()
    {
        var vm = await VmOnAsync(RealDiscovery(), new ProjectManifest());
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
