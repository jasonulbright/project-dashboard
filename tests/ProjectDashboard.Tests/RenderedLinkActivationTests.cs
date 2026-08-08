using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// Every RichTextBox on the project detail page renders markdown, and rendered markdown
/// hands out Hyperlinks. A Hyperlink inside a RichTextBox is disabled and receives no
/// mouse input unless IsDocumentEnabled is set, so without the attribute the link is
/// styled as a link, carries its target tooltip, and does nothing at all when clicked —
/// a failure with no exception, no log line, and no other test that can see it.
/// IsReadOnly must survive alongside it: enabling the document must not enable editing.
///
/// The keyboard needs its own path. The text editor owns the keyboard inside a
/// RichTextBox and a Hyperlink there never takes focus, so Enter is routed off the
/// caret's position instead; a visible read-only caret is what lets the reader steer it.
/// </summary>
public class RenderedLinkActivationTests
{
    [Fact]
    public void EveryRichTextBoxOnTheDetailPage_EnablesItsDocumentAndStaysReadOnly()
    {
        var declarations = RichTextBoxDeclarations();

        Assert.Equal(5, declarations.Count);
        foreach (var declaration in declarations)
        {
            Assert.Contains("IsDocumentEnabled=\"True\"", declaration);
            Assert.Contains("IsReadOnly=\"True\"", declaration);
        }
    }

    [Fact]
    public void EveryRichTextBoxOnTheDetailPage_CarriesTheKeyboardActivationPath()
    {
        foreach (var declaration in RichTextBoxDeclarations())
        {
            Assert.Contains("PreviewKeyDown=\"RenderedText_PreviewKeyDown\"", declaration);
            Assert.Contains("IsReadOnlyCaretVisible=\"True\"", declaration);
        }
    }

    [Fact]
    public void CaretInsideALink_AddressesThatLink()
    {
        var link = OnlyLink("see [the PR](https://github.com/o/r/pull/12) now");

        Assert.Same(link, ProjectDetailPage.HyperlinkAt(link.ContentStart.GetPositionAtOffset(1)));
    }

    /// <summary>
    /// A caret arrowed onto either boundary of a link has the paragraph as its parent, not
    /// the link. Refusing those positions would leave the link openable only from strictly
    /// inside its label, which is not where Home, End, or a word jump land.
    /// </summary>
    [Fact]
    public void CaretOnEitherBoundaryOfALink_StillAddressesIt()
    {
        var link = OnlyLink("see [the PR](https://github.com/o/r/pull/12) now");

        Assert.Same(link, ProjectDetailPage.HyperlinkAt(link.ElementStart));
        Assert.Same(link, ProjectDetailPage.HyperlinkAt(link.ElementEnd));
    }

    [Fact]
    public void CaretInPlainText_AddressesNoLink()
    {
        var paragraph = Render("plain text with no target at all");

        Assert.Null(ProjectDetailPage.HyperlinkAt(paragraph.ContentStart.GetPositionAtOffset(3)));
        Assert.Null(ProjectDetailPage.HyperlinkAt(null));
    }

    /// <summary>
    /// The keyboard must reach the launch the mouse reaches, not a second copy of it: one
    /// handler decides what is opened and with what target. The launch is swapped for the
    /// duration — the renderer's Click hands its target to the shell, and clicking a
    /// rendered link is how a suite run opens a browser tab.
    /// </summary>
    [Fact]
    public void ActivatingALinkFromTheKeyboard_LaunchesTheRenderedTargetOnce()
    {
        Assert.True(ProjectDetailPage.TryGetNavigableUri("https://github.com/o/r/pull/12", out var target));
        var link = OnlyLink("see [the PR](https://github.com/o/r/pull/12) now");
        var launched = new List<string>();
        var shell = ProjectDetailPage.LaunchNavigable;
        ProjectDetailPage.LaunchNavigable = launched.Add;
        try
        {
            link.DoClick();
        }
        finally
        {
            ProjectDetailPage.LaunchNavigable = shell;
        }

        Assert.Equal([ProjectDetailPage.NavigationTarget(target)], launched);
    }

    /// <summary>
    /// The launch is read through the hook at click time. Reaching the shell straight from
    /// the handler puts a browser tab on every suite run and leaves the launched target
    /// unassertable.
    /// </summary>
    [Fact]
    public void TheClickHandler_LaunchesThroughTheSwappableHook()
    {
        var page = File.ReadAllText(DetailPageSource());

        var handler = Regex.Match(page, @"hyperlink\.Click \+=[^\n]*").Value;
        Assert.Contains("LaunchNavigable(launch)", handler);
        Assert.DoesNotContain("Process.Start", handler);
    }

