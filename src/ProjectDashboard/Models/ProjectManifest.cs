namespace ProjectDashboard.Models;

public sealed class ProjectManifest
{
    public string Description { get; set; } = "";
    public string ProjectType { get; set; } = "unknown";
    public string Status { get; set; } = "active";
    public string Category { get; set; } = "Uncategorized";
    public string ValidationSchedule { get; set; } = "none";
    public string Notes { get; set; } = "";

    /// <summary>
    /// A detached copy. No caller ever holds a reference into the stored index: a mutated shared
    /// instance would persist on the next unrelated save.
    /// </summary>
    public ProjectManifest Copy() => new()
    {
        Description = Description,
        ProjectType = ProjectType,
        Status = Status,
        Category = Category,
        ValidationSchedule = ValidationSchedule,
        Notes = Notes,
    };
}
