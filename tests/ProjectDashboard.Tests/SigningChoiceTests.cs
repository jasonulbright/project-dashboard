using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Surgery;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Object signing on the everyday paths — Commit/Amend and tag creation: what each repository is
/// read to be configured as, what each <see cref="SigningChoice"/> puts on git's command line, and
/// what the Changes tab and the tag viewer do with a repository that signs.
///
/// The fixtures configure signing on and point git at a signing program that does not exist, so a
/// signed run fails at once. Nothing here needs — or may require — a real key.
/// </summary>
public class SigningChoiceTests
{
    /// <summary>Commit signing configured on, with no signer that can answer for it.</summary>
    private static async Task<TempRepo> SigningRepoAsync()
    {
        var repo = await TempRepo.CreateWithCommitAsync("signing");
        await repo.GitAsync("config", "commit.gpgsign", "true");
        await repo.GitAsync("config", "gpg.program", "pd-no-such-signing-program");
        return repo;
    }

    /// <summary>
    /// Installs a pre-commit hook that rejects every commit, printing the given words to stderr.
    /// git runs the hook before it writes or signs anything, so the hook's refusal is the whole
    /// failure — which is what makes it the test for classifying one.
    /// </summary>
    private static void RejectCommitsWithHook(TempRepo repo, string stderrLine)
    {
        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"),
            $"#!/bin/sh\necho \"{stderrLine}\" >&2\nexit 1\n".Replace("\r\n", "\n"));
    }

    /// <summary>Tag signing configured on and commit signing off, so the two answers stay apart.</summary>
    private static async Task<TempRepo> TagSigningRepoAsync()
    {
        var repo = await TempRepo.CreateWithCommitAsync("tag-signing");
        await repo.GitAsync("config", "tag.gpgsign", "true");
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
        await vm.SigningRefresh;
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

    // ── Classifying a signing failure ───────────────────────────────────────

    /// <summary>
    /// The wordings git actually emits, pinned literally so a token dropped for being too loose
    /// cannot take a real one with it.
    /// </summary>
    [Theory]
    [InlineData("error: gpg failed to sign the data:\n(no gpg output)\nfatal: failed to write commit object")]
    [InlineData("error: user.signingKey needs to be set for ssh signing\nfatal: failed to write commit object")]
    [InlineData("fatal: either user.signingkey or gpg.ssh.defaultKeyCommand needs to be configured")]
    [InlineData("error: cannot spawn no-such-program: No such file or directory\nerror: unable to sign the tag")]
    public void GitsOwnSigningWordings_AreClassifiedAsSigningFailures(string stderr)
    {
        Assert.True(GitService.IsSigningFailure(new ProcessResult(128, "", stderr, TimedOut: false)));
    }

    /// <summary>
    /// A commit runs arbitrary hooks and their stderr lands in the same text the classifier reads.
    /// A hook refusing a commit over a signing POLICY is not the signing key failing, and calling
    /// it one offers an unsigned retry that reruns the same hook and fails identically.
    /// </summary>
    [Theory]
    [InlineData("commits must include a signing acknowledgement")]
    [InlineData("policy: signing off on this change requires a reviewer")]
    [InlineData("pre-commit: designing docs must accompany schema changes")]
    public void AHookRefusingForItsOwnReasons_IsNotASigningFailure(string stderr)
    {
        Assert.False(GitService.IsSigningFailure(new ProcessResult(1, "", stderr, TimedOut: false)));
    }

    /// <summary>
    /// The same thing end to end: a real hook rejecting a real commit in a repository that really
    /// does sign. The reader gets the hook's own words and no signing advice, and the offer stays
    /// down — an unsigned retry would rerun the hook and fail the same way.
    /// </summary>
    [Fact]
    public async Task AHookRejectingACommitInASigningRepository_ReportsTheHookRatherThanTheKey()
    {
        using var repo = await SigningRepoAsync();
        RejectCommitsWithHook(repo, "commits must include a signing acknowledgement");
        await StageAsync(repo, "second.txt", "two\n");
        var head = await repo.HeadShaAsync();
        var vm = await VmForAsync(repo, new GitService());
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);

        await vm.CommitSignedCommand.ExecuteAsync(null);

        Assert.Contains("signing acknowledgement", vm.SyncStatusText);
        Assert.DoesNotContain("signing key could not sign", vm.SyncStatusText);
        Assert.DoesNotContain("pinentry", vm.SyncStatusText);
        Assert.False(vm.CommitSigningOfferVisible);
        Assert.Equal(head, await repo.HeadShaAsync());
        Assert.Equal("second", vm.CommitMessage);
    }

    // ── Tags: the read and the command line ─────────────────────────────────

    [Fact]
    public async Task SignsTags_IsReadSeparatelyFromTheCommitSetting()
    {
        using var commitsOnly = await SigningRepoAsync();
        using var tagsOnly = await TagSigningRepoAsync();
        var git = new GitService();

        Assert.True(await git.SignsCommitsAsync(commitsOnly.Path));
        Assert.False(await git.SignsTagsAsync(commitsOnly.Path));
        Assert.False(await git.SignsCommitsAsync(tagsOnly.Path));
        Assert.True(await git.SignsTagsAsync(tagsOnly.Path));
    }

    [Theory]
    [InlineData(SigningChoice.NotChosen)]
    [InlineData(SigningChoice.KeepSigning)]
    public async Task ATagThatWasNotToldToProceedUnsigned_CarriesNoSigningOverride(SigningChoice signing)
    {
        var git = new ArgvGitService();

        await git.CreateTagAsync(@"C:\repo", "v1", "release", targetCommit: null, signing);

        Assert.Equal(["tag", "-a", "--cleanup=whitespace", "-m", "release", "v1"], git.Runs[0]);
    }

    /// <summary>
    /// The override goes ahead of the subcommand, and the message cleanup pin still rides along:
    /// `tag -a` takes strip as its own default and honours no config, so losing the pin would
    /// silently delete every message line that opens with the comment character.
    /// </summary>
    [Fact]
    public async Task ATagProceedingUnsigned_PinsTagSigningOffAndKeepsTheMessageCleanupPin()
    {
        var git = new ArgvGitService();

        await git.CreateTagAsync(@"C:\repo", "v1", "release", "abc123", SigningChoice.ProceedUnsigned);

        Assert.Equal(
            ["-c", "tag.gpgsign=false", "tag", "-a", "--cleanup=whitespace", "-m", "release", "v1", "abc123"],
            git.Runs[0]);
    }

    [Fact]
    public async Task ALightweightTagProceedingUnsigned_CarriesTheOverrideAndNoMessageFlags()
    {
        var git = new ArgvGitService();

        await git.CreateTagAsync(@"C:\repo", "v1", message: null, null, SigningChoice.ProceedUnsigned);

        Assert.Equal(["-c", "tag.gpgsign=false", "tag", "v1"], git.Runs[0]);
    }

    [Fact]
    public async Task OnlyTheSigningTagRun_GetsTheLongerBudget()
    {
        var git = new ArgvGitService();

        await git.CreateTagAsync(@"C:\repo", "a", "m", null, SigningChoice.NotChosen);
        await git.CreateTagAsync(@"C:\repo", "b", "m", null, SigningChoice.KeepSigning);

        Assert.Null(git.Timeouts[0]);
        Assert.True(git.Timeouts[1] > TimeSpan.FromSeconds(30));
    }

    // ── Tags: the service against a repository that signs ───────────────────

    [Fact]
    public async Task ASignedTagWithNoWorkingSigner_CreatesNothingAndIsRecognisedAsASigningFailure()
    {
        using var repo = await TagSigningRepoAsync();

        var result = await new GitService().CreateTagAsync(
            repo.Path, "v1", "release", null, SigningChoice.KeepSigning);

        Assert.False(result.Success);
        Assert.True(GitService.IsSigningFailure(result));
        Assert.Empty((await new GitService().GetTagsAsync(repo.Path)).Tags);
    }

    [Fact]
    public async Task AnUnsignedAnnotatedTag_IsATagObjectCarryingNoSignature()
    {
        using var repo = await TagSigningRepoAsync();

        var result = await new GitService().CreateTagAsync(
            repo.Path, "v1", "release", null, SigningChoice.ProceedUnsigned);

        Assert.True(result.Success);
        Assert.Equal("tag", (await repo.GitAsync("cat-file", "-t", "v1")).Trim());
        Assert.DoesNotContain("-----BEGIN", await repo.GitAsync("cat-file", "-p", "v1"));
        Assert.Equal("true", (await repo.GitAsync("config", "--get", "tag.gpgsign")).Trim());
    }

    /// <summary>
    /// A lightweight request is NOT exempt from tag.gpgsign: git turns a bare `git tag` into a
    /// signed one, which needs a message the request never carried, and fails outright. The
    /// override is what makes a lightweight tag lightweight again — a ref with no tag object.
    /// </summary>
    [Fact]
    public async Task ALightweightRequest_FailsUnderTagSigningAndIsGenuinelyLightweightUnderTheOverride()
    {
        using var repo = await TagSigningRepoAsync();
        var head = await repo.HeadShaAsync();
        var git = new GitService();

        var signed = await git.CreateTagAsync(repo.Path, "v-signed", message: null, null, SigningChoice.KeepSigning);
        Assert.False(signed.Success);

        var unsigned = await git.CreateTagAsync(repo.Path, "v-light", message: null, null, SigningChoice.ProceedUnsigned);

        Assert.True(unsigned.Success);
        Assert.Equal("commit", (await repo.GitAsync("cat-file", "-t", "v-light")).Trim());
        Assert.Equal(head, (await repo.GitAsync("rev-parse", "v-light")).Trim());
    }

    /// <summary>A repository that signs neither is untouched by the gate on either path.</summary>
    [Fact]
    public async Task ARepositoryThatSignsNothing_CreatesBothKindsWithNoChoiceAtAll()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("plain-tags");
        var git = new GitService();

        Assert.True((await git.CreateTagAsync(repo.Path, "v-light")).Success);
        Assert.True((await git.CreateTagAsync(repo.Path, "v-annot", "release")).Success);
        Assert.Equal("commit", (await repo.GitAsync("cat-file", "-t", "v-light")).Trim());
        Assert.Equal("tag", (await repo.GitAsync("cat-file", "-t", "v-annot")).Trim());
    }

    // ── Tags: the viewer ────────────────────────────────────────────────────

    private static async Task<ConfirmingViewModel> TagVmForAsync(TempRepo repo, bool confirm = true)
    {
        var vm = await VmForAsync(repo, new GitService(), confirm);
        await vm.OpenTagsCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task ARepositoryThatSignsTags_ShowsTheTagChip()
    {
        using var repo = await TagSigningRepoAsync();
        await repo.GitAsync("config", "gpg.format", "ssh");
        var vm = await TagVmForAsync(repo);

        Assert.True(vm.TagSigningChipVisible);
        Assert.Equal("Signs tags", vm.TagSigningChipText);
        Assert.Contains("gpg.format is ssh", vm.TagSigningChipTooltip);
        Assert.Contains("lightweight requests included", vm.TagSigningChipTooltip);
    }

    /// <summary>
    /// The two settings are independent and so are the answers: a repository that signs commits
    /// only must not put a tag chip up, and its tags must create without a question.
    /// </summary>
    [Fact]
    public async Task ARepositoryThatSignsCommitsOnly_LeavesTheTagPathUngated()
    {
        using var repo = await SigningRepoAsync();
        var vm = await TagVmForAsync(repo);

        vm.NewTagName = "v1";
        vm.NewTagMessage = "release";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.True(vm.CommitSigningChipVisible);
        Assert.False(vm.TagSigningChipVisible);
        Assert.False(vm.TagSigningOfferVisible);
        Assert.Equal("tag", (await repo.GitAsync("cat-file", "-t", "v1")).Trim());
    }

    [Fact]
    public async Task TheFirstTagOnASigningRepository_AsksBeforeAnythingRuns()
    {
        using var repo = await TagSigningRepoAsync();
        var vm = await TagVmForAsync(repo);

        vm.NewTagName = "v1";
        vm.NewTagMessage = "release";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.True(vm.TagSigningOfferVisible);
        Assert.Contains("tag.gpgsign", vm.TagSigningOfferText);
        Assert.Equal("v1", vm.NewTagName);
        Assert.Equal("release", vm.NewTagMessage);
        Assert.Empty((await new GitService().GetTagsAsync(repo.Path)).Tags);
        Assert.False(File.Exists(Path.Combine(repo.Path, ".git", "TAG_EDITMSG")));
    }

    [Fact]
    public async Task TheTagOffersUnsignedAnswer_ConfirmsFirstAndThenCreatesAnUnsignedTag()
    {
        using var repo = await TagSigningRepoAsync();
        var vm = await TagVmForAsync(repo);
        vm.NewTagName = "v1";
        vm.NewTagMessage = "release";
        await vm.CreateTagCommand.ExecuteAsync(null);

        await vm.CreateTagUnsignedCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Confirmations);
        Assert.Contains("unsigned", vm.LastMessage);
        Assert.False(vm.TagSigningOfferVisible);
        Assert.Equal("Signs tags — this session: unsigned", vm.TagSigningChipText);
        Assert.Equal("tag", (await repo.GitAsync("cat-file", "-t", "v1")).Trim());
        Assert.DoesNotContain("-----BEGIN", await repo.GitAsync("cat-file", "-p", "v1"));
    }

    [Fact]
    public async Task DecliningTheUnsignedTagConfirm_LeavesTheTagUncreatedAndTheOfferStanding()
    {
        using var repo = await TagSigningRepoAsync();
        var vm = await TagVmForAsync(repo, confirm: false);
        vm.NewTagName = "v1";
        vm.NewTagMessage = "release";
        await vm.CreateTagCommand.ExecuteAsync(null);

        await vm.CreateTagUnsignedCommand.ExecuteAsync(null);

        Assert.True(vm.TagSigningOfferVisible);
        Assert.Equal("v1", vm.NewTagName);
        Assert.Empty((await new GitService().GetTagsAsync(repo.Path)).Tags);
    }

    [Fact]
    public async Task TheSignedTagAnswerFailing_NamesTheSigningAndPutsTheChoiceBack()
    {
        using var repo = await TagSigningRepoAsync();
        var vm = await TagVmForAsync(repo);
        vm.NewTagName = "v1";
        vm.NewTagMessage = "release";
        await vm.CreateTagCommand.ExecuteAsync(null);

        await vm.CreateTagSignedCommand.ExecuteAsync(null);

        Assert.Contains("signing key could not sign", vm.SyncStatusText);
        Assert.True(vm.TagSigningOfferVisible);
        Assert.Equal("v1", vm.NewTagName);
        Assert.Empty((await new GitService().GetTagsAsync(repo.Path)).Tags);
    }

    /// <summary>
    /// The offer for a lightweight request states what tag.gpgsign does to it, and signing as
    /// configured is refused before git runs — a signed tag needs a message this one has not been
    /// given, which is knowable without spawning anything.
    /// </summary>
    [Fact]
    public async Task ALightweightRequestOnASigningRepository_SaysWhatSigningWouldDoToIt()
    {
        using var repo = await TagSigningRepoAsync();
        var vm = await TagVmForAsync(repo);
        vm.NewTagName = "v-light";

        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.True(vm.TagSigningOfferVisible);
        Assert.Contains("will not create a lightweight", vm.TagSigningOfferText);

        await vm.CreateTagSignedCommand.ExecuteAsync(null);

        Assert.Contains("signed tag needs a message", vm.TagsErrorText);
        Assert.Empty((await new GitService().GetTagsAsync(repo.Path)).Tags);
        // Refusing the signed answer must leave the other one reachable, or the lightweight tag
        // the reader asked for has no route at all.
        Assert.True(vm.TagSigningOfferVisible);

        await vm.CreateTagUnsignedCommand.ExecuteAsync(null);

        Assert.Equal("commit", (await repo.GitAsync("cat-file", "-t", "v-light")).Trim());
    }

    /// <summary>
    /// Answering for one object kind must not answer for the other: a reader who declined signing
    /// on a working commit has not declined it on a release tag, and inheriting the answer would
    /// drop a signature nobody was asked about.
    /// </summary>
    [Fact]
    public async Task TheCommitAnswer_DoesNotAnswerForTags()
    {
        using var repo = await SigningRepoAsync();
        await repo.GitAsync("config", "tag.gpgsign", "true");
        await StageAsync(repo, "second.txt", "two\n");
        var vm = await TagVmForAsync(repo);
        await vm.RefreshWorkingStateAsync();
        vm.CommitMessage = "second";
        await vm.CommitCommand.ExecuteAsync(null);
        await vm.CommitUnsignedCommand.ExecuteAsync(null);

        Assert.Equal("second", await repo.HeadSubjectAsync());
        Assert.Equal("Signs tags", vm.TagSigningChipText);

        vm.NewTagName = "v1";
        vm.NewTagMessage = "release";
        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.True(vm.TagSigningOfferVisible);
        Assert.Empty((await new GitService().GetTagsAsync(repo.Path)).Tags);
    }
}
