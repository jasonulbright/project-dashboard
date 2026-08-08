namespace ProjectDashboard.Models;

/// <summary>What git could establish about a path and the ignore rules.</summary>
public enum IgnoreState
{
    Ignored,
    NotIgnored,

    /// <summary>Git could not answer — the question failed rather than came back "no".</summary>
    Unknown,
}

/// <summary>
/// One answer from `git check-ignore`. <paramref name="Tracked"/> is meaningful only alongside
/// <see cref="IgnoreState.NotIgnored"/>: check-ignore consults the index, so a tracked path is
/// reported as not ignored even when a rule matches it.
/// </summary>
/// <param name="Error">Why the answer is <see cref="IgnoreState.Unknown"/>; empty otherwise.</param>
public sealed record IgnoreAnswer(IgnoreState State, bool Tracked, string Error);
