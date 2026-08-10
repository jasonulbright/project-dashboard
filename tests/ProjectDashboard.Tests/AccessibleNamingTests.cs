using ProjectDashboard.Models;
using ProjectDashboard.ViewModels.Pages;

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
    /// Ten digits, twelve tabs. A sheet that listed only the digit jumps would read as though the
    /// tabs past the tenth had no keyboard route at all. The count is asserted so that adding a
    /// thirteenth tab fails here rather than silently leaving the arrow-key row describing fewer
    /// tabs than the page hosts — that failure is the reminder, and it is meant to be loud.
    /// </summary>
    [Fact]
    public void TheCheatSheet_SaysHowToReachTheTabsPastTheDigits()
    {
        Assert.Equal(12, Enum.GetValues<DetailTab>().Length);

        var detail = ShortcutTable.All
            .Where(e => e.Group == ShortcutTable.DetailGroup)
            .ToList();

        Assert.Contains(detail, e => e.Gesture == "Ctrl+0");
        var arrows = detail.Single(e => e.Gesture == "Left / Right");
        Assert.Contains("twelve", arrows.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("twelfth", arrows.Description, StringComparison.OrdinalIgnoreCase);
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
    /// The dashboard card's name is composed out of the project it is drawn for, so what a reader
    /// hears is the binding the page declares and the property together. A repository level with
    /// its upstream has nothing to add, and a name that pastes that emptiness in ends on its own
    /// separator.
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

        Assert.Equal(expected, MarkupName.From(CardNameBinding(), project));
    }

    /// <summary>
    /// A repository with no local clone has no branch and nothing uncommitted to count. Naming it
    /// from a fixed sentence read out an empty branch and reported zero changes in a working tree
    /// that does not exist.
    /// </summary>
    [Fact]
    public void ACloudCard_SaysItIsNotClonedAndClaimsNoBranchOrChangeCount()
    {
        var project = new ProjectInfo
        {
            DirectoryName = "app-packager",
            DisplayName = "app-packager",
            IsRemoteOnly = true,
            RemoteSlug = "owner/app-packager"
        };

        var name = MarkupName.From(CardNameBinding(), project);

        Assert.Equal("app-packager, not cloned", name);
        Assert.DoesNotContain("branch", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uncommitted", name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A status git could not report is not a clean working tree. The count is measured, so a card
    /// that never got one says so instead of claiming zero.
    /// </summary>
    [Fact]
    public void ACardWhoseStatusFailed_ClaimsNoChangeCount()
    {
        var project = new ProjectInfo
        {
            DisplayName = "trackr",
            GitStatus = new GitStatus { HasError = true }
        };

        Assert.Equal("trackr, status unavailable", MarkupName.From(CardNameBinding(), project));
    }

    /// <summary>
    /// The generated container carries the name a reader enumerates the grid by; the Border inside
    /// it is what the keyboard lands on. The two have to be the same name, or one of the two
    /// surfaces announces something the other does not.
    /// </summary>
    [Fact]
    public void TheFocusedCardElement_CarriesTheSameNameAsItsContainer()
    {
        var markup = MarkupName.Markup(DashboardXaml);
        var card = MarkupName.Element(markup,
            "//*[local-name()='DataTemplate']/*[local-name()='FocusableCard']", DashboardXaml);

        Assert.Equal(CardNameBinding(), card.GetAttribute("AutomationProperties.Name"));
    }

    private const string DashboardXaml = "src/ProjectDashboard/Views/Pages/DashboardPage.xaml";
    private const string DetailXaml = "src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml";
    private const string FileHistoryXaml = "src/ProjectDashboard/Views/Pages/FileHistoryView.xaml";

    private static string CardNameBinding()
    {
        var markup = MarkupName.Markup(DashboardXaml);
        var setter = MarkupName.Element(markup,
            "//*[local-name()='ListBox.ItemContainerStyle']/*[local-name()='Style']" +
            "/*[local-name()='Setter'][@Property='AutomationProperties.Name']", DashboardXaml);
        return setter.GetAttribute("Value");
    }

    /// <summary>
    /// A worktree row names a path, what is checked out there, and what git flags about it. Two of
    /// the three are absent for ordinary entries: a linked worktree git flags nothing about ended
    /// the name on a separator, and the main worktree ran its branch straight into its state.
    /// </summary>
    [Theory]
    [InlineData(false, false, @"C:\projects\trackr\.wt\fix", "fix", @"C:\projects\trackr\.wt\fix, fix")]
    [InlineData(true, false, @"C:\projects\trackr", "main", @"C:\projects\trackr, main, main worktree")]
    [InlineData(true, true, @"C:\projects\trackr", "main",
        @"C:\projects\trackr, main, main worktree · this checkout")]
    public void AWorktreeRow_IsNamedWithoutADanglingSeparatorOrARunOnState(
        bool isMain, bool isCurrent, string path, string branch, string expected)
    {
        var row = new WorktreeRow(
            new WorktreeEntry { Path = path, Branch = branch, IsMain = isMain }, isCurrent);

        Assert.Equal(expected, RowName(DetailXaml, "Worktrees of this repository", row));
    }

    /// <summary>A bare checkout has a state word standing in for the branch it has none of.</summary>
    [Fact]
    public void ABareWorktreeRow_IsNamedByTheStateThatStandsInForItsBranch()
    {
        var row = new WorktreeRow(
            new WorktreeEntry { Path = @"C:\projects\trackr.git", IsBare = true, IsMain = true }, IsCurrent: false);

        Assert.Equal(@"C:\projects\trackr.git, bare, main worktree",
            RowName(DetailXaml, "Worktrees of this repository", row));
    }

    /// <summary>A published release carries no state word, and the name used to end on its space.</summary>
    [Theory]
    [InlineData(false, false, "v2.0.0 — Rewrite engine")]
    [InlineData(true, false, "v2.0.0 — Rewrite engine draft")]
    [InlineData(false, true, "v2.0.0 — Rewrite engine prerelease")]
    public void AReleaseRow_IsNamedWithoutADanglingSeparator(bool draft, bool prerelease, string expected)
    {
        var release = new Release
        {
            TagName = "v2.0.0", Name = "Rewrite engine", IsDraft = draft, IsPrerelease = prerelease
        };

        Assert.Equal(expected, RowName(DetailXaml, "Releases", release));
    }

    /// <summary>A run GitHub reports no head branch for is named by what it is, not by "on".</summary>
    [Theory]
    [InlineData("main", "Build, success, on main")]
    [InlineData("", "Build, success")]
    public void AWorkflowRunRow_IsNamedWithoutADanglingPreposition(string branch, string expected)
    {
        var run = new WorkflowRun
        {
            DisplayTitle = "Build", Status = "completed", Conclusion = "success", Branch = branch
        };

        Assert.Equal(expected, RowName(DetailXaml, "Workflow runs", run));
    }

    /// <summary>An index-only gitlink has no URL, and the name used to end on the comma before it.</summary>
    [Theory]
    [InlineData("https://github.com/owner/lib", "vendor/lib, https://github.com/owner/lib")]
    [InlineData("", "vendor/lib")]
    public void ASubmoduleRow_IsNamedWithoutADanglingSeparator(string url, string expected)
    {
        var submodule = new SubmoduleEntry { Name = "lib", Path = "vendor/lib", Url = url };

        Assert.Equal(expected, RowName(DetailXaml, "Submodules of this repository", submodule));
    }

    /// <summary>A blank line in a blamed file has no text, and the name used to end on its colon.</summary>
    [Theory]
    [InlineData("var x = 1;", "Line 12, a1b2c3d4 by Ada: var x = 1;")]
    [InlineData("", "Line 12, a1b2c3d4 by Ada")]
    public void ABlameRow_IsNamedWithoutADanglingSeparator(string text, string expected)
    {
        var line = new BlameLine { LineNumber = 12, Sha = "a1b2c3d4e5", Author = "Ada", Text = text };

        Assert.Equal(expected, RowName(FileHistoryXaml, "Blame lines", line));
    }

    /// <summary>
    /// The name the list identified by <paramref name="listName"/> gives its rows, composed out of
    /// that list's own item-container style in the shipped markup.
    /// </summary>
    private static string RowName(string viewXaml, string listName, object row)
    {
        var markup = MarkupName.Markup(viewXaml);
        var binding = MarkupName.Element(markup,
            $"//*[local-name()='ListBox'][@AutomationProperties.Name='{listName}']" +
            "//*[local-name()='Setter'][@Property='AutomationProperties.Name']" +
            "//*[local-name()='MultiBinding']", viewXaml);

        return MarkupName.From(binding, row);
    }
}
