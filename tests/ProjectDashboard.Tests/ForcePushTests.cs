using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.Services.Rewrite;
using ProjectDashboard.Services.Safety;
using ProjectDashboard.ViewModels.Pages;
using Xunit.Abstractions;

namespace ProjectDashboard.Tests;

/// <summary>
/// Publishing a rewritten history. Every fixture is a local bare "remote" reached over file://,
/// so a force-push test replaces refs on a repository this test created and on nothing else.
///
/// What is asserted is that the app never pushes on its own, that the push is always
/// force-with-lease and never a plain force, that the lease is the remote-tracking position the
/// surface showed, that a broken lease changes nothing on the remote and is never retried, and
/// that the typed repository name is what stands between a plan and a push.
/// </summary>
public class ForcePushTests
{
    private readonly ITestOutputHelper _output;

    public ForcePushTests(ITestOutputHelper output) => _output = output;

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A bare file:// origin, the clone under test, and the two shas that matter.</summary>
    private sealed class Diverged : IDisposable
    {
        public required TempRepo Seed { get; init; }
        public required TempRepo Bare { get; init; }
        public required TempRepo Local { get; init; }

        public Task<string> OriginMainAsync() => RevParseAsync(Bare, "refs/heads/main");

        public Task<string> LocalMainAsync() => RevParseAsync(Local, "refs/heads/main");

        public Task<string> TrackingAsync() => RevParseAsync(Local, "refs/remotes/origin/main");

        private static async Task<string> RevParseAsync(TempRepo repo, string reference) =>
            (await repo.GitAsync("rev-parse", reference)).Trim();

        /// <summary>
        /// A clone whose main is neither ahead of nor behind-only its remote: the tip is amended,
        /// which is exactly what a history rewrite leaves on every branch it touched.
        /// </summary>
        public static async Task<Diverged> CreateAsync(string prefix)
        {
            var seed = await TempRepo.CreateWithCommitAsync(prefix + "-seed");
            var bare = await TempRepo.CreateBareFromAsync(seed, prefix + "-origin");
            var local = await TempRepo.CloneFromAsync(bare, prefix + "-local");

            local.WriteFile("secret.txt", "a password\n");
            await local.CommitAllAsync("add the file");
            await local.GitAsync("push");

            local.WriteFile("secret.txt", "redacted\n");
            await local.GitAsync("commit", "-a", "--amend", "-m", "add the file (rewritten)");

            return new Diverged { Seed = seed, Bare = bare, Local = local };
        }

        /// <summary>A second clone pushing on top of what origin holds, standing in for someone else's work landing.</summary>
        public async Task<string> SomeoneElsePushesAsync()
        {
            using var other = await TempRepo.CloneFromAsync(Bare, "other");
            other.WriteFile("theirs.txt", "landed after the plan was built\n");
            await other.CommitAllAsync("their commit");
            await other.GitAsync("push");
            return await OriginMainAsync();
        }

        /// <summary>
        /// A clone that has fetched commits it never merged: the remote is ahead of it and it is
        /// ahead of the remote by nothing. A plain pull publishes nothing and loses nothing, which
        /// is the whole difference between this and divergence.
        /// </summary>
        public static async Task<Diverged> BehindOnlyAsync(string prefix)
        {
            var seed = await TempRepo.CreateWithCommitAsync(prefix + "-seed");
            var bare = await TempRepo.CreateBareFromAsync(seed, prefix + "-origin");
            var local = await TempRepo.CloneFromAsync(bare, prefix + "-local");

            using (var other = await TempRepo.CloneFromAsync(bare, prefix + "-other"))
            {
                other.WriteFile("theirs.txt", "their work\n");
                await other.CommitAllAsync("their commit");
                await other.GitAsync("push");
            }
            await local.GitAsync("fetch");

            return new Diverged { Seed = seed, Bare = bare, Local = local };
        }

        /// <summary>Two rewritten branches, so a plan has more than one row to choose between.</summary>
        public static async Task<Diverged> TwoDivergedAsync(string prefix)
        {
            var fixture = await CreateAsync(prefix);
            await fixture.Local.GitAsync("switch", "-c", "topic");
            fixture.Local.WriteFile("topic.txt", "topic work\n");
            await fixture.Local.CommitAllAsync("topic commit");
            await fixture.Local.GitAsync("push", "-u", "origin", "topic");
            fixture.Local.WriteFile("topic.txt", "topic work, redacted\n");
            await fixture.Local.GitAsync("commit", "-a", "--amend", "-m", "topic commit (rewritten)");
            await fixture.Local.GitAsync("switch", "main");
            return fixture;
        }

