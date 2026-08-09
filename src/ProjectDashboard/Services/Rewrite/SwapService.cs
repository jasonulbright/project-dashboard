using System.Text;
using ProjectDashboard.Services.History;

namespace ProjectDashboard.Services.Rewrite;

/// <summary>One ref the swap moved. <see cref="OldOid"/> is null for a ref the rewrite created, <see cref="NewOid"/> is null for a ref it deleted.</summary>
public sealed record SwapRefChange(string RefName, string? OldOid, string? NewOid);

/// <summary>
/// Outcome of an <see cref="SwapService.ApplySwapAsync"/> call. On refusal nothing in the
/// source repository changed and <see cref="RefusalReason"/> says why; on success the refs
/// listed in <see cref="RefChanges"/> were reconciled to the rewrite and the working tree
/// was reset to <see cref="NewHead"/>.
/// </summary>
public sealed record SwapResult(
    bool Success,
    string? RefusalReason,
    IReadOnlyList<SwapRefChange> RefChanges,
    string? OldHead,
    string? NewHead)
{
    public static SwapResult Refused(string reason) => new(false, reason, [], null, null);
}

/// <summary>
/// The only writer of rewritten history into a real repository. Applies a rewritten temp
/// bare (produced by <see cref="HistoryRewriter"/>, which never touches the source) into
/// the source repo: pre-flight refuses loudly and changes nothing if the source is dirty,
/// a rewritten path could never check out on Windows, or the temp bare fails fsck; the swap
/// itself fetches only objects, reconciles refs in one atomic transaction, then resets the
/// working tree. A partial, unrecoverable state is impossible by construction — the
/// pre-flight guards the checkout before any ref moves, and the ref reconciliation is a
/// single all-or-nothing `git update-ref --stdin`.
///
/// The clean-tree gate is read twice: once at entry, and again immediately before the ref
/// transaction, because the pre-scan, the fsck and the fetch sit between them and the closing
/// `reset --hard` discards whatever was written in that window. What remains is the span from
/// that second read to the reset — the ref transaction and two ref writes, no unbounded step
/// among them — during which an edit is still discarded with no backup holding it. A backup
/// bundles refs, not working-tree bytes, so no stage of this pipeline can restore them.
///
/// Cancellation is honoured only up to the point of no return marked inside
/// <see cref="ApplySwapAsync"/>: everything before it is pre-flight and a scratch-namespace
/// fetch, everything after it moves the source's own refs and runs under
/// <see cref="CancellationToken.None"/>.
/// </summary>
public class SwapService
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FsckTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResetTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The expected-old value that requires a ref to be absent. `git update-ref --stdin` reads a
    /// quoted empty string as "must not exist"; a zero object id means the same but only at the
    /// repository's own hash length, so it fails outright in a SHA-256 repository.
    /// </summary>
    private const string MustNotExist = "\"\"";

    private readonly GitService _git;

    public SwapService(GitService git) => _git = git;

    /// <summary>
    /// Applies <paramref name="tempBareRepo"/> into <paramref name="sourceRepo"/>. Refuses
    /// (changing nothing) if the source working tree is dirty, if any rewritten path can
    /// never check out on Windows, or if the temp bare fails fsck. On success the source's
    /// non-remote refs match the temp bare, HEAD is repositioned, and the working tree is
    /// reset. Remote-tracking refs (refs/remotes/*) are never touched — divergence shows
    /// until an explicit force-push, which is not this stage.
    ///
    /// Throws <see cref="OperationCanceledException"/> for a cancellation observed before the
    /// point of no return, having changed no ref; a cancellation requested after it is not
    /// honoured and the swap runs to completion. <paramref name="phase"/> is reported exactly
    /// once, at that boundary, so a surface offering cancel withdraws the offer at the same
    /// instant the swap stops accepting it.
    /// </summary>
    public virtual async Task<SwapResult> ApplySwapAsync(
        string sourceRepo, string tempBareRepo, IProgress<RewritePhase>? phase = null, CancellationToken ct = default)
    {
        // (a) A dirty source cannot be reset without discarding uncommitted work; the caller
        // offers stash, not this method. A null state means git could not read the repo at
        // all — also a refusal, never a silent proceed.
        var state = await _git.GetWorkingStateAsync(sourceRepo, ct);
        if (state is null)
            return SwapResult.Refused($"source repository '{sourceRepo}' could not be read by git — refusing the swap");
        if (state.IsDirty)
            return SwapResult.Refused($"source working tree has {state.Files.Count} uncommitted change(s) — refusing the swap (stash or commit first)");

        var desired = await ReadRefsAsync(tempBareRepo, ct);
        if (desired is null)
            return SwapResult.Refused($"could not read refs from the rewrite target '{tempBareRepo}'");

        // (b) NTFS pre-scan BEFORE any real ref moves: a mid-swap `reset --hard` that fails
        // on an illegal name — or on a path past MAX_PATH — would leave refs advanced past a
        // tree the working copy cannot hold. Scanning every rewritten tree up front makes that
        // impossible.
        var pathBudget = await CheckoutPathBudgetAsync(sourceRepo, ct);
        if (await FirstUncheckoutablePathAsync(tempBareRepo, desired, pathBudget, ct) is { } bad)
            return SwapResult.Refused($"rewritten path '{bad.Path}' can never check out on Windows: {bad.Reason}");

        // (c) fsck the temp bare: a corrupt object graph must never be fetched into the source.
        var fsck = await RunAsync(tempBareRepo, ["fsck", "--strict", "--no-progress"], FsckTimeout, ct);
        if (!fsck.Success)
            return SwapResult.Refused($"rewrite target failed fsck — refusing the swap: {fsck.FirstError}");

        var current = await ReadRefsAsync(sourceRepo, ct);
        if (current is null)
            return SwapResult.Refused($"could not read current refs from '{sourceRepo}'");

        var head = await ReadHeadAsync(tempBareRepo, desired, ct);
        if (head is null)
            return SwapResult.Refused($"could not resolve HEAD in the rewrite target '{tempBareRepo}'");

        var oldHead = (await RunAsync(sourceRepo, ["rev-parse", "--verify", "-q", "HEAD"], RefTimeout, ct)).StdOut.Trim();

        // Bring every rewritten object into the source WITHOUT moving a real ref: a scratch
        // namespace holds them so the reconciliation below is a pure ref transaction. The
        // scratch refs are deleted in the finally; core.protectNTFS=false is NOT propagated —
        // that override lives only in the engine's bare import, never in a repo that checks out.
        var scratch = $"refs/pd-swap/{Guid.NewGuid():N}";
        try
        {
            // A cancelled fetch can leave a partial .git/objects/pack/tmp_pack_* behind. It is
            // unreferenced, reclaimed by git gc, and moves no ref, commit, or tracked file — so
            // the cancelled outcome's claim that nothing was touched still holds.
            var fetch = await RunAsync(sourceRepo,
            [
                "-c", "transfer.fsckObjects=true", "fetch", "--no-tags", "--no-write-fetch-head", "--quiet",
                NativeUrl(tempBareRepo),
                $"+refs/*:{scratch}/refs/*",
                $"+HEAD:{scratch}/HEAD"
            ], FetchTimeout, ct);
            if (!fetch.Success)
                return SwapResult.Refused($"fetching rewritten objects failed — nothing changed: {fetch.FirstError}");

            // (d) Re-read the working tree. The gate at (a) is as old as the pre-scan, the fsck
            // and the fetch together — minutes on a large repository — and the `reset --hard`
            // below discards anything written since, which no backup holds because the backup
            // bundles refs, not the working tree. This is the last point at which a refusal
            // still changes nothing.
            var atSwap = await _git.GetWorkingStateAsync(sourceRepo, ct);
            if (atSwap is null)
                return SwapResult.Refused($"source repository '{sourceRepo}' became unreadable while the swap was preparing — refusing the swap");
            if (atSwap.IsDirty)
                return SwapResult.Refused(
                    $"the working tree gained {atSwap.Files.Count} uncommitted change(s) while the swap was preparing — " +
                    "refusing the swap (the reset would discard them, and no backup holds them)");

            // ── Point of no return ──────────────────────────────────────────────────────
            // Everything above is pre-flight plus a fetch into a scratch namespace the finally
            // deletes, so a cancellation observed here has moved no ref. Below, `git update-ref
            // --stdin` commits its transaction by renaming lock files one at a time: killing it
            // part-way through leaves some refs moved and others not — the one outcome this
            // stage exists to make impossible. The tail therefore runs under a token that is
            // never cancelled, and the cancel offer is withdrawn on this line.
            ct.ThrowIfCancellationRequested();
            phase?.Report(RewritePhase.Applying);
            var applying = CancellationToken.None;

            // Reconcile the source's non-remote refs to EXACTLY the temp bare's in one atomic
            // `git update-ref --stdin`: delete refs the rewrite dropped, set the rest. The whole
            // script commits under one lock, so a ref-lock contention, a missing target object,
            // or an IO stall aborts the entire transaction with NOTHING changed — never a partial
            // swap. HEAD is set after, since a symbolic HEAD is not expressible in this script.
            //
            // Every line carries the value the ref held when `current` was read, so a ref another
            // process moved in the window since that read aborts the whole transaction instead of
            // being silently overwritten by the rewrite's value.
            var changes = BuildRefChanges(current, desired);
            var script = new StringBuilder();
            foreach (var change in changes)
            {
                var expected = change.OldOid ?? MustNotExist;
                if (change.NewOid is null)
                    script.Append("delete ").Append(change.RefName).Append(' ').Append(expected).Append('\n');
                else
                    script.Append("update ").Append(change.RefName).Append(' ').Append(change.NewOid)
                        .Append(' ').Append(expected).Append('\n');
            }

            if (script.Length > 0)
            {
                var reconcile = await _git.RunWithInputAsync(
                    sourceRepo, ["update-ref", "--stdin"], script.ToString(), applying, RefTimeout);
                if (!reconcile.Success)
                    return SwapResult.Refused($"ref reconciliation transaction failed — nothing changed: {reconcile.FirstError}");
            }

            // HEAD, then the working tree. A symbolic HEAD names the branch; a detached HEAD is
            // written with --no-deref so it is not followed into a branch move.
            if (head.Value.SymbolicTarget is { } branch)
                await RunCheckedAsync(sourceRepo, ["symbolic-ref", "HEAD", branch], RefTimeout, applying);
            else
                await RunCheckedAsync(sourceRepo, ["update-ref", "--no-deref", "HEAD", head.Value.Oid], RefTimeout, applying);

            var reset = await RunAsync(sourceRepo, ["reset", "--hard", head.Value.Oid], ResetTimeout, applying);
            if (!reset.Success)
                return new SwapResult(false,
                    $"refs reconciled but working-tree reset failed: {reset.FirstError}", changes, oldHead, head.Value.Oid);

            return new SwapResult(true, null, changes, oldHead.Length > 0 ? oldHead : null, head.Value.Oid);
        }
        finally
        {
            await DeleteScratchAsync(sourceRepo, scratch);
        }
    }

    /// <summary>The changes the reconciliation applies: every dropped non-remote ref deleted, every rewritten ref set. Unchanged refs are omitted.</summary>
    private static List<SwapRefChange> BuildRefChanges(
        IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> desired)
    {
        var changes = new List<SwapRefChange>();
        foreach (var (name, oid) in current)
            if (!desired.ContainsKey(name))
                changes.Add(new SwapRefChange(name, oid, null));
        foreach (var (name, oid) in desired)
        {
            var old = current.GetValueOrDefault(name);
            if (!string.Equals(old, oid, StringComparison.Ordinal))
                changes.Add(new SwapRefChange(name, old, oid));
        }
        return changes;
    }

    /// <summary>Every non-remote ref and its object id. Remote-tracking refs are excluded so the swap leaves them untouched on both sides.</summary>
    private async Task<Dictionary<string, string>?> ReadRefsAsync(string repo, CancellationToken ct)
    {
        var result = await RunAsync(repo, ["for-each-ref", "--format=%(objectname) %(refname)"], RefTimeout, ct);
        if (!result.Success)
            return null;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var name = line[(sp + 1)..];
            if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
                continue;
            map[name] = line[..sp];
        }
        return map;
    }

    private readonly record struct HeadState(string Oid, string? SymbolicTarget);

    /// <summary>
    /// Where the temp bare's HEAD points: the branch it names (attached) with that branch's
    /// oid, or the commit oid alone (detached). Null when HEAD cannot be resolved.
    /// </summary>
    private async Task<HeadState?> ReadHeadAsync(string repo, IReadOnlyDictionary<string, string> refs, CancellationToken ct)
    {
        var symref = await RunAsync(repo, ["symbolic-ref", "-q", "HEAD"], RefTimeout, ct);
        if (symref.Success)
        {
            var branch = symref.StdOut.Trim();
            // The branch HEAD names must exist among the rewritten refs, or resolving its tip
            // via rev-parse would silently fall through to whatever else that name matches.
            if (refs.TryGetValue(branch, out var branchOid))
                return new HeadState(branchOid, branch);
            return null;
        }

        var oid = await RunAsync(repo, ["rev-parse", "--verify", "-q", "HEAD"], RefTimeout, ct);
        if (!oid.Success || oid.StdOut.Trim().Length == 0)
            return null;
        return new HeadState(oid.StdOut.Trim(), null);
    }

    /// <summary>
    /// Characters a repo-relative path may occupy in <paramref name="sourceRepo"/>'s working tree.
    /// The budget depends on the checkout's own location, so it is read from the source rather than
    /// assumed; an unreadable toplevel or config falls back to the strict MAX_PATH reading, which
    /// refuses more than it must but never lets a path through that the reset would fail on.
    /// </summary>
    private async Task<int> CheckoutPathBudgetAsync(string sourceRepo, CancellationToken ct)
    {
        var longPaths = await RunAsync(sourceRepo, ["config", "--type=bool", "--get", "core.longpaths"], RefTimeout, ct);
        if (longPaths.Success && longPaths.StdOut.Trim() == "true")
            return int.MaxValue;

        var toplevel = await RunAsync(sourceRepo, ["rev-parse", "--show-toplevel"], RefTimeout, ct);
        var root = toplevel.Success ? toplevel.StdOut.Trim() : "";
        return WindowsPathGuard.BudgetFor(root.Length > 0 ? root : System.IO.Path.GetFullPath(sourceRepo), longPathsEnabled: false);
    }

    /// <summary>Scans every rewritten tree for a path a Windows checkout could never realize. Tag-of-blob/tree refs carry no tree and are skipped.</summary>
    private async Task<(string Path, string Reason)?> FirstUncheckoutablePathAsync(
        string tempBareRepo, IReadOnlyDictionary<string, string> refs, int pathBudget, CancellationToken ct)
    {
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var oid in refs.Values)
        {
            if (!scanned.Add(oid))
                continue;
            var lsTree = await RunAsync(tempBareRepo, ["ls-tree", "-r", "--name-only", oid], FsckTimeout, ct);
            // A ref that does not resolve to a tree-ish (an annotated tag of a blob) exits
            // non-zero with no paths; it carries nothing checkout-bound, so skip it.
            if (!lsTree.Success)
                continue;
            var paths = lsTree.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimEnd('\r'));
            if (WindowsPathGuard.FirstUncheckoutable(paths, pathBudget) is { } bad)
                return bad;
        }
        return null;
    }

    private async Task DeleteScratchAsync(string sourceRepo, string scratch)
    {
        try
        {
            var listed = await RunAsync(sourceRepo, ["for-each-ref", "--format=%(refname)", scratch], RefTimeout, CancellationToken.None);
            if (!listed.Success)
                return;
            var script = new StringBuilder();
            foreach (var raw in listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                script.Append("delete ").Append(raw.TrimEnd('\r')).Append('\n');
            if (script.Length > 0)
                await _git.RunWithInputAsync(
                    sourceRepo, ["update-ref", "--stdin"], script.ToString(), CancellationToken.None, RefTimeout);
        }
        catch (Exception ex)
        {
            // Leftover scratch refs are harmless hygiene, not a correctness problem: the real
            // refs already reference every object, so the swap outcome stands regardless.
            Log.Warn($"could not clear swap scratch refs '{scratch}' in {sourceRepo}", ex);
        }
    }

    /// <summary>A local path as a fetch URL. Forward slashes keep Git for Windows from reading a `C:` drive prefix as an scp-style host.</summary>
    private static string NativeUrl(string path) => System.IO.Path.GetFullPath(path).Replace('\\', '/');

    private Task<ProcessResult> RunAsync(string repo, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        _git.RunAsync(repo, args, ct, timeout);

    private async Task RunCheckedAsync(string repo, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await RunAsync(repo, args, timeout, ct);
        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in '{repo}': {result.FirstError}");
    }
}
