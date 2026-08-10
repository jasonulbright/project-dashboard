using System.IO;
using System.Windows;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// The full log of one workflow run, in a pane of its own rather than inline on the Actions tab:
/// a run log reaches megabytes, and the list it is rendered as virtualizes only while it owns the
/// height to scroll in.
///
/// The log is a capped read, and the cap is disclosed wherever the log goes — on screen, in the
/// copy, and in the saved file. A search over a capped log and a saved copy of one are both
/// partial answers, and nothing here lets them pass as whole. A read that failed shows why and
/// leaves no lines behind it: an empty viewer would read as a run that logged nothing.
/// </summary>
public partial class ProjectDetailViewModel
{
    [ObservableProperty] private bool _workflowLogVisible;

    partial void OnWorkflowLogVisibleChanged(bool value) => OnPropertyChanged(nameof(SafetyOverlayHidden));

    /// <summary>The run the lines on screen came from; the header names it, and a save names the file after it.</summary>
    [ObservableProperty] private WorkflowRun? _workflowLogRun;

    [ObservableProperty] private ObservableCollection<WorkflowLogLine> _workflowLogLines = [];

    [ObservableProperty] private bool _workflowLogLoading;

    [ObservableProperty] private string _workflowLogError = "";

    [ObservableProperty] private string _workflowLogStatusText = "";

    /// <summary>
    /// True once a read has finished and found nothing to show. The empty state must not show
    /// before that, and a failed read never sets it.
    /// </summary>
    [ObservableProperty] private bool _workflowLogEmpty;

    /// <summary>
    /// What the cap cut short, or "" when the whole log is on screen. Rendered rather than only
    /// logged: every count, search and saved copy below it describes a prefix of the run's output.
    /// </summary>
    [ObservableProperty] private string _workflowLogTruncationNotice = "";

    [ObservableProperty] private string _workflowLogSearchText = "";

    /// <summary>What the search found, or "" when nothing has been searched for.</summary>
    [ObservableProperty] private string _workflowLogSearchStatus = "";

    [ObservableProperty] private WorkflowLogLine? _selectedWorkflowLogLine;

    /// <summary>
    /// The read the open command started and did not await. Held so a caller — and a headless
    /// test — can wait for the lines instead of polling the properties they are written to.
    /// </summary>
    internal Task WorkflowLogLoad { get; private set; } = Task.CompletedTask;

    internal const string WorkflowLogFetchFailed =
        "Couldn't read this run's log. Check that the GitHub CLI is installed and signed in.";

    internal const string WorkflowLogNothingToCopy = "There is no log on screen to copy.";

    internal const string WorkflowLogNothingToSave = "There is no log on screen to save.";

    /// <summary>
    /// The log as one string again, for the clipboard and for the file. Rebuilt from the lines on
    /// screen so what leaves the viewer is exactly what it showed, truncation marker included.
    /// </summary>
    internal string WorkflowLogText =>
        string.Join(Environment.NewLine, WorkflowLogLines.Select(l => l.Text));

    /// <summary>
    /// The run log's text, or null when the read failed. Overridable so every outcome — whole,
    /// capped, failed — is reachable without spawning gh against a real run.
    /// </summary>
    internal virtual Task<WorkflowRunLog?> FetchWorkflowRunLogAsync(string slug, long runId)
        => _gitHubService.GetWorkflowRunLogAsync(slug, runId);

    [RelayCommand]
    private Task OpenWorkflowLog()
    {
        var slug = Slug;
        var run = SelectedWorkflowRun;
        // A scrim stops the mouse and no keystroke, so the pane never opens over another one.
        if (!SafetyOverlayHidden) return Task.CompletedTask;
        if (!HasGitHubTarget(slug, run, "a workflow run")) return Task.CompletedTask;

        WorkflowLogRun = run;
        WorkflowLogLines = [];
        WorkflowLogError = "";
        WorkflowLogStatusText = "";
        WorkflowLogTruncationNotice = "";
        WorkflowLogSearchText = "";
        WorkflowLogSearchStatus = "";
        SelectedWorkflowLogLine = null;
        WorkflowLogEmpty = false;
        WorkflowLogVisible = true;
        WorkflowLogLoad = LoadWorkflowLogAsync(slug, run);
        return WorkflowLogLoad;
    }

