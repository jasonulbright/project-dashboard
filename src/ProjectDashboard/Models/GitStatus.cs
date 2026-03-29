namespace ProjectDashboard.Models;

public sealed class GitStatus
{
    public string Branch { get; set; } = "";
    public bool IsDirty { get; set; }
    public int ModifiedCount { get; set; }
    public int UntrackedCount { get; set; }
    public int AheadBy { get; set; }
    public string LatestTag { get; set; } = "";
    public DateTimeOffset? LastCommitDate { get; set; }
    public string LastCommitMessage { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string Visibility { get; set; } = "local"; // "public", "private", "local"
}
