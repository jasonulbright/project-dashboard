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

    [ObservableProperty] private GitStatus _gitStatus = new();
    [ObservableProperty] private ProjectManifest _manifest = new();
    [ObservableProperty] private int _openIssueCount;
    [ObservableProperty] private List<GitCommit> _recentCommits = [];
    [ObservableProperty] private List<GitHubIssue> _issues = [];

    public int TaskCount => CountNotePrefix("TASK:");
    public int BugCount => CountNotePrefix("BUG:");
    public int WaitCount => CountNotePrefix("WAIT:");
    public int PlanCount => CountNotePrefix("PLAN:");

    private int CountNotePrefix(string prefix) =>
        string.IsNullOrEmpty(Manifest.Notes) ? 0 :
        Manifest.Notes.Split('\n').Count(l => l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public string GitHubSlug
    {
        get
        {
            if (string.IsNullOrEmpty(GitStatus.RemoteUrl)) return "";
            var url = GitStatus.RemoteUrl.Replace(".git", "");
            var parts = url.Split('/');
            return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : "";
        }
    }

    /// <summary>Repo name from the remote slug (e.g. "trackr" from "jasonulbright/trackr"), or "".</summary>
    public string RemoteRepoName
    {
        get
        {
            var slug = GitHubSlug;
            var idx = slug.LastIndexOf('/');
            return idx >= 0 && idx < slug.Length - 1 ? slug[(idx + 1)..] : "";
        }
    }

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
