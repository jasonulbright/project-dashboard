using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Commit signing on the everyday Commit/Amend path: what the repository is read to be
/// configured as, what each <see cref="SigningChoice"/> puts on git's command line, and what the
/// Changes tab does with a repository that signs.
///
/// The fixtures configure signing on and point git at a signing program that does not exist, so
/// a signed run fails at once. Nothing here needs — or may require — a real key.
/// </summary>
public class CommitSigningTests
{
    /// <summary>Signing configured on, with no signer that can answer for it.</summary>
    private static async Task<TempRepo> SigningRepoAsync()
    {
        var repo = await TempRepo.CreateWithCommitAsync("signing");
        await repo.GitAsync("config", "commit.gpgsign", "true");
        await repo.GitAsync("config", "gpg.program", "pd-no-such-signing-program");
        return repo;
    }

    /// <summary>Captures one run's argv and timeout without spawning git.</summary>
    private sealed class ArgvGitService : GitService
    {
        public List<string[]> Runs { get; } = [];
        public List<TimeSpan?> Timeouts { get; } = [];

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            Runs.Add([.. args]);
            Timeouts.Add(timeout);
            return Task.FromResult(new ProcessResult(0, "", "", TimedOut: false));
        }
    }

    /// <summary>Answers every commit as the timeout a killed signing run reports, and nothing else.</summary>
    private sealed class TimingOutCommitGitService : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, IReadOnlyDictionary<string, string>? environment,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var captured = args.ToList();
            return captured.Contains("commit")
                ? Task.FromResult(new ProcessResult(-1, "", "", TimedOut: true))
                : base.RunAsync(repoPath, captured, environment, ct, timeout);
        }
    }

    private sealed class ConfirmingViewModel(GitService git, bool answer)
        : ProjectDetailViewModel(null!, git, null!)
    {
        public int Confirmations { get; private set; }
        public string LastMessage { get; private set; } = "";

        internal override Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirmations++;
            LastMessage = message;
            return Task.FromResult(answer);
        }
    }

    private static async Task<ConfirmingViewModel> VmForAsync(TempRepo repo, GitService git, bool confirm = true)
    {
        var vm = new ConfirmingViewModel(git, confirm);
        await OpenAsync(vm, repo);
        return vm;
    }

    private static async Task OpenAsync(ProjectDetailViewModel vm, TempRepo repo)
    {
        var name = Path.GetFileName(repo.Path);
        await vm.SetProjectAsync(new ProjectInfo
        {
            DirectoryName = name,
            DisplayName = name,
            FullPath = repo.Path
        });
        await vm.WorkingStateRefresh;
        await vm.CommitSigningRefresh;
    }

    /// <summary>Stages a new file so the commit gate has something to commit.</summary>
    private static async Task StageAsync(TempRepo repo, string file, string content)
    {
        repo.WriteFile(file, content);
        await repo.GitAsync("add", "--", file);
    }

    // ── The read ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignsCommits_ReportsWhatTheRepositoryIsConfiguredAs()
    {
        using var plain = await TempRepo.CreateWithCommitAsync("plain");
        using var signing = await SigningRepoAsync();
        var git = new GitService();

        Assert.False(await git.SignsCommitsAsync(plain.Path));
        Assert.True(await git.SignsCommitsAsync(signing.Path));
    }

    /// <summary>
    /// git's own default for an unset gpg.format is openpgp; reporting that would name a value
    /// the repository never set, which the chip's tooltip would then attribute to it.
    /// </summary>
    [Fact]
    public async Task TheSigningFormat_IsEmptyWhenUnsetAndTheConfiguredValueOtherwise()
    {
        using var repo = await SigningRepoAsync();
        var git = new GitService();

        Assert.Equal("", await git.GetSigningFormatAsync(repo.Path));

        await repo.GitAsync("config", "gpg.format", "ssh");
        Assert.Equal("ssh", await git.GetSigningFormatAsync(repo.Path));
    }

    // ── The command line ────────────────────────────────────────────────────

    [Theory]
    [InlineData(SigningChoice.NotChosen)]
    [InlineData(SigningChoice.KeepSigning)]
    public async Task ACommitThatWasNotToldToProceedUnsigned_CarriesNoSigningOverride(SigningChoice signing)
    {
        var git = new ArgvGitService();

        await git.CommitAsync(@"C:\repo", "subject", amend: false, signing);

        Assert.Equal(["commit", "--cleanup=whitespace", "-m", "subject"], git.Runs[0]);
    }

    [Fact]
    public async Task ACommitProceedingUnsigned_PinsSigningOffForTheRunAheadOfTheSubcommand()
    {
        var git = new ArgvGitService();

        await git.CommitAsync(@"C:\repo", "subject", amend: true, SigningChoice.ProceedUnsigned);

        Assert.Equal(
            ["-c", "commit.gpgsign=false", "commit", "--cleanup=whitespace", "-m", "subject", "--amend"],
            git.Runs[0]);
    }

    /// <summary>
    /// A signer can put a passphrase prompt on screen for a reader to answer; the unsigned budget
    /// would kill that mid-answer, and raising it for every commit would make an ordinary hang
    /// take four times as long to report.
    /// </summary>
    [Fact]
    public async Task OnlyTheSigningRun_GetsTheLongerBudget()
    {
        var git = new ArgvGitService();

        await git.CommitAsync(@"C:\repo", "s", amend: false, SigningChoice.NotChosen);
        await git.CommitAsync(@"C:\repo", "s", amend: false, SigningChoice.ProceedUnsigned);
        await git.CommitAsync(@"C:\repo", "s", amend: false, SigningChoice.KeepSigning);

        Assert.Equal(TimeSpan.FromSeconds(30), git.Timeouts[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), git.Timeouts[1]);
        Assert.True(git.Timeouts[2] > TimeSpan.FromSeconds(30));
    }

    // ── The service against a repository that signs ─────────────────────────

    [Fact]
    public async Task ASignedCommitWithNoWorkingSigner_WritesNothingAndIsRecognisedAsASigningFailure()
    {
        using var repo = await SigningRepoAsync();
        var head = await repo.HeadShaAsync();
        await StageAsync(repo, "second.txt", "two\n");

        var result = await new GitService().CommitAsync(repo.Path, "second", amend: false, SigningChoice.KeepSigning);

        Assert.False(result.Success);
        Assert.True(GitService.IsSigningFailure(result));
        Assert.Equal(head, await repo.HeadShaAsync());
    }

    [Fact]
    public async Task AnUnsignedCommit_WritesTheCommitAndLeavesTheConfigurationAlone()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");

        var result = await new GitService().CommitAsync(
            repo.Path, "second", amend: false, SigningChoice.ProceedUnsigned);

        Assert.True(result.Success);
        Assert.Equal("second", await repo.HeadSubjectAsync());
        Assert.Equal("N", (await repo.GitAsync("log", "-1", "--format=%G?")).Trim());
        Assert.Equal("true", (await repo.GitAsync("config", "--get", "commit.gpgsign")).Trim());
    }

    /// <summary>
    /// The ssh signer fails fast with its own wording rather than the openpgp one, and it fails
    /// without waiting on anything — a message blaming a passphrase prompt for it would be wrong.
    /// </summary>
    [Fact]
    public async Task TheSshSignerFailingForItsOwnReason_IsStillRecognisedAsASigningFailure()
    {
        using var repo = await SigningRepoAsync();
        await repo.GitAsync("config", "gpg.format", "ssh");
        await StageAsync(repo, "second.txt", "two\n");

        var result = await new GitService().CommitAsync(repo.Path, "second", amend: false, SigningChoice.KeepSigning);

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.True(GitService.IsSigningFailure(result));
    }

    // ── The Changes tab ─────────────────────────────────────────────────────

    [Fact]
    public async Task ARepositoryThatDoesNotSign_ShowsNoChipAndCommitsWithoutAsking()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("plain");
        await StageAsync(repo, "second.txt", "two\n");
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();

        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.CommitSigningChipVisible);
        Assert.False(vm.CommitSigningOfferVisible);
        Assert.Equal("second", await repo.HeadSubjectAsync());
    }

    [Fact]
    public async Task ARepositoryThatSigns_ShowsTheChipWithTheFormatItConfigured()
    {
        using var repo = await SigningRepoAsync();
        await repo.GitAsync("config", "gpg.format", "ssh");
        var vm = await VmForAsync(repo, new GitService());

        Assert.True(vm.CommitSigningChipVisible);
        Assert.Equal("Signs commits", vm.CommitSigningChipText);
        Assert.Contains("gpg.format is ssh", vm.CommitSigningChipTooltip);
    }

    /// <summary>
    /// The refusal is a question, not an attempt: no commit is spawned, and the draft the reader
    /// typed survives it — a message cleared by a refusal is work the reader has to retype.
    /// </summary>
    [Fact]
    public async Task TheFirstCommitOnASigningRepository_AsksBeforeAnythingRuns()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");
        var head = await repo.HeadShaAsync();
        // Written by the fixture's own commit; a `git commit` that reached git would replace it.
        var editMsgPath = Path.Combine(repo.Path, ".git", "COMMIT_EDITMSG");
        var editMsg = File.ReadAllText(editMsgPath);
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();

        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.CommitSigningOfferVisible);
        Assert.Contains("commit.gpgsign", vm.CommitSigningOfferText);
        Assert.Equal("second", vm.CommitMessage);
        Assert.Equal(head, await repo.HeadShaAsync());
        Assert.Equal(editMsg, File.ReadAllText(editMsgPath));
    }

    [Fact]
    public async Task TheOffersUnsignedAnswer_ConfirmsFirstAndThenCommitsWithoutSigning()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        await vm.CommitUnsignedCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("unsigned", vm.LastMessage);
        Assert.False(vm.CommitSigningOfferVisible);
        Assert.Equal("second", await repo.HeadSubjectAsync());
        Assert.Equal("N", (await repo.GitAsync("log", "-1", "--format=%G?")).Trim());
        Assert.Equal("", vm.CommitMessage);
    }

    /// <summary>
    /// The answer is invisible state until the chip says so; a reader who came back to the page
    /// later would otherwise have no way to tell this project's commits are coming out unsigned.
    /// </summary>
    [Fact]
    public async Task AfterTheUnsignedAnswer_TheChipSaysSoAndTheNextCommitDoesNotAskAgain()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);
        await vm.CommitUnsignedCommand.ExecuteAsync(null);

        Assert.Equal("Signs commits — this session: unsigned", vm.CommitSigningChipText);

        await StageAsync(repo, "third.txt", "three\n");
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "third";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.CommitSigningOfferVisible);
        Assert.Equal("third", await repo.HeadSubjectAsync());
    }

    [Fact]
    public async Task DecliningTheUnsignedConfirm_LeavesTheCommitUnrunAndTheOfferStanding()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");
        var head = await repo.HeadShaAsync();
        var vm = await VmForAsync(repo, new GitService(), confirm: false);
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        await vm.CommitUnsignedCommand.ExecuteAsync(null);

        Assert.True(vm.CommitSigningOfferVisible);
        Assert.Equal("second", vm.CommitMessage);
        Assert.Equal(head, await repo.HeadShaAsync());
    }

    /// <summary>
    /// The signed answer is the one that can fail on the signing, and the other answer is then
    /// unreachable unless the offer comes back — the choice is already made, so pressing Commit
    /// again would only repeat it.
    /// </summary>
    [Fact]
    public async Task TheSignedAnswerFailing_NamesTheSigningAndPutsTheChoiceBack()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");
        var head = await repo.HeadShaAsync();
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        await vm.CommitSignedCommand.ExecuteAsync(null);

        Assert.Contains("signing key could not sign", vm.SyncStatusText);
        Assert.True(vm.CommitSigningOfferVisible);
        Assert.Equal("second", vm.CommitMessage);
        Assert.Equal(head, await repo.HeadShaAsync());
    }

    /// <summary>
    /// Guards the failure this whole surface exists for: a signing repository whose passphrase is
    /// not cached leaves git waiting on a prompt no window here shows, and the attempt is killed
    /// at its budget with nothing on stderr to explain it. "Commit failed: timed out" is what the
    /// reader used to get.
    /// </summary>
    [Fact]
    public async Task ASignedCommitKilledAtItsTimeout_NamesThePassphrasePrompt()
    {
        using var repo = await SigningRepoAsync();
        await StageAsync(repo, "second.txt", "two\n");
        var vm = await VmForAsync(repo, new TimingOutCommitGitService());
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        await vm.CommitSignedCommand.ExecuteAsync(null);

        Assert.Contains("pinentry", vm.SyncStatusText);
        Assert.Contains("passphrase", vm.SyncStatusText);
        Assert.True(vm.CommitSigningOfferVisible);
        Assert.Equal("second", vm.CommitMessage);
    }

    /// <summary>
    /// The answer is per repository and written nowhere: a persisted "never sign" would strip
    /// signatures the reader asked for in every later session without saying so.
    /// </summary>
    [Fact]
    public async Task LeavingTheProject_ForgetsTheUnsignedAnswer()
    {
        using var repo = await SigningRepoAsync();
        using var other = await TempRepo.CreateWithCommitAsync("other");
        await StageAsync(repo, "second.txt", "two\n");
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);
        await vm.CommitUnsignedCommand.ExecuteAsync(null);

        await OpenAsync(vm, other);
        Assert.False(vm.CommitSigningChipVisible);

        await OpenAsync(vm, repo);
        await StageAsync(repo, "third.txt", "three\n");
        await vm.RefreshWorkingStateAsync();

        Assert.Equal("Signs commits", vm.CommitSigningChipText);
        vm.CommitMessage = "third";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.CommitSigningOfferVisible);
        Assert.Equal("second", await repo.HeadSubjectAsync());
    }
}
