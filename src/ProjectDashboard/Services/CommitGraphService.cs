using ProjectDashboard.Models;

namespace ProjectDashboard.Services;

/// <summary>
/// The commit DAG a graph view renders: one topologically ordered page of commits with
/// parent edges, decorations, and a column per commit. One `git log` per request — no
/// per-commit process launches.
/// </summary>
public sealed class CommitGraphService
{
    private static readonly TimeSpan GraphTimeout = TimeSpan.FromSeconds(60);

    // 0x1f separates fields: it cannot appear in a sha, a ref name, or an ISO date, and a
    // subject carrying one still parses because the subject field is taken as the remainder.
    private const string LogFormat = "--format=%H%x1f%h%x1f%P%x1f%an%x1f%aI%x1f%D%x1f%s";

    private readonly GitService _git;

    public CommitGraphService(GitService git) => _git = git;

    /// <summary>
    /// One page of the graph. Lanes are computed over the walk from the ref tips down to
    /// the end of the requested page, so a commit keeps the same lane in every page that
    /// contains it; the cost of page N is proportional to Skip + Take, not to Take alone.
    /// </summary>
    public async Task<CommitGraphPage> GetGraphAsync(string repoPath, CommitGraphRequest? request = null,
        CancellationToken ct = default)
    {
        request ??= new CommitGraphRequest();
        var skip = request.NormalizedSkip;
        var take = request.NormalizedTake;

        // One extra commit answers HasMore without a second walk.
        var walk = (long)skip + take + 1;

        var args = new List<string>
        {
            "log", "--topo-order", "--decorate=full", LogFormat, "-n", walk.ToString()
        };
        // A ref may legally be named "--all", "-5", or "-g"; as bare argv git reads it as an
        // option and silently widens, truncates, or repurposes the walk, so caller-supplied
        // revisions follow --end-of-options. --ignore-missing applies only to the default
        // set, where an unborn HEAD (empty repo, orphan checkout) is a state rather than a
        // failure; a revision the caller named must still fail when it does not resolve.
        if (!string.IsNullOrWhiteSpace(request.Branch)) { args.Add("--end-of-options"); args.Add(request.Branch); }
        else if (request.Refs is { Count: > 0 }) { args.Add("--end-of-options"); args.AddRange(request.Refs); }
        else { args.Add("--ignore-missing"); args.Add("--branches"); args.Add("HEAD"); }
        // Terminator: a ref name that also names a file is otherwise ambiguous to git.
        args.Add("--");

        var result = await _git.RunAsync(repoPath, args, ct, GraphTimeout);
        if (!result.Success)
        {
            Log.Warn($"git log --topo-order failed for {repoPath}: {result.FirstError}");
            return new CommitGraphPage { Skip = skip, HasError = true };
        }

        var ordered = ParseLog(result.StdOut);
        AssignLanes(ordered);
        return BuildPage(ordered, skip, take);
    }

    /// <summary>
    /// Slices a lane-assigned walk into one page: the rows, the lane state entering the
    /// first of them, and the column count the two together demand.
    /// <para>
    /// OpenLanes is post-row state, so the lanes entering the page come from the row
    /// BEFORE it. Counting only in-page rows hides every lane that closes at the first row
    /// and every edge crossing the page's top, which no page after the first can draw.
    /// </para>
    /// </summary>
    internal static CommitGraphPage BuildPage(IReadOnlyList<GraphCommit> ordered, int skip, int take)
    {
        var hasMore = ordered.Count > (long)skip + take;
        var page = new List<GraphCommit>();
        for (var i = skip; i < ordered.Count && page.Count < take; i++)
            page.Add(ordered[i]);

        IReadOnlyList<int> incoming = skip > 0 && skip <= ordered.Count ? ordered[skip - 1].OpenLanes : [];

        var laneCount = 0;
        foreach (var lane in incoming) laneCount = Math.Max(laneCount, lane + 1);
        foreach (var commit in page)
        {
            laneCount = Math.Max(laneCount, commit.Lane + 1);
            foreach (var lane in commit.OpenLanes) laneCount = Math.Max(laneCount, lane + 1);
        }

        return new CommitGraphPage
        {
            Commits = page,
            HasMore = hasMore,
            Skip = skip,
            IncomingLanes = incoming,
            LaneCount = laneCount
        };
    }

