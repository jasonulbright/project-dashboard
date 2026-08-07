namespace ProjectDashboard.Tests;

/// <summary>
/// Reads shipped source files from the working tree. XAML compiles to BAML with no
/// runtime API for the attached properties and bindings declared on a template's
/// elements, so markup-level guarantees — a body panel per empty state, a keyboard
/// navigation mode on a container — are asserted against the file itself.
/// </summary>
public static class RepoSource
{
    public static string Read(string relativePath)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"source file not found under the working tree: {relativePath}");
        return File.ReadAllText(full);
    }

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "ProjectDashboard", "ProjectDashboard.csproj")))
                return dir.FullName;

        throw new DirectoryNotFoundException(
            $"no repository root above {AppContext.BaseDirectory}; source-level assertions need the working tree");
    }
}
