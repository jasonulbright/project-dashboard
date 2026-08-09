using System.Text.RegularExpressions;

namespace ProjectDashboard.Services.Update;

/// <summary>How a release tag orders against the running build.</summary>
public enum VersionComparison
{
    /// <summary>The tag is outside the accepted shape and orders against nothing.</summary>
    Unreadable,
    Older,
    Same,
    Newer
}

/// <summary>
/// The release-tag shapes a comparison accepts, and the ordering over them. The accepted
/// shape is the one the release workflow gates a tag on; anything else — a pre-release
/// suffix, a moving name, an empty string — is unreadable rather than older or newer, and
/// an unreadable tag never produces a prompt.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>
    /// Three or four numeric parts. <c>\z</c> rather than <c>$</c>: <c>$</c> also matches
    /// before a trailing newline, which would accept a tag carrying one.
    /// </summary>
    private static readonly Regex Accepted =
        new(@"^\d+\.\d+\.\d+(\.\d+)?\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The four-part form of <paramref name="tag"/>, or false when the tag is outside the
    /// accepted shape. A missing fourth part is zero: <see cref="Version"/> orders an absent
    /// revision below zero, so a four-part assembly version would otherwise read as newer
    /// than the three-part tag it was built from.
    /// </summary>
    public static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrEmpty(tag)) return false;

        var text = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        if (!Accepted.IsMatch(text)) return false;
        if (!Version.TryParse(text, out var parsed)) return false;

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    /// <summary>Where <paramref name="tag"/> sits relative to <paramref name="current"/>.</summary>
    public static VersionComparison Compare(string? tag, Version current)
    {
        if (!TryParse(tag, out var candidate)) return VersionComparison.Unreadable;

        var normalized = new Version(
            current.Major, current.Minor, Math.Max(current.Build, 0), Math.Max(current.Revision, 0));

        return candidate.CompareTo(normalized) switch
        {
            > 0 => VersionComparison.Newer,
            < 0 => VersionComparison.Older,
            _ => VersionComparison.Same
        };
    }
}
