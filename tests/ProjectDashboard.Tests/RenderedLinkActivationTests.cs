using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ProjectDashboard.Tests;

/// <summary>
/// Every RichTextBox on the project detail page renders markdown, and rendered markdown
/// hands out Hyperlinks. A Hyperlink inside a RichTextBox is disabled and receives no
/// mouse input unless IsDocumentEnabled is set, so without the attribute the link is
/// styled as a link, carries its target tooltip, and does nothing at all when clicked —
/// a failure with no exception, no log line, and no other test that can see it.
/// IsReadOnly must survive alongside it: enabling the document must not enable editing.
/// </summary>
public class RenderedLinkActivationTests
{
    [Fact]
    public void EveryRichTextBoxOnTheDetailPage_EnablesItsDocumentAndStaysReadOnly()
    {
        var xaml = File.ReadAllText(DetailPageXaml());
        var declarations = Regex.Matches(xaml, @"<RichTextBox\b[^>]*>");

        Assert.Equal(5, declarations.Count);
        foreach (var declaration in declarations.Select(d => d.Value))
        {
            Assert.Contains("IsDocumentEnabled=\"True\"", declaration);
            Assert.Contains("IsReadOnly=\"True\"", declaration);
        }
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
