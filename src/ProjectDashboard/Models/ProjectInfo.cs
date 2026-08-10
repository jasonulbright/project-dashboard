namespace ProjectDashboard.Models;

public partial class ProjectInfo : ObservableObject
{
    public string DirectoryName { get; set; } = "";
    public string FullPath { get; set; } = "";

    /// <summary>
    /// The configured root this repository was found under; empty for a remote-only card, which
    /// was found under none. Two roots can hold folders of the same name, so the surfaces that
    /// group, hide, or report per root need the root as well as the path.
    /// </summary>
    public string RootPath { get; set; } = "";

    /// <summary>
    /// Where this repository is, shown only when another discovered repository carries the same
    /// display name. Two identical-looking cards for two different working trees is the failure
    /// recursion and multiple roots both make ordinary.
    /// </summary>
    [ObservableProperty] private string _locationHint = "";

    public bool HasLocationHint => LocationHint.Length > 0;

    partial void OnLocationHintChanged(string value) => OnPropertyChanged(nameof(HasLocationHint));

    /// <summary>
    /// What the scan read this repository to be, carried so a metadata write can say which
    /// repository it means and not merely which folder it was opened from. Null for a remote-only
    /// card, and for one whose identity the scan did not read.
    /// </summary>
    public RepoFingerprint? Fingerprint { get; set; }

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

    /// <summary>
    /// Mirrors the pinned-paths setting for the card glyph and ordering. Re-applied
    /// from settings after every load: the discovery cache can carry a stale value.
    /// </summary>
    [ObservableProperty] private bool _isPinned;

    /// <summary>Ticked in the grid's selection mode. Never persisted — a selection is one action long.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// What the card draws for each metadata field, resolved against the reader's lists rather
    /// than matched in markup against literal values: a renamed value would stop matching a
    /// literal without anything reporting that the chip had gone plain.
    /// </summary>
    [ObservableProperty] private TaxonomyBadge _statusBadge = TaxonomyBadge.Hidden;
    [ObservableProperty] private TaxonomyBadge _categoryBadge = TaxonomyBadge.Hidden;
    [ObservableProperty] private TaxonomyBadge _typeBadge = TaxonomyBadge.Hidden;
    [ObservableProperty] private TaxonomyBadge _scheduleBadge = TaxonomyBadge.Hidden;

    /// <summary>True for a GitHub repo that isn't cloned locally (a "Cloud" card — no git status, offers Clone).</summary>
    public bool IsRemoteOnly { get; set; }
    /// <summary>owner/repo for a remote-only entry (drives Clone + browser links).</summary>
    public string RemoteSlug { get; set; } = "";

    [ObservableProperty] private GitStatus _gitStatus = new();

    /// <summary>
    /// When this repository's remote was last actually read, or why the background fetch has
    /// parked it. Ahead/behind counts age between fetches, and a count with no timestamp reads
    /// as live; empty when no fetch has ever been recorded.
    /// </summary>
    [ObservableProperty] private string _syncFreshnessText = "";

    /// <summary>The ahead/behind affordance carries the staleness a bare count would hide.</summary>
    public string AheadBehindToolTip =>
        SyncFreshnessText.Length == 0 ? "Open the Branches tab" : $"Open the Branches tab. {SyncFreshnessText}";

    partial void OnSyncFreshnessTextChanged(string value) => OnPropertyChanged(nameof(AheadBehindToolTip));
    [ObservableProperty] private ProjectManifest _manifest = new();
    // Null = "couldn't fetch" — rendered as absent, never as zero.
    [ObservableProperty] private int? _openIssueCount;
    [ObservableProperty] private int? _openPrCount;
    [ObservableProperty] private List<GitCommit> _recentCommits = [];
    [ObservableProperty] private List<GitHubIssue> _issues = [];

    partial void OnGitStatusChanged(GitStatus value) => OnPropertyChanged(nameof(AccessibleName));

    /// <summary>
    /// What a reader is handed for the card. A repository with no local clone has no branch and
    /// no working tree, and one whose status could not be read has neither measured — a name that
    /// pastes those empty values into a fixed sentence reports a branch and a change count that
    /// were never observed.
    /// </summary>
    public string AccessibleName =>
        IsRemoteOnly ? $"{DisplayName}, not cloned"
        : GitStatus.HasError ? $"{DisplayName}, status unavailable"
        : $"{DisplayName}{GitStatus.BranchSuffix}, {GitStatus.TotalChanges} uncommitted{GitStatus.AheadBehindSuffix}";

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