    [RelayCommand]
    private void CloseWorkflowLog()
    {
        WorkflowLogVisible = false;
        WorkflowLogRun = null;
        WorkflowLogLines = [];
        WorkflowLogError = "";
        WorkflowLogStatusText = "";
        WorkflowLogTruncationNotice = "";
        WorkflowLogSearchText = "";
        WorkflowLogSearchStatus = "";
        SelectedWorkflowLogLine = null;
        WorkflowLogEmpty = false;
    }

    /// <summary>Drops the pane as the page leaves this repository; the log it holds is another repository's.</summary>
    private void CloseWorkflowLogOnProjectSwitch()
    {
        if (!WorkflowLogVisible) return;
        CloseWorkflowLog();
    }

    private async Task LoadWorkflowLogAsync(string slug, WorkflowRun run)
    {
        var gen = _generation;
        WorkflowLogLoading = true;
        try
        {
            WorkflowRunLog? log;
            try
            {
                log = await FetchWorkflowRunLogAsync(slug, run.Id);
            }
            catch (Exception ex)
            {
                // A read that threw and a read that answered null say the same thing to the
                // reader: nothing was established about this run's output.
                Log.Warn($"workflow run log read failed for {slug} run {run.Id}", ex);
                log = null;
            }
            if (!IsCurrent(gen) || !ReferenceEquals(WorkflowLogRun, run)) return;

            if (log is null)
            {
                WorkflowLogError = WorkflowLogFetchFailed;
                return;
            }
            WorkflowLogError = "";
            WorkflowLogLines = new ObservableCollection<WorkflowLogLine>(SplitLogLines(log.Text));
            WorkflowLogEmpty = WorkflowLogLines.Count == 0;
            WorkflowLogTruncationNotice = log.Truncated ? TruncationNotice(log.Cap) : "";
        }
        finally
        {
            if (IsCurrent(gen)) WorkflowLogLoading = false;
        }
    }

    /// <summary>
    /// Numbered lines from the captured text. A trailing newline ends the last line rather than
    /// starting an empty one, which would be a line the run never wrote.
    /// </summary>
    internal static List<WorkflowLogLine> SplitLogLines(string text)
    {
        if (text.Length == 0) return [];
        var normalized = text.ReplaceLineEndings("\n");
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return [.. normalized.Split('\n').Select((line, index) => new WorkflowLogLine(index + 1, line))];
    }

    /// <summary>
    /// What the reader is told a capped log is missing. It names the bound that applied and where
    /// the whole log still is, because nothing in this app can fetch past the cap.
    /// </summary>
    internal static string TruncationNotice(int cap) =>
        $"This log was cut off at {cap:N0} characters. What follows the cut is not on screen, " +
        "not in a copy, and not in a saved file — open the run on GitHub for the whole log.";

    // ── Search ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lines holding <paramref name="term"/>, case-insensitively. A blank term matches nothing
    /// rather than everything: "found 25,000 matches" answers a search nobody made.
    /// </summary>
    internal static List<int> MatchingLines(IReadOnlyList<WorkflowLogLine> lines, string term)
    {
        var needle = term.Trim();
        if (needle.Length == 0) return [];
        return [.. lines.Index()
            .Where(pair => pair.Item.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Index)];
    }

    internal static string SearchStatusText(int matches, int position, string term)
    {
        if (term.Trim().Length == 0) return "";
        if (matches == 0) return "No lines match that text.";
        return $"Match {position + 1} of {matches}.";
    }

    [RelayCommand]
    private void FindNextInWorkflowLog() => MoveThroughMatches(forward: true);

    [RelayCommand]
    private void FindPreviousInWorkflowLog() => MoveThroughMatches(forward: false);