        /// <summary>
        /// A branch published under a different name, which branch.&lt;name&gt;.merge allows: the
        /// local name and the remote ref's name are then not the same string.
        /// </summary>
        public static async Task<Diverged> RenamedUpstreamAsync(string prefix)
        {
            var seed = await TempRepo.CreateWithCommitAsync(prefix + "-seed");
            var bare = await TempRepo.CreateBareFromAsync(seed, prefix + "-origin");
            var local = await TempRepo.CloneFromAsync(bare, prefix + "-local");

            await local.GitAsync("switch", "-c", "work");
            local.WriteFile("secret.txt", "a password\n");
            await local.CommitAllAsync("add the file");
            await local.GitAsync("push", "origin", "work:published");
            await local.GitAsync("config", "branch.work.remote", "origin");
            await local.GitAsync("config", "branch.work.merge", "refs/heads/published");
            await local.GitAsync("fetch", "origin");

            local.WriteFile("secret.txt", "redacted\n");
            await local.GitAsync("commit", "-a", "--amend", "-m", "add the file (rewritten)");

            return new Diverged { Seed = seed, Bare = bare, Local = local };
        }

        public Task<string> BareRefAsync(string reference) => RevParseAsync(Bare, reference);

        public Task<string> LocalRefAsync(string reference) => RevParseAsync(Local, reference);

        public void Dispose()
        {
            Local.Dispose();
            Bare.Dispose();
            Seed.Dispose();
        }
    }

    private static ForcePushService NewService(RepoBusyRegistry? busy = null, GitService? git = null) =>
        new(git ?? new GitService(), busy ?? new RepoBusyRegistry());

    private static ProjectInfo ProjectFor(TempRepo repo)
    {
        var name = System.IO.Path.GetFileName(repo.Path);
        return new ProjectInfo { DirectoryName = name, DisplayName = name, FullPath = repo.Path };
    }

    private static ProjectDetailViewModel NewVm(ForcePushService? forcePush, RepoBusyRegistry? busy = null) =>
        new(null!, new GitService(), null!, null, busy, forcePush: forcePush);

    /// <summary>Records every git argument list, so the exact push command is assertable rather than inferred.</summary>
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

    // ── The plan ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePlan_NamesTheBranchTheRemoteRefAndTheLeaseBasis()
    {
        using var fixture = await Diverged.CreateAsync("fp-plan");

        var plan = await NewService().PlanAsync(fixture.Local.Path);

        Assert.Null(plan.Refusal);
        var branch = Assert.Single(plan.Diverged);
        Assert.Equal("main", branch.BranchName);
        Assert.Equal("refs/heads/main", branch.LocalRef);
        Assert.Equal("origin", branch.Remote);
        // The name on the REMOTE side, not the local one: they can differ, and the lease is taken
        // against the remote's ref.
        Assert.Equal("refs/heads/main", branch.RemoteRef);
        Assert.Equal("refs/remotes/origin/main", branch.TrackingRef);
        Assert.Equal(await fixture.LocalMainAsync(), branch.LocalOid);
        // The lease basis IS the remote-tracking ref, which after a rewrite still holds the
        // pre-rewrite position because the swap never touches refs/remotes/*.
        Assert.Equal(await fixture.TrackingAsync(), branch.LeaseOid);
        Assert.Equal(await fixture.OriginMainAsync(), branch.LeaseOid);
        Assert.Equal(1, branch.Ahead);
        Assert.Equal(1, branch.Behind);
        _output.WriteLine($"plan: {branch.BranchName} → {branch.Remote}/{branch.BranchName}, lease {branch.LeaseOid[..8]}");
    }

