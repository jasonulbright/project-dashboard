using ProjectDashboard.Services.Safety;

namespace ProjectDashboard.Services.Rewrite;

/// <summary>
/// One local branch that holds commits its remote counterpart does not AND whose remote
/// counterpart holds commits it does not, so publishing it can only replace what the remote has.
/// <see cref="LeaseOid"/> is the remote-tracking ref's current value — the newest position this
/// repository has observed for <see cref="RemoteRef"/> — and is the whole of the lease's basis.
///
/// <see cref="BranchName"/> and <see cref="RemoteRef"/> can name different branches:
/// branch.&lt;name&gt;.merge sets what a branch tracks, and it need not carry the branch's own name.
/// <see cref="RemoteDisplayName"/> is therefore the only form that names the ref a push replaces.
/// </summary>
public sealed record DivergedBranch(
    string BranchName,
    string LocalRef,
    string LocalOid,
    string Remote,
    string RemoteRef,
    string TrackingRef,
    string LeaseOid,
    int Ahead,
    int Behind)
{
    public string RemoteDisplayName => $"{Remote}/{ForcePushService.ShortBranchName(RemoteRef)}";
}

/// <summary>
/// What a force-push would cover. <see cref="Diverged"/> is the whole of it; the other three lists
/// exist so the surface can say why a branch is absent instead of leaving its absence to be
/// inferred. <see cref="AheadOnly"/> branches need no force and this flow does not push them;
/// <see cref="BehindOnly"/> branches hold nothing the remote lacks, so forcing one would drop the
/// remote's commits and publish nothing; <see cref="UpstreamGone"/> branches have no
/// remote-tracking ref left, so there is no lease to take and nothing to overwrite.
/// </summary>
public sealed record ForcePushPlan(
    IReadOnlyList<DivergedBranch> Diverged,
    IReadOnlyList<string> AheadOnly,
    IReadOnlyList<string> BehindOnly,
    IReadOnlyList<string> UpstreamGone,
    string? Refusal)
{
    public static ForcePushPlan Refused(string reason) => new([], [], [], [], reason);
}

/// <summary>One ref's push outcome. <see cref="LeaseRejected"/> means the remote had moved, so nothing on it was replaced.</summary>
public sealed record ForcePushRefOutcome(string BranchName, bool Success, bool LeaseRejected, string Detail);

/// <summary>
/// The whole force-push. <see cref="Success"/> is true only when every ref landed; a refusal
/// before any push carries <see cref="RefusalReason"/> and an empty <see cref="Refs"/>.
/// </summary>
public sealed record ForcePushOutcome(bool Success, string? RefusalReason, IReadOnlyList<ForcePushRefOutcome> Refs)
{
    public static ForcePushOutcome Refused(string reason) => new(false, reason, []);
}

/// <summary>
/// The only path by which this app replaces history on a remote. Nothing here runs on its own:
/// a caller builds a plan, shows it, takes a typed confirmation, and only then calls
/// <see cref="PushAsync"/>.
///
/// Every push is <c>--force-with-lease=&lt;remote ref&gt;:&lt;expected&gt;</c> with the expected value
/// stated outright rather than left to git's implicit form, so the object id the surface showed
/// is the object id the push depends on. Plain <c>--force</c> is never issued and a rejected
/// lease is never retried: a lease fails only because the remote is somewhere this repository has
/// never seen, which is the case force exists to overwrite.
///
/// One git invocation per ref: a lease that fails then names the branch it failed for, and the
/// branches beside it are neither blocked by it nor silently carried along with it.
/// </summary>
public class ForcePushService
{
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RefTimeout = TimeSpan.FromSeconds(30);

    private readonly GitService _git;
    private readonly RepoBusyRegistry _busy;

    public ForcePushService(GitService git, RepoBusyRegistry busy)
    {
        _git = git;
        _busy = busy;
    }

    /// <summary>
    /// What publishing this repository's local branches would replace on their remotes. A branch
    /// is included only when it holds commits its remote-tracking ref does not AND that ref holds
    /// commits the branch does not — the exact condition under which a plain push is rejected and
    /// only a force can land. A branch that is merely behind is excluded: forcing one would drop
    /// the remote's commits and publish nothing of its own.
    ///
    /// Divergence is read from the refs on disk, so nothing here contacts a remote: the answer
    /// describes the remote as of this repository's last fetch, which is also what the lease is
    /// based on. Tags are outside this entirely — a rewrite rewrites them too, but replacing a
    /// published tag is its own decision and this flow neither offers nor makes it.
    /// </summary>
    public virtual async Task<ForcePushPlan> PlanAsync(string repoPath, CancellationToken ct = default)
    {
        if (repoPath.Length == 0 || !GitService.IsGitRepo(repoPath))
            return ForcePushPlan.Refused($"'{repoPath}' is not a git repository.");

        var format = string.Join(GitService.FieldSeparator,
            "%(refname)", "%(objectname)", "%(upstream)", "%(upstream:remotename)",
            "%(upstream:remoteref)", "%(upstream:track)");
        var result = await _git.RunAsync(repoPath,
            ["for-each-ref", "refs/heads", "--format=" + format], ct, RefTimeout);
        if (!result.Success)
            return ForcePushPlan.Refused($"Could not read this repository's branches: {result.FirstError}");

        var diverged = new List<DivergedBranch>();
        var aheadOnly = new List<string>();
        var behindOnly = new List<string>();
        var gone = new List<string>();

        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split(GitService.FieldSeparator);
            if (parts.Length < 6) continue;

            var localRef = parts[0];
            var localOid = parts[1];
            var trackingRef = parts[2];
            var remote = parts[3];
            var remoteRef = parts[4];
            var track = parts[5];
            var name = ShortBranchName(localRef);

            // No upstream at all: this flow publishes over something, and there is nothing here
            // for it to publish over.
            if (trackingRef.Length == 0 || remote.Length == 0 || remoteRef.Length == 0) continue;

            if (track.Contains("gone", StringComparison.OrdinalIgnoreCase))
            {
                gone.Add(name);
                continue;
            }

            var (ahead, behind) = ParseTrack(track);
            if (behind == 0)
            {
                // Ahead-only or identical: a plain push either fast-forwards or has nothing to do.
                if (ahead > 0) aheadOnly.Add(name);
                continue;
            }
            if (ahead == 0)
            {
                // Behind and nothing else. A force here publishes no commit this branch holds and
                // removes every commit it is behind by.
                behindOnly.Add(name);
                continue;
            }

            var lease = await _git.RunAsync(repoPath, ["rev-parse", "--verify", "-q", trackingRef], ct, RefTimeout);
            var leaseOid = lease.StdOut.Trim();
            if (!lease.Success || leaseOid.Length == 0)
            {
                // The track field named a remote-tracking ref that no longer resolves. Without its
                // object id there is no lease to state, and a push without one is a plain force.
                gone.Add(name);
                continue;
            }

            diverged.Add(new DivergedBranch(
                name, localRef, localOid, remote, remoteRef, trackingRef, leaseOid, ahead, behind));
        }

