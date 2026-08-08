namespace ProjectDashboard.Services;

/// <summary>
/// A starting layout New Project can seed. Definitions live in code and nowhere else: a
/// layout this app cannot create in full is never offered, and an editable template surface
/// would let one be named that it cannot.
/// </summary>
/// <param name="Creates">
/// Every path the template writes, relative to the project folder, with <c>{name}</c> standing
/// in for the project name. This is what the picker shows before the scaffold runs, so a path
/// missing here is a path the reader was not told about.
/// </param>
/// <param name="DotnetTemplate">
/// Short name passed to <c>dotnet new</c>; empty for a layout this app writes itself.
/// </param>
/// <param name="ProjectType">Manifest project type recorded for the new project.</param>
public sealed record ProjectTemplate(
    string Id,
    string Name,
    string Summary,
    IReadOnlyList<string> Creates,
    string DotnetTemplate,
    string ProjectType)
{
    /// <summary>True when seeding shells out to <c>dotnet new</c>, which needs an SDK present.</summary>
    public bool NeedsDotnetSdk => DotnetTemplate.Length > 0;

    /// <summary>The paths this template writes for a project of this name.</summary>
    public IReadOnlyList<string> CreatesFor(string projectName) =>
        [.. Creates.Select(path => path.Replace("{name}", projectName, StringComparison.Ordinal))];

    /// <summary>One line naming everything the template writes, for the picker.</summary>
    public string CreatesLine(string projectName) => "Creates: " + string.Join(", ", CreatesFor(projectName));
}

/// <summary>The layouts New Project offers, in the order the picker lists them.</summary>
public static class ProjectTemplates
{
    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        new("empty", "Empty project",
            "A readme, a changelog and an ignore file. Nothing is assumed about the language.",
            ["README.md", "CHANGELOG.md", ".gitignore"],
            DotnetTemplate: "", ProjectType: "unknown"),

        new("docs", "Documentation",
            "Notes and specifications rather than code: the same three files plus a docs folder with an index page.",
            ["README.md", "CHANGELOG.md", ".gitignore", "docs/index.md"],
            DotnetTemplate: "", ProjectType: "docs"),

        new("powershell", "PowerShell script",
            "A runnable entry script under strict mode, with an ignore file covering PowerShell build output.",
            ["README.md", "CHANGELOG.md", ".gitignore", "{name}.ps1"],
            DotnetTemplate: "", ProjectType: "powershell"),

        new("dotnet-console", ".NET console app",
            "The SDK's console template, plus this app's readme, changelog and ignore file. Needs the .NET SDK.",
            ["README.md", "CHANGELOG.md", ".gitignore", "{name}.csproj", "Program.cs"],
            DotnetTemplate: "console", ProjectType: "dotnet"),

        new("dotnet-classlib", ".NET class library",
            "The SDK's class-library template, plus this app's readme, changelog and ignore file. Needs the .NET SDK.",
            ["README.md", "CHANGELOG.md", ".gitignore", "{name}.csproj", "Class1.cs"],
            DotnetTemplate: "classlib", ProjectType: "dotnet"),
    ];

    /// <summary>The layout used when a caller names none.</summary>
    public static ProjectTemplate Default => All[0];

    public static ProjectTemplate? ById(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