    /// <summary>A branch that is only ahead needs no force, so this flow declines it and says which flow does push it.</summary>
    [Fact]
    public async Task ABranchThatIsOnlyAhead_IsNamedAsExcludedRatherThanForcePushed()
    {
        using var origin = await TempRepo.CreateWithCommitAsync("fp-ahead-seed");
        using var bare = await TempRepo.CreateBareFromAsync(origin, "fp-ahead-origin");
        using var local = await TempRepo.CloneFromAsync(bare, "fp-ahead-local");
        local.WriteFile("new.txt", "ordinary work\n");
        await local.CommitAllAsync("a plain commit");

        var plan = await NewService().PlanAsync(local.Path);

        Assert.Empty(plan.Diverged);
        Assert.Equal("main", Assert.Single(plan.AheadOnly));
        var text = ProjectDetailViewModel.DescribeExclusions(plan);
        Assert.Contains("needing no force", text);
        Assert.Contains("Tags are never published by this flow", text);
    }

    /// <summary>No remote-tracking ref means no lease to take, so the branch is named as excluded, never pushed with a bare force.</summary>
    [Fact]
    public async Task ABranchWhoseUpstreamIsGone_IsExcludedBecauseThereIsNoLeaseToTake()
    {
        using var origin = await TempRepo.CreateWithCommitAsync("fp-gone-seed");
        using var bare = await TempRepo.CreateBareFromAsync(origin, "fp-gone-origin");
        using var local = await TempRepo.CloneFromAsync(bare, "fp-gone-local");

        await local.GitAsync("switch", "-c", "topic");
        local.WriteFile("topic.txt", "topic work\n");
        await local.CommitAllAsync("topic commit");
        await local.GitAsync("push", "-u", "origin", "topic");
        await bare.GitAsync("update-ref", "-d", "refs/heads/topic");
        await local.GitAsync("fetch", "--prune");

        var plan = await NewService().PlanAsync(local.Path);

        Assert.Empty(plan.Diverged);
        Assert.Contains("topic", plan.UpstreamGone);
        Assert.Contains("No remote-tracking ref to take a lease on", ProjectDetailViewModel.DescribeExclusions(plan));
    }