        diverged.Sort((a, b) => string.CompareOrdinal(a.BranchName, b.BranchName));
        aheadOnly.Sort(StringComparer.Ordinal);
        behindOnly.Sort(StringComparer.Ordinal);
        gone.Sort(StringComparer.Ordinal);
        return new ForcePushPlan(diverged, aheadOnly, behindOnly, gone, null);
    }

    /// <summary>
    /// Publishes exactly the branches handed in, each under its own lease. The lease values are
    /// the caller's — the ones the reader was shown and agreed to — so a fetch between the plan
    /// and this call makes the push fail rather than silently overwrite whatever that fetch
    /// brought in.
    ///
    /// The source of each refspec is the object id the plan captured, not the local ref: a commit
    /// or a reset landing between the plan and this call would otherwise publish history the
    /// report does not describe.
    ///
    /// Holds the repository lease for the whole run: a rewrite or a restore landing between two
    /// of these pushes would publish half of one history and half of another.
    /// </summary>
    public virtual async Task<ForcePushOutcome> PushAsync(
        string repoPath, IReadOnlyList<DivergedBranch> branches, CancellationToken ct = default)
    {
        if (branches.Count == 0)
            return ForcePushOutcome.Refused("Nothing to push — no branch here differs from its remote counterpart.");
        if (!_busy.TryAcquire(repoPath, out var lease))
            return ForcePushOutcome.Refused($"Repository is busy with another operation: {repoPath}");

        using (lease)
        {
            var outcomes = new List<ForcePushRefOutcome>();
            foreach (var branch in branches)
            {
                var push = await _git.RunAsync(repoPath,
                [
                    "push", branch.Remote,
                    $"--force-with-lease={branch.RemoteRef}:{branch.LeaseOid}",
                    $"{branch.LocalOid}:{branch.RemoteRef}"
                ], ct, NetworkTimeout);

                if (push.Success)
                {
                    outcomes.Add(new ForcePushRefOutcome(branch.BranchName, true, false,
                        $"{branch.RemoteDisplayName} now holds {Short(branch.LocalOid)}; " +
                        $"{branch.Behind} commit(s) it had are no longer on it."));
                    continue;
                }

                var stale = IsLeaseRejection(push);
                outcomes.Add(new ForcePushRefOutcome(branch.BranchName, false, stale,
                    stale
                        ? $"Refused: {branch.RemoteDisplayName} is no longer at {Short(branch.LeaseOid)}, " +
                          "so someone moved it after this repository last fetched. Nothing on the remote was replaced. " +
                          "Fetch and look at what landed before deciding again."
                        : $"Failed: {push.FirstError}"));
            }

            var failed = outcomes.Where(o => !o.Success).ToList();
            return new ForcePushOutcome(failed.Count == 0, null, outcomes);
        }
    }

    /// <summary>
    /// Whether the push was refused by its lease rather than by anything else. git reports a
    /// broken lease as "stale info" on the rejected ref line; every other rejection is a different
    /// problem, and conflating them would present a permission failure as a moved remote.
    /// </summary>
    public static bool IsLeaseRejection(ProcessResult push) =>
        (push.StdErr + push.StdOut).Contains("stale info", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ahead/behind counts inside an upstream:track field; zeros when it names neither.</summary>
    internal static (int Ahead, int Behind) ParseTrack(string track)
    {
        int ahead = 0, behind = 0;
        foreach (var segment in track.Trim('[', ']').Split(','))
        {
            var s = segment.Trim();
            if (s.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(s[6..], out var a)) ahead = a;
            else if (s.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(s[7..], out var b)) behind = b;
        }
        return (ahead, behind);
    }

    internal static string ShortBranchName(string fullRef) =>
        fullRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? fullRef["refs/heads/".Length..] : fullRef;

    internal static string Short(string oid) => oid.Length > 8 ? oid[..8] : oid;
}
