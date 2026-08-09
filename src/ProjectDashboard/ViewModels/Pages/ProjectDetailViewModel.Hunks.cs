using System.ComponentModel;
using ProjectDashboard.Models;
using ProjectDashboard.Services;

namespace ProjectDashboard.ViewModels.Pages;

/// <summary>
/// Hunk-level staging in the Changes tab.
///
/// Every patch is sliced out of a freshly read RAW <c>git diff</c> by
/// <see cref="GitService.ExtractHunkPatch"/> and applied with <c>git apply</c>. Nothing here
/// rebuilds a patch from <see cref="DiffLine"/> rows: the parsed model has already dropped the
/// CR of a CRLF line and cannot tell the "\ No newline at end of file" marker from a context
/// line whose content begins with a backslash, so a patch built from it is either rejected or
/// applied with the wrong bytes.
///
/// The rendered diff is a snapshot; the slice is taken from a read made at click time. The two
/// are reconciled by comparing the hunk header the slice starts with against the header the row
/// on screen shows — a mismatch means the file moved under the view, and the operation is
/// refused rather than applied to whatever now sits at that index.
/// </summary>
public partial class ProjectDetailViewModel
{
    /// <summary>The diff row the reader is on. A row anywhere inside a hunk names that hunk.</summary>
    [ObservableProperty] private DiffLine? _selectedDiffLine;

    /// <summary>True while the pane shows a merge (combined) diff, which carries no single-sided hunks.</summary>
    [ObservableProperty] private bool _diffIsCombined;

    /// <summary>
    /// The hunk the pane should land on once a re-read has rebuilt the rows. The rebuild
    /// replaces every <see cref="DiffLine"/>, so a selection held by reference is lost and the
    /// reader is thrown back to the top of a long diff.
    /// The file and side it was taken in travel with it: a file switch racing the refresh
    /// renders a different diff, where the same place names a hunk the reader never acted on.
    /// </summary>
    private DiffFocus? _diffFocus;

    /// <summary>
    /// Where the pane lands, named one of two ways. A hunk operation names the INDEX: the hunk
    /// it moved is gone from this side, and the index that followed it is where the reader was
    /// left. A re-read after an edit made outside the app names the HEADER: nothing moved across
    /// the index, the hunk the reader is on is still theirs, and an edit above it renumbers every
    /// index below. <see cref="Hunk"/> is -1 wherever a header names the place.
    /// </summary>
    private readonly record struct DiffFocus(string Path, bool Staged, int Hunk, string? Header = null);

    /// <summary>
    /// Everything a hunk operation acts on, read at the moment the reader chose it. An
    /// operation that re-read the live selection would act on whatever the pane holds when its
    /// dialog closes, which a background refresh or a click on another file can have moved —
    /// and finding nothing there, would return silently after a confirmation was given. The
    /// header travels with the index because the index alone survives an edit that renumbers
    /// every hunk in the file.
    /// </summary>
    private readonly record struct HunkTarget(WorkingFile File, bool Staged, int Hunk, string Header);

    /// <summary>The hunk the pane is on right now, or null when it is on none.</summary>
    private HunkTarget? CurrentHunk =>
        DiffTarget is { } target && SelectedDiffLine is { HunkIndex: >= 0 } row
            ? new HunkTarget(target.File, target.Staged, row.HunkIndex, HeaderTextFor(row.HunkIndex))
            : null;

    /// <summary>The file the diff pane is showing and which side of it, or null when it shows none.</summary>
    private (WorkingFile File, bool Staged)? DiffTarget =>
        SelectedStagedFile is { } staged ? (staged, true)
        : SelectedUnstagedFile is { } unstaged ? (unstaged, false)
        : null;

    /// <summary>What stops any hunk operation whatever its direction, or null.</summary>
    private string? HunkBlockedReason =>
        DiffTarget is not { } target ? "Select a changed file first."
        : IsBusy ? "Another git operation is running."
        : target.File.IsUntracked ? "An untracked file has no hunks — stage the whole file instead."
        // On the staged side the pane reads a rename with both of its paths and gets the rename
        // diff; a slice is read with the new path alone and gets a whole-file add, whose reverse
        // would unstage the rename rather than one hunk. No header can match across the two. The
        // unstaged side of the same file carries only the worktree edit — the rename is in the
        // index, contributes nothing to either read, and its hunks slice normally.
        : target.Staged && target.File.OrigPath is not null ? "This is a staged rename — unstage the file to work on its hunks."
        : DiffIsBinary ? "Binary file — there are no hunks to stage."
        : DiffIsCombined ? "This is a merge diff — resolve the conflict and stage the file."
        // A truncated diff is a prefix, so the rows below the cut are missing and the slice a
        // row names can be half a hunk. Applying that patch is a corrupt edit, not a partial one.
        : DiffIsTruncated ? "This diff was too large to read in full — stage or discard the whole file instead."
        : SelectedDiffLine is not { HunkIndex: >= 0 } ? "Select a line inside a hunk first."
        : null;

