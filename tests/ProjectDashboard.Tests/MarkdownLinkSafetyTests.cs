using System.Windows.Documents;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The detail page renders third-party issue and comment bodies, and its link click
/// path is ShellExecute. A markdown link target is therefore only clickable when it
/// is http/https; every other scheme must land as inert text, so a body such as
/// [https://github.com/o/r/pull/12](file:///C:/Users/u/Downloads/setup.exe) cannot
/// launch anything from one click.
/// </summary>
public class MarkdownLinkSafetyTests
{
    [Theory]
    [InlineData("http://example.com/x")]
    [InlineData("https://example.com/x")]
    [InlineData("HTTPS://Example.COM/x")]
    [InlineData("https://github.com/o/r/pull/12?tab=files#diff")]
    public void HttpAndHttps_AreNavigable(string url)
        => Assert.True(ProjectDetailPage.IsNavigableLink(url));

    [Theory]
    [InlineData("file:///C:/Users/u/Downloads/setup.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("vscode://file/C:/x")]
    [InlineData("ftp://example.com/x")]
    [InlineData("mailto:a@b.c")]
    [InlineData("/relative/path")]
    [InlineData("../up.md")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://")]
    [InlineData(":::not a url:::")]
    public void EverythingElse_IsNotNavigable(string url)
        => Assert.False(ProjectDetailPage.IsNavigableLink(url));

    [Theory]
    [InlineData("file:///C:/Users/u/Downloads/setup.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("ms-settings:windowsupdate")]
    public void RejectedTarget_RendersAsPlainTextNotHyperlink(string url)
    {
        var inlines = Render($"see [https://github.com/o/r/pull/12]({url}) for details");

        Assert.Empty(inlines.OfType<Hyperlink>());
        // The deceptive label survives, but the real target is printed next to it.
        Assert.Contains(url, TextOf(inlines));
        Assert.Contains("https://github.com/o/r/pull/12", TextOf(inlines));
    }

    [Fact]
    public void AcceptedTarget_RendersAsHyperlinkWithTargetTooltip()
    {
        var inlines = Render("see [the PR](https://github.com/o/r/pull/12) now");

        var link = Assert.Single(inlines.OfType<Hyperlink>());
        Assert.Equal("https://github.com/o/r/pull/12", link.ToolTip);
        Assert.Equal("the PR", string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text)));
    }

    [Fact]
    public void MixedBody_KeepsOnlyTheHttpLinkClickable()
    {
        var inlines = Render("[a](https://ok.example) and [b](file:///C:/x.exe)");

        var link = Assert.Single(inlines.OfType<Hyperlink>());
        // The tooltip is built from the parsed Uri, which spells an empty path as "/".
        Assert.Equal("https://ok.example/", link.ToolTip);
    }

    /// <summary>
    /// The tooltip is the only thing telling a reader where an attacker-labelled link
    /// really goes, so it must not repeat a host the reader cannot tell apart from the
    /// one in the label. A Cyrillic а renders identically to the Latin a; punycode does
    /// not.
    /// </summary>
    [Fact]
    public void UnicodeLookalikeHost_IsDisclosedAsPunycode()
    {
        var inlines = Render("see [https://github.com/o/r/pull/12](http://\u0430pple.com/login) now");

        var link = Assert.Single(inlines.OfType<Hyperlink>());
        var tooltip = Assert.IsType<string>(link.ToolTip);
        Assert.Equal("http://xn--pple-43d.com/login", tooltip);
        Assert.DoesNotContain("\u0430", tooltip);
    }

    /// <summary>
    /// The mouse discloses a link's target on hover; a caret has no hover, so the keyboard
    /// path discloses through a confirmation instead. It is owed only where the label makes
    /// a claim about the destination that the launch does not keep — a label naming another
    /// host, and a label whose own characters are a host the reader cannot tell from an
    /// ASCII one. A label that names no destination contradicts nothing and costs no
    /// second keystroke.
    /// </summary>
    [Theory]
    [InlineData("the PR", "https://github.com/o/r/pull/12")]
    [InlineData("here", "https://evil.example/x")]
    [InlineData("v1.2.0", "https://github.com/o/r/releases/tag/v1.2.0")]
    [InlineData("see github.com for more", "https://evil.example/x")]
    [InlineData("https://github.com/o/r/pull/12", "https://github.com/o/r/pull/12")]
    [InlineData("github.com/o/r/pull/12", "https://github.com/o/r/pull/12")]
    [InlineData("example.com", "https://example.com")]
    [InlineData("https://example.com", "https://example.com")]
    public void ALabelClaimingNoOtherDestination_ActivatesWithoutADisclosure(string label, string url)
    {
        Assert.True(ProjectDetailPage.TryGetNavigableUri(url, out var target));
        Assert.Null(ProjectDetailPage.KeyboardDisclosure(label, target));
    }

    [Theory]
    // The label names one host and the link carries another.
    [InlineData("https://github.com/o/r/pull/12", "http://\u0430pple.com/login", "http://xn--pple-43d.com/login")]
    [InlineData("github.com", "https://evil.example/x", "https://evil.example/x")]
    [InlineData("github.com/o/r/pull/12", "https://github.com.evil.example/o/r/pull/12",
        "https://github.com.evil.example/o/r/pull/12")]
    // The label IS the target, and renders as a host it is not: the punycode is the point.
    [InlineData("\u0430pple.com", "https://\u0430pple.com", "https://xn--pple-43d.com/")]
    [InlineData("https://\u0430pple.com/login", "https://\u0430pple.com/login", "https://xn--pple-43d.com/login")]
    // Userinfo hides the host in a label that reads as an honest one.
    [InlineData("https://github.com/x", "https://github.com@evil.example/x", "https://github.com@evil.example/x")]
    public void ALabelNamingAnotherDestination_IsDisclosedInPunycode(string label, string url, string expected)
    {
        Assert.True(ProjectDetailPage.TryGetNavigableUri(url, out var target));
        Assert.Equal(expected, ProjectDetailPage.KeyboardDisclosure(label, target));
    }

    [Theory]
    [InlineData("https://github.com/o/r/pull/12", "https://github.com/o/r/pull/12")]
    [InlineData("HTTPS://Example.COM/x", "https://example.com/x")]
    [InlineData("https://example.com/a?b=c#d", "https://example.com/a?b=c#d")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")]
    // Userinfo is what makes the host easy to miss, so it stays in the disclosure.
    [InlineData("https://github.com@evil.example/x", "https://github.com@evil.example/x")]
    // An IPv6 literal keeps its brackets: unbracketed, the address colons run into the
    // port and the disclosed string is not a URL.
    [InlineData("https://[::1]:8080/x", "https://[::1]:8080/x")]
    [InlineData("https://[2001:db8::1]:8443/x", "https://[2001:db8::1]:8443/x")]
    [InlineData("http://[2001:db8::1]/x", "http://[2001:db8::1]/x")]
    public void AsciiHost_IsDisclosedUnchanged(string url, string expected)
    {
        Assert.True(ProjectDetailPage.TryGetNavigableUri(url, out var uri));
        Assert.Equal(expected, ProjectDetailPage.LinkDisclosure(uri));
    }

    /// <summary>
    /// What reaches ShellExecute is the parsed form, not the raw capture. An embedded
    /// carriage return or tab and any surrounding whitespace survive the http/https
    /// allow-list untouched, so the normalization has to happen before the launch.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/\rfoo", "https://evil.example/%0Dfoo")]
    [InlineData("https://x/\t/y", "https://x/%09/y")]
    [InlineData("  https://x  ", "https://x/")]
    [InlineData("https://evil.example/\nfoo", "https://evil.example/%0Afoo")]
    [InlineData("HTTPS://Example.COM/x", "https://example.com/x")]
    [InlineData("https://github.com/o/r/pull/12", "https://github.com/o/r/pull/12")]
    public void LaunchedTarget_IsTheNormalizedAbsoluteUri(string url, string expected)
    {
        Assert.True(ProjectDetailPage.TryGetNavigableUri(url, out var uri));
        Assert.Equal(expected, ProjectDetailPage.NavigationTarget(uri));
    }

    [Fact]
    public void PaddedLinkTarget_StillRendersClickable()
    {
        // The markdown capture keeps the padding; the allow-list must not reject it and
        // the launch must not carry it.
        var inlines = Render("[a](  https://ok.example/x  )");

        var link = Assert.Single(inlines.OfType<Hyperlink>());
        Assert.Equal("https://ok.example/x", link.ToolTip);
    }

    private static List<Inline> Render(string markdown)
    {
        var paragraph = new Paragraph();
        ProjectDetailPage.AddFormattedInlines(paragraph.Inlines, markdown);
        return [.. paragraph.Inlines];
    }

    private static string TextOf(IEnumerable<Inline> inlines) =>
        string.Concat(inlines.Select(i => i switch
        {
            Run r => r.Text,
            Span s => string.Concat(s.Inlines.OfType<Run>().Select(r => r.Text)),
            _ => ""
        }));
}
