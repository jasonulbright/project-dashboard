using System.Text.RegularExpressions;
using ProjectDashboard.Services.Update;

namespace ProjectDashboard.Tests;

/// <summary>
/// The pure half of the update check: what a release tag orders as, which links may be
/// opened, and what one response means. None of it needs a socket, a settings file, or a
/// window, and the release response is untrusted input — a tag and a link that arrived over
/// the network decide whether a notice is shown and what the shell is handed.
/// </summary>
public class UpdateVersionTests
{
    private static readonly Version Current = new(2, 0, 1, 0);

    [Theory]
    // Equal, in both the shapes the two sources produce: tags are three-part for new
    // releases, the assembly version is always four.
    [InlineData("2.0.1", VersionComparison.Same)]
    [InlineData("2.0.1.0", VersionComparison.Same)]
    [InlineData("v2.0.1", VersionComparison.Same)]
    [InlineData("V2.0.1", VersionComparison.Same)]
    // Newer, one part at a time.
    [InlineData("2.0.2", VersionComparison.Newer)]
    [InlineData("2.1.0", VersionComparison.Newer)]
    [InlineData("3.0.0", VersionComparison.Newer)]
    [InlineData("v2.0.1.1", VersionComparison.Newer)]
    // Older, including the legacy four-part shape.
    [InlineData("2.0.0", VersionComparison.Older)]
    [InlineData("1.9.9", VersionComparison.Older)]
    [InlineData("2.0.0.9", VersionComparison.Older)]
    // Outside the accepted shape. None of these is older or newer — they are unreadable,
    // and an unreadable tag never produces a prompt.
    [InlineData("2.1.0-rc1", VersionComparison.Unreadable)]
    [InlineData("v3.0", VersionComparison.Unreadable)]
    [InlineData("latest", VersionComparison.Unreadable)]
    [InlineData("", VersionComparison.Unreadable)]
    [InlineData(null, VersionComparison.Unreadable)]
    [InlineData("v", VersionComparison.Unreadable)]
    [InlineData("2.0.1.2.3", VersionComparison.Unreadable)]
    [InlineData("vv2.0.2", VersionComparison.Unreadable)]
    [InlineData(" 2.0.2", VersionComparison.Unreadable)]
    // A trailing newline is what $ would have accepted.
    [InlineData("2.0.2\n", VersionComparison.Unreadable)]
    [InlineData("99999999999999999999.0.0", VersionComparison.Unreadable)]
    public void ATagOrdersAgainstTheRunningBuild(string? tag, VersionComparison expected) =>
        Assert.Equal(expected, ReleaseVersion.Compare(tag, Current));

    /// <summary>
    /// A three-part tag and the four-part assembly version built from it are the same
    /// release. <see cref="Version"/> orders an absent revision below zero, so without the
    /// normalization the build would report itself newer than the tag it was cut from.
    /// </summary>
    [Fact]
    public void AThreePartTagIsNotOlderThanItsOwnFourPartBuild()
    {
        Assert.True(ReleaseVersion.TryParse("2.0.1", out var parsed));
        Assert.Equal(new Version(2, 0, 1, 0), parsed);
    }

