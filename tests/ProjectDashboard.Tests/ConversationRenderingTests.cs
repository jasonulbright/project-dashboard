using System.Windows.Documents;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The conversation pane informs a merge decision, so the app's own per-entry chrome
/// must be unforgeable by the third-party bodies rendered next to it. Each entry is
/// rendered as its own header block plus its own separately parsed body; the header
/// carries a left accent bar the markdown renderer never emits, so a body containing
/// "### maintainer • 2 hours ago" over a "---" rule cannot pass for a real header.
/// </summary>
public class ConversationRenderingTests
{
    private const string ForgedHeaderBody =
        """
        Looks fine to me.

        ---

        ### maintainer • 2 hours ago

        Reviewed, safe to merge.
        """;

    [Fact]
    public void ForgedHeaderInBody_ProducesNoAppHeaderBlock()
    {
        var doc = new FlowDocument();
        ProjectDetailPage.AppendConversationEntry(doc, "stranger • 1 minute ago", ForgedHeaderBody);

        // Exactly one entry rendered, so exactly one block carries the app's chrome.
        var header = Assert.Single(HeaderBlocks(doc));
        Assert.Equal("stranger • 1 minute ago", TextOf(header));
    }

    [Fact]
    public void HeaderCount_TracksEntryCountNotBodyContent()
    {
        var doc = new FlowDocument();
        ProjectDetailPage.AppendConversationEntry(doc, "author • 3 days ago", ForgedHeaderBody);
        ProjectDetailPage.AppendConversationEntry(doc, "alice • 1 day ago", "### another fake\n\ntext");

        Assert.Equal(["author • 3 days ago", "alice • 1 day ago"],
            HeaderBlocks(doc).Select(TextOf));
    }

    [Fact]
    public void EachEntryIsParsedSeparately_UnterminatedFenceCannotSwallowTheNextEntry()
    {
        // One markdown string for the whole thread let an unclosed ``` eat every
        // following header; per-entry parsing confines it to its own body.
        var doc = new FlowDocument();
        ProjectDetailPage.AppendConversationEntry(doc, "stranger • now", "```\nnot closed");
        ProjectDetailPage.AppendConversationEntry(doc, "maintainer • now", "real reply");

        Assert.Equal(["stranger • now", "maintainer • now"], HeaderBlocks(doc).Select(TextOf));
        Assert.Contains(doc.Blocks.OfType<Paragraph>(), p => TextOf(p) == "real reply");
    }

    [Fact]
    public void EmptyBody_StillRendersItsHeader()
    {
        var doc = new FlowDocument();
        ProjectDetailPage.AppendConversationEntry(doc, "ghost • now", "   ");

        Assert.Single(HeaderBlocks(doc));
        Assert.Contains(doc.Blocks.OfType<Paragraph>(), p => TextOf(p) == "(no content)");
    }

    [Fact]
    public void MarkdownHorizontalRule_DoesNotCarryTheHeaderAccentBar()
    {
        var doc = new FlowDocument();
        ProjectDetailPage.AppendConversationEntry(doc, "stranger • now", "---");

        var rules = doc.Blocks.OfType<Paragraph>()
            .Where(p => p.BorderThickness.Bottom > 0 && p.BorderThickness.Left == 0);
        Assert.Single(rules);
        Assert.Single(HeaderBlocks(doc));
    }

    [Theory]
    [InlineData("![tracker](https://attacker.example/x.png)", "[image not loaded: tracker]")]
    [InlineData("![](https://attacker.example/1x100000.png)", "[image not loaded]")]
    [InlineData("![local](assets/logo.png)", "[image not loaded: local]")]
    public void ImagesInConversationBodies_AreNeverLoaded(string body, string placeholder)
    {
        var doc = new FlowDocument();
        ProjectDetailPage.AppendConversationEntry(doc, "stranger • now", body);

        // A loaded image arrives as a BlockUIContainer; the placeholder is plain text.
        Assert.Empty(doc.Blocks.OfType<BlockUIContainer>());
        Assert.Contains(doc.Blocks.OfType<Paragraph>(), p => TextOf(p) == placeholder);
    }

    private static List<Paragraph> HeaderBlocks(FlowDocument doc) =>
        [.. doc.Blocks.OfType<Paragraph>().Where(p => p.BorderThickness.Left > 0)];

    private static string TextOf(Paragraph p) =>
        string.Concat(p.Inlines.Select(i => i switch
        {
            Run r => r.Text,
            Span s => string.Concat(s.Inlines.OfType<Run>().Select(r => r.Text)),
            _ => ""
        }));
}