    /// <summary>
    /// A clone that has merely not pulled is not diverged. Nothing here would be replaced by a
    /// force — the remote's commits would be DROPPED — so the branch is excluded by name, the pane
    /// offers nothing to confirm, and the Branches tab does not raise the force affordance.
    /// </summary>
    [Fact]
    public async Task ABranchThatIsOnlyBehind_IsExcludedByNameAndNeverReachesThePush()
    {
        using var fixture = await Diverged.BehindOnlyAsync("fp-behind");
        var originBefore = await fixture.OriginMainAsync();

        var plan = await NewService().PlanAsync(fixture.Local.Path);

        Assert.Empty(plan.Diverged);
        Assert.Empty(plan.AheadOnly);
        Assert.Equal("main", Assert.Single(plan.BehindOnly));
        Assert.Contains("Behind only — pull instead, no force needed",
            ProjectDetailViewModel.DescribeExclusions(plan));

        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));
        await vm.OpenForcePushCommand.ExecuteAsync(null);

        Assert.True(vm.ForcePushEmpty);
        Assert.False(vm.ForcePushHasRows);
        // Even typed exactly, the confirmation reaches nothing.
        vm.ForcePushConfirmInput = vm.ForcePushConfirmPhrase;
        Assert.False(vm.PushRewrittenHistoryCommand.CanExecute(null));
        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);
        Assert.Equal(originBefore, await fixture.OriginMainAsync());

        await vm.LoadBranchesCommand.ExecuteAsync(null);
        Assert.False(vm.BranchesDivergedFromRemote);
        _output.WriteLine(ProjectDetailViewModel.DescribeExclusions(plan));
    }

    [Fact]
    public async Task ARepositoryWithNoUpstreamAtAll_PlansNothingAndRefusesNothing()
    {
        using var repo = await TempRepo.CreateWithCommitAsync("fp-noremote");

        var plan = await NewService().PlanAsync(repo.Path);

        Assert.Null(plan.Refusal);
        Assert.Empty(plan.Diverged);
        Assert.Empty(plan.AheadOnly);
        Assert.Empty(plan.UpstreamGone);
    }

    // ── The push ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePush_ReplacesTheRemoteBranchWithTheRewrittenHistory()
    {
        using var fixture = await Diverged.CreateAsync("fp-push");
        var service = NewService();
        var plan = await service.PlanAsync(fixture.Local.Path);

        var outcome = await service.PushAsync(fixture.Local.Path, plan.Diverged);

        Assert.True(outcome.Success);
        Assert.Null(outcome.RefusalReason);
        var landed = Assert.Single(outcome.Refs);
        Assert.True(landed.Success);
        Assert.False(landed.LeaseRejected);
        Assert.Equal(await fixture.LocalMainAsync(), await fixture.OriginMainAsync());
        _output.WriteLine($"pushed: {landed.Detail}");
    }

    /// <summary>
    /// The command is force-WITH-LEASE, the expected value is stated rather than left implicit,
    /// the source is the object id the plan captured rather than a ref that can move, and no plain
    /// force is ever issued — including as a fallback after a rejection.
    /// </summary>
    [Fact]
    public async Task ThePushCommand_StatesItsLeaseAndNeverUsesAPlainForce()
    {
        using var fixture = await Diverged.CreateAsync("fp-args");
        var git = new RecordingGit();
        var service = NewService(git: git);
        var plan = await service.PlanAsync(fixture.Local.Path);
        var lease = plan.Diverged.Single().LeaseOid;
        var local = await fixture.LocalMainAsync();
        git.Calls.Clear();

        await service.PushAsync(fixture.Local.Path, plan.Diverged);

        var push = Assert.Single(git.Calls, c => c.Contains("push"));
        Assert.Contains(push, a => a == $"--force-with-lease=refs/heads/main:{lease}");
        Assert.Contains(push, a => a == $"{local}:refs/heads/main");
        Assert.DoesNotContain(push, a => a == "refs/heads/main:refs/heads/main");
        Assert.DoesNotContain(push, a => a is "--force" or "-f" or "--force-if-includes");
        _output.WriteLine("push argv: " + string.Join(" ", push));
        Assert.Equal(local, await fixture.OriginMainAsync());
    }

    /// <summary>
    /// The push publishes the object id the plan captured, not whatever the local ref points at by
    /// the time the reader confirms. A commit made while the pane was open must not ride along
    /// under a report that names the planned id.
    /// </summary>
    [Fact]
    public async Task ACommitMadeAfterThePlan_DoesNotRideAlongWithThePush()
    {
        using var fixture = await Diverged.CreateAsync("fp-planned-oid");
        var service = NewService();
        var plan = await service.PlanAsync(fixture.Local.Path);
        var planned = plan.Diverged.Single().LocalOid;

        fixture.Local.WriteFile("later.txt", "committed while the pane was open\n");
        await fixture.Local.CommitAllAsync("a commit the reader never saw on the plan");
        var newTip = await fixture.LocalMainAsync();
        Assert.NotEqual(planned, newTip);

        var outcome = await service.PushAsync(fixture.Local.Path, plan.Diverged);

        Assert.True(outcome.Success, outcome.Refs.FirstOrDefault()?.Detail);
        Assert.Equal(planned, await fixture.OriginMainAsync());
        Assert.NotEqual(newTip, await fixture.OriginMainAsync());
        // The report is about what landed, so it must not name an id the remote never took.
        var landed = Assert.Single(outcome.Refs);
        Assert.Contains(ForcePushService.Short(planned), landed.Detail);
        Assert.DoesNotContain(ForcePushService.Short(newTip), landed.Detail);
        _output.WriteLine($"planned {planned[..8]}, local moved to {newTip[..8]}, remote took {(await fixture.OriginMainAsync())[..8]}");
    }

    /// <summary>
    /// The remote ref can carry a different name than the branch. Every string the reader is shown
    /// — and every line the outcome reports — has to name the ref that is actually replaced.
    /// </summary>
    [Fact]
    public async Task AnUpstreamWithADifferentName_IsNamedByItsRemoteRefEverywhere()
    {
        using var fixture = await Diverged.RenamedUpstreamAsync("fp-renamed");
        var service = NewService();
        var plan = await service.PlanAsync(fixture.Local.Path);

        var branch = Assert.Single(plan.Diverged);
        Assert.Equal("work", branch.BranchName);
        Assert.Equal("refs/heads/published", branch.RemoteRef);

        var row = ProjectDetailViewModel.Describe(branch);
        Assert.Equal("work → origin/published", row.Headline);
        Assert.Contains("origin/published", row.Impact);
        Assert.Contains("origin/published", row.Lease);
        Assert.DoesNotContain("origin/work", row.Headline + row.Impact + row.Lease);

        var outcome = await service.PushAsync(fixture.Local.Path, plan.Diverged);

        var landed = Assert.Single(outcome.Refs);
        Assert.True(landed.Success, landed.Detail);
        Assert.Contains("origin/published", landed.Detail);
        Assert.DoesNotContain("origin/work", landed.Detail);
        Assert.Equal(await fixture.LocalRefAsync("refs/heads/work"),
            await fixture.BareRefAsync("refs/heads/published"));
        _output.WriteLine($"{row.Headline} | {landed.Detail}");
    }

    /// <summary>The refusal names the ref it could not replace, which on a renamed upstream is not the branch's own name.</summary>
    [Fact]
    public async Task AStaleLeaseOnARenamedUpstream_NamesTheRemoteRefItLeftAlone()
    {
        using var fixture = await Diverged.RenamedUpstreamAsync("fp-renamed-stale");
        var service = NewService();
        var plan = await service.PlanAsync(fixture.Local.Path);

        using (var other = await TempRepo.CloneFromAsync(fixture.Bare, "fp-renamed-other"))
        {
            await other.GitAsync("switch", "published");
            other.WriteFile("theirs.txt", "landed after the plan was built\n");
            await other.CommitAllAsync("their commit");
            await other.GitAsync("push", "origin", "HEAD:refs/heads/published");
        }
        var movedTo = await fixture.BareRefAsync("refs/heads/published");

        var outcome = await service.PushAsync(fixture.Local.Path, plan.Diverged);

        var refused = Assert.Single(outcome.Refs);
        Assert.True(refused.LeaseRejected);
        Assert.Contains("origin/published", refused.Detail);
        Assert.DoesNotContain("origin/work", refused.Detail);
        Assert.Equal(movedTo, await fixture.BareRefAsync("refs/heads/published"));
        _output.WriteLine(refused.Detail);
    }

    /// <summary>
    /// The lease's whole guarantee: if the remote moved after the plan was built, the push is
    /// refused, the remote is untouched, and the refusal names the ref and why.
    /// </summary>
    [Fact]
    public async Task WhenTheRemoteMovedAfterThePlan_TheLeaseRefusesAndTheRemoteIsUnchanged()
    {
        using var fixture = await Diverged.CreateAsync("fp-stale");
        var service = NewService();
        var plan = await service.PlanAsync(fixture.Local.Path);

        var movedTo = await fixture.SomeoneElsePushesAsync();
        Assert.NotEqual(plan.Diverged.Single().LeaseOid, movedTo);

        var outcome = await service.PushAsync(fixture.Local.Path, plan.Diverged);

        Assert.False(outcome.Success);
        var refused = Assert.Single(outcome.Refs);
        Assert.False(refused.Success);
        Assert.True(refused.LeaseRejected);
        Assert.Equal("main", refused.BranchName);
        Assert.Contains("no longer at", refused.Detail);
        Assert.Contains("Nothing on the remote was replaced", refused.Detail);
        // The remote is exactly where the other push left it.
        Assert.Equal(movedTo, await fixture.OriginMainAsync());
        _output.WriteLine($"lease refused: {refused.Detail}");
    }

    /// <summary>A rejected lease is a fact about the remote, not a step to retry — nothing here offers force.</summary>
    [Fact]
    public async Task ARejectedLease_IsNeverFollowedByAPlainForce()
    {
        using var fixture = await Diverged.CreateAsync("fp-noretry");
        var git = new RecordingGit();
        var service = NewService(git: git);
        var plan = await service.PlanAsync(fixture.Local.Path);
        var movedTo = await fixture.SomeoneElsePushesAsync();
        git.Calls.Clear();

        await service.PushAsync(fixture.Local.Path, plan.Diverged);

        var pushes = git.Calls.Where(c => c.Contains("push")).ToList();
        Assert.Single(pushes);
        Assert.DoesNotContain(pushes[0], a => a == "--force" || a == "-f");
        Assert.Equal(movedTo, await fixture.OriginMainAsync());
    }

    [Fact]
    public async Task PushingWhileTheRepositoryIsBusy_IsRefusedWithoutTouchingTheRemote()
    {
        using var fixture = await Diverged.CreateAsync("fp-busy");
        var busy = new RepoBusyRegistry();
        var service = NewService(busy);
        var plan = await service.PlanAsync(fixture.Local.Path);
        var before = await fixture.OriginMainAsync();

        using (busy.Acquire(fixture.Local.Path))
        {
            var outcome = await service.PushAsync(fixture.Local.Path, plan.Diverged);

            Assert.False(outcome.Success);
            Assert.Contains("busy", outcome.RefusalReason!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(outcome.Refs);
            Assert.Equal(before, await fixture.OriginMainAsync());
        }
    }

    /// <summary>The lease is held for the whole push, so a rewrite or a restore cannot land between two refs.</summary>
    [Fact]
    public async Task ThePush_HoldsTheRepositoryLeaseAndReleasesIt()
    {
        using var fixture = await Diverged.CreateAsync("fp-lease");
        var busy = new RepoBusyRegistry();
        var service = NewService(busy);
        var plan = await service.PlanAsync(fixture.Local.Path);

        var transitions = new List<bool>();
        var refusedDuringPush = false;
        busy.Changed += r =>
        {
            var held = busy.IsBusy(r);
            transitions.Add(held);
            if (held) refusedDuringPush = !busy.TryAcquire(r, out _);
        };

        await service.PushAsync(fixture.Local.Path, plan.Diverged);

        Assert.Equal([true, false], transitions);
        Assert.True(refusedDuringPush);
        Assert.False(busy.IsBusy(fixture.Local.Path));
    }

    // ── The surface ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fail-first: without the typed repository name the command refuses, and the remote is
    /// byte-identical afterwards. Typed exactly, the same click lands.
    /// </summary>
    [Fact]
    public async Task PushingWithoutTheTypedRepositoryName_IsRefusedAndTheRemoteIsUnchanged()
    {
        using var fixture = await Diverged.CreateAsync("fp-typed");
        var before = await fixture.OriginMainAsync();
        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));

        await vm.OpenForcePushCommand.ExecuteAsync(null);

        Assert.True(vm.ForcePushVisible);
        Assert.True(vm.ForcePushHasRows);
        Assert.False(vm.PushRewrittenHistoryCommand.CanExecute(null));

        vm.ForcePushConfirmInput = "yes";
        Assert.False(vm.PushRewrittenHistoryCommand.CanExecute(null));
        // The guard holds on the command itself, not only on the button's enabled state.
        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);
        Assert.Equal(before, await fixture.OriginMainAsync());

        vm.ForcePushConfirmInput = vm.ForcePushConfirmPhrase;
        Assert.True(vm.PushRewrittenHistoryCommand.CanExecute(null));
        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);

        Assert.Equal(await fixture.LocalMainAsync(), await fixture.OriginMainAsync());
        Assert.Contains("published", vm.ForcePushStatusText);
        // The confirmation is spent: a second push is its own decision.
        Assert.Equal("", vm.ForcePushConfirmInput);
        _output.WriteLine($"typed-confirm push: {vm.ForcePushStatusText}");
    }

    /// <summary>
    /// The typed name confirms the rows that are checked, not the plan as a whole. An unchecked
    /// branch is left exactly where it was on the remote, and unchecking every row leaves the
    /// command with nothing to run.
    /// </summary>
    [Fact]
    public async Task AnUncheckedRow_IsLeftOnTheRemoteWhileTheCheckedOneIsPublished()
    {
        using var fixture = await Diverged.TwoDivergedAsync("fp-optout");
        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));
        await vm.OpenForcePushCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.ForcePushRows.Count);
        Assert.All(vm.ForcePushRows, r => Assert.True(r.Include));
        var topicBefore = await fixture.BareRefAsync("refs/heads/topic");

        // Nothing checked is nothing to confirm, however exactly the name is typed.
        foreach (var row in vm.ForcePushRows) row.Include = false;
        vm.ForcePushConfirmInput = vm.ForcePushConfirmPhrase;
        Assert.False(vm.PushRewrittenHistoryCommand.CanExecute(null));
        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);
        Assert.Equal(topicBefore, await fixture.BareRefAsync("refs/heads/topic"));

        vm.ForcePushRows.Single(r => r.Branch.BranchName == "main").Include = true;
        Assert.True(vm.PushRewrittenHistoryCommand.CanExecute(null));
        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);

        Assert.Equal(await fixture.LocalMainAsync(), await fixture.OriginMainAsync());
        Assert.Equal(topicBefore, await fixture.BareRefAsync("refs/heads/topic"));
        Assert.DoesNotContain(vm.ForcePushResults, line => line.Contains("topic", StringComparison.Ordinal));
        _output.WriteLine(string.Join("\n", vm.ForcePushResults));
    }

    /// <summary>
    /// The plan's own field separator cannot be a character a ref name may contain: this surface
    /// promises that an absent branch is explained, and a branch dropped by the parse is explained
    /// nowhere.
    /// </summary>
    [Fact]
    public async Task ABranchNameContainingTheOldSeparator_IsStillPlanned()
    {
        using var fixture = await Diverged.CreateAsync("fp-pipe");
        const string name = "topic|with-a-pipe";
        var local = await fixture.LocalMainAsync();
        var tracking = await fixture.TrackingAsync();
        // Windows cannot hold this ref as a file, so it goes in where git keeps refs that are not files.
        PackRefs(fixture.Local.Path,
            (local, $"refs/heads/{name}"),
            (tracking, $"refs/remotes/origin/{name}"));
        await fixture.Local.GitAsync("config", $"branch.{name}.remote", "origin");
        await fixture.Local.GitAsync("config", $"branch.{name}.merge", $"refs/heads/{name}");

        var plan = await NewService().PlanAsync(fixture.Local.Path);

        var branch = Assert.Single(plan.Diverged, b => b.BranchName == name);
        Assert.Equal($"refs/heads/{name}", branch.RemoteRef);
        Assert.Equal(tracking, branch.LeaseOid);
        Assert.Equal(local, branch.LocalOid);
        _output.WriteLine($"planned {branch.BranchName} → {branch.Remote}/{name}");
    }

    /// <summary>Appends refs to packed-refs, which is how git stores a ref whose name is not a legal file name.</summary>
    private static void PackRefs(string repoPath, params (string Oid, string Ref)[] refs)
    {
        var file = Path.Combine(repoPath, ".git", "packed-refs");
        var lines = (File.Exists(file) ? File.ReadAllLines(file) : [])
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Concat(refs.Select(r => $"{r.Oid} {r.Ref}"))
            .OrderBy(l => l[(l.IndexOf(' ') + 1)..], StringComparer.Ordinal);
        // git's parser takes the whole line as the ref name, so a CR would land inside it.
        File.WriteAllText(file,
            string.Join('\n', new[] { "# pack-refs with: peeled fully-peeled sorted " }.Concat(lines)) + "\n");
    }

    /// <summary>Opening the pane reads refs and shows a plan; it must not push anything by doing so.</summary>
    [Fact]
    public async Task OpeningThePane_ShowsThePlanAndPushesNothing()
    {
        using var fixture = await Diverged.CreateAsync("fp-open");
        var before = await fixture.OriginMainAsync();
        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));

        await vm.OpenForcePushCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.ForcePushRows);
        Assert.Equal("main → origin/main", row.Headline);
        Assert.Contains("moves from", row.Change);
        Assert.Contains("stop being reachable", row.Impact);
        Assert.Contains("refused unless origin/main is still exactly at", row.Lease);
        Assert.False(vm.ForcePushEmpty);
        Assert.Equal(before, await fixture.OriginMainAsync());
        _output.WriteLine(row.Lease);
    }

    /// <summary>The Branches tab raises the same affordance, off divergence data that tab already loads.</summary>
    [Fact]
    public async Task TheBranchesTab_FlagsDivergenceOnlyWhenAForceWouldBeNeeded()
    {
        using var fixture = await Diverged.CreateAsync("fp-branches");
        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));

        await vm.LoadBranchesCommand.ExecuteAsync(null);
        Assert.True(vm.BranchesDivergedFromRemote);

        // Publish it, and the affordance goes away because an ordinary push would now suffice.
        await vm.OpenForcePushCommand.ExecuteAsync(null);
        vm.ForcePushConfirmInput = vm.ForcePushConfirmPhrase;
        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);

        Assert.False(vm.BranchesDivergedFromRemote);
        Assert.True(vm.ForcePushEmpty);
    }

    /// <summary>A refused lease is reported as such on the surface, and the surface says it will not be retried with force.</summary>
    [Fact]
    public async Task AFailedLease_IsReportedOnTheSurfaceWithNoRetryOffer()
    {
        using var fixture = await Diverged.CreateAsync("fp-surface-stale");
        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));
        await vm.OpenForcePushCommand.ExecuteAsync(null);

        var movedTo = await fixture.SomeoneElsePushesAsync();
        vm.ForcePushConfirmInput = vm.ForcePushConfirmPhrase;

        await vm.PushRewrittenHistoryCommand.ExecuteAsync(null);

        Assert.Contains("refused because the remote had moved", vm.ForcePushStatusText);
        Assert.Contains("will not retry them with a plain force", vm.ForcePushStatusText);
        var line = Assert.Single(vm.ForcePushResults);
        Assert.StartsWith("refused — main:", line);
        Assert.Equal(movedTo, await fixture.OriginMainAsync());

        var markup = await File.ReadAllTextAsync(ViewSource("ForcePushView.xaml"));
        Assert.DoesNotContain("force anyway", markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never retries with a plain force", markup);
    }

    /// <summary>With no service the pane refuses instead of pretending the repository has nothing to publish.</summary>
    [Fact]
    public async Task WithNoPushServiceConfigured_ThePaneRefusesRatherThanReportingNoDivergence()
    {
        using var fixture = await Diverged.CreateAsync("fp-noservice");
        var vm = NewVm(forcePush: null);
        await vm.SetProjectAsync(ProjectFor(fixture.Local));

        await vm.OpenForcePushCommand.ExecuteAsync(null);

        Assert.Contains("was not configured", vm.ForcePushErrorText);
        Assert.False(vm.ForcePushEmpty);
        Assert.Empty(vm.ForcePushRows);
    }

    /// <summary>A project switch drops the pane: the plan it holds names a repository the page no longer shows.</summary>
    [Fact]
    public async Task AProjectSwitch_ClosesThePaneAndDropsThePlan()
    {
        using var fixture = await Diverged.CreateAsync("fp-switch");
        using var other = await TempRepo.CreateWithCommitAsync("fp-switch-other");
        var vm = NewVm(NewService());
        await vm.SetProjectAsync(ProjectFor(fixture.Local));
        await vm.OpenForcePushCommand.ExecuteAsync(null);
        Assert.True(vm.ForcePushVisible);

        await vm.SetProjectAsync(ProjectFor(other));

        Assert.False(vm.ForcePushVisible);
        Assert.Empty(vm.ForcePushRows);
        Assert.Equal("", vm.ForcePushConfirmInput);
        Assert.True(vm.SafetyOverlayHidden);
    }

    [Fact]
    public void TheOverlayGate_CoversTheTwoNewPanes()
    {
        var vm = NewVm(NewService());
        Assert.True(vm.SafetyOverlayHidden);
        Assert.True(vm.MaintenanceOverlayHidden);

        vm.ForcePushVisible = true;
        Assert.False(vm.SafetyOverlayHidden);
        Assert.False(vm.MaintenanceOverlayHidden);

        vm.ForcePushVisible = false;
        vm.ReflogVisible = true;
        Assert.False(vm.SafetyOverlayHidden);
        Assert.False(vm.MaintenanceOverlayHidden);
    }

    /// <summary>
    /// A rejected lease is told apart from every other rejection by the words git prints, and git
    /// translates those words. The message locale is pinned for every call this app makes, so the
    /// same sniff holds on a machine whose git speaks something else.
    /// </summary>
    [Fact]
    public void TheGitEnvironment_PinsTheMessageLocaleTheOutputSniffsDependOn()
    {
        Assert.Equal("C", GitService.GitEnvironment["LC_ALL"]);
        Assert.Equal("C", GitService.GitEnvironment["LANGUAGE"]);
        Assert.True(ForcePushService.IsLeaseRejection(
            new ProcessResult(1, "", "! [rejected] main -> main (stale info)", false)));
        Assert.False(ForcePushService.IsLeaseRejection(
            new ProcessResult(1, "", "remote: Permission to org/repo.git denied", false)));
    }

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("[ahead 3]", 3, 0)]
    [InlineData("[behind 7]", 0, 7)]
    [InlineData("[ahead 2, behind 5]", 2, 5)]
    [InlineData("[gone]", 0, 0)]
    public void TrackParsing_ReadsTheCountsItIsGiven(string track, int ahead, int behind)
        => Assert.Equal((ahead, behind), ForcePushService.ParseTrack(track));

    private static string ViewSource(string name, [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"markup not found at {path}");
        return path;
    }
}