    [Theory]
    // The shape the endpoint actually returns.
    [InlineData("https://github.com/jasonulbright/project-dashboard/releases/tag/v2.0.2", true)]
    [InlineData("https://github.com/jasonulbright/project-dashboard/releases", true)]
    [InlineData("https://GitHub.com/JasonUlbright/Project-Dashboard/releases/tag/v2.0.2", true)]
    // Another host outright.
    [InlineData("https://evil.example/jasonulbright/project-dashboard/releases/tag/v1", false)]
    // The pinned host as a label of another one, and as userinfo in front of another one.
    [InlineData("https://github.com.evil.example/jasonulbright/project-dashboard/releases", false)]
    [InlineData("https://github.com@evil.example/jasonulbright/project-dashboard/releases", false)]
    // A homoglyph host: the parsed form is punycode, which is not the pinned host.
    [InlineData("https://g\u0456thub.com/jasonulbright/project-dashboard/releases", false)]
    // Another repository, and a path that only shares the prefix.
    [InlineData("https://github.com/someone-else/project-dashboard/releases", false)]
    [InlineData("https://github.com/jasonulbright/other-repo/releases", false)]
    [InlineData("https://github.com/jasonulbright/project-dashboard/releases-mirror/x", false)]
    [InlineData("https://github.com/jasonulbright/project-dashboard/settings", false)]
    // Not https, not absolute, not a web link at all.
    [InlineData("http://github.com/jasonulbright/project-dashboard/releases", false)]
    [InlineData("https://github.com:8443/jasonulbright/project-dashboard/releases", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/jasonulbright/project-dashboard/releases", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyALinkIntoThisProjectsReleasesPageMayBeOpened(string? candidate, bool allowed) =>
        Assert.Equal(allowed, ReleaseLink.IsPinnedReleaseUrl(candidate));

    /// <summary>
    /// What is handed to the launcher is the parsed form, not the captured text: padding and
    /// an embedded control character survive a host comparison and would otherwise reach the
    /// shell intact.
    /// </summary>
    [Fact]
    public void TheOpenedTargetIsTheParsedForm()
    {
        Assert.True(ReleaseLink.TryNormalize(
            "  https://github.com/jasonulbright/project-dashboard/releases/tag/v2.0.2\t ", out var target));
        Assert.Equal("https://github.com/jasonulbright/project-dashboard/releases/tag/v2.0.2", target);

        Assert.False(ReleaseLink.TryNormalize("https://evil.example/x", out var refused));
        Assert.Equal("", refused);
    }

    /// <summary>The endpoint is a constant on the pinned API host, never composed from a response.</summary>
    [Fact]
    public void TheEndpointIsPinned()
    {
        Assert.Equal(
            "https://api.github.com/repos/jasonulbright/project-dashboard/releases/latest",
            ReleaseLink.LatestReleaseEndpoint);
        Assert.Equal("https://github.com/jasonulbright/project-dashboard/releases", ReleaseLink.ReleasesPage);
    }

    // ── What one response means ──────────────────────────────────────────────

    [Fact]
    public void ANewerPublishedRelease_IsOfferedWithItsOwnLink()
    {
        var result = UpdateCheckService.Interpret(Ok(Release("v2.0.2")), Current);

        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v2.0.2", result.Update!.TagName);
        Assert.Equal("https://github.com/jasonulbright/project-dashboard/releases/tag/v2.0.2", result.Update.ReleaseUrl);
        Assert.Contains("v2.0.2", result.Status);
    }

    [Theory]
    [InlineData("v2.0.1")]
    [InlineData("v2.0.0")]
    public void ATagThatIsNotNewer_OffersNothing(string tag)
    {
        var result = UpdateCheckService.Interpret(Ok(Release(tag)), Current);

        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Update);
        Assert.Contains("v2.0.1.0", result.Status);
    }

    /// <summary>
    /// The endpoint excludes both today. This is the guard for the day it does not, or for a
    /// different endpoint being read later: neither is a release to send a reader to.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ADraftOrPreRelease_IsNeverOffered(bool draft, bool prerelease)
    {
        var result = UpdateCheckService.Interpret(Ok(Release("v9.0.0", draft: draft, prerelease: prerelease)), Current);

        Assert.Equal(UpdateOutcome.Unknown, result.Outcome);
        Assert.Null(result.Update);
    }

    /// <summary>
    /// A response that does not say a release is published is not one to prompt on: an absent
    /// flag reads as set, so a shape that lost the field cannot offer a draft by omission.
    /// </summary>
    [Fact]
    public void AResponseMissingThePublishedFlags_IsNeverOffered()
    {
        var body = """{"tag_name":"v9.0.0","html_url":"https://github.com/jasonulbright/project-dashboard/releases/tag/v9.0.0"}""";

        Assert.Equal(UpdateOutcome.Unknown, UpdateCheckService.Interpret(Ok(body), Current).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"tag_name":123}""")]
    [InlineData("""{"html_url":"https://github.com/jasonulbright/project-dashboard/releases","draft":false,"prerelease":false}""")]
    public void ABodyThatNamesNoComparableRelease_IsUnknownAndOffersNothing(string body)
    {
        var result = UpdateCheckService.Interpret(Ok(body), Current);

        Assert.Equal(UpdateOutcome.Unknown, result.Outcome);
        Assert.Null(result.Update);
    }

    /// <summary>
    /// The highest-severity case: a newer release whose link points somewhere else. The
    /// response is untrusted and its link would reach the shell, so the whole answer is
    /// refused — nothing is offered, and there is no target for a later click to open.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/jasonulbright/project-dashboard/releases/tag/v9.0.0")]
    [InlineData("https://github.com@evil.example/jasonulbright/project-dashboard/releases")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("https://github.com/someone-else/project-dashboard/releases/tag/v9.0.0")]
    [InlineData("")]
    public void ANewerReleaseLinkingElsewhere_IsRefusedOutright(string hostile)
    {
        var result = UpdateCheckService.Interpret(Ok(Release("v9.0.0", url: hostile)), Current);

        Assert.Equal(UpdateOutcome.Unknown, result.Outcome);
        Assert.Null(result.Update);
        Assert.Contains("releases page", result.Status);
    }

    [Fact]
    public void ARateLimitedResponse_ReportsWhenTheQuotaRefills()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(37);
        var result = UpdateCheckService.Interpret(new ReleaseFetch(403, "", reset, null), Current);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("rate limit", result.Status);
        Assert.Contains(reset.ToLocalTime().ToString("HH:mm"), result.Status);
    }

    [Fact]
    public void ARateLimitedResponseWithNoResetHeader_StillSaysWhatHappened()
    {
        var result = UpdateCheckService.Interpret(new ReleaseFetch(429, "", null, null), Current);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains("rate limit", result.Status);
    }

    [Theory]
    [InlineData(404, "No releases published yet.")]
    [InlineData(301, "redirected")]
    [InlineData(500, "500")]
    public void AnUnusableStatus_FailsWithItsOwnReason(int status, string expected)
    {
        var result = UpdateCheckService.Interpret(new ReleaseFetch(status, "", null, null), Current);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Contains(expected, result.Status);
    }

    [Fact]
    public void AReadThatNeverArrived_CarriesTheTransportsOwnReason()
    {
        var result = UpdateCheckService.Interpret(ReleaseFetch.Unreachable("The check timed out."), Current);

        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.Equal("The check timed out.", result.Status);
    }

    /// <summary>
    /// The release link is opened through the same swappable launcher every rendered link
    /// goes through, and it is re-measured there. Reaching the shell straight from the
    /// command would put a browser tab on a suite run and leave the target unassertable.
    /// </summary>
    [Fact]
    public void TheOpenCommand_RevalidatesAndLaunchesThroughTheSwappableHook()
    {
        var source = RepoSource.Read("src/ProjectDashboard/ViewModels/Pages/DashboardViewModel.cs");
        var command = Regex.Match(source,
            @"private void OpenUpdateRelease\(\).*?\n    \}", RegexOptions.Singleline).Value;

        Assert.Contains("ReleaseLink.TryNormalize(_updateReleaseUrl, out var target)", command);
        Assert.Contains("ProjectDetailPage.LaunchNavigable(target)", command);
        Assert.DoesNotContain("Process.Start", command);
    }

    private static ReleaseFetch Ok(string body) => new(200, body, null, null);

    private static string Release(
        string tag,
        string url = "https://github.com/jasonulbright/project-dashboard/releases/tag/v2.0.2",
        bool draft = false,
        bool prerelease = false) =>
        $$"""
          {"tag_name":"{{tag}}","html_url":"{{url}}","draft":{{(draft ? "true" : "false")}},
           "prerelease":{{(prerelease ? "true" : "false")}},"assets":[{"name":"setup.exe"}]}
          """;
}
