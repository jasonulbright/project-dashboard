using System.Text.RegularExpressions;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Health;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The Health tab as a reader meets it: what runs on opening it, what refuses and says why, what a
/// cancelled check leaves behind, and the one hand-off it offers.
///
/// The tier split is the contract under test. A deep row must stay at Not run until its own button
/// is pressed, a cancelled check must leave the row where it was rather than at a verdict, and a
/// repository another operation is holding must be refused out loud rather than read mid-write.
/// </summary>
[Collection("app-data-sandbox")]
public class HealthSurfaceTests
{
    private const string Page = "src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml";

    public HealthSurfaceTests() => TestSandbox.ResetDataDir();

    private static ProjectInfo ProjectFor(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = path };
    }

    private static OperationHistory NewHistory() => new(TestEnv.NewDir("health-vm-ledger"));

    private static async Task<ProjectDetailViewModel> OpenedOn(
        string repoPath, GitService? git = null, RepoBusyRegistry? busy = null, BackupService? backups = null)
    {
        var vm = new ProjectDetailViewModel(
            null!, git ?? new GitService(), null!, null, busy ?? new RepoBusyRegistry(),
            backups: backups, history: NewHistory());
        await vm.SetProjectAsync(ProjectFor(repoPath));
        return vm;
    }

    /// <summary>
    /// Holds an fsck open so the page can be observed while a deep check is in flight. Every other
    /// call goes through to real git, so the surrounding quick tier is the shipped one.
    /// </summary>
    private sealed class BlockingFsckGitService : GitService
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public override async Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            if (list.Count > 0 && list[0] == "fsck")
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(ct);
                return new ProcessResult(0, "", "", TimedOut: false);
            }
            return await base.RunAsync(repoPath, list, environment, ct, timeout);
        }
    }

    private static HealthRow Row(ProjectDetailViewModel vm, string id) =>
        vm.HealthRows.First(r => r.Id == id && !r.IsObject);

    // ── Activation and refusal ──────────────────────────────────────────────

    /// <summary>
    /// The quick tier runs once on activation and a revisit is inert, matching every other lazy
    /// surface on this page. Every deep row exists from the first render and every one is Not run.
    /// </summary>
    [Fact]
    public async Task OpeningTheTab_RunsTheQuickTierOnceAndLeavesTheDeepRowsUnrun()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-tab");
        var vm = await OpenedOn(repo.Path);

        await vm.LoadHealthCommand.ExecuteAsync(null);

        Assert.True(vm.HealthLoaded);
        Assert.Contains(vm.HealthRows, r => r.Id == HealthCheckId.GitVersion);
        var deep = vm.HealthRows.Where(r => r.IsDeep && !r.IsObject).ToList();
        Assert.Equal(5, deep.Count);
        Assert.All(deep, r => Assert.Equal(HealthState.NotRun, r.State));
        Assert.All(deep, r => Assert.Equal("Not run", r.StateLabel));

        var header = vm.HealthHeaderText;
        await vm.LoadHealthCommand.ExecuteAsync(null);
        Assert.Equal(header, vm.HealthHeaderText);
    }

    /// <summary>
    /// A page that has run nothing says so. Rendered as a date it never took, the header would
    /// claim a check this session did not make.
    /// </summary>
    [Fact]
    public async Task BeforeAnythingRuns_TheHeaderSaysNothingHasBeenChecked()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-header");
        var vm = await OpenedOn(repo.Path);

        Assert.Equal(HealthCopy.NeverChecked, vm.HealthHeaderText);

        await vm.LoadHealthCommand.ExecuteAsync(null);

        Assert.StartsWith("Quick checks last run", vm.HealthHeaderText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every read here is leaseless, so a repository another operation holds is skipped and the
    /// skip is stated. Reading refs mid-swap would produce an answer about a repository in motion.
    /// </summary>
    [Fact]
    public async Task ABusyRepository_IsRefusedOutLoudRatherThanRead()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-busy");
        var busy = new RepoBusyRegistry();
        var vm = await OpenedOn(repo.Path, busy: busy);
        using var lease = busy.Acquire(repo.Path);

        await vm.LoadHealthCommand.ExecuteAsync(null);

        Assert.False(vm.HealthLoaded);
        Assert.Equal(SafetyCopy.RepoBusyRefusal, vm.HealthStatusText);
    }

    /// <summary>A deep check refuses on the same condition, and names it rather than doing nothing.</summary>
    [Fact]
    public async Task ADeepCheckAgainstABusyRepository_IsRefusedWithoutRunning()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-busy-deep");
        var busy = new RepoBusyRegistry();
        var vm = await OpenedOn(repo.Path, busy: busy);
        await vm.LoadHealthCommand.ExecuteAsync(null);
        using var lease = busy.Acquire(repo.Path);

        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Connectivity));

        Assert.Equal(SafetyCopy.RepoBusyRefusal, vm.HealthStatusText);
        Assert.Equal(HealthState.NotRun, Row(vm, HealthCheckId.Connectivity).State);
    }

    /// <summary>
    /// The page runs one check at a time, and a button that did nothing without saying so is the
    /// thing every other refusal on this surface exists to avoid.
    /// </summary>
    [Fact]
    public async Task ASecondCheckWhileOneIsRunning_IsRefusedWithTheReason()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-one-at-a-time");
        var git = new BlockingFsckGitService();
        var vm = await OpenedOn(repo.Path, git);
        await vm.LoadHealthCommand.ExecuteAsync(null);

        var first = vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Connectivity));
        await git.Started;

        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Strict));
        Assert.Equal(SafetyCopy.CheckAlreadyRunningRefusal, vm.HealthStatusText);

        git.Release();
        await first;
        Assert.Equal(HealthState.NotRun, Row(vm, HealthCheckId.Strict).State);
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    /// <summary>
    /// A cancelled check measured part of a store and nothing more. The row stays where it was —
    /// for a first run that is Not run, never a pass the check never reached.
    /// </summary>
    [Fact]
    public async Task ACancelledCheck_LeavesTheRowAtNotRun()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-cancel");
        var git = new BlockingFsckGitService();
        var vm = await OpenedOn(repo.Path, git);
        await vm.LoadHealthCommand.ExecuteAsync(null);

        var running = vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Connectivity));
        await git.Started;
        Assert.True(vm.HealthCheckRunning);

        vm.CancelHealthCheckCommand.Execute(null);
        await running;

        Assert.False(vm.HealthCheckRunning);
        Assert.Equal(HealthState.NotRun, Row(vm, HealthCheckId.Connectivity).State);
        Assert.Contains("cancelled", vm.HealthStatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A deep read outliving the page that asked for it holds a git process against a repository
    /// nobody is looking at. Leaving the page stops it.
    /// </summary>
    [Fact]
    public async Task LeavingThePage_StopsARunningCheck()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-leave");
        var git = new BlockingFsckGitService();
        var vm = await OpenedOn(repo.Path, git);
        await vm.LoadHealthCommand.ExecuteAsync(null);

        var running = vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Connectivity));
        await git.Started;

        vm.CancelHealthChecksOnLeave();
        await running;

        Assert.False(vm.HealthCheckRunning);
        Assert.Equal(HealthState.NotRun, Row(vm, HealthCheckId.Connectivity).State);
    }

    /// <summary>
    /// A result left standing across a project switch would describe the repository the page just
    /// left, under the name of the one that took the screen.
    /// </summary>
    [Fact]
    public async Task SwitchingProject_DropsEveryAnswerAndTheHeaderWithIt()
    {
        using var first = await TempRepo.CreateWithCommitAsync("health-switch-a");
        using var second = await TempRepo.CreateWithCommitAsync("health-switch-b");
        var vm = await OpenedOn(first.Path);
        await vm.LoadHealthCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.HealthRows);

        await vm.SetProjectAsync(ProjectFor(second.Path));

        Assert.False(vm.HealthLoaded);
        Assert.Empty(vm.HealthRows);
        Assert.Equal(HealthCopy.NeverChecked, vm.HealthHeaderText);
    }

    // ── Results ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An expensive result carries the moment it was taken. Nothing on this page re-runs one on its
    /// own, so a result still on screen from an earlier press reads as current without the stamp.
    /// </summary>
    [Fact]
    public async Task ADeepResult_CarriesWhenItWasTaken()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-stamp");
        var vm = await OpenedOn(repo.Path);
        await vm.LoadHealthCommand.ExecuteAsync(null);

        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Connectivity));

        var row = Row(vm, HealthCheckId.Connectivity);
        Assert.Equal(HealthState.Ok, row.State);
        Assert.StartsWith(HealthCopy.ConnectivityClean, row.Summary, StringComparison.Ordinal);
        Assert.Contains("As of ", row.Summary, StringComparison.Ordinal);
        Assert.Equal("Run again", row.ActionLabel);
    }

    /// <summary>
    /// Running the connectivity check leaves the full read exactly where it was. Escalating from
    /// the cheap answer to the expensive one would spend minutes nobody asked for.
    /// </summary>
    [Fact]
    public async Task AConnectivityPass_DoesNotEscalateIntoTheFullRead()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-no-escalate");
        var vm = await OpenedOn(repo.Path);
        await vm.LoadHealthCommand.ExecuteAsync(null);

        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.Connectivity));

        Assert.Equal(HealthState.NotRun, Row(vm, HealthCheckId.Strict).State);
        Assert.Equal(HealthCopy.StrictNotRun, Row(vm, HealthCheckId.Strict).Summary);
    }

    /// <summary>
    /// The verification the health page runs is written back onto the Backups browser's rows, so a
    /// bundle a restore would refuse is not shown as restorable on the other surface.
    /// </summary>
    [Fact]
    public async Task VerifyingBackupsHere_MarksTheBackupsBrowserRows()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-verify-writeback");
        var settings = new SettingsService();
        settings.Save(new AppSettings { BackupRetentionCount = 5 });
        var backups = new BackupService(new GitService(), settings, NewHistory());
        var handle = await backups.CreateBackupAsync(repo.Path, "fixture");
        var vm = await OpenedOn(repo.Path, backups: backups);
        await vm.LoadHealthCommand.ExecuteAsync(null);
        await vm.OpenBackupsCommand.ExecuteAsync(null);

        var entry = vm.BackupList.Single(e => e.Handle.UtcStamp == handle.UtcStamp);
        Assert.Null(entry.Verification);

        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.BackupVerify));
        Assert.Equal(BundleVerifyState.Verified, entry.Verification);

        await File.WriteAllTextAsync(handle.BundlePath, "not a bundle");
        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.BackupVerify));
        Assert.Equal(BundleVerifyState.Failed, entry.Verification);
    }

    /// <summary>
    /// The one place this tab acts, and it acts by handing off: the path lands in the wizard's
    /// purge field and nothing is removed. The wizard's own gates are what remove anything.
    /// </summary>
    [Fact]
    public async Task TheLargeObjectHandOff_FillsThePurgeFieldAndRemovesNothing()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("health-purge-handoff");
        repo.WriteFile("payload.bin", new string('z', 200_000));
        await repo.CommitAllAsync("add a payload");
        var vm = await OpenedOn(repo.Path);
        await vm.LoadHealthCommand.ExecuteAsync(null);

        await vm.RunHealthRowActionCommand.ExecuteAsync(Row(vm, HealthCheckId.LargeObjects));

        var objectRow = vm.HealthRows.First(r => r.IsObject);
        Assert.Equal("payload.bin", objectRow.Title);
        Assert.Equal("Purge…", objectRow.ActionLabel);

        await vm.RunHealthRowActionCommand.ExecuteAsync(objectRow);

        Assert.True(vm.RewriteWizardVisible);
        Assert.True(vm.RewriteOperationIsPurgePath);
        Assert.Equal("payload.bin", vm.RewritePurgePathsText);
        Assert.Equal(await repo.CommitCountAsync(), await repo.CommitCountAsync());
        Assert.True(repo.FileExists("payload.bin"));
    }

    // ── Accessible naming ───────────────────────────────────────────────────

    /// <summary>
    /// Each part of a composed name carries its own separator, so a row with no state word and no
    /// detail is announced without punctuation around the values it does not have.
    /// </summary>
    [Fact]
    public void ARowName_CarriesNoSeparatorAroundAValueItLacks()
    {
        var bare = new HealthRow
        {
            Id = "x", Title = "Largest objects", State = HealthState.Ok, Summary = "", IsObject = true,
        };
        Assert.Equal("Largest objects", bare.AccessibleName);

        var full = new HealthRow
        {
            Id = "x", Title = "Lock files", State = HealthState.Bad,
            Summary = "2 lock files present.", Detail = "HEAD.lock",
        };
        Assert.Equal("Lock files, Needs attention, 2 lock files present., HEAD.lock", full.AccessibleName);
    }

    /// <summary>An object row is a listing, and a state word beside it would read as a verdict about the file.</summary>
    [Fact]
    public void AnObjectRow_CarriesNoStateWord()
    {
        var row = new HealthRow
        {
            Id = HealthCheckId.LargeObjects, Title = "big.bin", State = HealthState.Ok,
            Summary = "293.0 KiB", IsObject = true,
        };

        Assert.Equal("", row.StateLabel);
    }

    // ── Shipped markup ──────────────────────────────────────────────────────

    /// <summary>
    /// XAML compiles to BAML with no runtime API for the attached properties a template declares,
    /// so the row's name and the tab's routing tag are asserted against the markup itself.
    /// </summary>
    [Fact]
    public void TheHealthTab_IsTaggedWithItsRoutingValue()
    {
        var page = RepoSource.Read(Page);

        Assert.Contains(
            @"<TabItem Header=""Health"" Tag=""{x:Static models:DetailTab.Health}"">", page,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHealthRowContainer_IsNamedFromTheModel()
    {
        var page = RepoSource.Read(Page);
        var list = Regex.Match(page,
            @"AutomationProperties\.Name=""Repository health checks"">(?<body>.*?)</ListBox>",
            RegexOptions.Singleline);

        Assert.True(list.Success, "the health list moved");
        Assert.Contains(
            @"<Setter Property=""AutomationProperties.Name"" Value=""{Binding AccessibleName}"" />",
            list.Groups["body"].Value, StringComparison.Ordinal);
    }

    /// <summary>The outcome line is announced when it changes; a refusal nobody hears is a button that did nothing.</summary>
    [Fact]
    public void TheHealthStatusLine_IsAnnouncedPolitely()
    {
        var page = RepoSource.Read(Page);
        var line = Regex.Match(page,
            @"AutomationProperties\.AutomationId=""HealthStatusText""\s+(?<rest>[^/]*)/>",
            RegexOptions.Singleline);

        Assert.True(line.Success, "the health status line moved");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", line.Groups["rest"].Value,
            StringComparison.Ordinal);
    }
}