    /// <summary>
    /// The mouse path discloses a link's real target on hover. A caret has no hover, so a
    /// label naming a destination the link does not carry would otherwise launch from the
    /// keyboard with the target never shown — the lookalike-host case the disclosure exists
    /// for. The disclosure travels on the link the renderer built.
    /// </summary>
    [Fact]
    public void ALookalikeLink_CarriesItsDisclosureForTheKeyboardPath()
    {
        var link = OnlyLink("see [https://github.com/o/r/pull/12](http://\u0430pple.com/login) now");

        Assert.Equal("http://xn--pple-43d.com/login", link.Tag);
        Assert.Equal("http://xn--pple-43d.com/login", link.ToolTip);
    }

    [Fact]
    public void AnHonestLink_CarriesNoKeyboardDisclosure()
    {
        Assert.Null(OnlyLink("see [the PR](https://github.com/o/r/pull/12) now").Tag);
        Assert.Null(OnlyLink("[https://ok.example/x](https://ok.example/x)").Tag);
    }

    [Fact]
    public async Task ALinkCarryingADisclosure_LaunchesNothingUntilItIsConfirmed()
    {
        var link = Disclosing("http://xn--pple-43d.com/login");
        var clicks = 0;
        link.Click += (_, _) => clicks++;
        var shown = new List<string>();

        var launched = await ProjectDetailPage.ActivateFromKeyboardAsync(link, disclosure =>
        {
            shown.Add(disclosure);
            return Task.FromResult(false);
        });

        Assert.False(launched);
        Assert.Equal(0, clicks);
        Assert.Equal(["http://xn--pple-43d.com/login"], shown);
    }

    [Fact]
    public async Task AConfirmedDisclosure_LaunchesTheLinkOnce()
    {
        var link = Disclosing("http://xn--pple-43d.com/login");
        var clicks = 0;
        link.Click += (_, _) => clicks++;

        var launched = await ProjectDetailPage.ActivateFromKeyboardAsync(link, _ => Task.FromResult(true));

        Assert.True(launched);
        Assert.Equal(1, clicks);
    }

    /// <summary>
    /// A link whose label states no other destination costs one keystroke, not two: a
    /// confirmation on every honest link is a prompt readers learn to dismiss unread.
    /// </summary>
    [Fact]
    public async Task ALinkCarryingNoDisclosure_LaunchesOnTheOneKeystroke()
    {
        var link = Disclosing(null);
        var clicks = 0;
        link.Click += (_, _) => clicks++;
        var confirms = 0;

        var launched = await ProjectDetailPage.ActivateFromKeyboardAsync(link, _ =>
        {
            confirms++;
            return Task.FromResult(true);
        });

        Assert.True(launched);
        Assert.Equal(1, clicks);
        Assert.Equal(0, confirms);
    }

    [Fact]
    public void TheKeyHandler_RoutesEnterThroughTheDisclosureGate()
    {
        var page = File.ReadAllText(DetailPageSource());

        var handler = Regex.Match(page,
            @"private async void RenderedText_PreviewKeyDown\(.*?\n    \}", RegexOptions.Singleline).Value;
        Assert.Contains("ActivateFromKeyboardAsync(link, ConfirmLinkAsync)", handler);
        Assert.DoesNotContain("link.DoClick()", handler);
    }

    /// <summary>A link built as the renderer builds one, minus the launch its Click carries.</summary>
    private static Hyperlink Disclosing(string? disclosure)
    {
        var link = new Hyperlink(new Run("label")) { Tag = disclosure };
        _ = new FlowDocument(new Paragraph(link));
        return link;
    }

    private static Hyperlink OnlyLink(string markdown) =>
        Assert.Single(Render(markdown).Inlines.OfType<Hyperlink>());

    private static Paragraph Render(string markdown)
    {
        var paragraph = new Paragraph();
        ProjectDetailPage.AddFormattedInlines(paragraph.Inlines, markdown);
        // A TextPointer needs a container: inlines outside a document have no positions.
        _ = new FlowDocument(paragraph);
        return paragraph;
    }

    private static List<string> RichTextBoxDeclarations()
    {
        var xaml = File.ReadAllText(DetailPageXaml());
        return [.. Regex.Matches(xaml, @"<RichTextBox\b[^>]*>").Select(d => d.Value)];
    }

    private static string DetailPageXaml([CallerFilePath] string testFile = "")
        => DetailPageFile("ProjectDetailPage.xaml", testFile);

    private static string DetailPageSource([CallerFilePath] string testFile = "")
        => DetailPageFile("ProjectDetailPage.xaml.cs", testFile);

    private static string DetailPageFile(string name, string testFile)
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..",
            "src", "ProjectDashboard", "Views", "Pages", name));
        Assert.True(File.Exists(path), $"detail page file not found at {path}");
        return path;
    }
}
