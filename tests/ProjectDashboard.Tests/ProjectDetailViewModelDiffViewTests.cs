using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The diff pane's two layouts as the Changes tab drives them (X-01). The rendering is the
/// only thing that changes: the same rows, the same hunk indexes, the same selection, and no
/// second read of the repository.
/// </summary>
public class ProjectDetailViewModelDiffViewTests
{
    private const string FifteenLines =
        "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nl15\n";
    private const string FifteenEdited =
        "L1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\nl13\nl14\nL15\n";

    /// <summary>Counts the git invocations a surface makes, so "no second git call" is a fact.</summary>
    private sealed class CountingGitService : GitService
    {
        public int Invocations { get; private set; }

        public override Task<ProcessResult> RunAsync(string repoPath, IEnumerable<string> args,
            IReadOnlyDictionary<string, string>? environment, CancellationToken ct = default,
            TimeSpan? timeout = null)
        {
            Invocations++;
            return base.RunAsync(repoPath, args, environment, ct, timeout);
        }
    }

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static async Task<TempRepo> TwoHunkRepoAsync(string prefix)
    {
        var repo = await TempRepo.CreateWithCommitAsync(prefix);
        repo.WriteFile("file.txt", FifteenLines);
        await repo.CommitAllAsync("fifteen lines");
        repo.WriteFile("file.txt", FifteenEdited);
        return repo;
    }

    private static async Task<ProjectDetailViewModel> OpenOnFileAsync(TempRepo repo, GitService git)
    {
        var vm = new ProjectDetailViewModel(null!, git, null!);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.WorkingStateRefresh;
        vm.SelectedUnstagedFile = vm.UnstagedFiles.First(f => f.Path == "file.txt");
        await vm.DiffRefresh;
        return vm;
    }

    [Fact]
    public async Task SwitchingToTheColumns_ReadsNothingFromGitAgain()
    {
        using var repo = await TwoHunkRepoAsync("vm-sbs-nocall");
        var git = new CountingGitService();
        var vm = await OpenOnFileAsync(repo, git);
        var before = git.Invocations;

        vm.ToggleDiffLayoutCommand.Execute(null);

        Assert.True(vm.DiffSideBySide);
        Assert.Equal(before, git.Invocations);
        Assert.NotEmpty(vm.DiffRows);
    }

    /// <summary>The unified rows stay untouched, so switching back costs nothing either.</summary>
    [Fact]
    public async Task TheColumns_AreBuiltFromTheRowsTheUnifiedPaneHolds()
    {
        using var repo = await TwoHunkRepoAsync("vm-sbs-sameRows");
        var vm = await OpenOnFileAsync(repo, new GitService());

        vm.ApplyDiffLayout(true);

        foreach (var line in vm.DiffLines)
            Assert.Single(vm.DiffRows, r => r.Covers(line));
    }

    [Fact]
    public async Task SelectingAColumnRow_NamesTheHunkTheUnifiedRowNames()
    {
        using var repo = await TwoHunkRepoAsync("vm-sbs-select");
        var vm = await OpenOnFileAsync(repo, new GitService());
        vm.ApplyDiffLayout(true);

        var second = vm.DiffRows.First(r => r.IsHunkStart && r.HunkIndex == 1);
        vm.SelectedDiffRow = second;

        Assert.NotNull(vm.SelectedDiffLine);
        Assert.Equal(1, vm.SelectedDiffLine!.HunkIndex);
        Assert.Null(vm.StageHunkBlockedReason);
    }

    /// <summary>
    /// The hunk gates read the selected LINE, so a selection made in the column pane has to
    /// reach them — otherwise every hunk action reads as unavailable in side-by-side.
    /// </summary>
    [Fact]
    public async Task StagingAHunkPickedInTheColumns_StagesOnlyThatHunk()
    {
        using var repo = await TwoHunkRepoAsync("vm-sbs-stage");
        var vm = await OpenOnFileAsync(repo, new GitService());
        vm.ApplyDiffLayout(true);

        vm.SelectedDiffRow = vm.DiffRows.First(r => r.IsHunkStart && r.HunkIndex == 0);
        await vm.StageHunkCommand.ExecuteAsync(null);

        var git = new GitService();
        var state = await git.GetWorkingStateAsync(repo.Path);
        var staged = await git.GetFileDiffAsync(repo.Path, state!.Staged.Single(), staged: true);
        Assert.Contains(staged!.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "L1");
        Assert.DoesNotContain(staged.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "L15");
    }