    /// <summary>
    /// Moves the selection to the next or previous matching line, wrapping at either end. The
    /// selection is what scrolls the list, so a match found off screen is brought into view.
    /// </summary>
    private void MoveThroughMatches(bool forward)
    {
        var matches = MatchingLines(WorkflowLogLines, WorkflowLogSearchText);
        if (matches.Count == 0)
        {
            SelectedWorkflowLogLine = null;
            WorkflowLogSearchStatus = SearchStatusText(0, 0, WorkflowLogSearchText);
            return;
        }

        var current = SelectedWorkflowLogLine is { } selected ? WorkflowLogLines.IndexOf(selected) : -1;
        var position = forward
            ? matches.FindIndex(i => i > current)
            : matches.FindLastIndex(i => i < current || current < 0);
        if (position < 0) position = forward ? 0 : matches.Count - 1;

        SelectedWorkflowLogLine = WorkflowLogLines[matches[position]];
        WorkflowLogSearchStatus = SearchStatusText(matches.Count, position, WorkflowLogSearchText);
    }

    partial void OnWorkflowLogSearchTextChanged(string value)
    {
        // The count follows the text; the jump waits for the reader to ask for one, so a long log
        // is not scrolled out from under them on every keystroke.
        var matches = MatchingLines(WorkflowLogLines, value);
        WorkflowLogSearchStatus = matches.Count == 0
            ? SearchStatusText(0, 0, value)
            : $"{matches.Count} matching {(matches.Count == 1 ? "line" : "lines")}.";
    }

    // ── Copy and save ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void CopyWorkflowLog()
    {
        if (WorkflowLogLines.Count == 0)
        {
            WorkflowLogStatusText = WorkflowLogNothingToCopy;
            return;
        }
        try
        {
            SetClipboardText(WorkflowLogText);
            WorkflowLogStatusText = $"Copied {WorkflowLogLines.Count:N0} lines.";
        }
        catch (Exception ex)
        {
            // Another process holding the clipboard makes SetText throw; that is a failed copy,
            // not a crash.
            Log.Warn("workflow log clipboard copy failed", ex);
            WorkflowLogStatusText = $"Copy failed — {ex.Message}";
        }
    }

    /// <summary>Overridable so a headless test observes the copy without a clipboard.</summary>
    internal virtual void SetClipboardText(string text) => Clipboard.SetText(text);

    internal const string SaveWorkflowLogTitle = "Save workflow log";

    /// <summary>The file name a save opens with: the run and its id, so two saves never collide.</summary>
    internal string WorkflowLogFileName =>
        WorkflowLogRun is { } run ? $"{SafeFileStem(run.Name)}-{run.Id}.log" : "workflow-run.log";

    /// <summary>A workflow name reaches this from GitHub and may hold anything a path may not.</summary>
    internal static string SafeFileStem(string name)
    {
        var stem = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]).Trim();
        return stem.Length == 0 ? "workflow-run" : stem;
    }

    [RelayCommand]
    private async Task SaveWorkflowLog()
    {
        if (WorkflowLogLines.Count == 0)
        {
            WorkflowLogStatusText = WorkflowLogNothingToSave;
            return;
        }
        var gen = _generation;
        var text = WorkflowLogText;
        var lines = WorkflowLogLines.Count;
        var destination = await PromptForSavePathAsync(WorkflowLogFileName, SaveWorkflowLogTitle);
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (!IsCurrent(gen))
        {
            WorkflowLogStatusText = ProjectSwitchedNotice("Save log");
            return;
        }
        try
        {
            await AtomicFile.WriteAllTextAsync(destination, text);
            if (IsCurrent(gen))
                WorkflowLogStatusText = $"Saved {lines:N0} lines to {destination}.";
        }
        catch (Exception ex)
        {
            Log.Warn($"workflow log save failed for {destination}", ex);
            if (IsCurrent(gen)) WorkflowLogStatusText = $"Save failed — {ex.Message}";
        }
    }
}