    internal static List<GraphCommit> ParseLog(string log)
    {
        var commits = new List<GraphCommit>();
        foreach (var raw in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Count 7 so a separator inside the subject stays part of the subject.
            var f = raw.TrimEnd('\r').Split('\u001f', 7);
            if (f.Length < 7) continue;
            commits.Add(new GraphCommit
            {
                Sha = f[0],
                ShortSha = f[1],
                Parents = f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Author = f[3],
                Date = DateTimeOffset.TryParse(f[4], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : default,
                Refs = ParseDecoration(f[5]),
                Subject = f[6]
            });
        }
        return commits;
    }

    /// <summary>
    /// Parses %D under --decorate=full: ", "-separated full ref names, with the current
    /// branch written "HEAD -&gt; refs/heads/main". Full names are what makes a local branch
    /// distinguishable from a remote-tracking branch whose short name looks the same.
    /// A ref name can contain no space, so the separator is unambiguous.
    /// </summary>
    internal static List<GraphRef> ParseDecoration(string decoration)
    {
        var refs = new List<GraphRef>();
        if (decoration.Length == 0) return refs;

        foreach (var part in decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;

            var arrow = item.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                refs.Add(new GraphRef(GraphRefKind.Head, item[..arrow]));
                item = item[(arrow + 4)..];
            }

            if (item == "HEAD") refs.Add(new GraphRef(GraphRefKind.Head, "HEAD"));
            else if (item.StartsWith("tag: refs/tags/", StringComparison.Ordinal))
                refs.Add(new GraphRef(GraphRefKind.Tag, item["tag: refs/tags/".Length..]));
            else if (item.StartsWith("refs/tags/", StringComparison.Ordinal))
                refs.Add(new GraphRef(GraphRefKind.Tag, item["refs/tags/".Length..]));
            else if (item.StartsWith("refs/heads/", StringComparison.Ordinal))
                refs.Add(new GraphRef(GraphRefKind.LocalBranch, item["refs/heads/".Length..]));
            else if (item.StartsWith("refs/remotes/", StringComparison.Ordinal))
                refs.Add(new GraphRef(GraphRefKind.RemoteBranch, item["refs/remotes/".Length..]));
            else refs.Add(new GraphRef(GraphRefKind.Other, item));
        }
        return refs;
    }

    /// <summary>
    /// Assigns each commit a lane and records the lanes still open after its row.
    /// <para>
    /// Invariant: a commit's lane is a function of the topologically ordered prefix ending
    /// at that commit, and of nothing after it. Lanes are therefore stable across pages of
    /// one ref set, and identical between two runs over the same DAG — provided the walk
    /// always starts at the ref tips, which is why paging re-walks from the tips instead of
    /// handing git a --skip.
    /// </para>
    /// A lane holds the sha it expects next. A commit claims the lane already expecting it,
    /// else the leftmost free lane, else a new lane on the right; any further lane expecting
    /// the same sha collapses into that one. The commit's first parent inherits the lane and
    /// each additional parent takes a lane of its own unless one already expects it.
    /// </summary>
    internal static void AssignLanes(IReadOnlyList<GraphCommit> ordered)
    {
        var lanes = new List<string?>();

        foreach (var commit in ordered)
        {
            var lane = lanes.IndexOf(commit.Sha);
            if (lane < 0)
            {
                lane = lanes.IndexOf(null);
                if (lane < 0) { lanes.Add(null); lane = lanes.Count - 1; }
            }
            for (var i = lane + 1; i < lanes.Count; i++)
                if (lanes[i] == commit.Sha) lanes[i] = null;

            commit.Lane = lane;

            lanes[lane] = commit.Parents.Count > 0 ? commit.Parents[0] : null;
            for (var p = 1; p < commit.Parents.Count; p++)
            {
                var parent = commit.Parents[p];
                if (lanes.Contains(parent)) continue;
                var free = lanes.IndexOf(null);
                if (free < 0) lanes.Add(parent);
                else lanes[free] = parent;
            }

            // Trailing free lanes are dropped so a finished branch does not widen every
            // later row.
            while (lanes.Count > 0 && lanes[^1] is null) lanes.RemoveAt(lanes.Count - 1);

            var open = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
                if (lanes[i] is not null) open.Add(i);
            commit.OpenLanes = open;
        }
    }
}
