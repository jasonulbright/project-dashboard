namespace ProjectDashboard.Models;

public sealed class ProjectManifest
{
    public string Description { get; set; } = "";
    public string ProjectType { get; set; } = "unknown";
    public string Status { get; set; } = "active";
    public string Category { get; set; } = "Uncategorized";
    public string ValidationSchedule { get; set; } = "none";
    public string Notes { get; set; } = "";
}
