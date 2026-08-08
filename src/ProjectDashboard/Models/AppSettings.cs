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

    /// <summary>Surface the user's GitHub repos that aren't cloned locally as "Cloud" cards.</summary>
    public bool EnableGitHubDiscovery { get; set; } = true;

    /// <summary>Refresh a repo's card automatically when its working tree changes on disk.</summary>
    public bool EnableAutoRefresh { get; set; } = true;

    /// <summary>Per-repo backups retained before a history rewrite prunes the oldest.</summary>
    public int BackupRetentionCount { get; set; } = 10;

    /// <summary>Gate for the destructive GitHub-admin surface; off until the user opts in.</summary>
    public bool DangerZoneEnabled { get; set; }

    /// <summary>
    /// Full paths of repos pinned to the front of the card grid. Paths, not folder
    /// names: two roots can hold folders of the same name.
    /// </summary>
    public string[] PinnedProjectPaths { get; set; } = [];

    /// <summary>Card density: "compact" tightens padding and minimum height; anything else is comfortable.</summary>
    public string CardDensity { get; set; } = "comfortable";

    /// <summary>Render diffs in two columns rather than as one unified list.</summary>
    public bool DiffSideBySide { get; set; }

    /// <summary>
    /// Saved window rect in device pixels. Null in a settings file written before this
    /// field existed, where the per-monitor DIP fields below hold the rect instead.
    /// </summary>
    public SavedWindowRect? WindowDeviceRect { get; set; }

    // Window rect in the closing monitor's DIPs, read only to migrate a settings file
    // that predates WindowDeviceRect. -1/-1 is the never-saved default.
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1621;
    public double WindowHeight { get; set; } = 823;

    public bool WindowMaximized { get; set; }
    public bool PaneOpen { get; set; } = true;
}

/// <summary>A window rect in device pixels: the unit monitor rectangles are also in.</summary>
public sealed record SavedWindowRect(int Left, int Top, int Width, int Height);
