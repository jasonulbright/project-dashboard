using System.IO;
using System.Xml;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using ProjectDashboard.ViewModels.Pages;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The saved-metadata section on the Settings page. Forgetting a record is the only deletion in
/// this feature, it happens by hand, and the page has to say plainly what it is offering to drop.
/// </summary>
[Collection("app-data-sandbox")]
public class SettingsMetadataSurfaceTests : IDisposable
{
    private readonly string _fixtures = TestEnv.NewDir("settings-metadata");
    private readonly string _root;

    public SettingsMetadataSurfaceTests()
    {
        TestSandbox.ResetDataDir();
        _root = Path.Combine(_fixtures, "projects");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TestEnv.TryDeleteTree(_fixtures);

    private SettingsService SaveRoots()
    {
        var service = new SettingsService();
        service.Save(new AppSettings
        {
            ProjectRoots = [new ProjectRoot { Path = _root }],
            GhPath = Path.Combine(_fixtures, "no-such-gh.exe"),
            RefreshIntervalSeconds = 7200,
        });
        return service;
    }

    private async Task<string> NewRepoAsync(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        await Git.RunAsync(path, "init", "-b", "main");
        File.WriteAllText(Path.Combine(path, "file.txt"), "one\n");
        await Git.RunAsync(path, "add", "-A");
        await Git.RunAsync(path, "commit", "-m", "initial commit");
        return path;
    }

    private static async Task<SettingsViewModel> NewViewModelAsync(
        SettingsService settings, ManifestStore store, ProjectDiscoveryService discovery)
    {
        var vm = new SettingsViewModel(settings, null!, null!, manifests: store, discovery: discovery);
        await vm.MetadataLoad;
        return vm;
    }

    [Fact]
    public async Task ARecordWhoseFolderIsGone_IsListedWithItsDescriptionAndOfferedForgetting()
    {
        var settings = SaveRoots();
        var store = new ManifestStore();
        var discovery = new ProjectDiscoveryService(
            new GitService(), new GitHubService(settings), settings, store);

        var gone = await NewRepoAsync("alpha");
        var kept = await NewRepoAsync("beta");
        store.Save(gone, new ProjectManifest { Description = "a departed project" });
        store.Save(kept, new ProjectManifest { Description = "still here" });
        await discovery.ForceRefreshAllAsync();

        TestEnv.TryDeleteTree(gone);
        await discovery.ForceRefreshAllAsync();

        var vm = await NewViewModelAsync(settings, store, discovery);

        var row = Assert.Single(vm.MetadataOrphans);
        Assert.Equal("alpha", row.Name);
        Assert.Equal(gone, row.Path);
        Assert.Equal("a departed project", row.Description);
        Assert.Contains("Last seen", row.LastSeen);
        Assert.True(vm.HasMetadataOrphans);
        Assert.Contains("1 record names a folder that is no longer there", vm.MetadataSummary);

        await vm.ForgetMetadataCommand.ExecuteAsync(row);

        Assert.Empty(vm.MetadataOrphans);
        Assert.False(vm.HasMetadataOrphans);
        Assert.Contains(gone, vm.MetadataStatus);
        Assert.False(new ManifestStore().TryGet(gone, out _));
        // Forgetting one record leaves every other one alone.
        Assert.True(new ManifestStore().TryGet(kept, out _));
    }

    [Fact]
    public async Task ForgettingAllOfThem_DropsOnlyTheRecordsOnScreen()
    {
        var settings = SaveRoots();
        var store = new ManifestStore();
        var discovery = new ProjectDiscoveryService(
            new GitService(), new GitHubService(settings), settings, store);

        var goneOne = await NewRepoAsync("alpha");
        var goneTwo = await NewRepoAsync("beta");
        var kept = await NewRepoAsync("gamma");
        foreach (var path in new[] { goneOne, goneTwo, kept })
            store.Save(path, new ProjectManifest { Description = Path.GetFileName(path) });
        await discovery.ForceRefreshAllAsync();

        TestEnv.TryDeleteTree(goneOne);
        TestEnv.TryDeleteTree(goneTwo);
        await discovery.ForceRefreshAllAsync();

        var vm = await NewViewModelAsync(settings, store, discovery);
        Assert.Equal(2, vm.MetadataOrphans.Count);

        await vm.ForgetAllMetadataCommand.ExecuteAsync(null);

        Assert.Empty(vm.MetadataOrphans);
        Assert.Contains("Forgot 2 records", vm.MetadataStatus);
        var reloaded = new ManifestStore();
        Assert.False(reloaded.TryGet(goneOne, out _));
        Assert.False(reloaded.TryGet(goneTwo, out _));
        Assert.True(reloaded.TryGet(kept, out _));
    }

    /// <summary>
    /// Before a scan has reported, no record can be said to name a folder that is gone. Reporting
    /// zero there would read as a clean bill rather than as an unanswered question.
    /// </summary>
    [Theory]
    [InlineData(3, 0, 0, "No scan has reported yet")]
    [InlineData(3, 0, 1, "Every record names a folder that is still there")]
    [InlineData(3, 1, 1, "1 record names a folder that is no longer there")]
    [InlineData(3, 2, 1, "2 records name folders that are no longer there")]
    public void TheSummary_SaysWhatIsHeldAndWhatIsUnplaced(int stored, int orphans, int roots, string expected) =>
        Assert.Contains(expected, SettingsViewModel.DescribeMetadata(stored, orphans, roots));

    [Fact]
    public void OneStoredRecord_IsCountedInTheSingular() =>
        Assert.Contains("1 project has saved metadata", SettingsViewModel.DescribeMetadata(1, 0, 1));

    [Fact]
    public void ARecordNoScanHasMet_SaysSoRatherThanShowingABlankDate()
    {
        var row = ProjectMetadataRow.From(new ManifestOrphan(@"C:\gone\alpha", "alpha", "", null));

        Assert.Equal("Not seen by a scan on this machine", row.LastSeen);
        Assert.Equal("", row.Description);
    }

    [Fact]
    public void ALongDescription_IsShortenedRatherThanWrappingTheWholeRow()
    {
        var row = ProjectMetadataRow.From(
            new ManifestOrphan(@"C:\gone\alpha", "alpha", new string('x', 200), null));

        Assert.Equal(ProjectMetadataRow.DescriptionLength + 1, row.Description.Length);
        Assert.EndsWith("…", row.Description);
    }

    /// <summary>
    /// The section states where metadata lives and that it follows a repository. A reader deciding
    /// whether to forget a record has to be told the app never wrote anything into the repository
    /// it names.
    /// </summary>
    [Fact]
    public void TheSectionSaysMetadataLivesOutsideRepositoriesAndFollowsOneThatMoves()
    {
        var markup = RepoSource.Read("src/ProjectDashboard/Views/Pages/SettingsPage.xaml");

        Assert.Contains("Project Metadata", markup);
        Assert.Contains("kept outside your repositories", markup);
        Assert.Contains("its metadata moves with it", markup);
        Assert.Contains("SettingsMetadataSummary", markup);
        Assert.Contains("Forget the saved metadata for this folder", markup);
        Assert.Contains("Forget every saved record for folders that are no longer there", markup);
    }

    /// <summary>Every control the section adds carries a name, per the page's own standard.</summary>
    [Fact]
    public void EveryControlInTheSection_IsNamedForAReader()
    {
        var document = new XmlDocument();
        document.LoadXml(RepoSource.Read("src/ProjectDashboard/Views/Pages/SettingsPage.xaml"));

        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("d", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        namespaces.AddNamespace("ui", "http://schemas.lepo.co/wpfui/2022/xaml");

        var list = document.SelectSingleNode(
            "//d:ItemsControl[@AutomationProperties.Name='Saved metadata for folders that are no longer there']",
            namespaces);
        Assert.NotNull(list);

        foreach (XmlElement button in list!.SelectNodes(".//ui:Button", namespaces)!)
            Assert.NotEqual("", button.GetAttribute("AutomationProperties.Name"));
    }

    /// <summary>
    /// The row template is resolved at parse time and by nothing the compiler checks, and a page
    /// laid out with an empty list never instantiates it. One row is what proves it renders — and
    /// that the reader is told what the button beside it drops.
    /// </summary>
    [Fact]
    public void ARowRendersWithItsForgetButtonNamedForAReader()
        => StaHost.Run(() =>
        {
            StaHost.Checkpoint("saving settings");
            var service = new SettingsService();
            service.Save(new AppSettings { ProjectRoots = [new ProjectRoot { Path = @"C:\one" }] });

            StaHost.Checkpoint("building the view model");
            var viewModel = new SettingsViewModel(service, null!, null!);
            // The page's own load runs on this dispatcher and refills the list; seeding a row
            // before it settles would have it cleared out from under the layout below.
            StaHost.Checkpoint("pumping the metadata load");
            Pump(viewModel.MetadataLoad);
            viewModel.MetadataOrphans.Add(ProjectMetadataRow.From(
                new ManifestOrphan(@"C:\gone\alpha", "alpha", "a departed project", DateTimeOffset.UtcNow)));

            StaHost.Checkpoint("building the page");
            var window = new System.Windows.Window
            {
                Content = new ProjectDashboard.Views.Pages.SettingsPage(viewModel),
                Width = 1000,
                Height = 1400,
                ShowActivated = false,
            };
            try
            {
                StaHost.Checkpoint("showing the window");
                window.Show();
                StaHost.Checkpoint("laying the window out");
                window.UpdateLayout();
                StaHost.Checkpoint("reading the rendered buttons");

                var buttons = Descendants(window)
                    .OfType<Wpf.Ui.Controls.Button>()
                    .Where(b => System.Windows.Automation.AutomationProperties.GetName(b)
                        == "Forget the saved metadata for this folder")
                    .ToList();

                Assert.Single(viewModel.MetadataOrphans);
                var forget = Assert.Single(buttons);
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(forget);
                Assert.NotNull(peer);
                Assert.Equal("Forget the saved metadata for this folder", peer!.GetName());
            }
            finally { window.Close(); }
        });

    /// <summary>Runs the dispatcher's queue until <paramref name="work"/> has settled on it.</summary>
    private static void Pump(Task work)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!work.IsCompleted && DateTime.UtcNow < deadline)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);

        Assert.True(work.IsCompleted, "the page's metadata load never settled on the dispatcher");
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>
    /// The dashboard's own notice: a record that moved and a record that could not be placed are
    /// both facts about a reader's own typing, and neither is left to the log.
    /// </summary>
    [Fact]
    public void TheDashboardCarriesTheNoticeAsAPoliteLiveRegion()
    {
        var markup = RepoSource.Read("src/ProjectDashboard/Views/Pages/DashboardPage.xaml");

        Assert.Contains("DashboardMetadataNoticeText", markup);
        Assert.Contains("MetadataNoticeVisible", markup);
        Assert.Contains(
            "AutomationProperties.AutomationId=\"DashboardMetadataNoticeText\"\n                           AutomationProperties.LiveSetting=\"Polite\"",
            markup.Replace("\r\n", "\n"));
    }
}
