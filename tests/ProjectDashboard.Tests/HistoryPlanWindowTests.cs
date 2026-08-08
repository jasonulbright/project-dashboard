using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Windows;

namespace ProjectDashboard.Tests;

/// <summary>
/// The planning dialog's interactive state: what its keyboard commands do to the plan, and
/// that the preview and the apply gate always agree with what a resolution would produce.
/// The window itself is not constructed here — a FluentWindow subscribes to the app-wide theme
/// manager for its lifetime, so one built on a test thread outlives that thread and makes a
/// later theme change throw from whichever test triggers it.
/// </summary>
public class HistoryPlanWindowTests
{
    private static List<PlannedCommit> Range(params string[] names) =>
        names.Select(n => new PlannedCommit { Sha = n.PadRight(40, '0'), Subject = n }).ToList();

    private static HistoryPlanViewModel WithMessage(HistoryPlanViewModel viewModel, string? message)
    {
        viewModel.PromptForCommitMessageAsync = (_, _, _) => Task.FromResult(message);
        return viewModel;
    }

    [Fact]
    public void ANewPlan_StartsOnTheNewestCommitWithNothingToApply()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c"));

        Assert.Equal(2, viewModel.SelectedIndex);
        Assert.False(viewModel.CanApply);
        Assert.Contains("Nothing to apply", viewModel.StatusText);
        Assert.Equal(3, viewModel.Preview.Count);
    }

    [Fact]
    public void MoveCommands_CarryTheSelectionWithTheMovedCommit()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 2 };

        viewModel.MoveUpCommand.Execute(null);

        Assert.Equal(1, viewModel.SelectedIndex);
        Assert.Equal("c", viewModel.Commits[1].Subject);
        Assert.True(viewModel.CanApply);
        Assert.Equal("Ready: order changed — 3 commit(s) after the replay.", viewModel.StatusText);

        viewModel.MoveDownCommand.Execute(null);

        Assert.Equal(2, viewModel.SelectedIndex);
        Assert.False(viewModel.CanApply);
    }

    [Fact]
    public void ToggleDrop_UpdatesThePreviewAndTheApplyGate()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 1 };

        viewModel.ToggleDropCommand.Execute(null);

        Assert.True(viewModel.CanApply);
        Assert.Equal("Ready: 1 commit(s) dropped — 2 commit(s) after the replay.", viewModel.StatusText);
        Assert.Equal(2, viewModel.Preview.Count);
        Assert.DoesNotContain(viewModel.Preview, line => line.EndsWith("  b"));

        viewModel.ToggleDropCommand.Execute(null);

        Assert.False(viewModel.CanApply);
        Assert.Equal(3, viewModel.Preview.Count);
    }

    [Fact]
    public void ToggleSquash_FoldsIntoThePrecedingLine()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 2 };

        viewModel.ToggleSquashCommand.Execute(null);

        Assert.Equal("Ready: 1 commit(s) squashed — 2 commit(s) after the replay.", viewModel.StatusText);
        Assert.Equal(2, viewModel.Preview.Count);
        Assert.EndsWith("b + c", viewModel.Preview[1]);
    }

    [Fact]
    public async Task Reword_PutsTheEnteredMessageOnTheSelectedCommit()
    {
        var viewModel = WithMessage(new HistoryPlanViewModel(Range("a", "b")) { SelectedIndex = 1 }, "b, reworded");

        await viewModel.RewordCommand.ExecuteAsync(null);

        Assert.Equal("b, reworded", viewModel.Commits[1].NewMessage);
        Assert.Equal("reword", viewModel.Commits[1].MarkLabel);
        Assert.EndsWith("  b, reworded", viewModel.Preview[1]);
        Assert.Equal("Ready: 1 commit(s) reworded — 2 commit(s) after the replay.", viewModel.StatusText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ARewordCancelledOrLeftBlank_ChangesNothing(string? entered)
    {
        var viewModel = WithMessage(new HistoryPlanViewModel(Range("a", "b")) { SelectedIndex = 1 }, entered);

        await viewModel.RewordCommand.ExecuteAsync(null);

        Assert.Null(viewModel.Commits[1].NewMessage);
        Assert.False(viewModel.CanApply);
        Assert.Contains("Nothing to apply", viewModel.StatusText);
    }

    [Fact]
    public async Task MarkingARewordedCommitForASquash_DropsTheMessageTheFoldWouldDiscard()
    {
        var viewModel = WithMessage(new HistoryPlanViewModel(Range("a", "b")) { SelectedIndex = 1 }, "b, reworded");
        await viewModel.RewordCommand.ExecuteAsync(null);

        viewModel.ToggleSquashCommand.Execute(null);

        Assert.Null(viewModel.Commits[1].NewMessage);
        Assert.Equal("squash", viewModel.Commits[1].MarkLabel);
        Assert.Equal("Ready: 1 commit(s) squashed — 1 commit(s) after the replay.", viewModel.StatusText);
    }

    [Fact]
    public async Task MixingKinds_ResolvesToOneApplyThatStatesEveryKind()
    {
        var viewModel = WithMessage(new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 2 }, "c, reworded");

        viewModel.MoveUpCommand.Execute(null);   // c ahead of b
        await viewModel.RewordCommand.ExecuteAsync(null);
        viewModel.SelectedIndex = 0;
        viewModel.ToggleDropCommand.Execute(null);

        Assert.True(viewModel.CanApply);
        Assert.Equal(
            "Ready: 1 commit(s) dropped, 1 commit(s) reworded, order changed — 2 commit(s) after the replay.",
            viewModel.StatusText);
        Assert.Equal(2, viewModel.Preview.Count);
    }

    [Fact]
    public void APlanNoReplayCanExpress_ShowsTheRefusalAndNoPreviewAtAll()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 1 };

        viewModel.ToggleDropCommand.Execute(null);
        viewModel.SelectedIndex = 2;
        viewModel.ToggleSquashCommand.Execute(null);

        Assert.False(viewModel.CanApply);
        Assert.Contains("a dropped commit cannot be a squash anchor", viewModel.StatusText);
        // A preview of a plan that cannot run would be a history nothing produces.
        Assert.Empty(viewModel.Preview);
    }

    [Fact]
    public async Task ResetPlan_RestoresTheOriginalOrderAndClearsEveryMark()
    {
        var viewModel = WithMessage(new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 2 }, "c, reworded");
        viewModel.MoveUpCommand.Execute(null);
        await viewModel.RewordCommand.ExecuteAsync(null);
        viewModel.SelectedIndex = 0;
        viewModel.ToggleDropCommand.Execute(null);

        viewModel.ResetPlanCommand.Execute(null);

        Assert.Equal(["a", "b", "c"], viewModel.Commits.Select(c => c.Subject));
        Assert.DoesNotContain(viewModel.Commits, c => c.Drop || c.SquashIntoPrevious || c.NewMessage is not null);
        Assert.False(viewModel.CanApply);
        Assert.Contains("Nothing to apply", viewModel.StatusText);
    }

    [Fact]
    public void MoveAtTheEdge_LeavesTheSelectionWhereItWas()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b")) { SelectedIndex = 0 };

        viewModel.MoveUpCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedIndex);
        Assert.Equal(["a", "b"], viewModel.Commits.Select(c => c.Subject));
    }

    [Fact]
    public void MarkingACommitDirectly_IsPickedUpByThePreview()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b"));

        viewModel.Commits[1].Drop = true;

        Assert.Equal("Ready: 1 commit(s) dropped — 1 commit(s) after the replay.", viewModel.StatusText);
        Assert.Single(viewModel.Preview);
    }
}
