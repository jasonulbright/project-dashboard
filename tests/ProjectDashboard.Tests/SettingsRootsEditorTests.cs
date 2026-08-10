using System.Text.RegularExpressions;
using System.Windows;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The projects-folder list on the Settings page. What it refuses matters as much as what it
/// accepts: a folder listed twice, or nested inside one already listed, has the scan walk one
/// tree twice and leaves the reader unable to tell which row a card came from.
/// </summary>
[Collection("app-data-sandbox")]
public class SettingsRootsEditorTests
{
    public SettingsRootsEditorTests() => TestSandbox.ResetDataDir();

    private static SettingsViewModel NewVm(params ProjectRoot[] roots)
    {
        var service = new SettingsService();
        service.Save(roots.Length == 0
            ? new AppSettings { ProjectsRootPath = @"C:\projects" }
            : new AppSettings { ProjectRoots = roots });
        var vm = new SettingsViewModel(service, null!, null!);
        vm.LoadSettings();
        return vm;
    }

    [Fact]
    public void TheRows_AreLoadedInOrderWithTheirOwnExclusionsAndDepth()
    {
        var vm = NewVm(
            new ProjectRoot { Path = @"C:\one", ExcludedDirectories = ["Internal"], MaxDepth = 3 },
            new ProjectRoot { Path = @"D:\two", Enabled = false, Label = "Archive" });

        Assert.Equal([@"C:\one", @"D:\two"], vm.ProjectRoots.Select(r => r.Path));
        Assert.Equal("Internal", vm.ProjectRoots[0].ExcludedDirectories);
        Assert.Equal(3, vm.ProjectRoots[0].ScanDepth);
        Assert.False(vm.ProjectRoots[1].Enabled);
        Assert.Equal("Archive", vm.ProjectRoots[1].Label);
    }

    [Fact]
    public void AddingAFolder_AppendsItAndSaysSo()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" });

        vm.AddRootPath(@"D:\two");

