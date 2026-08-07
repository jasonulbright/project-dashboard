using ProjectDashboard.Models;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The card verbs are one click from a surprise merge or a push onto a diverged
/// branch, so each refusal is pinned here rather than left to the button's enablement.
/// </summary>
public class DashboardCardActionTests
{
    private static ProjectInfo Healthy() => new()
    {
        DirectoryName = "alpha",
        DisplayName = "alpha",
        FullPath = @"C:\projects\alpha",
        GitStatus = new GitStatus { RemoteUrl = "https://github.com/o/alpha.git", Branch = "main" },
    };

    private static readonly CardAction[] EveryAction = [CardAction.Fetch, CardAction.Pull, CardAction.Push];

    [Fact]
    public void HealthyRepo_IsAllowed_ForEveryAction()
    {
        foreach (var action in EveryAction)
            Assert.Null(DashboardCardActions.RefuseReason(Healthy(), action, false, false, hasUpstream: true));
    }

    [Fact]
    public void NullProject_IsRefused()
    {
        Assert.Equal(DashboardCardActions.NotClonedReason,
            DashboardCardActions.RefuseReason(null, CardAction.Fetch, false, false));
    }

    [Fact]
    public void BulkOpRunning_RefusesEveryAction()
    {
        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.BulkReason,
                DashboardCardActions.RefuseReason(Healthy(), action, bulkOpRunning: true, repoBusy: false));
    }

    [Fact]
    public void BusyRepo_RefusesEveryAction()
    {
        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.BusyReason,
                DashboardCardActions.RefuseReason(Healthy(), action, false, repoBusy: true));
    }

    [Fact]
    public void RemoteOnlyCard_RefusesEveryAction()
    {
        var cloud = Healthy();
        cloud.IsRemoteOnly = true;

        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.NotClonedReason,
                DashboardCardActions.RefuseReason(cloud, action, false, false));
    }

    [Fact]
    public void EmptyPath_RefusesEveryAction()
    {
        var pathless = Healthy();
        pathless.FullPath = "";

        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.NotClonedReason,
                DashboardCardActions.RefuseReason(pathless, action, false, false));
    }

    [Fact]
    public void UnreadableStatus_RefusesEveryAction()
    {
        var broken = Healthy();
        broken.GitStatus.HasError = true;

        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.StatusUnavailableReason,
                DashboardCardActions.RefuseReason(broken, action, false, false));
    }

    [Fact]
    public void RepoNeedingAttention_RefusesEveryAction_AndNamesTheState()
    {
        var conflicted = Healthy();
        conflicted.GitStatus.HasConflicts = true;
        conflicted.GitStatus.ActivityLabel = "rebase";

        foreach (var action in EveryAction)
        {
            var reason = DashboardCardActions.RefuseReason(conflicted, action, false, false);
            Assert.NotNull(reason);
            Assert.Contains("rebase", reason);
            Assert.Contains("conflicts", reason);
        }
    }

    [Fact]
    public void DetachedHead_RefusesEveryAction()
    {
        var detached = Healthy();
        detached.GitStatus.IsDetached = true;

        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.DetachedReason,
                DashboardCardActions.RefuseReason(detached, action, false, false));
    }

    [Fact]
    public void NoRemote_RefusesEveryAction()
    {
        var local = Healthy();
        local.GitStatus.RemoteUrl = "";

        foreach (var action in EveryAction)
            Assert.Equal(DashboardCardActions.NoRemoteReason,
                DashboardCardActions.RefuseReason(local, action, false, false));
    }

    [Fact]
    public void NoUpstream_RefusesPullAndPush_ButNotFetch()
    {
        var project = Healthy();

        Assert.Null(DashboardCardActions.RefuseReason(project, CardAction.Fetch, false, false, hasUpstream: false));
        Assert.Equal(DashboardCardActions.NoUpstreamReason,
            DashboardCardActions.RefuseReason(project, CardAction.Pull, false, false, hasUpstream: false));
        Assert.Equal(DashboardCardActions.NoUpstreamReason,
            DashboardCardActions.RefuseReason(project, CardAction.Push, false, false, hasUpstream: false));
    }

    [Fact]
    public void UncommittedChanges_RefusePullAndPush_ButNotFetch()
    {
        var dirty = Healthy();
        dirty.GitStatus.IsDirty = true;
        dirty.GitStatus.ModifiedCount = 3;

        Assert.Null(DashboardCardActions.RefuseReason(dirty, CardAction.Fetch, false, false));
        Assert.Equal(DashboardCardActions.DirtyReason,
            DashboardCardActions.RefuseReason(dirty, CardAction.Pull, false, false));
        Assert.Equal(DashboardCardActions.DirtyReason,
            DashboardCardActions.RefuseReason(dirty, CardAction.Push, false, false));
    }

    [Fact]
    public void DivergedBranch_RefusesPullAndPush_ButNotFetch()
    {
        var diverged = Healthy();
        diverged.GitStatus.AheadBy = 2;
        diverged.GitStatus.BehindBy = 3;

        Assert.Null(DashboardCardActions.RefuseReason(diverged, CardAction.Fetch, false, false));
        foreach (var action in new[] { CardAction.Pull, CardAction.Push })
        {
            var reason = DashboardCardActions.RefuseReason(diverged, action, false, false);
            Assert.NotNull(reason);
            Assert.Contains("diverged", reason);
        }
    }

    [Fact]
    public void AheadOnly_AllowsPush()
    {
        var ahead = Healthy();
        ahead.GitStatus.AheadBy = 4;

        Assert.Null(DashboardCardActions.RefuseReason(ahead, CardAction.Push, false, false, hasUpstream: true));
    }

    [Fact]
    public void BehindOnly_AllowsPull()
    {
        var behind = Healthy();
        behind.GitStatus.BehindBy = 4;

        Assert.Null(DashboardCardActions.RefuseReason(behind, CardAction.Pull, false, false, hasUpstream: true));
    }

    [Fact]
    public void BulkRefusal_OutranksEveryOtherFault()
    {
        var wrecked = Healthy();
        wrecked.GitStatus.HasError = true;
        wrecked.GitStatus.IsDetached = true;

        Assert.Equal(DashboardCardActions.BulkReason,
            DashboardCardActions.RefuseReason(wrecked, CardAction.Push, bulkOpRunning: true, repoBusy: true));
    }

    [Theory]
    [InlineData(CardAction.Fetch, "Fetch")]
    [InlineData(CardAction.Pull, "Pull")]
    [InlineData(CardAction.Push, "Push")]
    public void VerbNamesMatchTheAction(CardAction action, string expected)
        => Assert.Equal(expected, DashboardCardActions.Verb(action));
}
