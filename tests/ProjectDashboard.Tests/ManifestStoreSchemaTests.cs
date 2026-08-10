using System.IO;
using System.Text.Json;
using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// The versioned store, and the shape written before it. A document read as the wrong shape
/// yields either one meaningless entry or none at all — both silently, which is how hand-typed
/// metadata disappears. Every write here still goes through the clone-then-swap the rest of
/// <see cref="ManifestStoreTests"/> pins down; a failed write must leave the live index
/// describing the file.
/// </summary>
[Collection("app-data-sandbox")]
public class ManifestStoreSchemaTests : IDisposable
{
    private static readonly string IndexPath = AppPaths.ManifestIndexFile;
    private static readonly string BlockedTmpPath = IndexPath + ".tmp";

    private const string AlphaPath = @"C:\projects\alpha";
    private const string BetaPath = @"D:\work\beta";

    public ManifestStoreSchemaTests()
    {
        UnblockTheWritePath();
        TestSandbox.ResetDataDir();
    }

    public void Dispose() => UnblockTheWritePath();

    private static void BlockTheWritePath() => Directory.CreateDirectory(BlockedTmpPath);

    private static void UnblockTheWritePath()
    {
        if (Directory.Exists(BlockedTmpPath)) Directory.Delete(BlockedTmpPath, recursive: true);
    }

    /// <summary>The shape every build before fingerprints wrote: a bare path→manifest map.</summary>
    private static void WriteLegacyIndex() => File.WriteAllText(IndexPath, $$"""
        {
          "{{AlphaPath.Replace(@"\", @"\\")}}": {
            "Description": "alpha desc",
            "ProjectType": "app",
            "Status": "active",
            "Category": "Tools",
            "ValidationSchedule": "monthly",
            "Notes": "hand typed"
          }
        }
        """);

    private static RepoFingerprint Print(string name, params string[] oids) =>
        RepoFingerprint.For(name, oids, "https://github.com/owner/" + name);

    [Fact]
    public void AStoreWrittenBeforeFingerprints_IsReadWithEveryFieldIntact()
    {
        WriteLegacyIndex();

        Assert.True(new ManifestStore().TryGet(AlphaPath, out var manifest));

        Assert.Equal("alpha desc", manifest!.Description);
        Assert.Equal("app", manifest.ProjectType);
        Assert.Equal("Tools", manifest.Category);
        Assert.Equal("monthly", manifest.ValidationSchedule);
        Assert.Equal("hand typed", manifest.Notes);
    }

    /// <summary>
    /// Reading the old shape does not rewrite it: a load that writes turns every read into a disk
    /// write, and a failure there has no caller to report to. The next write carries the shape up.
    /// </summary>
    [Fact]
    public void ReadingTheOldShape_LeavesTheFileAloneUntilSomethingIsWritten()
    {
        WriteLegacyIndex();
        var before = File.ReadAllText(IndexPath);

        var store = new ManifestStore();
        Assert.True(store.TryGet(AlphaPath, out _));
        Assert.Equal(before, File.ReadAllText(IndexPath));

        store.Save(BetaPath, new ProjectManifest { Description = "beta desc" });

        var document = JsonDocument.Parse(File.ReadAllText(IndexPath)).RootElement;
        Assert.Equal(ManifestStore.SchemaVersion, document.GetProperty("SchemaVersion").GetInt32());
        Assert.True(document.GetProperty("Entries").TryGetProperty(AlphaPath, out _));
    }

    [Fact]
    public void TheOldShapeSurvivesTheUpgrade_WithItsMetadataUnchanged()
    {
        WriteLegacyIndex();

        new ManifestStore().Save(BetaPath, new ProjectManifest { Description = "beta desc" });

        Assert.True(new ManifestStore().TryGet(AlphaPath, out var alpha));
        Assert.Equal("alpha desc", alpha!.Description);
        Assert.Equal("hand typed", alpha.Notes);
    }

    [Fact]
    public void AFingerprintAndItsStamps_RoundTripThroughTheFile()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        var seen = DateTimeOffset.UtcNow;

        Assert.True(store.ApplyScan([], new Dictionary<string, RepoFingerprint>
        {
            [AlphaPath] = Print("alpha", "aaa"),
        }, seen));

