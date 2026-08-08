using ProjectDashboard.Models;

namespace ProjectDashboard.Tests;

/// <summary>
/// The parts of the accessibility contract that need no visual tree. The rest of it — what a
/// reader is handed for a list row or a status line — is asserted inside
/// <see cref="DetailPageMarkupTests"/>, because the Application and the brushes in its
/// dictionaries belong to the one STA thread that built them.
/// </summary>
public class AccessibleNamingTests
{
    /// <summary>
    /// Ten digits, eleven tabs. A sheet that listed only the digit jumps would read as though
    /// the eleventh tab had no keyboard route at all.
    /// </summary>
    [Fact]
    public void TheCheatSheet_SaysHowToReachTheEleventhTab()
    {
        Assert.Equal(11, Enum.GetValues<DetailTab>().Length);

        var detail = ShortcutTable.All
            .Where(e => e.Group == ShortcutTable.DetailGroup)
            .ToList();

        Assert.Contains(detail, e => e.Gesture == "Ctrl+0");
        Assert.Contains(detail, e => e.Description.Contains("eleven", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The unified pane strips the +/- status column from a row's text and leaves the kind on
    /// the background tint and on which gutter is filled. Neither reaches a reader.
    /// </summary>
    [Theory]
    [InlineData(DiffLineKind.Added, "", "2", "fresh", "Added line 2: fresh")]
    [InlineData(DiffLineKind.Removed, "2", "", "gone", "Removed line 2: gone")]
    [InlineData(DiffLineKind.Context, "1", "1", "kept", "Line 1: kept")]
    [InlineData(DiffLineKind.HunkHeader, "", "", "@@ -1,2 +1,3 @@", "Hunk header @@ -1,2 +1,3 @@")]
    public void ADiffRowIsNarrated_WithTheKindTheTintOnlyShows(
        DiffLineKind kind, string oldNumber, string newNumber, string text, string expected)
    {
        var line = new DiffLine
        {
            Kind = kind, Text = text, OldNumber = oldNumber, NewNumber = newNumber
        };

        Assert.Equal(expected, Helpers.DiffLineNarrator.Narrate(line));
    }

    [Fact]
    public void TheNoNewlineMarker_IsNarratedAsItself()
    {
        var line = new DiffLine
        {
            Kind = DiffLineKind.Context,
            Text = @"\ No newline at end of file",
            IsNoNewlineMarker = true
        };

        Assert.Equal("No newline at end of file", Helpers.DiffLineNarrator.Narrate(line));
    }

    /// <summary>
    /// A two-column row draws a side with no counterpart as a grey block, which is silence to a
    /// reader; which side gained or lost the line has to be in words.
    /// </summary>
    [Fact]
    public void ATwoColumnRowIsNarrated_WithTheSideThatGainedOrLostTheLine()
    {
        var rows = SideBySideDiff.Build([
            new DiffLine { Kind = DiffLineKind.HunkHeader, Text = "@@ -1,2 +1,2 @@", HunkIndex = 0 },
            new DiffLine { Kind = DiffLineKind.Context, Text = "kept", OldNumber = "1", NewNumber = "1" },
            new DiffLine { Kind = DiffLineKind.Removed, Text = "gone", OldNumber = "2" },
            new DiffLine { Kind = DiffLineKind.Added, Text = "fresh", NewNumber = "2" },
            new DiffLine
            {
                Kind = DiffLineKind.Context,
                Text = @"\ No newline at end of file",
                IsNoNewlineMarker = true
            }
        ]);

        var narrated = rows.Select(Helpers.SideBySideRowNarrator.Narrate).ToList();

        Assert.Contains("Hunk header @@ -1,2 +1,2 @@", narrated);
        Assert.Contains("Line 1: kept", narrated);
        Assert.Contains(narrated, n =>
            n.Contains("gone") && (n.StartsWith("Removed line") || n.StartsWith("Changed line")));
        Assert.Contains(narrated, n =>
            n.Contains("fresh") && (n.StartsWith("Added line") || n.StartsWith("Changed line")));
        // The marker spans both columns, which is how a hunk header is carried too.
        Assert.Contains("No newline at end of file", narrated);
        Assert.DoesNotContain(narrated, n => n.StartsWith("Hunk header \\"));
    }
}
