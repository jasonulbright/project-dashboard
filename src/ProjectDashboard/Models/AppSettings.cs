using System.Text.Json.Serialization;

namespace ProjectDashboard.Models;

public sealed class AppSettings
{
    public string ProjectsRootPath { get; set; } = @"C:\projects";
    public int RefreshIntervalSeconds { get; set; } = 7200;
    public string Theme { get; set; } = "Dark";
    public string[] ExcludedDirectories { get; set; } = ["Internal", "games"];

    /// <summary>Optional explicit path to gh.exe (file or its folder). Empty = resolve via PATH / known locations.</summary>
    public string GhPath { get; set; } = "";

    /// <summary>Surface the user's GitHub repos that aren't cloned locally as "Cloud" cards (ROADMAP v1.1).</summary>
    public bool EnableGitHubDiscovery { get; set; } = true;

    // Window state
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1621;
    public double WindowHeight { get; set; } = 823;
    public bool WindowMaximized { get; set; }
    public bool PaneOpen { get; set; } = true;
}
