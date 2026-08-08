namespace ProjectDashboard.Tests;

/// <summary>
/// Serializes the tests that touch the process-wide decoded-image cache. Run in parallel
/// they would clear each other's entries mid-assertion.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MarkdownImageCollection
{
    public const string Name = "markdown-images";
}