    /// <summary>
    /// The refresh a hunk operation triggers replaces every row. The column pane follows the
    /// line the view model re-selected rather than dropping the reader at the top of the diff.
    /// </summary>
    [Fact]
    public async Task AfterAHunkOperation_TheColumnPaneIsStillOnAHunkRow()
    {
        using var repo = await TwoHunkRepoAsync("vm-sbs-focus");
        var vm = await OpenOnFileAsync(repo, new GitService());
        vm.ApplyDiffLayout(true);

        vm.SelectedDiffRow = vm.DiffRows.First(r => r.IsHunkStart && r.HunkIndex == 1);
        await vm.StageHunkCommand.ExecuteAsync(null);
        await vm.DiffRefresh;

        Assert.NotNull(vm.SelectedDiffRow);
        Assert.True(vm.SelectedDiffRow!.IsHunkStart);
        Assert.Same(vm.SelectedDiffLine, vm.SelectedDiffRow.Source);
    }

    /// <summary>Switching back leaves nothing behind to render or keep in step.</summary>
    [Fact]
    public async Task SwitchingBackToUnified_DropsTheColumnRows()
    {
        using var repo = await TwoHunkRepoAsync("vm-sbs-back");
        var vm = await OpenOnFileAsync(repo, new GitService());

        vm.ApplyDiffLayout(true);
        vm.ApplyDiffLayout(false);

        Assert.Empty(vm.DiffRows);
        Assert.True(vm.DiffUnified);
        Assert.NotEmpty(vm.DiffLines);
    }

    [Fact]
    public void TheLabel_NamesTheLayoutTheButtonSwitchesTo()
    {
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!);

        Assert.Equal("Side-by-side view", vm.DiffLayoutLabel);
        vm.ApplyDiffLayout(true);
        Assert.Equal("Unified view", vm.DiffLayoutLabel);
    }
}

/// <summary>
/// The layout is persisted and live-applied through the one settings notification path, so a
/// write from any surface lands on the pane instead of waiting for a relaunch.
/// </summary>
[Collection("app-data-sandbox")]
public class DiffLayoutSettingTests
{
    public DiffLayoutSettingTests() => TestSandbox.ResetDataDir();

    [Fact]
    public void TogglingTheLayout_SurvivesARelaunch()
    {
        var settings = new SettingsService();
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, settingsService: settings);

        vm.ToggleDiffLayoutCommand.Execute(null);

        Assert.True(new SettingsService().Load().DiffSideBySide);
    }

    [Fact]
    public void APaneOpenedAfterTheWrite_StartsInTheSavedLayout()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { DiffSideBySide = true });

        Assert.True(new ProjectDetailViewModel(null!, new GitService(), null!, settingsService: settings)
            .DiffSideBySide);
    }

    [Fact]
    public void ASettingsWriteFromAnotherSurface_ReachesTheOpenPane()
    {
        var settings = new SettingsService();
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, settingsService: settings);
        Assert.False(vm.DiffSideBySide);

        settings.Save(new AppSettings { DiffSideBySide = true });

        Assert.True(vm.DiffSideBySide);
    }

    /// <summary>The write is a read-modify-write: an unrelated setting must not be reset by it.</summary>
    [Fact]
    public void TogglingTheLayout_KeepsEverySettingItDoesNotOwn()
    {
        var settings = new SettingsService();
        settings.Save(new AppSettings { Theme = "Light", ProjectsRootPath = @"C:\elsewhere" });
        var vm = new ProjectDetailViewModel(null!, new GitService(), null!, settingsService: settings);

        vm.ApplyDiffLayout(true);

        var saved = new SettingsService().Load();
        Assert.True(saved.DiffSideBySide);
        Assert.Equal("Light", saved.Theme);
        Assert.Equal(@"C:\elsewhere", saved.ProjectsRootPath);
    }

    [Fact]
    public void ADiffLayoutWrite_IsTheOnlyTriggerItRaises()
    {
        var change = new SettingsChange(new AppSettings(), new AppSettings { DiffSideBySide = true });

        Assert.True(SettingsDelta.DiffLayoutChanged(change));
        Assert.False(SettingsDelta.RediscoveryRequired(change));
        Assert.False(SettingsDelta.ViewPreferencesChanged(change));
        Assert.False(SettingsDelta.ThemeChanged(change));
    }
}
