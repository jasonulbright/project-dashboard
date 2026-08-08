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
    /// handler decides what is opened and with what target.
    /// </summary>
    [Fact]
    public void ActivatingALinkFromTheKeyboard_RaisesTheSameClickTheMouseDoes()
    {
        var link = OnlyLink("see [the PR](https://github.com/o/r/pull/12) now");
        var clicks = 0;
        link.Click += (_, _) => clicks++;

        link.DoClick();

        Assert.Equal(1, clicks);
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
    {
        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..",
            "src", "ProjectDashboard", "Views", "Pages", "ProjectDetailPage.xaml"));
        Assert.True(File.Exists(path), $"detail page markup not found at {path}");
        return path;
    }
}
