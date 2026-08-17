using System.IO;
using System.Text;
using ProjectDashboard.Models;

namespace ProjectDashboard.Services.Surgery;

/// <summary>Which merge stage a read or a resolution names.</summary>
public enum ConflictSide
{
    /// <summary>Stage 1 — the common ancestor.</summary>
    Base = 1,
    /// <summary>Stage 2 — the side the checkout was on when the sequence started.</summary>
    Ours = 2,
    /// <summary>Stage 3 — the side being brought in.</summary>
    Theirs = 3
}

/// <summary>
/// Which stages the index holds for one unmerged path, and whether the entry is a gitlink.
/// A side with no stage has no content to preview and none to take.
/// </summary>
public sealed record ConflictStages(bool HasBase, bool HasOurs, bool HasTheirs, bool IsGitlink)
{
    public bool Has(ConflictSide side) => side switch
    {
        ConflictSide.Base => HasBase,
        ConflictSide.Ours => HasOurs,
        _ => HasTheirs
    };
}

/// <summary>
/// The unmerged index as one read, or why it could not be read. An empty map and a failed read
/// are different answers: the first says the index holds no conflict, the second says nothing.
/// </summary>
public sealed record ConflictIndexRead(Dictionary<string, ConflictStages> ByPath, string? Error);

