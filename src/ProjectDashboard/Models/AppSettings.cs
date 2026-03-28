using System.Text.Json.Serialization;

namespace ProjectDashboard.Models;

public sealed class AppSettings
{
    public string ProjectsRootPath { get; set; } = @"C:\projects";
    public int RefreshIntervalSeconds { get; set; } = 7200;
    public string Theme { get; set; } = "Dark";
    public string[] ExcludedDirectories { get; set; } = ["Internal", "games"];
}
