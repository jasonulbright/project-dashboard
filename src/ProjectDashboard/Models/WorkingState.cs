namespace ProjectDashboard.Models;

/// <summary>What the repo is in the middle of, beyond plain edits.</summary>
public enum RepoActivity
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
    Bisecting
}

/// <summary>One changed path from `git status --porcelain=v2`.</summary>
public sealed class WorkingFile
{
    public string Path { get; init; } = "";
    /// <summary>Rename/copy source, when applicable.</summary>
    public string? OrigPath { get; init; }
    /// <summary>Staged (index) status letter; '.' = unchanged in index.</summary>
    public char IndexStatus { get; init; } = '.';
    /// <summary>Unstaged (worktree) status letter; '.' = unchanged in worktree.</summary>
    public char WorktreeStatus { get; init; } = '.';
    public bool IsUntracked { get; init; }
    public bool IsConflicted { get; init; }

    public bool HasStagedChange => !IsUntracked && !IsConflicted && IndexStatus != '.';
    public bool HasUnstagedChange => IsUntracked || (!IsConflicted && WorktreeStatus != '.');

    /// <summary>Single letter for list display: staged column or worktree column.</summary>
    public string StagedLabel => IsConflicted ? "!" : IndexStatus.ToString();
    public string UnstagedLabel => IsConflicted ? "!" : IsUntracked ? "U" : WorktreeStatus.ToString();
}

/// <summary>
/// Full working-tree state from ONE `git status --porcelain=v2 --branch` call:
/// branch, upstream, ahead/behind, and per-file staged/unstaged/untracked/conflicted.
/// </summary>
public sealed class WorkingState
{
    public string Branch { get; set; } = "";
    public bool Detached { get; set; }
    public bool NoCommitsYet { get; set; }
    public string Upstream { get; set; } = "";
    public bool HasUpstream => Upstream.Length > 0;
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public List<WorkingFile> Files { get; } = [];
    public RepoActivity Activity { get; set; } = RepoActivity.None;

    public IEnumerable<WorkingFile> Staged => Files.Where(f => f.HasStagedChange);
    public IEnumerable<WorkingFile> Unstaged => Files.Where(f => f.HasUnstagedChange);
    public IEnumerable<WorkingFile> Conflicted => Files.Where(f => f.IsConflicted);
    public bool HasConflicts => Files.Any(f => f.IsConflicted);
    public bool IsDirty => Files.Count > 0;

    /// <summary>
    /// Parses `git status --porcelain=v2 --branch` output (core.quotepath=false, so
    /// paths arrive as raw UTF-8; v2 never shell-quotes paths).
    /// </summary>
    public static WorkingState Parse(string porcelainV2)
    {
        var state = new WorkingState();

        foreach (var raw in porcelainV2.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = line["# branch.head ".Length..];
                state.Detached = head == "(detached)";
                state.Branch = state.Detached ? "" : head;
                continue;
            }
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                state.NoCommitsYet = line["# branch.oid ".Length..] == "(initial)";
                continue;
            }
            if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                state.Upstream = line["# branch.upstream ".Length..];
                continue;
            }
            if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                // "+<ahead> -<behind>"
                var parts = line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (p.StartsWith('+') && int.TryParse(p[1..], out var a)) state.Ahead = a;
                    else if (p.StartsWith('-') && int.TryParse(p[1..], out var b)) state.Behind = b;
                }
                continue;
            }
            if (line.StartsWith('#')) continue;

            switch (line[0])
            {
                case '1': // ordinary: 1 XY sub mH mI mW hH hI path
                {
                    var fields = line.Split(' ', 9);
                    if (fields.Length < 9) break;
                    state.Files.Add(new WorkingFile
                    {
                        IndexStatus = fields[1][0],
                        WorktreeStatus = fields[1][1],
                        Path = fields[8]
                    });
                    break;
                }
                case '2': // rename/copy: 2 XY sub mH mI mW hH hI Xscore path<TAB>origPath
                {
                    var fields = line.Split(' ', 10);
                    if (fields.Length < 10) break;
                    var pathPair = fields[9].Split('\t');
                    state.Files.Add(new WorkingFile
                    {
                        IndexStatus = fields[1][0],
                        WorktreeStatus = fields[1][1],
                        Path = pathPair[0],
                        OrigPath = pathPair.Length > 1 ? pathPair[1] : null
                    });
                    break;
                }
                case 'u': // unmerged: u XY sub m1 m2 m3 mW h1 h2 h3 path
                {
                    var fields = line.Split(' ', 11);
                    if (fields.Length < 11) break;
                    state.Files.Add(new WorkingFile
                    {
                        IndexStatus = fields[1][0],
                        WorktreeStatus = fields[1][1],
                        Path = fields[10],
                        IsConflicted = true
                    });
                    break;
                }
                case '?':
                    state.Files.Add(new WorkingFile { Path = line[2..], IsUntracked = true });
                    break;
                // '!' (ignored) never requested
            }
        }

        return state;
    }
}
