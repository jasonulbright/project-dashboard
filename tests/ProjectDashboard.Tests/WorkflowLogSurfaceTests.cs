using System.Text.RegularExpressions;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The workflow run log viewer, driven through its read seam so a whole log, a capped one, and a
/// failed read are all reachable without gh. What is asserted throughout is that the pane never
/// hands a reader more than the read established: a capped log says it was cut off wherever it
/// goes, a failed read shows why rather than an empty log, and the copy and the saved file carry
/// exactly the lines that were on screen.
/// </summary>
public class WorkflowLogSurfaceTests
{
    private static ProjectInfo RemoteProject(string prefix = "gh-log")
    {
        var dir = TestEnv.NewDir(prefix);
        var project = new ProjectInfo { DirectoryName = prefix, DisplayName = prefix, FullPath = dir };
        project.GitStatus.RemoteUrl = "https://github.com/o/r.git";
        return project;
    }

    private static WorkflowRun Run(long id = 42, string name = "build") =>
        new() { Id = id, Name = name, DisplayTitle = "Add the thing", Status = "completed", Conclusion = "success" };

    /// <summary>Answers the log read and the save dialog without gh or a window.</summary>
    private class LogViewModel() : ProjectDetailViewModel(null!, new GitService(), null!)
    {
        /// <summary>Null stands for a read that failed.</summary>
        public WorkflowRunLog? Log { get; set; } = new("one\ntwo\n", Truncated: false, Cap: 2_000_000);

        /// <summary>Set instead of <see cref="Log"/> to make the read throw.</summary>
        public Exception? ReadThrows { get; set; }

        public int Reads { get; private set; }

        /// <summary>Null stands for a cancelled save dialog.</summary>
        public string? SavePath { get; set; }

        public string? SuggestedName { get; private set; }

        /// <summary>The caption the save dialog would carry — the only thing naming what is written.</summary>
        public string? SaveTitle { get; private set; }

        public string? Clipboard { get; private set; }

        internal override Task<WorkflowRunLog?> FetchWorkflowRunLogAsync(string slug, long runId)
        {
            Reads++;
            if (ReadThrows is { } ex) return Task.FromException<WorkflowRunLog?>(ex);
            return Task.FromResult(Log);
        }

        internal override Task<GitHubService.ListRead<GitHubService.IssuePage>> FetchIssuePageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.IssuePage>(
                new GitHubService.IssuePage([], false, query.Limit), ""));

        internal override Task<GitHubService.ListRead<GitHubService.PullRequestPage>> FetchPullRequestPageAsync(
            string slug, GitHubService.GitHubListQuery query)
            => Task.FromResult(new GitHubService.ListRead<GitHubService.PullRequestPage>(
                new GitHubService.PullRequestPage([], false, query.Limit), ""));

        internal override Task<List<Milestone>?> FetchMilestonesAsync(string slug)
            => Task.FromResult<List<Milestone>?>([]);

        internal override Task<string?> PromptForSavePathAsync(string suggestedName, string title)
        {
            SuggestedName = suggestedName;
            SaveTitle = title;
            return Task.FromResult(SavePath);
        }