        var entry = new ManifestStore().Snapshot()[AlphaPath];
        Assert.Equal(["aaa"], entry.Fingerprint!.RootCommitOids);
        Assert.Equal("github.com/owner/alpha", entry.Fingerprint.RemoteUrl);
        Assert.Equal(seen, entry.FirstSeenUtc);
        Assert.Equal(seen, entry.LastSeenUtc);
    }

    /// <summary>
    /// Strength is read off the fields, never off the file. Stored, it becomes a fact an editor
    /// could set to disagree with the fingerprint it describes.
    /// </summary>
    [Fact]
    public void WhetherAFingerprintIsStrongEnoughToMatchOn_IsNotWrittenToTheFile()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest());
        store.ApplyScan([], new Dictionary<string, RepoFingerprint> { [AlphaPath] = Print("alpha", "aaa") },
            DateTimeOffset.UtcNow);

        Assert.DoesNotContain("IsStrong", File.ReadAllText(IndexPath));
    }

    [Fact]
    public void SavingAManifest_KeepsTheFingerprintTheScanRecorded()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "v1" });
        store.ApplyScan([], new Dictionary<string, RepoFingerprint> { [AlphaPath] = Print("alpha", "aaa") },
            DateTimeOffset.UtcNow);

        store.Save(AlphaPath, new ProjectManifest { Description = "v2" });

        var entry = new ManifestStore().Snapshot()[AlphaPath];
        Assert.Equal("v2", entry.Manifest.Description);
        Assert.Equal(["aaa"], entry.Fingerprint!.RootCommitOids);
    }

    /// <summary>
    /// A scan that concluded nothing new writes nothing. The one file holding metadata nothing
    /// else can reconstruct is not rewritten on every reconcile tick.
    /// </summary>
    [Fact]
    public void AScanThatChangedNothing_DoesNotRewriteTheFile()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        var live = new Dictionary<string, RepoFingerprint> { [AlphaPath] = Print("alpha", "aaa") };
        var seen = DateTimeOffset.UtcNow;
        store.ApplyScan([], live, seen);

        var written = File.GetLastWriteTimeUtc(IndexPath);
        var body = File.ReadAllText(IndexPath);

        Assert.True(store.ApplyScan([], live, seen.AddMinutes(1)));

        Assert.Equal(written, File.GetLastWriteTimeUtc(IndexPath));
        Assert.Equal(body, File.ReadAllText(IndexPath));
    }

    [Fact]
    public void ARecordThatWasReKeyed_LeavesNoEntryAtTheOldPath()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc", Category = "Tools" });

        Assert.True(store.ApplyScan(
            [new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("beta", "aaa") },
            DateTimeOffset.UtcNow));

        var reloaded = new ManifestStore();
        Assert.False(reloaded.TryGet(AlphaPath, out _));
        Assert.True(reloaded.TryGet(BetaPath, out var moved));
        Assert.Equal("alpha desc", moved!.Description);
        Assert.Equal("Tools", moved.Category);
    }

    [Fact]
    public void AReKeyOntoAPathThatAlreadyHasARecord_IsRefusedByTheStoreToo()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        store.Save(BetaPath, new ProjectManifest { Description = "beta desc" });

        store.ApplyScan([new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint>(), DateTimeOffset.UtcNow);

        Assert.True(store.TryGet(AlphaPath, out var alpha));
        Assert.Equal("alpha desc", alpha!.Description);
        Assert.True(store.TryGet(BetaPath, out var beta));
        Assert.Equal("beta desc", beta!.Description);
    }

    /// <summary>
    /// A re-key adopted in memory alone reads as metadata that moved, and is gone at the next
    /// launch — with the reader told nothing at either moment.
    /// </summary>
    [Fact]
    public void AReKeyWhoseWriteFailed_LeavesTheLiveIndexDescribingWhatIsOnDisk()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });

        BlockTheWritePath();

        Assert.False(store.ApplyScan(
            [new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("beta", "aaa") },
            DateTimeOffset.UtcNow));

        Assert.True(store.TryGet(AlphaPath, out _));
        Assert.False(store.TryGet(BetaPath, out _));
    }

    [Fact]
    public void ForgettingARecord_RemovesItAndLeavesTheOthers()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        store.Save(BetaPath, new ProjectManifest { Description = "beta desc" });

        Assert.True(store.Forget([AlphaPath]));

        var reloaded = new ManifestStore();
        Assert.False(reloaded.TryGet(AlphaPath, out _));
        Assert.True(reloaded.TryGet(BetaPath, out _));
    }

    [Fact]
    public void ForgettingWhoseWriteFailed_KeepsTheRecord()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });

        BlockTheWritePath();
        Assert.False(store.Forget([AlphaPath]));

        Assert.True(store.TryGet(AlphaPath, out _));
        Assert.Contains("alpha desc", File.ReadAllText(IndexPath));
    }

    [Fact]
    public void ASnapshotIsDetached_SoAMutatedCopyNeverReachesTheStore()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });

        store.Snapshot()[AlphaPath].Manifest.Description = "tampered";

        Assert.True(store.TryGet(AlphaPath, out var manifest));
        Assert.Equal("alpha desc", manifest!.Description);
    }

    /// <summary>
    /// A rewrite replaces the root commits a record was fingerprinted from. Recording the new
    /// ones is keyed on the path the record already has, so it can never re-key anything.
    /// </summary>
    [Fact]
    public void RecordingWhatARepositoryIsNow_RefreshesTheFingerprintInPlace()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        store.ApplyScan([], new Dictionary<string, RepoFingerprint> { [AlphaPath] = Print("alpha", "aaa") },
            DateTimeOffset.UtcNow);

        Assert.True(store.RecordFingerprint(AlphaPath, Print("alpha", "bbb")));

        var reloaded = new ManifestStore();
        Assert.Equal(["bbb"], reloaded.Snapshot()[AlphaPath].Fingerprint!.RootCommitOids);
        Assert.True(reloaded.TryGet(AlphaPath, out var manifest));
        Assert.Equal("alpha desc", manifest!.Description);
    }

    [Fact]
    public void RecordingAgainstAPathWithNoRecord_WritesNothing()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "alpha desc" });
        var body = File.ReadAllText(IndexPath);

        Assert.True(store.RecordFingerprint(BetaPath, Print("beta", "bbb")));

        Assert.Equal(body, File.ReadAllText(IndexPath));
    }
}
