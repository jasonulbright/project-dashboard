namespace ProjectDashboard.Models;

/// <summary>
/// One configured place to look for repositories. Order is meaningful: it is the order roots
/// appear on the Settings page, and the tie-break when two roots hold a repository of the
/// same name.
/// </summary>
public sealed class ProjectRoot
{
    public string Path { get; set; } = "";

    /// <summary>Optional display name; the folder name stands in when blank.</summary>
    public string Label { get; set; } = "";

    /// <summary>Off means not scanned and not watched, and still listed in Settings.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Paths and names skipped under THIS root. An entry carrying a separator is matched as a
    /// root-relative path prefix; one without is matched as a directory name at any depth.
    /// </summary>
    public string[] ExcludedDirectories { get; set; } = [];

    /// <summary>Directory levels below the root the scan descends. 1 is the root's own children.</summary>
    public int MaxDepth { get; set; } = ProjectRootSettings.MinDepth;

    public ProjectRoot Copy() => new()
    {
        Path = Path,
        Label = Label,
        Enabled = Enabled,
        ExcludedDirectories = [.. ExcludedDirectories],
        MaxDepth = MaxDepth
    };
}

/// <summary>What the last scan found the root to be. Never inferred from an empty result.</summary>
public enum RootAvailability
{
    Available,

    /// <summary>The path is not there — an unplugged drive, a folder that moved.</summary>
    Missing,

    /// <summary>The path is there and could not be read — a denied ACL, a dropped share.</summary>
    Unreadable,

    /// <summary>Switched off by the user, so nothing was read and nothing is claimed.</summary>
    Disabled,
}

/// <summary>
/// One root's result from the last scan. <see cref="Truncated"/> says the walk stopped before it
/// ran out of directories, so <see cref="RepositoryCount"/> is a floor rather than a total — a
/// partial scan presented as complete is the failure this record exists to prevent.
/// </summary>
public sealed record RootStatus(
    string Path,
    string Label,
    RootAvailability Availability,
    int RepositoryCount,
    bool Truncated,
    string Detail)
{
    public static RootStatus For(ProjectRoot root, RootAvailability availability, int count = 0, bool truncated = false, string detail = "") =>
        new(root.Path, root.Label, availability, count, truncated, detail);

    /// <summary>The root's display name: its label, or the folder name, or the path itself.</summary>
    public string DisplayName =>
        Label.Length > 0 ? Label
        : System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name
        : Path;

    public bool IsUsable => Availability == RootAvailability.Available;
}
