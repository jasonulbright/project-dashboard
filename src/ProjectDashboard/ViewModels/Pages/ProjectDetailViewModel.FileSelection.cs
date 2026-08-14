using System.Text;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Multi-select in the Changes tab, and the undo offers a completed operation leaves behind.
///
/// A command reads the whole selection; the diff pane follows the focused file alone. Both are
/// written together by the list that owns them, and both survive the refresh every mutating
/// operation triggers — matched back by path, since each read of the working state builds new
/// <see cref="WorkingFile"/> instances.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>Every file selected on that side. The focused one is <see cref="SelectedUnstagedFile"/>.</summary>
    [ObservableProperty] private IReadOnlyList<WorkingFile> _selectedUnstagedFiles = [];
    [ObservableProperty] private IReadOnlyList<WorkingFile> _selectedStagedFiles = [];

    /// <summary>
    /// Applies a list's selection. The focused item is the one the reader just added, so the
    /// diff pane follows the click rather than the first row of the selection.
    /// </summary>
    internal void SetUnstagedSelection(IReadOnlyList<WorkingFile> selection, WorkingFile? focused)
    {
        SelectedUnstagedFiles = selection;
        SelectedUnstagedFile = FocusFor(selection, focused, SelectedUnstagedFile);
    }

    internal void SetStagedSelection(IReadOnlyList<WorkingFile> selection, WorkingFile? focused)
    {
        SelectedStagedFiles = selection;
        SelectedStagedFile = FocusFor(selection, focused, SelectedStagedFile);
    }

    /// <summary>
    /// Which file the diff pane follows: the one just added, else the one it already showed
    /// while that stays selected, else the last of the selection.
    /// </summary>
    private static WorkingFile? FocusFor(IReadOnlyList<WorkingFile> selection, WorkingFile? focused,
        WorkingFile? current)
    {
        if (selection.Count == 0) return null;
        if (focused is not null) return focused;
        return current is not null && selection.Contains(current) ? current : selection[^1];
    }

    internal IReadOnlyList<WorkingFile> UnstagedSelection() =>
        Selection(SelectedUnstagedFiles, SelectedUnstagedFile);

    internal IReadOnlyList<WorkingFile> StagedSelection() =>
        Selection(SelectedStagedFiles, SelectedStagedFile);

    private static IReadOnlyList<WorkingFile> Selection(IReadOnlyList<WorkingFile> selection,
        WorkingFile? focused) =>
        selection.Count > 0 ? selection : focused is null ? [] : [focused];

    [RelayCommand]
    private Task StageSelected() => StageFilesAsync(UnstagedSelection());

    [RelayCommand]
    private Task UnstageSelected() => UnstageFilesAsync(StagedSelection());

    [RelayCommand]
    private Task DiscardSelected() => DiscardFilesAsync(UnstagedSelection());

    // ── Ignoring a working file ─────────────────────────────────────────────

    /// <summary>
    /// Adds a rule for the focused unstaged file to the repository's own .gitignore. The global
    /// excludes file is written only by the two "everywhere" commands below, behind a confirmation
    /// — it lives outside every repository, where an accidental append leaves no trace in
    /// `git status` or `git diff` to notice it by.
    /// </summary>
    [RelayCommand]
    private Task IgnoreSelectedFile()
    {
        var file = SelectedUnstagedFile;
        return file is null
            ? NoFileToIgnore()
            : IgnoreAsync(file, GitService.IgnoreLineForPath(file.Path));
    }

    /// <summary>
    /// Adds a rule for the focused file's NAME to the global excludes file. The name rather than
    /// the path: the global file has no repository root to anchor a path to, and matching every
    /// namesake in every repository is what "everywhere" offers.
    /// </summary>
    [RelayCommand]
    private Task IgnoreSelectedFileEverywhere()
    {
        var file = SelectedUnstagedFile;
        if (file is null) return NoFileToIgnore();
        var name = System.IO.Path.GetFileName(file.Path.Replace('\\', '/').TrimEnd('/'));
        return IgnoreGloballyAsync(file, GitService.IgnoreLineForName(name),
            $"every file named '{name}'");
    }

    [RelayCommand]
    private Task IgnoreSelectedExtensionEverywhere()
    {
        var file = SelectedUnstagedFile;
        if (file is null) return NoFileToIgnore();

        var extension = System.IO.Path.GetExtension(file.Path).TrimStart('.');
        if (extension.Length == 0)
        {
            SyncStatusText = $"{file.Path} has no extension, so there is no rule of that kind to add.";
            return Task.CompletedTask;
        }
        return IgnoreGloballyAsync(file, GitService.IgnoreLineForExtension(extension),
            $"every .{extension} file");
    }

    /// <summary>
    /// Adds a rule for every file sharing the focused file's extension. Refused for a file that
    /// has none: there is no pattern to derive, and a bare name would ignore that one file under
    /// a menu entry promising a kind.
    /// </summary>
    [RelayCommand]
    private Task IgnoreSelectedExtension()
    {
        var file = SelectedUnstagedFile;
        if (file is null) return NoFileToIgnore();

        var extension = System.IO.Path.GetExtension(file.Path).TrimStart('.');
        if (extension.Length == 0)
        {
            SyncStatusText = $"{file.Path} has no extension, so there is no rule of that kind to add.";
            return Task.CompletedTask;
        }
        return IgnoreAsync(file, GitService.IgnoreLineForExtension(extension));
    }

    private Task NoFileToIgnore()
    {
        SyncStatusText = "Select a file to ignore first.";
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks git what it already makes of the path before writing anything, because two of the
    /// three answers make the write pointless in a way the reader would otherwise have to infer
    /// from a file that did not move: a tracked path stays tracked and stays listed whatever
    /// .gitignore says, and a path an existing rule already covers needs no second rule.
    /// </summary>
    private async Task IgnoreAsync(WorkingFile file, string pattern)
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Ignore"); return; }
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;

        var answer = await _gitService.CheckIgnoreAsync(repo, file.Path);
        if (!IsCurrent(gen)) return;
        switch (answer.State)
        {
            case IgnoreState.Unknown:
                SyncStatusText =
                    $"Could not tell whether {file.Path} is already ignored, so nothing was written: {answer.Error}";
                return;
            case IgnoreState.Ignored:
                SyncStatusText = $"{file.Path} is already ignored by an existing rule — nothing was written.";
                return;
            case IgnoreState.NotIgnored when answer.Tracked:
                SyncStatusText =
                    $"{file.Path} is tracked, so an ignore rule changes nothing for it: git keeps tracking a file " +
                    "already in the index, and it stays in this list until it is untracked.";
                return;
        }

        var wrote = false;
        var ok = await RunOp(async r =>
        {
            wrote = await _gitService.AppendIgnoreEntryAsync(r, pattern);
            return new ProcessResult(0, "", "", TimedOut: false);
        }, $"Ignore {pattern}", repo, gen);
        if (!ok || !IsCurrent(gen)) return;

        SyncStatusText = wrote
            ? $"Added {pattern} to .gitignore. It is an ordinary edit — commit it like any other file."
            : $"{pattern} is already in .gitignore — nothing was written.";
    }

    /// <summary>
    /// The global variant runs the same pre-checks, then confirms before writing: the excludes
    /// file lives outside every repository, so the append shows up in no `git status` or
    /// `git diff` anywhere, and the rule reaches repositories this app has never opened. The
    /// confirmation names the exact file and line so what to revert, and where, is on screen
    /// before anything is written.
    /// </summary>
    private async Task IgnoreGloballyAsync(WorkingFile file, string pattern, string reach)
    {
        if (IsBusy) { SyncStatusText = BusyNotice("Ignore everywhere"); return; }
        var repo = RepoPath;
        var gen = _generation;
        if (repo.Length == 0) return;

        var answer = await _gitService.CheckIgnoreAsync(repo, file.Path);
        if (!IsCurrent(gen)) return;
        switch (answer.State)
        {
            case IgnoreState.Unknown:
                SyncStatusText =
                    $"Could not tell whether {file.Path} is already ignored, so nothing was written: {answer.Error}";
                return;
            case IgnoreState.Ignored:
                SyncStatusText = $"{file.Path} is already ignored by an existing rule — nothing was written.";
                return;
            case IgnoreState.NotIgnored when answer.Tracked:
                SyncStatusText =
                    $"{file.Path} is tracked, so an ignore rule changes nothing for it: git keeps tracking a file " +
                    "already in the index, and it stays in this list until it is untracked.";
                return;
        }

        var excludesPath = await _gitService.GetGlobalExcludesPathAsync(repo);
        if (!IsCurrent(gen)) return;
        if (excludesPath is null)
        {
            SyncStatusText =
                "Where global ignore rules live could not be read from git config, so nothing was written.";
            return;
        }

        var confirmed = await ConfirmAsync("Ignore everywhere?",
            $"Add this line to the global ignore file?\n\n    {pattern}\n    in {excludesPath}\n\n" +
            $"It ignores {reach} in every repository on this machine, not only this one. The file is " +
            "outside every repository, so the edit appears in no repository's status or diff — reverting " +
            "it later means removing that line from that file by hand.", "Add the rule");
        if (!confirmed || !IsCurrent(gen)) return;

        var wrote = false;
        var ok = await RunOp(async _ =>
        {
            wrote = await _gitService.AppendGlobalIgnoreEntryAsync(excludesPath, pattern);
            return new ProcessResult(0, "", "", TimedOut: false);
        }, $"Ignore {pattern} everywhere", repo, gen);
        if (!ok || !IsCurrent(gen)) return;

        SyncStatusText = wrote
            ? $"Added {pattern} to {excludesPath}. It applies to every repository on this machine."
            : $"{pattern} is already in {excludesPath} — nothing was written.";
    }

    private async Task StageFilesAsync(IReadOnlyList<WorkingFile> files)
    {
        if (files.Count == 0) { SyncStatusText = "Select a file to stage first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Stage"); return; }

        var paths = files.Select(f => f.Path).ToList();
        var repo = RepoPath;
        var gen = _generation;
        if (await RunOp(r => _gitService.StageAsync(r, paths), "Stage", repo, gen) && IsCurrent(gen))
            OfferUndo(UndoLabel("Unstage", paths.Count), "Unstage", repo,
                r => _gitService.UnstageAsync(r, paths));
    }

    private async Task UnstageFilesAsync(IReadOnlyList<WorkingFile> files)
    {
        if (files.Count == 0) { SyncStatusText = "Select a file to unstage first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Unstage"); return; }

        var paths = files.Select(f => f.Path).ToList();
        var repo = RepoPath;
        var gen = _generation;
        if (await RunOp(r => _gitService.UnstageAsync(r, paths), "Unstage", repo, gen) && IsCurrent(gen))
            OfferUndo(UndoLabel("Stage", paths.Count), "Stage", repo,
                r => _gitService.StageAsync(r, paths));
    }

    private async Task DiscardFilesAsync(IReadOnlyList<WorkingFile> files)
    {
        if (files.Count == 0) { SyncStatusText = "Select a file to discard first."; return; }
        if (IsBusy) { SyncStatusText = BusyNotice("Discard"); return; }

        // Read before the dialog: the confirmation names this repo and these files, and the
        // discard is irreversible, so a switch landing while it is open must not redirect it
        // onto the project that takes the screen.
        var confirmedRepo = RepoPath;
        var gen = _generation;

        if (!await ConfirmAsync("Discard changes?", DiscardMessage(files), "Discard")) return;
        if (!IsCurrent(gen))
        {
            SyncStatusText = ProjectSwitchedNotice("Discard");
            return;
        }

        // No undo offer follows: what a discard removes was never committed and is in no
        // index, so no git command puts it back. The confirmation above is the whole guard.
        await RunOp(r => _gitService.DiscardAsync(r, files), "Discard", confirmedRepo, gen);
    }

    /// <summary>
    /// What the confirmation says. It names the count and the files, because a selection made
    /// with Shift can hold rows the reader never looked at, and caps the list so a hundred-file
    /// selection still fits a dialog. Untracked files are called out: they are deleted, not
    /// reverted.
    /// </summary>
    internal static string DiscardMessage(IReadOnlyList<WorkingFile> files, int cap = 8)
    {
        if (files.Count == 1)
        {
            var only = files[0];
            var verb = only.IsUntracked ? "Delete untracked file" : "Discard changes to";
            return $"{verb} {only.Path}?\n\nThis cannot be undone.";
        }

        var text = new StringBuilder($"Discard changes to {files.Count} files?\n\n");
        foreach (var file in files.Take(cap)) text.Append($"    {file.Path}\n");
        if (files.Count > cap) text.Append($"    +{files.Count - cap} more\n");

        var untracked = files.Count(f => f.IsUntracked);
        if (untracked == files.Count)
            text.Append("\nAll of them are untracked and will be deleted.");
        else if (untracked == 1)
            text.Append("\nOne of them is untracked and will be deleted.");
        else if (untracked > 1)
            text.Append($"\n{untracked} of them are untracked and will be deleted.");

        return text.Append("\n\nThis cannot be undone.").ToString();
    }

    /// <summary>
    /// An offer stands only where ONE git command puts the paths the operation named back the
    /// way they were: stage and unstage by selection, in either direction, and whole-tree only
    /// where the whole-tree command's inverse touches nothing else.
    /// Nothing irreversible is offered one — a discard, a stash drop, a branch delete removes
    /// content no command can reconstruct, and an "undo" beside them would promise a recovery
    /// that does not exist. Their confirmation is what carries the weight instead.
    /// </summary>
    [ObservableProperty] private bool _undoOfferVisible;

    /// <summary>The offer's button text, naming the action rather than calling it an undo.</summary>
    [ObservableProperty] private string _undoOfferLabel = "";

    private Func<string, Task<ProcessResult>>? _undoOfferOp;
    private string _undoOfferRepo = "";
    private string _undoOfferOpLabel = "";

    /// <summary>The record of the operation this offer inverts, so a taken offer links back to it.</summary>
    private string _undoOfferRecordId = "";

    private void OfferUndo(string buttonLabel, string opLabel, string repo,
        Func<string, Task<ProcessResult>> op)
    {
        _undoOfferOp = op;
        _undoOfferRepo = repo;
        _undoOfferOpLabel = opLabel;
        _undoOfferRecordId = _lastOperationRecordId;
        UndoOfferLabel = buttonLabel;
        UndoOfferVisible = true;
    }

    internal void ClearUndoOffer()
    {
        _undoOfferOp = null;
        _undoOfferRepo = "";
        _undoOfferOpLabel = "";
        _undoOfferRecordId = "";
        UndoOfferLabel = "";
        UndoOfferVisible = false;
    }

    private static string UndoLabel(string verb, int count) =>
        count == 1 ? $"{verb} that file" : $"{verb} those {count} files";

    /// <summary>
    /// Runs the offered inverse. It is bound to the repository the operation ran in, never to
    /// the live one: the offer is cleared on a project switch, and a click that beats the
    /// switch is refused rather than replayed against whatever took the screen.
    /// </summary>
    [RelayCommand]
    private async Task RunUndoOffer()
    {
        var op = _undoOfferOp;
        var repo = _undoOfferRepo;
        var label = _undoOfferOpLabel;
        var ofId = _undoOfferRecordId;
        ClearUndoOffer();

        if (op is null) return;
        if (IsBusy) { SyncStatusText = BusyNotice(label); return; }
        if (repo != RepoPath)
        {
            SyncStatusText = ProjectSwitchedNotice(label);
            return;
        }

        await RunOp(op, label, repo, _generation,
            recovery: new Services.Safety.RecoveryNote
            {
                Kind = Services.Safety.RecoveryKind.UndoOffered,
                AppliedUtc = DateTimeOffset.UtcNow,
                OfId = ofId
            });
    }
}
