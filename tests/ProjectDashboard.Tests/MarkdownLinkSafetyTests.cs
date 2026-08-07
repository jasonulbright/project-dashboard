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
        Assert.Equal("https://ok.example", link.ToolTip);
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