        internal override void SetClipboardText(string text) => Clipboard = text;
    }

    private static async Task<LogViewModel> OpenedOn(WorkflowRunLog? log, WorkflowRun? run = null)
    {
        var vm = new LogViewModel { Log = log };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        vm.SelectedWorkflowRun = run ?? Run();
        await vm.OpenWorkflowLogCommand.ExecuteAsync(null);
        await vm.WorkflowLogLoad;
        return vm;
    }

    // ── Opening and reading ─────────────────────────────────────────────────────

    [Fact]
    public async Task AWholeLog_IsListedLineByLineWithNoTruncationClaim()
    {
        var vm = await OpenedOn(new WorkflowRunLog("first\nsecond\nthird\n", false, 2_000_000));

        Assert.True(vm.WorkflowLogVisible);
        Assert.Equal(["first", "second", "third"], vm.WorkflowLogLines.Select(l => l.Text));
        Assert.Equal([1, 2, 3], vm.WorkflowLogLines.Select(l => l.Number));
        Assert.Equal("", vm.WorkflowLogTruncationNotice);
        Assert.Equal("", vm.WorkflowLogError);
        Assert.False(vm.WorkflowLogEmpty);
    }

    /// <summary>A trailing newline ends the last line rather than starting one the run never wrote.</summary>
    [Theory]
    [InlineData("one\ntwo\n", 2)]
    [InlineData("one\ntwo", 2)]
    [InlineData("one\r\ntwo\r\n", 2)]
    [InlineData("", 0)]
    public void ATrailingNewline_EndsTheLastLineRatherThanStartingAnEmptyOne(string text, int lines)
        => Assert.Equal(lines, ProjectDetailViewModel.SplitLogLines(text).Count);

    /// <summary>Null is a read that failed; an empty viewer would read as a run that logged nothing.</summary>
    [Fact]
    public async Task AFailedRead_ShowsWhyRatherThanAnEmptyLog()
    {
        var vm = await OpenedOn(null);

        Assert.True(vm.WorkflowLogVisible);
        Assert.Empty(vm.WorkflowLogLines);
        Assert.False(vm.WorkflowLogEmpty);
        Assert.Equal(ProjectDetailViewModel.WorkflowLogFetchFailed, vm.WorkflowLogError);
    }

    /// <summary>A read that threw and one that answered null establish the same nothing.</summary>
    [Fact]
    public async Task AReadThatThrew_IsTheSameAnswerAsOneThatFailed()
    {
        var vm = new LogViewModel { ReadThrows = new InvalidOperationException("gh is gone") };
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        vm.SelectedWorkflowRun = Run();

        await vm.OpenWorkflowLogCommand.ExecuteAsync(null);
        await vm.WorkflowLogLoad;

        Assert.Empty(vm.WorkflowLogLines);
        Assert.Equal(ProjectDetailViewModel.WorkflowLogFetchFailed, vm.WorkflowLogError);
        Assert.False(vm.WorkflowLogLoading);
    }

    [Fact]
    public async Task AnEmptyLog_SaysSoOnlyAfterAReadThatSucceeded()
    {
        var vm = await OpenedOn(new WorkflowRunLog("", false, 2_000_000));

        Assert.True(vm.WorkflowLogEmpty);
        Assert.Equal("", vm.WorkflowLogError);
    }

    [Fact]
    public async Task WithNoRunSelected_ThePaneDoesNotOpen()
    {
        var vm = new LogViewModel();
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;

        await vm.OpenWorkflowLogCommand.ExecuteAsync(null);

        Assert.False(vm.WorkflowLogVisible);
        Assert.Equal(0, vm.Reads);
        Assert.Contains("workflow run", vm.GitHubStatusText);
    }

    /// <summary>A scrim stops the mouse and no keystroke, so two panes are never up at once.</summary>
    [Fact]
    public async Task WithAnotherPaneUp_ThePaneRefusesToOpenOverIt()
    {
        var vm = new LogViewModel();
        await vm.SetProjectAsync(RemoteProject());
        await vm.IssuesPageLoad;
        vm.SelectedWorkflowRun = Run();
        await vm.OpenReflogCommand.ExecuteAsync(null);
        Assert.False(vm.SafetyOverlayHidden);

        await vm.OpenWorkflowLogCommand.ExecuteAsync(null);

        Assert.False(vm.WorkflowLogVisible);
        Assert.Equal(0, vm.Reads);
    }

    [Fact]
    public async Task WhileThePaneIsUp_TheSurfacesBehindItAreDisabled()
    {
        var vm = await OpenedOn(new WorkflowRunLog("one\n", false, 2_000_000));

        Assert.False(vm.SafetyOverlayHidden);

        vm.CloseWorkflowLogCommand.Execute(null);

        Assert.True(vm.SafetyOverlayHidden);
        Assert.Empty(vm.WorkflowLogLines);
    }

    [Fact]
    public async Task AProjectSwitch_DropsAPaneDescribingTheRepositoryBeingLeft()
    {
        var vm = await OpenedOn(new WorkflowRunLog("one\n", false, 2_000_000));

        await vm.SetProjectAsync(RemoteProject("gh-log-next"));

        Assert.False(vm.WorkflowLogVisible);
        Assert.Empty(vm.WorkflowLogLines);
    }

    // ── Truncation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACappedLog_DisclosesTheBoundThatCutIt()
    {
        var vm = await OpenedOn(new WorkflowRunLog("first\n[log truncated at 200 bytes]\n", true, 200));

        Assert.Equal(ProjectDetailViewModel.TruncationNotice(200), vm.WorkflowLogTruncationNotice);
        Assert.Contains("200", vm.WorkflowLogTruncationNotice);
        Assert.Contains("not in a copy", vm.WorkflowLogTruncationNotice);
    }

    /// <summary>
    /// The flag, not the text: a log whose own output happens to end with the marker's wording is
    /// not a capped read, and a capped read whose marker fell outside the capture still is one.
    /// </summary>
    [Fact]
    public async Task TheTruncationClaim_ComesFromTheReadRatherThanFromTheText()
    {
        var vm = await OpenedOn(new WorkflowRunLog("[log truncated at 200 bytes]\n", false, 2_000_000));

        Assert.Equal("", vm.WorkflowLogTruncationNotice);
    }

    /// <summary>The disclosure travels with a copy that leaves the app, not only with the pane.</summary>
    [Fact]
    public async Task ACopyOfACappedLog_CarriesTheMarkerTheServiceLeftInIt()
    {
        var vm = await OpenedOn(new WorkflowRunLog("first\n[log truncated at 200 bytes]\n", true, 200));

        vm.CopyWorkflowLogCommand.Execute(null);

        Assert.Contains("[log truncated at 200 bytes]", vm.Clipboard);
    }

    // ── Search ──────────────────────────────────────────────────────────────────

    private static async Task<LogViewModel> WithLines(params string[] lines) =>
        await OpenedOn(new WorkflowRunLog(string.Join("\n", lines) + "\n", false, 2_000_000));

    [Fact]
    public async Task TypingASearch_CountsTheMatchingLinesWithoutMovingTheReader()
    {
        var vm = await WithLines("alpha", "beta", "ALPHA again");

        vm.WorkflowLogSearchText = "alpha";

        Assert.Equal("2 matching lines.", vm.WorkflowLogSearchStatus);
        Assert.Null(vm.SelectedWorkflowLogLine);
    }

    [Fact]
    public async Task FindNext_WalksTheMatchesAndWrapsAtTheEnd()
    {
        var vm = await WithLines("alpha", "beta", "alpha again");
        vm.WorkflowLogSearchText = "alpha";

        vm.FindNextInWorkflowLogCommand.Execute(null);
        Assert.Equal(1, vm.SelectedWorkflowLogLine!.Number);
        Assert.Equal("Match 1 of 2.", vm.WorkflowLogSearchStatus);

        vm.FindNextInWorkflowLogCommand.Execute(null);
        Assert.Equal(3, vm.SelectedWorkflowLogLine!.Number);
        Assert.Equal("Match 2 of 2.", vm.WorkflowLogSearchStatus);

        vm.FindNextInWorkflowLogCommand.Execute(null);
        Assert.Equal(1, vm.SelectedWorkflowLogLine!.Number);
    }

    [Fact]
    public async Task FindPrevious_WalksBackwardsAndWrapsAtTheStart()
    {
        var vm = await WithLines("alpha", "beta", "alpha again");
        vm.WorkflowLogSearchText = "alpha";

        vm.FindPreviousInWorkflowLogCommand.Execute(null);
        Assert.Equal(3, vm.SelectedWorkflowLogLine!.Number);

        vm.FindPreviousInWorkflowLogCommand.Execute(null);
        Assert.Equal(1, vm.SelectedWorkflowLogLine!.Number);

        vm.FindPreviousInWorkflowLogCommand.Execute(null);
        Assert.Equal(3, vm.SelectedWorkflowLogLine!.Number);
    }

    [Fact]
    public async Task ASearchThatMatchesNothing_SaysSoAndSelectsNothing()
    {
        var vm = await WithLines("alpha", "beta");
        vm.WorkflowLogSearchText = "gamma";

        vm.FindNextInWorkflowLogCommand.Execute(null);

        Assert.Null(vm.SelectedWorkflowLogLine);
        Assert.Equal("No lines match that text.", vm.WorkflowLogSearchStatus);
    }

    /// <summary>"Found 25,000 matches" answers a search nobody made.</summary>
    [Fact]
    public void ABlankSearch_MatchesNothingRatherThanEverything()
        => Assert.Empty(ProjectDetailViewModel.MatchingLines(
            [new WorkflowLogLine(1, "alpha"), new WorkflowLogLine(2, "beta")], "   "));

    // ── Copy and save ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_PutsExactlyTheLinesOnScreenOnTheClipboard()
    {
        var vm = await WithLines("first", "second");

        vm.CopyWorkflowLogCommand.Execute(null);

        Assert.Equal($"first{Environment.NewLine}second", vm.Clipboard);
        Assert.Equal("Copied 2 lines.", vm.WorkflowLogStatusText);
    }

    [Fact]
    public async Task Copy_WithNoLogOnScreen_SaysSoRatherThanClearingTheClipboard()
    {
        var vm = await OpenedOn(null);

        vm.CopyWorkflowLogCommand.Execute(null);

        Assert.Null(vm.Clipboard);
        Assert.Equal(ProjectDetailViewModel.WorkflowLogNothingToCopy, vm.WorkflowLogStatusText);
    }

    [Fact]
    public async Task Save_WritesTheLinesOnScreenToTheChosenPath()
    {
        var vm = await WithLines("first", "second");
        var destination = Path.Combine(TestEnv.NewDir("gh-log-save"), "run.log");
        vm.SavePath = destination;

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.True(File.Exists(destination));
        Assert.Equal($"first{Environment.NewLine}second", await File.ReadAllTextAsync(destination));
        Assert.Contains(destination, vm.WorkflowLogStatusText);
    }

    /// <summary>The staged write leaves nothing behind for a reader to mistake for the log.</summary>
    [Fact]
    public async Task Save_LeavesNoStagingFileBesideTheOneItWrote()
    {
        var vm = await WithLines("first");
        var directory = TestEnv.NewDir("gh-log-staged");
        vm.SavePath = Path.Combine(directory, "run.log");

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.Equal(["run.log"], Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_NamesTheFileAfterTheRunItRead()
    {
        var vm = await OpenedOn(new WorkflowRunLog("one\n", false, 2_000_000), Run(77, "build and test"));
        vm.SavePath = null;

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.Equal("build and test-77.log", vm.SuggestedName);
    }

    /// <summary>
    /// One save dialog serves every save on this page, and its caption is the only thing on screen
    /// naming what the reader is about to commit a filename to. A run log offered under the release
    /// asset caption describes a different artefact entirely.
    /// </summary>
    [Fact]
    public async Task Save_CaptionsTheDialogWithWhatIsBeingWritten()
    {
        var vm = await WithLines("first");
        vm.SavePath = null;

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.Equal(ProjectDetailViewModel.SaveWorkflowLogTitle, vm.SaveTitle);
        Assert.NotEqual(ProjectDetailViewModel.SaveReleaseAssetTitle, vm.SaveTitle);
    }

    /// <summary>A workflow name arrives from GitHub and may hold anything a path may not.</summary>
    [Fact]
    public void AWorkflowNameThatIsNoFileName_IsMadeIntoOne()
    {
        Assert.Equal("build-test", ProjectDetailViewModel.SafeFileStem("build/test"));
        Assert.Equal("workflow-run", ProjectDetailViewModel.SafeFileStem("   "));
    }

    [Fact]
    public async Task Save_CancelledAtTheDialog_WritesNothingAndClaimsNothing()
    {
        var vm = await WithLines("first");
        vm.SavePath = null;

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.Equal("", vm.WorkflowLogStatusText);
    }

    [Fact]
    public async Task Save_WithNoLogOnScreen_SaysSoRatherThanWritingAnEmptyFile()
    {
        var vm = await OpenedOn(null);
        var destination = Path.Combine(TestEnv.NewDir("gh-log-none"), "run.log");
        vm.SavePath = destination;

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.False(File.Exists(destination));
        Assert.Equal(ProjectDetailViewModel.WorkflowLogNothingToSave, vm.WorkflowLogStatusText);
    }

    [Fact]
    public async Task Save_ThatFailed_ReportsTheFailureRatherThanASave()
    {
        var vm = await WithLines("first");
        // A directory standing where the file goes: the write cannot replace it.
        var destination = TestEnv.NewDir("gh-log-blocked");
        vm.SavePath = destination;

        await vm.SaveWorkflowLogCommand.ExecuteAsync(null);

        Assert.StartsWith("Save failed", vm.WorkflowLogStatusText);
    }

    // ── Reachability in the shipped markup ──────────────────────────────────────

    private static string PageMarkup => RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml");

    private static string ViewMarkup => RepoSource.Read("src/ProjectDashboard/Views/Pages/WorkflowLogView.xaml");

    [Fact]
    public void TheActionsTab_OffersTheViewerOnTheSelectedRun()
    {
        var button = Regex.Match(PageMarkup, @"<ui:Button[^>]*?\{Binding OpenWorkflowLogCommand\}[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(button.Success, "no button bound to OpenWorkflowLogCommand");
        Assert.Contains("AutomationProperties.Name=", button.Value);
    }

    [Fact]
    public void ThePane_IsHostedOnTheDetailPageBehindItsOwnFlag()
    {
        Assert.Contains("<pages:WorkflowLogView", PageMarkup);
        Assert.Contains(
            "{Binding WorkflowLogVisible, Converter={StaticResource BooleanToVisibilityConverter}}", PageMarkup);
    }

    /// <summary>
    /// A run log reaches megabytes; a list that materializes a container per line lays out every
    /// one of them before the pane draws.
    /// </summary>
    [Fact]
    public void TheLineList_IsVirtualized()
    {
        var list = Regex.Match(ViewMarkup, @"<ListBox [^>]*?ItemsSource=""\{Binding WorkflowLogLines\}"".*?>",
            RegexOptions.Singleline);

        Assert.True(list.Success, "no list bound to WorkflowLogLines");
        Assert.Contains(@"VirtualizingPanel.IsVirtualizing=""True""", list.Value);
        Assert.Contains(@"VirtualizingPanel.VirtualizationMode=""Recycling""", list.Value);
    }

    [Theory]
    [InlineData("WorkflowLogTruncationNotice")]
    [InlineData("WorkflowLogSearchStatus")]
    [InlineData("WorkflowLogStatusText")]
    public void EachLineTheReaderDependsOn_IsAnnouncedWhenItChanges(string binding)
    {
        var block = Regex.Match(ViewMarkup, @"<TextBlock Text=""\{Binding " + binding + @"\}"".*?/>",
            RegexOptions.Singleline);

        Assert.True(block.Success, $"no block bound to {binding}");
        Assert.Contains(@"AutomationProperties.LiveSetting=""Polite""", block.Value);
    }

    [Theory]
    [InlineData("CopyWorkflowLogCommand")]
    [InlineData("SaveWorkflowLogCommand")]
    [InlineData("FindNextInWorkflowLogCommand")]
    [InlineData("FindPreviousInWorkflowLogCommand")]
    [InlineData("CloseWorkflowLogCommand")]
    public void EachAction_HasANamedButton(string command)
    {
        var button = Regex.Match(ViewMarkup, @"<ui:Button[^>]*?\{Binding " + command + @"\}[^>]*?/>",
            RegexOptions.Singleline);

        Assert.True(button.Success, $"no button bound to {command}");
        Assert.Contains("AutomationProperties.Name=", button.Value);
    }

    [Fact]
    public void ThePane_ClosesOnEscape()
        => Assert.Contains(
            @"<KeyBinding Key=""Escape"" Command=""{Binding CloseWorkflowLogCommand}"" />", ViewMarkup);
}
