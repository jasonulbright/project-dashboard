using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// The reflog viewer and the deep clean. What is asserted is that the viewer reads what git
/// recorded and mutates nothing but by adding a branch, that the branch it adds is bound to the
/// entry's commit rather than its shifting <c>@{n}</c> position, and that the deep clean refuses
/// on every gate it claims — the danger-zone opt-in, the typed repository name, a pending
/// interrupted operation, and a stash stack — before it makes anything unrecoverable.
///
/// The deep clean's journal gate reads the app's own journal under AppPaths, so these join the
/// serialized sandbox collection.
/// </summary>
[Collection("app-data-sandbox")]
public class ReflogDeepCleanTests
{
    private readonly ITestOutputHelper _output;

    public ReflogDeepCleanTests(ITestOutputHelper output)
    {
        _output = output;
        TestSandbox.ResetDataDir();
    }

    private static ProjectInfo ProjectFor(RailsRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static DeepCleanService NewDeepClean(RepoBusyRegistry? busy = null, RewriteJournal? journal = null) =>
        new(new GitService(), busy ?? new RepoBusyRegistry(), journal ?? new RewriteJournal());

    /// <summary>Supplies the typed repository name without a window, so the confirmed path is drivable headless.</summary>
    private sealed class PromptingViewModel(DeepCleanService? deepClean, RepoBusyRegistry? busy = null)
        : ProjectDetailViewModel(null!, new GitService(), null!, null, busy, deepClean: deepClean)
    {
        /// <summary>Null stands for a cancelled prompt.</summary>
        public string? Typed { get; set; }

        public bool DangerZone { get; set; } = true;

        public int Prompts { get; private set; }

        internal override bool ReadDangerZoneEnabled() => DangerZone;

        /// <summary>Runs while the prompt is notionally on screen, for the states a reader can reach mid-decision.</summary>
        public Func<Task>? WhileThePromptIsOpen { get; set; }

        internal override async Task<string?> PromptForTextAsync(string title, string message, string confirmLabel)
        {
            Prompts++;
            LastPromptMessage = message;
            if (WhileThePromptIsOpen is { } during) await during();
            return Typed;
        }

        public string LastPromptMessage { get; private set; } = "";
    }

    /// <summary>A repository whose reflog holds a state no ref points at any more.</summary>
    private static async Task<RailsRepo> RepoWithAbandonedStateAsync(string prefix)
    {
        var repo = await RailsRepo.CreateAsync(prefix);
        repo.Write("secret.txt", "a password\n");
        await repo.CommitAllAsync("add the secret");
        repo.Write("secret.txt", "redacted\n");
        await repo.GitAsync("commit", "-a", "--amend", "-m", "add the secret (rewritten)");
        return repo;
    }

    // ── The viewer ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TheViewer_ReadsEveryEntryWithItsSelectorActionSubjectShaAndDate()
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-read");

        var entries = await new GitService().GetReflogAsync(repo.Path, "HEAD");

        Assert.NotEmpty(entries);
        Assert.Equal("HEAD@{0}", entries[0].Selector);
        Assert.Equal("HEAD@{1}", entries[1].Selector);
        Assert.Equal("commit (amend)", entries[0].Action);
        Assert.Equal("add the secret (rewritten)", entries[0].Subject);
        Assert.All(entries, e => Assert.Equal(40, e.Sha.Length));
        Assert.All(entries, e => Assert.NotNull(e.When));
        // The pre-amend commit is still recorded, which is exactly what keeps it reachable.
        Assert.Contains(entries, e => e.Subject == "add the secret");
        _output.WriteLine(string.Join("\n", entries.Select(e => $"{e.Selector} {e.ShortSha} {e.Description}")));
    }

    [Theory]
    [InlineData("commit: add the secret", "commit", "add the secret")]
    [InlineData("commit (amend): fix it", "commit (amend)", "fix it")]
    [InlineData("checkout: moving from main to topic", "checkout", "moving from main to topic")]
    [InlineData("clone", "clone", "")]
    [InlineData("", "(no action recorded)", "")]
    public void ReflogSubjects_SplitIntoAnActionAndItsDetail(string raw, string action, string subject)
        => Assert.Equal((action, subject), GitService.SplitReflogSubject(raw));

