using System.Xml;
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

    /// <summary>
    /// A working-file row draws its status as one letter, and a letter is what a reader is read.
    /// The column the row names has to spell the state out, while the drawn letter stays a letter.
    /// </summary>
    [Theory]
    [InlineData('M', '.', false, false, "M", "modified", ".", "unchanged")]
    [InlineData('.', 'M', false, false, ".", "unchanged", "M", "modified")]
    [InlineData('A', '.', false, false, "A", "added", ".", "unchanged")]
    [InlineData('.', 'D', false, false, ".", "unchanged", "D", "deleted")]
    [InlineData('R', '.', false, false, "R", "renamed", ".", "unchanged")]
    [InlineData('C', '.', false, false, "C", "copied", ".", "unchanged")]
    [InlineData('.', 'T', false, false, ".", "unchanged", "T", "type changed")]
    [InlineData('.', '.', true, false, ".", "unchanged", "U", "untracked")]
    [InlineData('U', 'U', false, true, "!", "conflicted", "!", "conflicted")]
    public void AWorkingFileStatus_IsDrawnAsALetterAndNamedAsAWord(
        char index, char worktree, bool untracked, bool conflicted,
        string stagedLetter, string stagedWord, string unstagedLetter, string unstagedWord)
    {
        var file = new WorkingFile
        {
            Path = "src/a.txt",
            IndexStatus = index,
            WorktreeStatus = worktree,
            IsUntracked = untracked,
            IsConflicted = conflicted
        };

        Assert.Equal(stagedLetter, file.StagedLabel);
        Assert.Equal(unstagedLetter, file.UnstagedLabel);
        Assert.Equal(stagedWord, file.StagedStatusName);
        Assert.Equal(unstagedWord, file.UnstagedStatusName);
    }

    /// <summary>
    /// The dashboard card's name is composed in markup out of four bound values, so what a reader
    /// hears is the format string and the properties together. A repository level with its upstream
    /// has nothing to add, and a name that pastes that emptiness in ends on its own separator.
    /// </summary>
    [Theory]
    [InlineData(0, 0, "trackr, branch main, 3 uncommitted")]
    [InlineData(2, 0, "trackr, branch main, 3 uncommitted ↑2")]
    [InlineData(0, 4, "trackr, branch main, 3 uncommitted ↓4")]
    [InlineData(2, 4, "trackr, branch main, 3 uncommitted ↑2 ↓4")]
    public void ADashboardCard_IsNamedWithoutADanglingSeparator(int ahead, int behind, string expected)
    {
        var project = new ProjectInfo
        {
            DisplayName = "trackr",
            GitStatus = new GitStatus
            {
                Branch = "main",
                ModifiedCount = 2,
                UntrackedCount = 1,
                AheadBy = ahead,
                BehindBy = behind
            }
        };

        Assert.Equal(expected, CardNameFromMarkup(project));
    }

    private const string DashboardXaml = "src/ProjectDashboard/Views/Pages/DashboardPage.xaml";

    /// <summary>
    /// Composes the card name the way WPF does, out of the shipped markup: the format string and
    /// the binding paths the page declares, resolved against a real project.
    /// </summary>
    private static string CardNameFromMarkup(ProjectInfo project)
    {
        var markup = new XmlDocument();
        markup.LoadXml(RepoSource.Read(DashboardXaml));
        var binding = markup.SelectSingleNode(
            "//*[local-name()='ListBox.ItemContainerStyle']/*[local-name()='Style']" +
            "/*[local-name()='Setter'][@Property='AutomationProperties.Name']" +
            "/*[local-name()='Setter.Value']/*[local-name()='MultiBinding']") as XmlElement;
        Assert.True(binding is not null, $"no card-name MultiBinding in {DashboardXaml}");

        // "{}" opens a XAML string that would otherwise be read as a markup extension.
        var format = binding!.GetAttribute("StringFormat");
        if (format.StartsWith("{}", StringComparison.Ordinal)) format = format[2..];

        var values = binding.ChildNodes.OfType<XmlElement>()
            .Where(b => b.LocalName == "Binding")
            .Select(b => Resolve(project, b.GetAttribute("Path")))
            .ToArray();

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, values);
    }

    private static object? Resolve(object? root, string path)
    {
        foreach (var step in path.Split('.'))
        {
            if (root is null) return null;
            var property = root.GetType().GetProperty(step);
            Assert.True(property is not null, $"{root.GetType().Name} has no {step}; the card name binds to it");
            root = property!.GetValue(root);
        }
        return root;
    }
}
