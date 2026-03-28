namespace ProjectDashboard.Models;

public sealed class GitCommit
{
    public string ShortHash { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string Message { get; set; } = "";
}
