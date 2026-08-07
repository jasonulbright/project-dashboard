namespace ProjectDashboard.Models;

/// <summary>One configured remote with its fetch and push URLs (they can differ).</summary>
public sealed class RemoteEntry
{
    public string Name { get; init; } = "";
    public string FetchUrl { get; init; } = "";
    public string PushUrl { get; init; } = "";
}
