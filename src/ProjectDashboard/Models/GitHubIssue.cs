namespace ProjectDashboard.Models;

public sealed class GitHubIssue
{
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public int Number { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