    [Fact]
    public void AReflogSelectorThatCarriesNoDate_YieldsNoDateRatherThanAFabricatedOne()
    {
        Assert.Null(GitService.ParseReflogStamp("HEAD@{3}"));
        Assert.Null(GitService.ParseReflogStamp("HEAD"));
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-07T22:37:30-04:00", System.Globalization.CultureInfo.InvariantCulture),
            GitService.ParseReflogStamp("main@{2026-08-07T22:37:30-04:00}"));
    }

    [Fact]
    public async Task TheViewer_OffersHeadAndEveryLocalBranchAndOpensOnHead()
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-refs");
        await repo.GitAsync("branch", "topic");
        var vm = new PromptingViewModel(NewDeepClean());
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.OpenReflogCommand.ExecuteAsync(null);

        Assert.True(vm.ReflogVisible);
        Assert.Equal(["HEAD", "main", "topic"], vm.ReflogRefChoices);
        Assert.Equal("HEAD", vm.SelectedReflogRef);
        Assert.NotEmpty(vm.ReflogEntries);
        Assert.False(vm.ReflogEmpty);
        Assert.False(vm.SafetyOverlayHidden);
    }

    /// <summary>
    /// After a deep clean the ref still exists and its reflog does not. The empty state has to say
    /// that an expired reflog and a ref that never moved look identical here.
    /// </summary>
    [Fact]
    public async Task AnExpiredReflog_ShowsAnEmptyStateThatDoesNotOverclaim()
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-empty");
        Assert.True((await NewDeepClean().RunAsync(repo.Path)).Success);

        var vm = new PromptingViewModel(NewDeepClean());
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenReflogCommand.ExecuteAsync(null);

        vm.SelectedReflogRef = "main";
        await vm.ReflogRefresh;

        Assert.Empty(vm.ReflogEntries);
        Assert.True(vm.ReflogEmpty);
        var markup = await File.ReadAllTextAsync(ViewSource("ReflogView.xaml"));
        Assert.Contains("an expired reflog reads the same way", markup);
    }

    // ── The one mutation ────────────────────────────────────────────────────

    [Fact]
    public async Task CheckingAnEntryOut_CreatesABranchAtItsCommitAndMovesNothingElse()
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-checkout");
        var mainBefore = (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim();

        var vm = new PromptingViewModel(NewDeepClean());
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.LoadBranchesCommand.ExecuteAsync(null);
        await vm.OpenReflogCommand.ExecuteAsync(null);

        var abandoned = vm.ReflogEntries.First(e => e.Subject == "add the secret");
        vm.SelectedReflogEntry = abandoned;
        vm.ReflogBranchName = "recovered";
        await vm.CheckOutReflogEntryCommand.ExecuteAsync(null);

        Assert.Equal(abandoned.Sha, (await repo.GitAsync("rev-parse", "refs/heads/recovered")).Trim());
        // main is exactly where it was: the only ref this can write is the new one.
        Assert.Equal(mainBefore, (await repo.GitAsync("rev-parse", "refs/heads/main")).Trim());
        Assert.Equal("recovered", (await repo.GitAsync("symbolic-ref", "--short", "HEAD")).Trim());
        Assert.Contains("Created recovered at", vm.ReflogStatusText);
        Assert.Equal("", vm.ReflogBranchName);
        _output.WriteLine(vm.ReflogStatusText);
    }

    [Fact]
    public async Task CheckingAnEntryOut_IsRefusedWithoutTheConfirmation()
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-unconfirmed");
        var vm = new PromptingViewModel(NewDeepClean());
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(false);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenReflogCommand.ExecuteAsync(null);

        vm.SelectedReflogEntry = vm.ReflogEntries[0];
        vm.ReflogBranchName = "declined";
        await vm.CheckOutReflogEntryCommand.ExecuteAsync(null);

        var refs = await repo.GitAsync("for-each-ref", "--format=%(refname)");
        Assert.DoesNotContain("refs/heads/declined", refs);
        Assert.Equal("", vm.ReflogStatusText);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("bad..name")]
    [InlineData("-leading-dash")]
    [InlineData("trailing.lock")]
    public async Task AnInvalidBranchName_IsRefusedBeforeAnythingIsWritten(string name)
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-name");
        var confirms = 0;
        var vm = new PromptingViewModel(NewDeepClean());
        vm.ConfirmPrompt = (_, _, _) => { confirms++; return Task.FromResult(true); };
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.OpenReflogCommand.ExecuteAsync(null);

        vm.SelectedReflogEntry = vm.ReflogEntries[0];
        vm.ReflogBranchName = name;
        await vm.CheckOutReflogEntryCommand.ExecuteAsync(null);

        Assert.Equal(0, confirms);
        Assert.Contains("is not a valid branch name", vm.ReflogErrorText);
        Assert.DoesNotContain(name, await repo.GitAsync("for-each-ref", "--format=%(refname)"));
    }

    [Fact]
    public async Task AnExistingBranchName_IsRefusedRatherThanReportedAsAGitFailure()
    {
        using var repo = await RepoWithAbandonedStateAsync("reflog-dupe");
        var vm = new PromptingViewModel(NewDeepClean());
        vm.ConfirmPrompt = (_, _, _) => Task.FromResult(true);
        await vm.SetProjectAsync(ProjectFor(repo));
        await vm.LoadBranchesCommand.ExecuteAsync(null);
        await vm.OpenReflogCommand.ExecuteAsync(null);

        vm.SelectedReflogEntry = vm.ReflogEntries[0];
        vm.ReflogBranchName = "main";
        await vm.CheckOutReflogEntryCommand.ExecuteAsync(null);

        Assert.Contains("already exists here", vm.ReflogErrorText);
    }

    // ── Deep clean: what it does ────────────────────────────────────────────

    /// <summary>
    /// The point of the whole action: before the clean the replaced commit is still reachable
    /// through the reflog, and afterwards it is gone from the object store entirely.
    /// </summary>
    [Fact]
    public async Task DeepClean_MakesTheReplacedCommitUnreachableAndThenAbsent()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-effect");
        var abandoned = (await new GitService().GetReflogAsync(repo.Path, "HEAD"))
            .First(e => e.Subject == "add the secret").Sha;

        // Still a real object before the clean, which is what makes a purge incomplete.
        Assert.Equal("commit", (await repo.GitAsync("cat-file", "-t", abandoned)).Trim());

        var result = await NewDeepClean().RunAsync(repo.Path);

        Assert.True(result.Success, result.RefusalReason);
        Assert.Empty(await new GitService().GetReflogAsync(repo.Path, "HEAD"));
        var probe = await Git.TryRunAsync(repo.Path, "cat-file", "-t", abandoned);
        Assert.False(probe.Success);
        // The live history is untouched.
        Assert.Equal("add the secret (rewritten)", (await repo.GitAsync("log", "-1", "--format=%s")).Trim());
        _output.WriteLine(ProjectDetailViewModel.DescribeDeepClean(result));
    }

    [Fact]
    public async Task DeepClean_ReportsTheReclaimItMeasuredRatherThanTheOneItIntended()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-measure");

        var result = await NewDeepClean().RunAsync(repo.Path);

        Assert.True(result.Success, result.RefusalReason);
        Assert.True(result.Measured);
        var text = ProjectDetailViewModel.DescribeDeepClean(result);
        Assert.Contains("Deep clean finished", text);
        Assert.Contains("objects down to", text);
        // Nothing here knows whether a bundle was ever taken, so the outcome claims only what a
        // bundle would hold if one exists — the same hedge the confirmation is written in.
        Assert.Contains("whatever a backup bundle captured", text);
        Assert.DoesNotContain("still holds them", text);
    }

    /// <summary>A store that could not be measured is reported as unmeasured, never as a reclaim of zero.</summary>
    [Fact]
    public void AnUnmeasuredStore_IsSaidToBeUnknownRatherThanZero()
    {
        var text = ProjectDetailViewModel.DescribeDeepClean(new DeepCleanResult(true, null, null, null));

        Assert.Contains("could not be measured", text);
        Assert.DoesNotContain("reclaimed 0", text);
    }

    /// <summary>Repacking can cost more than the prune saves; that is reported as growth, not dressed up.</summary>
    [Fact]
    public void AStoreThatGrew_IsSaidToHaveGrown()
    {
        var text = ProjectDetailViewModel.DescribeDeepClean(new DeepCleanResult(true, null,
            new RepoObjectCounts(0, 0, 10, 100), new RepoObjectCounts(0, 0, 10, 140)));

        Assert.Contains("grew by", text);
        Assert.Contains("repacking cost more than the prune saved", text);
    }

    // ── Deep clean: the gates ───────────────────────────────────────────────

    /// <summary>
    /// Fail-first: an interrupted operation on record blocks the clean, and clearing that record
    /// is what unblocks it. The reflog is intact throughout the refusal.
    /// </summary>
    [Fact]
    public async Task DeepClean_RefusesWhileAnInterruptedOperationIsRecordedForTheRepository()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-journal");
        var journal = new RewriteJournal();
        await journal.BeginAsync(new RewriteJournalEntry
        {
            RepoPath = repo.Path, Phase = "swap", UtcStamp = "20260807-120000000",
        });
        var service = NewDeepClean(journal: journal);
        var before = (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count;

        Assert.Equal(DeepCleanService.InterruptedOperationRefusal, await service.DescribeBlockerAsync(repo.Path));
        var refused = await service.RunAsync(repo.Path);

        Assert.False(refused.Success);
        Assert.Equal(DeepCleanService.InterruptedOperationRefusal, refused.RefusalReason);
        Assert.Equal(before, (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count);

        // With the record gone the same call runs.
        await journal.CompleteAsync(repo.Path);
        Assert.Null(await service.DescribeBlockerAsync(repo.Path));
        Assert.True((await service.RunAsync(repo.Path)).Success);
        _output.WriteLine("pending journal entry blocked the clean; clearing it unblocked it");
    }

    /// <summary>
    /// The stash stack is a reflog, and a backup bundle holds only its top entry, so expiring it
    /// would destroy states that exist nowhere else.
    /// </summary>
    [Fact]
    public async Task DeepClean_RefusesWhileAStashExistsAndSaysWhyTheStackCannotBeKept()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-stash");
        repo.Write("file.txt", "work in progress\n");
        await repo.GitAsync("stash", "push", "-m", "wip");
        var stashBefore = await repo.GitAsync("stash", "list");
        Assert.NotEqual("", stashBefore.Trim());

        var result = await NewDeepClean().RunAsync(repo.Path);

        Assert.False(result.Success);
        Assert.Contains("stash", result.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("holds only the top entry", result.RefusalReason!);
        Assert.Equal(stashBefore, await repo.GitAsync("stash", "list"));
        _output.WriteLine(result.RefusalReason!);
    }

    /// <summary>
    /// A stash read that failed is not a repository without stashes. Treating the two alike would
    /// let the one operation that erases the stash stack run precisely when nothing could confirm
    /// the stack was empty.
    /// </summary>
    [Fact]
    public async Task WhenTheStashStackCannotBeRead_TheCleanRefusesRatherThanExpiringIt()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-stash-unreadable");
        repo.Write("file.txt", "work in progress\n");
        await repo.GitAsync("stash", "push", "-m", "wip");
        var stashBefore = await repo.GitAsync("stash", "list");
        var reflogBefore = (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count;
        var service = new DeepCleanService(new StashReadFailingGit(), new RepoBusyRegistry(), new RewriteJournal());

        var result = await service.RunAsync(repo.Path);

        Assert.False(result.Success);
        Assert.Contains("stash", result.RefusalReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stashBefore, await repo.GitAsync("stash", "list"));
        Assert.Equal(reflogBefore, (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count);
        _output.WriteLine(result.RefusalReason!);
    }

    /// <summary>
    /// Every gate is a claim about the repository at the moment the destructive command runs, so
    /// each one is read while the lease is held. A gate read before the lease describes a
    /// repository that anything could have changed since.
    /// </summary>
    [Fact]
    public async Task EveryGate_IsReadUnderTheRepositoryLeaseAndNotBeforeIt()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-gate-lease");
        var busy = new RepoBusyRegistry();
        var git = new LeaseWatchingGit(busy);

        Assert.True((await new DeepCleanService(git, busy, new RewriteJournal()).RunAsync(repo.Path)).Success);

        Assert.NotEmpty(git.Gates);
        Assert.All(git.Gates, g => Assert.True(g.Held, $"the '{g.Call}' gate was read before the lease was held"));
        _output.WriteLine(string.Join(", ", git.Gates.Select(g => $"{g.Call}:{g.Held}")));
    }

    /// <summary>
    /// The confirmation dialog is open for as long as a reader takes, and a stash pushed in that
    /// window exists nowhere a bundle could hold it.
    /// </summary>
    [Fact]
    public async Task AStashCreatedWhileThePromptIsOpen_IsCaughtBeforeAnythingIsExpired()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-stash-race");
        var vm = new PromptingViewModel(NewDeepClean()) { Typed = System.IO.Path.GetFileName(repo.Path) };
        vm.WhileThePromptIsOpen = async () =>
        {
            repo.Write("file.txt", "work in progress\n");
            await repo.GitAsync("stash", "push", "-m", "wip");
        };
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Prompts);
        Assert.Contains("stash", vm.DeepCleanStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("", (await repo.GitAsync("stash", "list")).Trim());
        Assert.NotEmpty(await new GitService().GetReflogAsync(repo.Path, "HEAD"));
        _output.WriteLine(vm.DeepCleanStatusText);
    }

    /// <summary>Fails only the calls that read the stash stack; every other git call is real.</summary>
    private sealed class StashReadFailingGit : GitService
    {
        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            if (list.Any(a => a.Contains("stash", StringComparison.Ordinal)))
                return Task.FromResult(new ProcessResult(128, "", "fatal: unable to read the object store", false));
            return base.RunAsync(repoPath, list, ct, timeout);
        }
    }

    /// <summary>Records whether the repository lease was held at the moment each gate read ran.</summary>
    private sealed class LeaseWatchingGit(RepoBusyRegistry busy) : GitService
    {
        public List<(string Call, bool Held)> Gates { get; } = [];

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            if (list.Contains("status") || list.Any(a => a.Contains("stash", StringComparison.Ordinal)))
                Gates.Add((string.Join(' ', list.Take(2)), busy.IsBusy(repoPath)));
            return base.RunAsync(repoPath, list, ct, timeout);
        }
    }

    [Fact]
    public async Task DeepClean_RefusesWhileTheRepositoryIsBusy()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-busy");
        var busy = new RepoBusyRegistry();
        var service = NewDeepClean(busy);
        var before = (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count;

        using (busy.Acquire(repo.Path))
        {
            var result = await service.RunAsync(repo.Path);

            Assert.False(result.Success);
            Assert.Contains("busy", result.RefusalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count);
        }
    }

    [Fact]
    public async Task DeepClean_HoldsTheRepositoryLeaseAndReleasesIt()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-lease");
        var busy = new RepoBusyRegistry();
        var transitions = new List<bool>();
        var refusedDuringClean = false;
        busy.Changed += r =>
        {
            var held = busy.IsBusy(r);
            transitions.Add(held);
            if (held) refusedDuringClean = !busy.TryAcquire(r, out _);
        };

        Assert.True((await NewDeepClean(busy).RunAsync(repo.Path)).Success);

        Assert.Equal([true, false], transitions);
        Assert.True(refusedDuringClean);
        Assert.False(busy.IsBusy(repo.Path));
    }

    /// <summary>
    /// Fail-first: without the typed repository name nothing is expired. Typed exactly, the same
    /// command runs.
    /// </summary>
    [Fact]
    public async Task DeepClean_RefusesWithoutTheTypedRepositoryName()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-typed");
        var before = (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count;
        var vm = new PromptingViewModel(NewDeepClean()) { Typed = "yes" };
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.Prompts);
        Assert.Contains("that isn't", vm.DeepCleanStatusText);
        Assert.Equal(before, (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count);

        vm.Typed = System.IO.Path.GetFileName(repo.Path);
        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Contains("Deep clean finished", vm.DeepCleanStatusText);
        Assert.Empty(await new GitService().GetReflogAsync(repo.Path, "HEAD"));
        _output.WriteLine(vm.DeepCleanStatusText);
    }

    /// <summary>A cancelled prompt says nothing, because nothing was decided.</summary>
    [Fact]
    public async Task ACancelledPrompt_LeavesNoStatusAndNoChange()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-cancel");
        var before = (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count;
        var vm = new PromptingViewModel(NewDeepClean()) { Typed = null };
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Equal("", vm.DeepCleanStatusText);
        Assert.Equal(before, (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count);
    }

    /// <summary>The gate is re-read at the command, not trusted from the bound flag a keyboard can reach past.</summary>
    [Fact]
    public async Task DeepClean_RefusesWithTheDangerZoneOffAndNeverReachesThePrompt()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-danger");
        var before = (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count;
        var vm = new PromptingViewModel(NewDeepClean()) { DangerZone = false, Typed = System.IO.Path.GetFileName(repo.Path) };
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Prompts);
        Assert.Equal(ProjectDetailViewModel.DeepCleanDangerZoneOffNotice, vm.DeepCleanStatusText);
        Assert.Equal(before, (await new GitService().GetReflogAsync(repo.Path, "HEAD")).Count);
    }

    /// <summary>A refusal the service already knows about must not cost a typed repository name to discover.</summary>
    [Fact]
    public async Task AKnownRefusal_IsStatedBeforeTheReaderIsAskedToType()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-early");
        repo.Write("file.txt", "work in progress\n");
        await repo.GitAsync("stash", "push", "-m", "wip");

        var vm = new PromptingViewModel(NewDeepClean()) { Typed = System.IO.Path.GetFileName(repo.Path) };
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.Prompts);
        Assert.Contains("stash", vm.DeepCleanStatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The confirmation states both sides of recoverability, because that is the whole decision.</summary>
    [Fact]
    public async Task TheConfirmation_NamesWhatSurvivesInTheBackupAndWhatDoesNotSurviveAnywhere()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-message");
        var name = System.IO.Path.GetFileName(repo.Path);
        var vm = new PromptingViewModel(NewDeepClean()) { Typed = name };
        await vm.SetProjectAsync(ProjectFor(repo));

        await vm.DeepCleanCommand.ExecuteAsync(null);

        Assert.Contains("Still recoverable afterwards", vm.LastPromptMessage);
        Assert.Contains("Backups can restore it", vm.LastPromptMessage);
        Assert.Contains("Not recoverable afterwards", vm.LastPromptMessage);
        Assert.Contains("only ever lived in a reflog", vm.LastPromptMessage);
        Assert.Contains($"Type {name} to confirm.", vm.LastPromptMessage);
    }

    /// <summary>The reclaim is not aggressive: --aggressive recomputes deltas and is unrelated to dropping the objects.</summary>
    [Fact]
    public async Task TheCleanCommands_ExpireEveryReflogAndPruneWithNoGracePeriod()
    {
        using var repo = await RepoWithAbandonedStateAsync("clean-args");
        var git = new RecordingGit();
        await new DeepCleanService(git, new RepoBusyRegistry(), new RewriteJournal()).RunAsync(repo.Path);

        var expire = Assert.Single(git.Calls, c => c.Contains("reflog") && c.Contains("expire"));
        Assert.Contains(expire, a => a == "--expire=now");
        Assert.Contains(expire, a => a == "--expire-unreachable=now");
        Assert.Contains(expire, a => a == "--all");

        var gc = Assert.Single(git.Calls, c => c.Contains("gc"));
        Assert.Contains(gc, a => a == "--prune=now");
        Assert.DoesNotContain(gc, a => a == "--aggressive");
    }

    private sealed class RecordingGit : GitService
    {
        public List<List<string>> Calls { get; } = [];

        public override Task<ProcessResult> RunAsync(
            string repoPath, IEnumerable<string> args, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            var list = args.ToList();
            Calls.Add(list);
            return base.RunAsync(repoPath, list, ct, timeout);
        }
    }

    private static string ViewSource(string name, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