        Assert.Equal([@"C:\one", @"D:\two"], vm.ProjectRoots.Select(r => r.Path));
        Assert.Contains(@"D:\two", vm.RootsStatus);
    }

    [Theory]
    [InlineData(@"C:\one", "already in the list")]
    [InlineData(@"C:\one\inner", "is inside")]
    [InlineData(@"C:\", "contains")]
    public void AFolderThatCollidesWithOneAlreadyListed_IsRefusedByName(string candidate, string reason)
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" });

        vm.AddRootPath(candidate);

        Assert.Single(vm.ProjectRoots);
        Assert.Contains(reason, vm.RootsStatus);
        Assert.Contains(@"C:\one", vm.RootsStatus);
    }

    /// <summary>
    /// Containment compares whole segments, and a drive root already carries its separator.
    /// Prefix matching alone puts <c>C:\projects2</c> under <c>C:\projects</c> and refuses a
    /// perfectly good folder by name.
    /// </summary>
    [Theory]
    [InlineData(@"C:\projects", @"C:\projects", true)]
    [InlineData(@"C:\projects\alpha", @"C:\projects", true)]
    [InlineData(@"C:\projects2", @"C:\projects", false)]
    [InlineData(@"C:\projects", @"C:\", true)]
    [InlineData(@"D:\projects", @"C:\", false)]
    public void RootContainment_ComparesWholeSegments(string candidate, string ancestor, bool contained)
        => Assert.Equal(contained, RepoPaths.IsAtOrUnder(candidate, ancestor));

    [Fact]
    public void ASiblingWhoseNameSharesAPrefix_IsNotRefused()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\projects" });

        vm.AddRootPath(@"C:\projects2");

        Assert.Equal([@"C:\projects", @"C:\projects2"], vm.ProjectRoots.Select(r => r.Path));
    }

    [Fact]
    public void ReorderingRows_ChangesTheOrderWrittenBack()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" }, new ProjectRoot { Path = @"D:\two" });

        vm.MoveRootDownCommand.Execute(vm.ProjectRoots[0]);
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal([@"D:\two", @"C:\one"], new SettingsService().Load().ProjectRoots.Select(r => r.Path));
    }

    [Fact]
    public void MovingTheFirstRowUp_IsANoOpRatherThanAReorder()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" }, new ProjectRoot { Path = @"D:\two" });

        vm.MoveRootUpCommand.Execute(vm.ProjectRoots[0]);

        Assert.Equal([@"C:\one", @"D:\two"], vm.ProjectRoots.Select(r => r.Path));
    }

    [Fact]
    public void RemovingTheLastFolder_SaysThereIsNothingLeftToScan()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" });

        vm.RemoveRootCommand.Execute(vm.ProjectRoots[0]);

        Assert.Empty(vm.ProjectRoots);
        Assert.True(vm.HasNoRoots);
        Assert.Contains("nothing to scan", vm.RootsStatus);
    }

    /// <summary>
    /// New Project and Clone need one destination. Removing the row that held it has to leave
    /// another row holding it, or both surfaces refuse for a reason nobody chose.
    /// </summary>
    [Fact]
    public void RemovingTheDefaultFolder_HandsTheDefaultToAnother()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" }, new ProjectRoot { Path = @"D:\two" });
        Assert.True(vm.ProjectRoots[0].IsDefault);

        vm.RemoveRootCommand.Execute(vm.ProjectRoots[0]);
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal(@"D:\two", new SettingsService().Load().DefaultRootPath);
    }

    [Fact]
    public void ChoosingADifferentDefault_IsTheOnlyRowMarkedAfterwards()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" }, new ProjectRoot { Path = @"D:\two" });

        vm.MakeDefaultRootCommand.Execute(vm.ProjectRoots[1]);
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal([false, true], vm.ProjectRoots.Select(r => r.IsDefault));
        Assert.Equal(@"D:\two", new SettingsService().Load().DefaultRootPath);
    }

    [Fact]
    public void SavingTheRows_WritesEveryPerRootFieldAndKeepsTheSingularMirrorInStep()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" });

        vm.AddRootPath(@"D:\two");
        vm.ProjectRoots[1].ExcludedDirectories = @"vendor, clients\archive";
        vm.ProjectRoots[1].ScanDepth = 3;
        vm.ProjectRoots[0].Enabled = false;
        vm.SaveSettingsCommand.Execute(null);

        var saved = new SettingsService().Load();
        Assert.Equal([@"C:\one", @"D:\two"], saved.ProjectRoots.Select(r => r.Path));
        Assert.False(saved.ProjectRoots[0].Enabled);
        Assert.Equal(["vendor", @"clients\archive"], saved.ProjectRoots[1].ExcludedDirectories);
        Assert.Equal(3, saved.ProjectRoots[1].MaxDepth);

        // The mirror follows the first ENABLED root, which is the one a downgraded build scans.
        Assert.Equal(@"D:\two", saved.ProjectsRootPath);
        Assert.Equal(["vendor", @"clients\archive"], saved.ExcludedDirectories);
    }

    [Fact]
    public void ADepthTypedBeyondTheCeiling_IsClampedOnTheWayToDisk()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" });

        vm.ProjectRoots[0].ScanDepth = 99;
        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal(ProjectRootSettings.MaxDepth, Assert.Single(new SettingsService().Load().ProjectRoots).MaxDepth);
    }

    /// <summary>A folder nothing has scanned yet says so rather than claiming to be fine.</summary>
    [Fact]
    public void ARowNoScanHasReportedOn_SaysItHasNotBeenScanned()
        => Assert.Equal("Not scanned yet", NewVm(new ProjectRoot { Path = @"C:\one" }).ProjectRoots[0].Status);

    [Fact]
    public void ADisabledRow_ReportsItselfAsOffRatherThanAsAFolderThatIsNotThere()
    {
        var vm = NewVm(new ProjectRoot { Path = @"C:\one" });

        vm.ProjectRoots[0].Enabled = false;

        Assert.Equal("Off", vm.ProjectRoots[0].Status);
    }

    // ── Shipped surface ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every control in a row carries a UIA name. XAML compiles to BAML with no runtime API for
    /// the attached property, so the markup itself is what is asserted.
    /// </summary>
    [Theory]
    [InlineData("Scan this projects folder")]
    [InlineData("Projects folder path")]
    [InlineData("Browse for this projects folder")]
    [InlineData("Move this projects folder up")]
    [InlineData("Move this projects folder down")]
    [InlineData("Remove this projects folder")]
    [InlineData("Name for this projects folder")]
    [InlineData("How deep to scan this projects folder")]
    [InlineData("Folders to skip under this projects folder")]
    [InlineData("Put new projects and clones in this folder")]
    [InlineData("Add a projects folder")]
    public void EveryRootRowControl_IsNamedForAScreenReader(string name)
        => Assert.Contains($@"AutomationProperties.Name=""{name}""",
            RepoSource.Read("src/ProjectDashboard/Views/Pages/SettingsPage.xaml"), StringComparison.Ordinal);

    /// <summary>The single-folder field is gone; a page carrying both would have two truths.</summary>
    [Fact]
    public void TheSettingsPage_NoLongerBindsASingleProjectsRootField()
    {
        var page = RepoSource.Read("src/ProjectDashboard/Views/Pages/SettingsPage.xaml");

        Assert.DoesNotContain("Binding ProjectsRootPath", page, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"ItemsSource=""\{Binding ProjectRoots\}"""), page);
    }

    /// <summary>
    /// Every StaticResource, converter and template reference in the page is resolved at parse
    /// time and by nothing the compiler checks, so the page is built and laid out for real.
    /// </summary>
    [Fact]
    public void TheSettingsPage_ResolvesItsMarkupAndRendersARootRow()
        => StaHost.Run(() =>
        {
            var service = new SettingsService();
            service.Save(new AppSettings
            {
                ProjectRoots = [new ProjectRoot { Path = @"C:\one" }, new ProjectRoot { Path = @"D:\two" }],
            });

            var viewModel = new SettingsViewModel(service, null!, null!);
            var page = new SettingsPage(viewModel);
            var window = new Window { Content = page, Width = 1000, Height = 900, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal(2, viewModel.ProjectRoots.Count);
            }
            finally
            {
                window.Close();
            }
        });
}
