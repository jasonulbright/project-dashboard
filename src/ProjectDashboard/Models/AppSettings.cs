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
    /// Whether the app may read the project's latest published release and compare it with
    /// this build. Off makes every path — launch and manual — read nothing and send nothing.
    /// </summary>
    public bool EnableUpdateCheck { get; set; } = true;

    /// <summary>When the last update check ran, whatever it concluded. Null until one has.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// What the last update check concluded, as it is reported on the Settings page. Persisted
    /// so the cooldown cannot hide a check that has been failing since an earlier session.
    /// </summary>
    public string LastUpdateCheckStatus { get; set; } = "";

    /// <summary>
    /// The release tag the last answer found newer than the build that recorded it, or empty
    /// when the last answer found none. A found release is a fact about the repository, not
    /// about the process that read it, so the notice survives a relaunch inside the cooldown.
    /// </summary>
    public string LastUpdateTagName { get; set; } = "";

    /// <summary>
    /// The link recorded alongside <see cref="LastUpdateTagName"/>. Editable text on disk that
    /// would reach the shell, so it is re-validated against the pinned releases path every time
    /// it is read back — never trusted because this app wrote it.
    /// </summary>
    public string LastUpdateReleaseUrl { get; set; } = "";

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
