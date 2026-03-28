using System.Text.Json.Serialization;

namespace ProjectDashboard.Models;

public sealed class AppSettings
{
    public string ProjectsRootPath { get; set; } = @"C:\projects";
    public int RefreshIntervalSeconds { get; set; } = 300;
    public string Theme { get; set; } = "Dark";
    public string[] ExcludedDirectories { get; set; } = ["sccmclictr-1.0.7.2", "sccmclictrlib.1.0.1", "Internal", "games"];
}
