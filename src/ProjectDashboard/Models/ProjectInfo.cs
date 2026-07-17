namespace ProjectDashboard.Models;

public partial class ProjectInfo : ObservableObject
{
    public string DirectoryName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool HasReadme { get; set; }
    public bool HasChangelog { get; set; }
    public bool HasManifest { get; set; }
    public string ReadmeContent { get; set; } = "";
    public string ChangelogContent { get; set; } = "";

    /// <summary>Set only by the Hidden view — never persisted; manifest Status stays untouched.</summary>
    [ObservableProperty] private bool _isHidden;

    /// <summary>True for a GitHub repo that isn't cloned locally (a "Cloud" card — no git status, offers Clone).</summary>
    public bool IsRemoteOnly { get; set; }
    /// <summary>owner/repo for a remote-only entry (drives Clone + browser links).</summary>
    public string RemoteSlug { get; set; } = "";

    [ObservableProperty] private GitStatus _gitStatus = new();
    [ObservableProperty] private ProjectManifest _manifest = new();
    // Null = "couldn't fetch" — rendered as absent, never as zero.
    [ObservableProperty] private int? _openIssueCount;
    [ObservableProperty] private int? _openPrCount;
    [ObservableProperty] private List<GitCommit> _recentCommits = [];
    [ObservableProperty] private List<GitHubIssue> _issues = [];

    public int TaskCount => CountNotePrefix("TASK:");
    public int BugCount => CountNotePrefix("BUG:");
    public int WaitCount => CountNotePrefix("WAIT:");
    public int PlanCount => CountNotePrefix("PLAN:");

    private int CountNotePrefix(string prefix) =>
        string.IsNullOrEmpty(Manifest.Notes) ? 0 :
        Manifest.Notes.Split('\n').Count(l => l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>"owner/repo" when origin is a github.com remote; "" otherwise (non-GitHub hosts get no GitHub links or gh calls).</summary>
    public string GitHubSlug
    {
        get
        {
            if (IsRemoteOnly) return RemoteSlug;
            var remote = GitRemote.Parse(GitStatus.RemoteUrl);
            return remote is { IsGitHub: true } ? $"{remote.Owner}/{remote.Repo}" : "";
        }
    }

    /// <summary>Repo name from the origin URL on ANY host (e.g. "trackr"), or "".</summary>
    public string RemoteRepoName => GitRemote.Parse(GitStatus.RemoteUrl)?.Repo ?? "";

    /// <summary>
    /// True when a remote exists but its repo name doesn't match the local folder name
    /// (e.g. trackr's origin pointing at app-packager). No remote = no mismatch.
    /// </summary>
    public bool HasRemoteMismatch =>
        !string.IsNullOrEmpty(RemoteRepoName) &&
        !string.Equals(RemoteRepoName, DirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when there's no stored manifest or its Description is blank.</summary>
    public bool HasIncompleteMetadata =>
        !HasManifest || string.IsNullOrWhiteSpace(Manifest.Description);
}
