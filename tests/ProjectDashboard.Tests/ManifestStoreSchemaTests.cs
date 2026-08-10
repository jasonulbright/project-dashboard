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

    // ── A save that arrives after the record moved ─────────────────

    /// <summary>
    /// A surface that opened a project before a scan re-keyed its record still holds the old path.
    /// Written by path alone, the edit lands in a fresh empty record at a folder nobody is looking
    /// at, and the record the reader was editing never receives it — the edit is gone at the next
    /// launch with nothing said. Following happens under the store's own lock, which is the only
    /// place the re-key and the write can be ordered against each other.
    /// </summary>
    [Fact]
    public void AnEditWrittenAgainstThePathARecordMovedOff_LandsOnTheRecord()
    {
        var store = new ManifestStore();
        var identity = Print("alpha", "aaa");
        store.Save(AlphaPath, new ProjectManifest { Description = "before", Category = "Tools" });
        store.ApplyScan(
            [new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = identity },
            DateTimeOffset.UtcNow);

        Assert.True(store.Save(AlphaPath, new ProjectManifest { Description = "after", Category = "Tools" }, identity));

        var reloaded = new ManifestStore();
        Assert.True(reloaded.TryGet(BetaPath, out var moved));
        Assert.Equal("after", moved!.Description);
        // No empty record left behind at the vacated folder.
        Assert.False(reloaded.TryGet(AlphaPath, out _));
        Assert.Single(reloaded.Snapshot());
    }

    /// <summary>
    /// The trail is followed only by the repository that left. A different repository that later
    /// occupies the vacated folder keeps its own key — redirecting its metadata onto the one that
    /// moved away would be the wrong-adoption failure by another route.
    /// </summary>
    [Fact]
    public void AnEditFromADifferentRepositoryInTheVacatedFolder_KeepsItsOwnRecord()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "the one that moved" });
        store.ApplyScan(
            [new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("alpha", "aaa") },
            DateTimeOffset.UtcNow);

        Assert.True(store.Save(AlphaPath, new ProjectManifest { Description = "a newcomer" }, Print("alpha", "zzz")));

        var reloaded = new ManifestStore();
        Assert.True(reloaded.TryGet(AlphaPath, out var newcomer));
        Assert.Equal("a newcomer", newcomer!.Description);
        Assert.True(reloaded.TryGet(BetaPath, out var moved));
        Assert.Equal("the one that moved", moved!.Description);
    }

    /// <summary>A caller offering no identity is asking about a folder, and is answered literally.</summary>
    [Fact]
    public void AnEditWithNoIdentity_IsTakenAtThePathItNames()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "before" });
        store.ApplyScan(
            [new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("alpha", "aaa") },
            DateTimeOffset.UtcNow);

        Assert.True(store.Save(AlphaPath, new ProjectManifest { Description = "literal" }));

        Assert.True(store.TryGet(AlphaPath, out var literal));
        Assert.Equal("literal", literal!.Description);
        Assert.True(store.TryGet(BetaPath, out var moved));
        Assert.Equal("before", moved!.Description);
    }

    /// <summary>A record re-keyed twice is still reachable from the path the first move left.</summary>
    [Fact]
    public void ARecordThatMovedTwice_IsStillReachedFromWhereItStarted()
    {
        const string GammaPath = @"E:\archive\gamma";
        var store = new ManifestStore();
        var identity = Print("alpha", "aaa");
        store.Save(AlphaPath, new ProjectManifest { Description = "before" });
        store.ApplyScan([new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = identity }, DateTimeOffset.UtcNow);
        store.ApplyScan([new ManifestAdoption(BetaPath, GammaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [GammaPath] = identity }, DateTimeOffset.UtcNow);

        Assert.True(store.Save(AlphaPath, new ProjectManifest { Description = "after" }, identity));

        Assert.True(new ManifestStore().TryGet(GammaPath, out var moved));
        Assert.Equal("after", moved!.Description);
    }

    /// <summary>
    /// A read is a question about a folder, never about a repository that left it. Following one
    /// would hand a fresh repository in the vacated folder the metadata of the one that moved.
    /// </summary>
    [Fact]
    public void AReadAgainstTheVacatedPath_DoesNotFollowTheRecord()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "before" });
        store.ApplyScan([new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("alpha", "aaa") }, DateTimeOffset.UtcNow);

        Assert.False(store.TryGet(AlphaPath, out var found));
        Assert.Null(found);
    }

    /// <summary>
    /// The trail lives exactly as long as the record it points at. Forgetting that record leaves
    /// nothing for a later save to follow.
    /// </summary>
    [Fact]
    public void ForgettingTheMovedRecord_TakesItsTrailWithIt()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "before" });
        store.ApplyScan([new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("alpha", "aaa") }, DateTimeOffset.UtcNow);
        Assert.Single(store.ForwardSnapshot());

        Assert.True(store.Forget([BetaPath]));

        Assert.Empty(store.ForwardSnapshot());
        Assert.Empty(new ManifestStore().ForwardSnapshot());
    }

    /// <summary>A record taking the vacated key again is what that key means; the trail gives way.</summary>
    [Fact]
    public void ARecordTakingTheVacatedPathAgain_ClearsTheTrail()
    {
        var store = new ManifestStore();
        store.Save(AlphaPath, new ProjectManifest { Description = "before" });
        store.ApplyScan([new ManifestAdoption(AlphaPath, BetaPath, "alpha")],
            new Dictionary<string, RepoFingerprint> { [BetaPath] = Print("alpha", "aaa") }, DateTimeOffset.UtcNow);

        store.Save(AlphaPath, new ProjectManifest { Description = "a newcomer" }, Print("alpha", "zzz"));
        store.ApplyScan([], new Dictionary<string, RepoFingerprint>(), DateTimeOffset.UtcNow.AddHours(2));

        Assert.Empty(store.ForwardSnapshot());
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
