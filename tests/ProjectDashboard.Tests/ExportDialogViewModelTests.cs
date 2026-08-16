using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Windows;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The export dialog's state: what the preview claims, what the summary counts, and what
/// survives to the next export. The preview truncates only what is SHOWN — the exported set is
/// asserted whole beside it, which is the honesty the dialog owes.
/// </summary>
public class ExportDialogViewModelTests
{
    private static ProjectInfo Project(string name, string visibility = "", bool hidden = false)
    {
        var p = PortfolioExportTests.NewProject(name);
        p.GitStatus.Visibility = visibility;
        p.IsHidden = hidden;
        return p;
    }

    private static ExportDialogViewModel NewDialog(
        IReadOnlyList<ProjectInfo>? all = null,
        IReadOnlyList<ProjectInfo>? view = null,
        ExportPreferences? remembered = null)
        => new(all ?? [Project("alpha")], view ?? [], Taxonomy.Seed(), remembered);

    [Fact]
    public void ThePreview_ShowsAtMostTwentyRowsWhileTheExportSetStaysWhole()
    {
        var many = Enumerable.Range(0, 25).Select(i => Project($"repo{i:D2}")).ToList();
        var dialog = NewDialog(many);

        // Header + the preview window, and not one row more.
        Assert.Equal(ExportDialogViewModel.PreviewRows + 1,
            dialog.PreviewText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("and 5 more", dialog.PreviewMoreNotice);
        Assert.Equal(25, dialog.ExportSet().Count);
        Assert.Contains("25 projects", dialog.SummaryText);
    }

    [Fact]
    public void ASmallSet_ShowsEveryRowAndNoTruncationNotice()
    {
        var dialog = NewDialog([Project("alpha"), Project("bravo")]);

        Assert.Equal(3, dialog.PreviewText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("", dialog.PreviewMoreNotice);
    }

    [Fact]
    public void ThePreview_IsTheCsvTheChoicesWouldActuallyWrite()
    {
        var dialog = NewDialog([Project("alpha")]);
        dialog.PathMode = ExportPathMode.Full;

        Assert.Contains(@"C:\projects\alpha", dialog.PreviewText);

        dialog.PathMode = ExportPathMode.FolderName;
        Assert.DoesNotContain(@"C:\projects", dialog.PreviewText);

        dialog.Columns.First(r => r.Key == "Name").Selected = false;
        Assert.DoesNotContain("Name,", dialog.PreviewText);
    }

    /// <summary>Internal repositories are as unshareable as private ones; the warning counts both.</summary>
    [Fact]
    public void PrivateAndInternalRepositories_AreCountedInAWarningTheDialogShowsBeforeAnyFileExists()
    {
        var dialog = NewDialog([Project("open", "public"), Project("locked", "private"), Project("shared", "internal")]);
        Assert.Contains("2 of these projects are private or internal repositories", dialog.PrivateWarning);

        var none = NewDialog([Project("open", "public")]);
        Assert.Equal("", none.PrivateWarning);
        Assert.Contains("internal", dialog.VisibilityChoices);
    }

    [Fact]
    public void CurrentViewOnly_SwitchesTheSourceToWhatTheDashboardShows()
    {
        var all = new[] { Project("alpha"), Project("bravo"), Project("charlie") };
        var dialog = NewDialog(all, view: [all[1]]);

        Assert.Equal(3, dialog.ExportSet().Count);
        dialog.CurrentViewOnly = true;
        Assert.Single(dialog.ExportSet());
        Assert.Contains("1 project,", dialog.SummaryText);
    }

    [Fact]
    public void NothingSelectedOrNothingMatching_DisablesTheExport()
    {
        var dialog = NewDialog([Project("alpha", "public")]);
        Assert.True(dialog.CanExport);

        dialog.VisibilityFilter = "private";
        Assert.False(dialog.CanExport);

        dialog.VisibilityFilter = "";
        foreach (var row in dialog.Columns) row.Selected = false;
        Assert.False(dialog.CanExport);
    }

    [Fact]
    public void RememberedChoices_ComeBackAndUnknownColumnKeysAreDropped()
    {
        var remembered = new ExportPreferences
        {
            Columns = ["Name", "Branch", "NoSuchColumn"],
            PathMode = "Omit",
            ExcludeHidden = true,
            CategoryFilter = "Web",
        };

        var dialog = NewDialog(remembered: remembered);

        Assert.Equal(["Name", "Branch"], dialog.Columns.Where(r => r.Selected).Select(r => r.Key));
        Assert.Equal(ExportPathMode.Omit, dialog.PathMode);
        Assert.True(dialog.ExcludeHidden);
        Assert.Equal("Web", dialog.CategoryFilter);

        var roundTrip = dialog.ToPreferences();
        Assert.Equal(["Name", "Branch"], roundTrip.Columns);
        Assert.Equal("Omit", roundTrip.PathMode);
    }

    /// <summary>A remembered filter naming a taxonomy value that no longer exists falls back to all.</summary>
    [Fact]
    public void ARememberedFilterWhoseValueIsGone_FallsBackToAllRatherThanFilteringOnAGhost()
    {
        var dialog = NewDialog(remembered: new ExportPreferences { CategoryFilter = "Departed" });

        Assert.Equal("", dialog.CategoryFilter);
    }

    [Fact]
    public void ResetToDefaults_RestoresTheOriginalColumnsAndClearsEveryFilter()
    {
        var dialog = NewDialog([Project("alpha"), Project("ghost", hidden: true)]);
        dialog.Columns.First(r => r.Key == "Name").Selected = false;
        dialog.PathMode = ExportPathMode.Full;
        dialog.ExcludeHidden = true;

        dialog.ResetToDefaultsCommand.Execute(null);

        Assert.Equal(
            PortfolioExport.Registry.Where(c => c.DefaultOn).Select(c => c.Key),
            dialog.Columns.Where(r => r.Selected).Select(r => r.Key));
        Assert.Equal(ExportPathMode.FolderName, dialog.PathMode);
        Assert.False(dialog.ExcludeHidden);
        Assert.Equal(2, dialog.ExportSet().Count);
    }

    [Fact]
    public void TheFilterChoices_ComeFromTheReadersOwnTaxonomyLists()
    {
        var taxonomy = Taxonomy.Seed();
        taxonomy.Categories.Add(new TaxonomyEntry { Name = "Clients" });
        var dialog = new ExportDialogViewModel([Project("alpha")], [], taxonomy, null);

        Assert.Contains("Clients", dialog.CategoryChoices);
        Assert.Equal("", dialog.CategoryChoices[0]);
    }
}