    public string? StageHunkBlockedReason =>
        HunkBlockedReason ?? (DiffTarget!.Value.Staged ? "This hunk is already staged." : null);

    public string? UnstageHunkBlockedReason =>
        HunkBlockedReason ?? (DiffTarget!.Value.Staged ? null : "This hunk is not staged yet.");

    public string? DiscardHunkBlockedReason =>
        HunkBlockedReason ?? (DiffTarget!.Value.Staged
            ? "Unstage this hunk first — discard works on the working tree."
            : null);

    private bool CanStageHunk() => StageHunkBlockedReason is null;
    private bool CanUnstageHunk() => UnstageHunkBlockedReason is null;
    private bool CanDiscardHunk() => DiscardHunkBlockedReason is null;

    /// <summary>
    /// The gates read state owned by the other partials, which cannot carry this one's
    /// notification attributes. Dispatched from the class's single OnPropertyChanged override.
    /// </summary>
    private void HandleHunkPropertyChanged(PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedDiffLine):
            case nameof(SelectedStagedFile):
            case nameof(SelectedUnstagedFile):
            case nameof(IsBusy):
            case nameof(DiffIsBinary):
            case nameof(DiffIsCombined):
            case nameof(DiffIsTruncated):
            case nameof(DiffLines):
                StageHunkCommand.NotifyCanExecuteChanged();
                UnstageHunkCommand.NotifyCanExecuteChanged();
                DiscardHunkCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(StageHunkBlockedReason));
                OnPropertyChanged(nameof(UnstageHunkBlockedReason));
                OnPropertyChanged(nameof(DiscardHunkBlockedReason));
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStageHunk))]
    private Task StageHunk() =>
        ApplyHunkAsync("Stage hunk", (repo, patch) => _gitService.StageHunkAsync(repo, patch),
            CurrentHunk, RepoPath, _generation);

    [RelayCommand(CanExecute = nameof(CanUnstageHunk))]
    private Task UnstageHunk() =>
        ApplyHunkAsync("Unstage hunk", (repo, patch) => _gitService.UnstageHunkAsync(repo, patch),
            CurrentHunk, RepoPath, _generation);

    /// <summary>
    /// Moves the selected hunk across the index in whichever direction the pane it is shown in
    /// allows. Bound to the double-click, which must never be the destructive direction.
    /// </summary>
    [RelayCommand]
    private Task ToggleHunkStaging()
    {
        if (DiffTarget is not { } target) return Task.CompletedTask;
        if (target.Staged) return CanUnstageHunk() ? UnstageHunk() : Task.CompletedTask;
        return CanStageHunk() ? StageHunk() : Task.CompletedTask;
    }

    /// <summary>
    /// Reverts one hunk in the working tree. Irreversible — the content it removes was never
    /// committed and is in no index — so it is confirmed, and the confirmation names the file
    /// and the hunk the reader is looking at.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDiscardHunk))]
    private async Task DiscardHunk()
    {
        // Read before the dialog, and acted on afterwards: `git apply --reverse` cannot be
        // undone, so neither a project switch nor a refresh landing while it is open may
        // redirect the discard onto another repository or another hunk.
        if (CurrentHunk is not { } hunk) return;
        var repo = RepoPath;
        var gen = _generation;

        var confirmed = await ConfirmAsync("Discard this hunk?",
            $"Revert one hunk of {hunk.File.Path} in the working tree?\n\n{hunk.Header}\n\n" +
            "The rest of the file keeps its changes. This cannot be undone.", "Discard hunk");
        if (!confirmed) return;
        if (!IsCurrent(gen))
        {
            SyncStatusText = ProjectSwitchedNotice("Discard hunk");
            return;
        }

        await ApplyHunkAsync("Discard hunk", (r, patch) => _gitService.DiscardHunkAsync(r, patch),
            hunk, repo, gen);
    }

    /// <summary>The header line of the hunk at <paramref name="hunkIndex"/> in the rendered diff, or "" when it has none.</summary>
    private string HeaderTextFor(int hunkIndex) =>
        DiffLines.FirstOrDefault(l => l.IsHunkStart && l.HunkIndex == hunkIndex)?.Text ?? "";

    /// <summary>
    /// Slices the selected hunk out of a freshly read raw diff and applies it. The slice, the
    /// staleness check, and the operation all run against the repo path and generation the
    /// caller captured, never the live ones.
    /// </summary>
    private async Task ApplyHunkAsync(string label, Func<string, string, Task<ProcessResult>> operate,
        HunkTarget? bound, string repo, int gen)
    {
        if (bound is not { } hunk) return;
        if (repo.Length == 0 || IsBusy) return;

        var raw = await _gitService.GetFileDiffRawAsync(repo, hunk.File.Path, hunk.Staged);
        if (!IsCurrent(gen)) return;
        if (raw is null)
        {
            SyncStatusText = $"{label} failed: the diff for {hunk.File.Path} could not be read.";
            return;
        }

        // The gate that let this operation start read the diff on display; this read is a fresh
        // one, and a file that grew past the budget in between hands back a prefix whose slice
        // ends mid-hunk. Refused and reloaded, the same as a diff that moved underneath.
        if (raw.Truncated)
        {
            SyncStatusText = $"{label} refused: the diff for {hunk.File.Path} is too large to read in full, " +
                             "so a hunk cannot be sliced out of it. It has been reloaded — " +
                             "stage or discard the whole file instead.";
            await ReloadDiffForCurrentSelectionAsync();
            return;
        }

        var patch = GitService.ExtractHunkPatch(raw.Text, hunk.File.Path, hunk.Hunk);
        if (patch is null || !HeaderMatches(patch, hunk.Header))
        {
            SyncStatusText = $"{label} refused: {hunk.File.Path} changed since this diff was shown. " +
                             "It has been reloaded — pick the hunk again.";
            await ReloadDiffForCurrentSelectionAsync();
            return;
        }

        _diffFocus = new DiffFocus(hunk.File.Path, hunk.Staged, hunk.Hunk);
        var ok = await RunOp(r => operate(r, patch), label, repo, gen);
        if (!ok) _diffFocus = null;
    }

    /// <summary>
    /// Whether the slice starts at the hunk the pane is showing. Compared on the header line —
    /// the ranges and the section hint — because a hunk INDEX survives an edit that renumbers
    /// every hunk in the file, and applying index N of a diff the reader never saw stages
    /// somebody else's change.
    /// </summary>
    internal static bool HeaderMatches(string patch, string shownHeader)
    {
        if (shownHeader.Length == 0) return false;
        foreach (var raw in patch.Split('\n'))
        {
            if (!raw.StartsWith("@@", StringComparison.Ordinal)) continue;
            return string.Equals(raw.TrimEnd('\r'), shownHeader, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>Re-renders the pane for whichever side is selected, after a refusal that left the view stale.</summary>
    private Task ReloadDiffForCurrentSelectionAsync() =>
        DiffTarget is { } target ? ShowDiffAsync(target.File, target.Staged) : Task.CompletedTask;

    /// <summary>
    /// Re-selects the row the reader was on once the rows have been rebuilt. A focus taken by
    /// index follows a hunk operation: the hunk it moved is gone from this side, so the same
    /// index now names the hunk that followed it, clamped to the last when it was the final one.
    /// A focus taken by header follows a re-read of a file nothing moved, and a header the
    /// re-read no longer holds leaves the pane on no row rather than on a hunk at that place.
    /// Only for the file and side the focus was taken on: it is spent either way, so a diff for
    /// anything else drops it rather than selecting a hunk of its own.
    /// </summary>
    private void RestoreDiffFocus(WorkingFile file, bool staged)
    {
        var wanted = _diffFocus;
        _diffFocus = null;
        if (wanted is not { } focus || DiffLines.Count == 0) return;
        if (focus.Staged != staged || !string.Equals(focus.Path, file.Path, StringComparison.Ordinal)) return;

        var headers = DiffLines.Where(l => l.IsHunkStart).ToList();
        if (headers.Count == 0) return;
        SelectedDiffLine = focus.Header is { } header
            ? headers.FirstOrDefault(l => string.Equals(l.Text, header, StringComparison.Ordinal))
            : headers[Math.Min(focus.Hunk, headers.Count - 1)];
    }
}