/// <summary>
/// Drives git's sequencers over a conflicted repository: reads the unmerged index, renders the
/// stages read-only, records a per-file resolution, and continues or aborts the merge, rebase,
/// cherry-pick or revert in progress.
///
/// Nothing here edits a merged buffer. A resolution is one of three recordings — take stage 2,
/// take stage 3, or accept what the working tree already holds — and each is a single git call
/// against a literal pathspec.
///
/// `GIT_EDITOR=true` is pinned on every continue. `git merge --continue`,
/// `git rebase --continue`, `git cherry-pick --continue` and `git revert --continue` all open an
/// editor for the commit message by default, and a windowless process stalls there until its
/// timeout kills it mid-sequence. `--continue` accepts no message arguments — `git merge
/// --continue --no-edit` is rejected outright — so a message the reader edited is written by
/// `git commit -F` first and the sequencer is then continued over the commit that already exists.
///
/// A continue writes a commit, so it carries a <see cref="SigningChoice"/>: a repository
/// configured to sign with an uncached passphrase otherwise waits on a prompt this app cannot
/// show. Signing is never disabled here on this service's own initiative.
/// </summary>
public sealed class ConflictResolver
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AbortTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ContinueTimeout = TimeSpan.FromMinutes(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The mode git records a gitlink under; taking a side of one picks a commit, not content.</summary>
    private const string GitlinkMode = "160000";

    /// <summary>Bytes of a working-tree file the marker scan reads before it gives up on the file.</summary>
    internal const long MarkerScanByteLimit = 32L * 1024 * 1024;

    private readonly GitService _git;

    public ConflictResolver(GitService git) => _git = git;

    // ── Reads ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every unmerged path with the stages the index holds for it. One process; the paths come
    /// back exactly as git records them, NUL-separated so no name can split a record.
    /// </summary>
    public async Task<ConflictIndexRead> ReadUnmergedAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await _git.RunAsync(repoPath, ["ls-files", "-u", "-z"], ct, ShortTimeout);
        if (!result.Success)
            return new ConflictIndexRead([], GitService.ReadFailureText(result, ShortTimeout));
        return new ConflictIndexRead(ParseUnmerged(result.StdOut), null);
    }

    /// <summary>
    /// Parses `git ls-files -u -z`: "&lt;mode&gt; &lt;sha&gt; &lt;stage&gt;\t&lt;path&gt;" per NUL-separated record,
    /// one record per stage a path has.
    /// </summary>
    internal static Dictionary<string, ConflictStages> ParseUnmerged(string output)
    {
        var byPath = new Dictionary<string, ConflictStages>(StringComparer.Ordinal);
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            if (!int.TryParse(fields[2], out var stage)) continue;

            var path = record[(tab + 1)..];
            var gitlink = fields[0] == GitlinkMode;
            byPath.TryGetValue(path, out var held);
            held ??= new ConflictStages(false, false, false, false);
            byPath[path] = new ConflictStages(
                held.HasBase || stage == 1,
                held.HasOurs || stage == 2,
                held.HasTheirs || stage == 3,
                held.IsGitlink || gitlink);
        }
        return byPath;
    }

    /// <summary>
    /// The diff between two stages of one conflicted path, rendered by the same parser every other
    /// diff on the page goes through. Null when git could not read the pair — a stage the index
    /// does not hold, most often, which the caller establishes from
    /// <see cref="ReadUnmergedAsync"/> before asking.
    /// </summary>
    public async Task<FileDiff?> ReadStageDiffAsync(
        string repoPath, string path, ConflictSide from, ConflictSide to, CancellationToken ct = default)
    {
        var result = await _git.RunAsync(repoPath,
            ["diff", "--no-color", StageSpec(from, path), StageSpec(to, path)], ct, ShortTimeout);
        if (!result.Success)
        {
            Log.Warn($"stage diff failed for {path}: {result.FirstError}");
            return null;
        }

        var diff = FileDiff.ParseUnified(result.StdOut).FirstOrDefault();
        if (diff is null) return new FileDiff { Path = path };
        diff.Path = path;
        diff.Truncated = result.Truncated;
        return diff;
    }

    /// <summary>The content of one stage, for a side the other stage has nothing to be compared against.</summary>
    public async Task<FileDiff?> ReadStageContentAsync(
        string repoPath, string path, ConflictSide side, CancellationToken ct = default)
    {
        var result = await _git.RunAsync(repoPath, ["show", StageSpec(side, path)], ct, ShortTimeout);
        if (!result.Success)
        {
            Log.Warn($"stage read failed for {path}: {result.FirstError}");
            return null;
        }

        var diff = new FileDiff { Path = path, Truncated = result.Truncated };
        if (result.StdOut.Contains('\0'))
        {
            diff.IsBinary = true;
            return diff;
        }

        var lines = result.StdOut.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        diff.Lines.Add(new DiffLine { Kind = DiffLineKind.HunkHeader, Text = $"@@ {count} line(s) @@" });
        for (var i = 0; i < count; i++)
            diff.Lines.Add(new DiffLine
            {
                Kind = DiffLineKind.Context,
                Text = lines[i].TrimEnd('\r'),
                OldNumber = (i + 1).ToString(),
                NewNumber = (i + 1).ToString()
            });
        return diff;
    }

    internal static string StageSpec(ConflictSide side, string path) => $":{(int)side}:{path}";

    // ── Per-file resolutions ────────────────────────────────────────────────

    /// <summary>
    /// Records one side of a conflict as the resolution. Where that side holds content the file is
    /// checked out from its stage and added; where that side deleted the file the deletion itself
    /// is what the side says, and `git rm` is the recording of it. Both leave the path merged in
    /// the index, which is what the sequencer's continue requires.
    /// </summary>
    public async Task<ProcessResult> TakeSideAsync(
        string repoPath, string path, ConflictSide side, bool sideHasContent, CancellationToken ct = default)
    {
        var pathspec = GitService.LiteralPathspec(path);
        if (!sideHasContent)
            return await _git.RunAsync(repoPath, ["rm", "-f", "--", pathspec], ct, ShortTimeout);

        var flag = side == ConflictSide.Ours ? "--ours" : "--theirs";
        var checkout = await _git.RunAsync(repoPath, ["checkout", flag, "--", pathspec], ct, ShortTimeout);
        if (!checkout.Success) return checkout;
        return await _git.RunAsync(repoPath, ["add", "--", pathspec], ct, ShortTimeout);
    }

    /// <summary>What a stage-resolved attempt did, and why it did not stage when it did not.</summary>
    public sealed record StageResolvedResult
    {
        /// <summary>The working tree's content for this path is in the index and the path is merged.</summary>
        public required bool Staged { get; init; }

        /// <summary>The marker text the scan found; null when it found none.</summary>
        public string? Marker { get; init; }

        /// <summary>The file changed between the scan and the stage, so what would have been staged was never checked.</summary>
        public bool ChangedWhileStaging { get; init; }

        /// <summary>The unmerged stages are back and the working tree holds what it held.</summary>
        public bool ConflictRestored { get; init; }

        /// <summary>The git call that failed, when one did.</summary>
        public ProcessResult? Failure { get; init; }
    }

    /// <summary>
    /// Stages what the working tree holds for a path the reader resolved outside this app. `-A` so
    /// a resolution that deleted the file records the deletion rather than failing on a pathspec
    /// that matches nothing.
    ///
    /// The marker scan and the stage are two operations over one file, so what git reads is not
    /// necessarily what was scanned. The content is therefore identified before the scan and the
    /// staged blob is compared against that identity afterwards: a path whose content moved in
    /// between is unstaged again — the unmerged stages recreated, the working tree's own bytes put
    /// back — and refused. What ends up in the index is content this scan actually read.
    /// </summary>
    public async Task<StageResolvedResult> StageResolvedAsync(
        string repoPath, string path, CancellationToken ct = default)
    {
        var pathspec = GitService.LiteralPathspec(path);
        var scanned = await BlobIdentityAsync(repoPath, path, ct);

        if (FindConflictMarker(repoPath, path) is { } marker)
            return new StageResolvedResult { Staged = false, Marker = marker };

        var add = await _git.RunAsync(repoPath, ["add", "-A", "--", pathspec], ct, ShortTimeout);
        if (!add.Success) return new StageResolvedResult { Staged = false, Failure = add };

        var staged = await StagedIdentityAsync(repoPath, path, ct);
        if (scanned is null || staged is null || string.Equals(scanned, staged, StringComparison.Ordinal))
            return new StageResolvedResult { Staged = true };

        var restored = await RestoreConflictAsync(repoPath, path, ct);
        return new StageResolvedResult
        {
            Staged = false,
            ChangedWhileStaging = true,
            ConflictRestored = restored
        };
    }

    /// <summary>
    /// The blob git would record for the working tree's copy of a path, with the same filters an
    /// `add` applies to it. Null for a path with no file — a resolution that deleted it, which
    /// stages a removal and has no content to check.
    /// </summary>
    private async Task<string?> BlobIdentityAsync(string repoPath, string path, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(repoPath, path.Replace('/', Path.DirectorySeparatorChar)))) return null;
        var hash = await _git.RunAsync(repoPath, ["hash-object", "--", path], ct, ShortTimeout);
        return hash.Success && hash.StdOut.Trim().Length > 0 ? hash.StdOut.Trim() : null;
    }

    /// <summary>The blob the index holds for a path at stage 0, or null when it holds none.</summary>
    private async Task<string?> StagedIdentityAsync(string repoPath, string path, CancellationToken ct)
    {
        var read = await _git.RunAsync(repoPath, ["ls-files", "-s", "--", GitService.LiteralPathspec(path)], ct, ShortTimeout);
        if (!read.Success) return null;
        var line = read.StdOut.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        var fields = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 2 } ? fields[1] : null;
    }

    /// <summary>
    /// Puts an unmerged path back the way it was before a stage collapsed it: git recreates the
    /// conflicted merge for the path, and the working tree's own bytes are written back over the
    /// markers that recreation leaves. Reports whether both halves landed — a restore that failed
    /// leaves the caller to say so rather than to claim the index is untouched.
    /// </summary>
    private async Task<bool> RestoreConflictAsync(string repoPath, string path, CancellationToken ct)
    {
        var full = Path.Combine(repoPath, path.Replace('/', Path.DirectorySeparatorChar));
        byte[]? held = null;
        try
        {
            if (File.Exists(full)) held = await File.ReadAllBytesAsync(full, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not hold the working-tree copy of {path} before restoring its conflict", ex);
            return false;
        }

        var restore = await _git.RunAsync(
            repoPath, ["checkout", "--merge", "--", GitService.LiteralPathspec(path)], ct, ShortTimeout);
        if (!restore.Success) return false;

        try
        {
            if (held is not null) await File.WriteAllBytesAsync(full, held, ct);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not put the working-tree copy of {path} back after restoring its conflict", ex);
            return false;
        }
    }

    /// <summary>
    /// Which conflict marker the working-tree file still carries, or null when it carries none.
    /// Returns <see cref="MarkerScanUnreadable"/> when the file cannot be read at all: a file
    /// nothing can scan is never reported as clean.
    /// </summary>
    public static string? FindConflictMarker(string repoPath, string path)
    {
        var full = Path.Combine(repoPath, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(full)) return null;
            if (new FileInfo(full).Length > MarkerScanByteLimit) return MarkerScanUnreadable;

            using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
                if (IsConflictMarker(line))
                    return line.Length > 40 ? line[..40] : line;
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"conflict-marker scan failed for {path}", ex);
            return MarkerScanUnreadable;
        }
    }

    /// <summary>Stands in for the marker text when the file could not be scanned; the guard refuses either way.</summary>
    public const string MarkerScanUnreadable = "(this file could not be read)";

    /// <summary>
    /// Whether a line is one of git's conflict markers. The angle-bracket runs carry a label and
    /// are matched on the run plus its separator; the equals run is matched whole, so a rule of
    /// equals signs under a heading is not read as the separator of a conflict that is not there.
    /// </summary>
    internal static bool IsConflictMarker(string line) =>
        IsMarkerRun(line, '<') || IsMarkerRun(line, '>') || IsMarkerRun(line, '|') || line == "=======";

    private static bool IsMarkerRun(string line, char marker)
    {
        if (line.Length < 7) return false;
        for (var i = 0; i < 7; i++)
            if (line[i] != marker) return false;
        return line.Length == 7 || line[7] == ' ';
    }

    // ── Sequencer ───────────────────────────────────────────────────────────

    /// <summary>The message git has prepared for the commit a continue would write; empty when there is none.</summary>
    public async Task<string> ReadPreparedMessageAsync(
        string repoPath, RepoActivity activity, CancellationToken ct = default)
    {
        var relative = activity == RepoActivity.Rebasing ? "rebase-merge/message" : "MERGE_MSG";
        var probe = await _git.RunAsync(repoPath, ["rev-parse", "--git-path", relative], ct, ShortTimeout);
        if (!probe.Success) return "";

        var file = probe.StdOut.Trim();
        if (file.Length == 0) return "";
        if (!Path.IsPathRooted(file)) file = Path.Combine(repoPath, file);
        try
        {
            return File.Exists(file) ? StripCommentLines(File.ReadAllText(file)) : "";
        }
        catch (Exception ex)
        {
            Log.Warn($"prepared message read failed for {repoPath}", ex);
            return "";
        }
    }

    /// <summary>
    /// The prepared message without the lines git writes for the editor it expects to open. They
    /// are scaffolding rather than message: shown in the box they would be committed verbatim,
    /// because every commit this app writes pins `--cleanup=whitespace`.
    /// </summary>
    internal static string StripCommentLines(string message)
    {
        var kept = message.Split('\n')
            .Where(line => !line.TrimEnd('\r').StartsWith('#'))
            .Select(line => line.TrimEnd('\r'));
        return string.Join('\n', kept).Trim('\n');
    }

    /// <summary>
    /// Continues the sequence in progress. With <paramref name="editedMessage"/> null the commit
    /// carries the message git prepared; with one, the commit is written from it first and the
    /// sequencer is continued afterwards only while it is still running — a merge is finished by
    /// the commit itself, and a one-commit cherry-pick or revert is too.
    /// </summary>
    public async Task<ProcessResult> ContinueAsync(
        string repoPath, RepoActivity activity, string? editedMessage, SigningChoice signing,
        CancellationToken ct = default)
    {
        if (ContinueVerb(activity) is not { } verb)
            return Refusal($"there is no {Describe(activity)} to continue");

        var env = new Dictionary<string, string> { ["GIT_EDITOR"] = "true" };
        if (editedMessage is not null)
        {
            var written = await CommitEditedMessageAsync(repoPath, editedMessage, signing, env, ct);
            if (!written.Success) return written;
            if (await _git.DetectActivityAsync(repoPath, ct) != activity) return written;
        }

        var args = SigningPin(signing);
        args.AddRange([verb, "--continue"]);
        return await _git.RunAsync(repoPath, args, env, ct, ContinueTimeout);
    }

    /// <summary>
    /// Writes the commit a continue would have written, from the message the reader edited.
    /// `--continue` takes no message arguments on any of the four sequencers, so this is the only
    /// route an edited message has that does not open an editor.
    /// </summary>
    private async Task<ProcessResult> CommitEditedMessageAsync(
        string repoPath, string message, SigningChoice signing,
        IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var scratch = Path.Combine(AppPaths.LocalDir, "conflict-work");
        var file = Path.Combine(scratch, $"msg-{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(scratch);
            await File.WriteAllTextAsync(file, message, Utf8NoBom, ct);
        }
        catch (Exception ex)
        {
            Log.Warn("could not write the continue message file", ex);
            return Refusal($"the edited message could not be written to disk: {ex.Message}");
        }

        try
        {
            var args = SigningPin(signing);
            args.AddRange(["commit", GitService.MessageCleanupPin, "-F", file]);
            return await _git.RunAsync(repoPath, args, env, ct, ContinueTimeout);
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception ex) { Log.Warn($"could not delete the continue message file {file}", ex); }
        }
    }

    /// <summary>Abandons the sequence in progress, returning the repository to where it started.</summary>
    public async Task<ProcessResult> AbortAsync(
        string repoPath, RepoActivity activity, CancellationToken ct = default)
    {
        if (ContinueVerb(activity) is not { } verb)
            return Refusal($"there is no {Describe(activity)} to abort");
        return await _git.RunAsync(repoPath, [verb, "--abort"], ct, AbortTimeout);
    }

    /// <summary>The git command that drives one activity; null for anything this surface does not drive.</summary>
    internal static string? ContinueVerb(RepoActivity activity) => activity switch
    {
        RepoActivity.Merging => "merge",
        RepoActivity.Rebasing => "rebase",
        RepoActivity.CherryPicking => "cherry-pick",
        RepoActivity.Reverting => "revert",
        _ => null
    };

    /// <summary>The activity in the words the surface uses for it.</summary>
    public static string Describe(RepoActivity activity) => activity switch
    {
        RepoActivity.Merging => "merge",
        RepoActivity.Rebasing => "rebase",
        RepoActivity.CherryPicking => "cherry-pick",
        RepoActivity.Reverting => "revert",
        RepoActivity.Bisecting => "bisect",
        _ => "operation"
    };

    private static List<string> SigningPin(SigningChoice signing) =>
        signing == SigningChoice.ProceedUnsigned ? ["-c", "commit.gpgsign=false"] : [];

    private static ProcessResult Refusal(string reason) => new(-1, "", reason, TimedOut: false);
}
