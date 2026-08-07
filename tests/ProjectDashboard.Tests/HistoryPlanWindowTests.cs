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
        Assert.Contains("replay 3 commit(s) in the new order", viewModel.StatusText);

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
        Assert.Equal("Ready: drop 1 commit(s).", viewModel.StatusText);
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

        Assert.Equal("Ready: fold 2 commit(s) into one.", viewModel.StatusText);
        Assert.Equal(2, viewModel.Preview.Count);
        Assert.EndsWith("b + c", viewModel.Preview[1]);
    }

    [Fact]
    public void MixingKinds_ShowsTheRefusalAndBlocksApply()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 2 };

        viewModel.MoveUpCommand.Execute(null);
        viewModel.SelectedIndex = 0;
        viewModel.ToggleDropCommand.Execute(null);

        Assert.False(viewModel.CanApply);
        Assert.Contains("mixes a reorder and a drop", viewModel.StatusText);
    }

    [Fact]
    public void ResetPlan_RestoresTheOriginalOrderAndClearsEveryMark()
    {
        var viewModel = new HistoryPlanViewModel(Range("a", "b", "c")) { SelectedIndex = 2 };
        viewModel.MoveUpCommand.Execute(null);
        viewModel.SelectedIndex = 0;
        viewModel.ToggleDropCommand.Execute(null);

        viewModel.ResetPlanCommand.Execute(null);

        Assert.Equal(["a", "b", "c"], viewModel.Commits.Select(c => c.Subject));
        Assert.DoesNotContain(viewModel.Commits, c => c.Drop || c.SquashIntoPrevious);
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

        Assert.Equal("Ready: drop 1 commit(s).", viewModel.StatusText);
        Assert.Single(viewModel.Preview);
    }
}
