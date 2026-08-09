namespace ProjectDashboard.Services.Update;

/// <summary>
/// One read of the latest-release endpoint, as the transport saw it. The seam every test
/// substitutes: a body and a status code, with no socket behind them.
/// </summary>
/// <param name="StatusCode">The HTTP status, or 0 when no response arrived.</param>
/// <param name="RateLimitReset">When the caller's quota refills, when the response said so.</param>
/// <param name="TransportError">Why no response arrived, phrased for a reader; null when one did.</param>
public sealed record ReleaseFetch(int StatusCode, string Body, DateTimeOffset? RateLimitReset, string? TransportError)
{
    public static ReleaseFetch Unreachable(string reason) => new(0, "", null, reason);

    public bool Reached => TransportError is null;
}

/// <summary>What a check concluded. Every member is a distinct thing to tell a reader.</summary>
public enum UpdateOutcome
{
    /// <summary>The toggle is off, so nothing was asked and nothing was sent.</summary>
    Disabled,

    /// <summary>The launch cooldown had not elapsed; the previous outcome still stands.</summary>
    Cooldown,

    UpToDate,
    UpdateAvailable,

    /// <summary>
    /// GitHub answered and the answer named no version this app can order against — an
    /// unreadable body, a tag outside the accepted shape, a draft or pre-release, or a link
    /// that is not into this project's releases page. Never a prompt.
    /// </summary>
    Unknown,

    /// <summary>The read did not produce an answer: offline, refused, timed out, or absent.</summary>
    Failed
}

/// <summary>
/// A check's conclusion and the sentence that reports it. <paramref name="Status"/> is
/// written for a reader on the Settings page and is what a failed check leaves behind, so a
/// check that has been failing quietly is one page away from being visible.
/// </summary>
public sealed record UpdateCheckResult(UpdateOutcome Outcome, string Status, AvailableUpdate? Update = null);

/// <summary>A published release newer than this build, and the page that describes it.</summary>
/// <param name="TagName">The release's own tag, in the shape the workflow gates on.</param>
/// <param name="ReleaseUrl">A link validated against the pinned releases path.</param>
public sealed record AvailableUpdate(string TagName, string ReleaseUrl)
{
    /// <summary>The tag as a reader reads a version, whether or not the tag carries the v.</summary>
    public string Display => TagName.Length > 0 && TagName[0] is 'v' or 'V' ? TagName : $"v{TagName}";
}
