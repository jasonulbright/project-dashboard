using System.IO;
using System.Text;

namespace ProjectDashboard.Services;

/// <summary>
/// Probes which templates this machine can actually create, and writes the one that was
/// chosen. The SDK-backed layouts are the reason for the probe: <c>dotnet new</c> is not
/// part of this app and a machine without it can create no .NET project at all, so those
/// layouts are withheld from the picker rather than offered and then failed.
/// </summary>
/// <param name="dotnetExe">
/// The SDK driver to invoke. Resolved through PATH by default; a caller passes an explicit
/// path to exercise the machine-without-an-SDK behaviour.
/// </param>
public class ProjectTemplateService(string dotnetExe = "dotnet")
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SeedTimeout = TimeSpan.FromMinutes(3);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Why this template cannot be created here, or null when it can. A layout this app
    /// writes itself is always available; an SDK-backed one is available only while
    /// <c>dotnet new</c> still offers the short name it needs.
    /// </summary>
    public virtual async Task<string?> UnavailableReasonAsync(ProjectTemplate template, CancellationToken ct = default)
    {
        if (!template.NeedsDotnetSdk) return null;

        var probe = await ProcessRunner.RunAsync(
            dotnetExe, ["new", "list", template.DotnetTemplate, "--type", "project"],
            workingDirectory: null, ProbeTimeout, environment: null, ct);

        return probe.Success ? null : MissingSdkReason(template);
    }

    /// <summary>The refusal text for a template whose SDK support is not on this machine.</summary>
    public static string MissingSdkReason(ProjectTemplate template) =>
        $"{template.Name} is created by the .NET SDK, and \"dotnet new {template.DotnetTemplate}\" is not available on this machine.";

    /// <summary>
    /// The subset of <paramref name="candidates"/> that can be created here. Probes run
    /// together: each SDK-backed one costs a process launch, and a picker that opened a
    /// launch at a time would stall for as long as the slowest chain of them.
    /// </summary>
    public async Task<List<ProjectTemplate>> AvailableAsync(
        IEnumerable<ProjectTemplate> candidates, CancellationToken ct = default)
    {
        var ordered = candidates.ToList();
        var reasons = await Task.WhenAll(ordered.Select(t => UnavailableReasonAsync(t, ct)));
        return [.. ordered.Where((_, i) => reasons[i] is null)];
    }

    /// <summary>
    /// Writes the template's layout into <paramref name="projectPath"/>, creating the folder.
    /// Returns the error text when the layout could not be completed — the caller owns
    /// removing what was written, since only it knows the folder did not exist beforehand —
    /// and null when every listed path is on disk.
    /// </summary>
    public virtual async Task<string?> SeedAsync(
        ProjectTemplate template, string projectPath, string projectName, CancellationToken ct = default)
    {
        if (template.NeedsDotnetSdk)
        {
            // The SDK creates the folder as part of writing into it. --no-restore keeps the
            // result to the source files the picker named: an implicit restore drops an obj
            // tree into a folder whose contents were promised in full before this ran.
            var result = await ProcessRunner.RunAsync(
                dotnetExe, ["new", template.DotnetTemplate, "--output", projectPath, "--name", projectName, "--no-restore"],
                workingDirectory: null, SeedTimeout, environment: null, ct);
            if (!result.Success)
                return $"dotnet new {template.DotnetTemplate} failed: {result.FirstError}";
        }
        else
        {
            Directory.CreateDirectory(projectPath);
        }

        await WriteAsync(Path.Combine(projectPath, "README.md"), $"# {projectName}\n\n", ct);
        await WriteAsync(Path.Combine(projectPath, "CHANGELOG.md"),
            $"# Changelog\n\n## [0.1.0] - {DateTime.Now:yyyy-MM-dd}\n\n### Added\n- Initial project scaffold\n", ct);
        await WriteAsync(Path.Combine(projectPath, ".gitignore"), Gitignore(template), ct);

        switch (template.Id)
        {
            case "docs":
                var docs = Path.Combine(projectPath, "docs");
                Directory.CreateDirectory(docs);
                await WriteAsync(Path.Combine(docs, "index.md"), $"# {projectName}\n\nDocumentation index.\n", ct);
                break;

            case "powershell":
                await WriteAsync(Path.Combine(projectPath, $"{projectName}.ps1"), PowerShellEntryScript(projectName), ct);
                break;
        }

        return null;
    }

    private static string Gitignore(ProjectTemplate template)
    {
        var sb = new StringBuilder("# Editor and OS noise\n.vs/\n.vscode/\n.idea/\nThumbs.db\ndesktop.ini\n*.swp\n");
        if (template.NeedsDotnetSdk) sb.Append("\n# Build output\n[Bb]in/\n[Oo]bj/\n*.user\n");
        if (template.Id == "powershell") sb.Append("\n# PowerShell packaging output\noutput/\n*.nupkg\n");
        return sb.ToString();
    }

    private static string PowerShellEntryScript(string projectName) =>
        "#Requires -Version 7.0\n"
        + "[CmdletBinding()]\n"
        + "param()\n\n"
        + "Set-StrictMode -Version Latest\n"
        + "$ErrorActionPreference = 'Stop'\n\n"
        + $"Write-Output '{projectName}'\n";

    /// <summary>
    /// UTF-8 without a byte-order mark and LF endings, matching every other file this app
    /// writes: a BOM in a script file is executed as content by several interpreters.
    /// </summary>
    private static Task WriteAsync(string path, string content, CancellationToken ct) =>
        File.WriteAllTextAsync(path, content, Utf8NoBom, ct);
}
