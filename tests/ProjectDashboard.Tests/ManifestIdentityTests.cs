using ProjectDashboard.Models;
using ProjectDashboard.Services;
using Xunit;

namespace ProjectDashboard.Tests;

/// <summary>
/// Which saved record belongs to which repository. Wrong adoption is data loss — one project's
/// notes on another — so the refusals are asserted at least as hard as the adoptions: every
/// ambiguous shape here must leave both records exactly where they were.
/// </summary>
public class ManifestIdentityTests
{
    private const string OidA = "1111111111111111111111111111111111111111";
    private const string OidB = "2222222222222222222222222222222222222222";

    private static readonly RootStatus AvailableC =
        new(@"C:\projects", "", RootAvailability.Available, 0, false, 0, "");

    private static readonly RootStatus AvailableD =
        new(@"D:\work", "", RootAvailability.Available, 0, false, 0, "");

    private static Dictionary<string, ManifestEntry> Stored(params (string Path, RepoFingerprint? Print)[] entries)
    {
        var stored = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, print) in entries)
            stored[path] = new ManifestEntry
            {
                Manifest = new ProjectManifest { Description = $"notes for {path}" },
                Fingerprint = print,
            };
        return stored;
    }

    private static Dictionary<string, RepoFingerprint> Live(params (string Path, RepoFingerprint Print)[] entries)
    {
        var live = new Dictionary<string, RepoFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, print) in entries) live[path] = print;
        return live;
    }

    private static RepoFingerprint Print(string name, string[] oids, string remote = "") =>
        RepoFingerprint.For(name, oids, remote);

    /// <summary>Nothing in these cases exists on disk unless the case says otherwise.</summary>
    private static ManifestIdentityReport Reconcile(
        Dictionary<string, ManifestEntry> stored,
        Dictionary<string, RepoFingerprint> live,
        IReadOnlyList<RootStatus>? roots = null,
        Func<string, bool>? exists = null) =>
        ManifestIdentity.Reconcile(stored, live, roots ?? [AvailableC, AvailableD], exists ?? (_ => false));

    [Fact]
    public void ARepositoryFoundUnderAnotherRoot_TakesItsRecordWithIt()
    {
        var report = Reconcile(
            Stored((@"C:\projects\tabkit", Print("tabkit", [OidA]))),
            Live((@"D:\work\tabkit", Print("tabkit", [OidA]))));

        var adoption = Assert.Single(report.Adoptions);
        Assert.Equal(@"C:\projects\tabkit", adoption.FromPath);
        Assert.Equal(@"D:\work\tabkit", adoption.ToPath);
        Assert.Empty(report.Refusals);
        Assert.Empty(report.Orphans);
    }

    [Fact]
    public void ARenameInPlace_IsTheSameMatch()
    {
        var report = Reconcile(
            Stored((@"C:\projects\tabkit", Print("tabkit", [OidA]))),
            Live((@"C:\projects\tab-kit", Print("tab-kit", [OidA]))));

        Assert.Equal(@"C:\projects\tab-kit", Assert.Single(report.Adoptions).ToPath);
    }

    /// <summary>
    /// Two clones of one upstream share every strong field there is. Picking one is a coin flip
    /// with a reader's notes on it.
    /// </summary>
    [Fact]
    public void ARecordMatchingTwoRepositories_IsRefusedAndNamed()
    {
        var report = Reconcile(
            Stored((@"C:\projects\tabkit", Print("tabkit", [OidA]))),
            Live(
                (@"D:\work\tabkit", Print("tabkit", [OidA])),
                (@"D:\work\tabkit-fork", Print("tabkit-fork", [OidA]))));

        Assert.Empty(report.Adoptions);
        var refusal = Assert.Single(report.Refusals);
        Assert.Equal(ManifestRefusalReason.SeveralRepositoriesMatch, refusal.Reason);
        Assert.Equal(2, refusal.Candidates.Count);
        Assert.Contains("matches 2 repositories", ManifestIdentity.DescribeRefusals(report.Refusals));

        // The record is still there to be re-keyed by hand, and still listed as unplaced.
        Assert.Contains(report.Orphans, o => o.Path == @"C:\projects\tabkit");
    }

    [Fact]
    public void ARepositoryThatAlreadyHasItsOwnRecord_IsNeverOverwritten()
    {
        var report = Reconcile(
            Stored(
                (@"C:\projects\tabkit", Print("tabkit", [OidA])),
                (@"D:\work\tabkit", Print("tabkit", [OidA]))),
            Live((@"D:\work\tabkit", Print("tabkit", [OidA]))),
            exists: path => path == @"D:\work\tabkit");

        Assert.Empty(report.Adoptions);
        var refusal = Assert.Single(report.Refusals);
        Assert.Equal(ManifestRefusalReason.TargetAlreadyHasMetadata, refusal.Reason);
        Assert.Contains("already has metadata of its own", ManifestIdentity.DescribeRefusals(report.Refusals));
    }

    [Fact]
    public void TwoRecordsMatchingOneRepository_LeaveBothWhereTheyAre()
    {
        var report = Reconcile(
            Stored(
                (@"C:\projects\tabkit", Print("tabkit", [OidA])),
                (@"C:\projects\tabkit-old", Print("tabkit-old", [OidA]))),
            Live((@"D:\work\tabkit", Print("tabkit", [OidA]))));

        Assert.Empty(report.Adoptions);
        Assert.Equal(2, report.Refusals.Count);
        Assert.All(report.Refusals, r => Assert.Equal(ManifestRefusalReason.SeveralRecordsMatch, r.Reason));
    }

    /// <summary>
    /// A repository with no commits and no remote carries nothing that identifies it. The folder
    /// name is recorded and is deliberately not a match input: two folders share a name routinely.
    /// </summary>
    [Fact]
    public void ARecordWithNothingStrongToMatchOn_AdoptsNothingAndClaimsNothing()
    {
        var report = Reconcile(
            Stored((@"C:\projects\fresh", Print("fresh", []))),
            Live((@"D:\work\fresh", Print("fresh", []))));

        Assert.Empty(report.Adoptions);
        Assert.Empty(report.Refusals);
        Assert.Equal(@"C:\projects\fresh", Assert.Single(report.Orphans).Path);
    }

    [Fact]
    public void ARecordWithNoFingerprintAtAll_AdoptsNothing()
    {
        var report = Reconcile(
            Stored((@"C:\projects\legacy", null)),
            Live((@"D:\work\legacy", Print("legacy", [OidA]))));

        Assert.Empty(report.Adoptions);
        Assert.Empty(report.Refusals);
        Assert.Single(report.Orphans);
    }

    [Fact]
    public void ARemoteAlone_IdentifiesARecordThatCarriesNoRootCommit()
    {
        var report = Reconcile(
            Stored((@"C:\projects\tabkit", Print("tabkit", [], "https://github.com/owner/tabkit.git"))),
            Live((@"D:\work\tabkit", Print("tabkit", [OidA], "git@github.com:owner/tabkit"))));

        Assert.Equal(@"D:\work\tabkit", Assert.Single(report.Adoptions).ToPath);
    }

    /// <summary>
    /// A rewritten history under an unchanged remote is a different history. The remote match is
    /// not allowed to overrule the root commits that disagree with it.
    /// </summary>
    [Fact]
    public void RootCommitsThatDisagree_OverruleAMatchingRemote()
    {
        var report = Reconcile(
            Stored((@"C:\projects\tabkit", Print("tabkit", [OidA], "https://github.com/owner/tabkit"))),
            Live((@"D:\work\tabkit", Print("tabkit", [OidB], "https://github.com/owner/tabkit"))));

        Assert.Empty(report.Adoptions);
        Assert.Empty(report.Refusals);
    }

    [Fact]
    public void SeveralRootCommits_MatchAsASetRatherThanInOrder()
    {
        var report = Reconcile(
            Stored((@"C:\projects\tabkit", Print("tabkit", [OidB, OidA]))),
            Live((@"D:\work\tabkit", Print("tabkit", [OidA, OidB]))));

        Assert.Single(report.Adoptions);
    }

    // ── What is, and is not, a record with no repository ────────────

    [Theory]
    [InlineData(RootAvailability.Missing)]
    [InlineData(RootAvailability.Unreadable)]
    [InlineData(RootAvailability.Disabled)]
    public void ARecordUnderAFolderTheScanCouldNotRead_IsNotTreatedAsGone(RootAvailability availability)
    {
        var stored = Stored((@"D:\work\tabkit", Print("tabkit", [OidA])));
        var roots = new[] { AvailableC, AvailableD with { Availability = availability } };

        Assert.Empty(ManifestIdentity.OrphanKeys(stored, [], roots, _ => false));
    }

    [Fact]
    public void ARecordWhoseFolderIsStillOnDisk_IsNotGoneEvenWhenNoFolderCoversItAnyMore()
    {
        var stored = Stored((@"E:\elsewhere\tabkit", Print("tabkit", [OidA])));

        Assert.Empty(ManifestIdentity.OrphanKeys(stored, [], [AvailableC], _ => true));
    }

    [Fact]
    public void BeforeAnyScanHasReported_NoRecordIsCalledGone()
    {
        var stored = Stored((@"C:\projects\tabkit", Print("tabkit", [OidA])));

        Assert.Empty(ManifestIdentity.OrphanKeys(stored, [], [], _ => false));
    }

    [Fact]
    public void AProbeThatThrows_LeavesTheRecordAlone()
    {
        var stored = Stored((@"C:\projects\tabkit", Print("tabkit", [OidA])));

        Assert.Empty(ManifestIdentity.OrphanKeys(
            stored, [], [AvailableC], _ => throw new UnauthorizedAccessException("denied")));
    }

    [Fact]
    public void ARepositoryTheScanMet_IsNeverAnOrphanHoweverItIsSpelled()
    {
        var stored = Stored((@"C:\projects\tabkit", Print("tabkit", [OidA])));

        Assert.Empty(ManifestIdentity.OrphanKeys(stored, [@"C:\projects\tabkit\"], [AvailableC], _ => false));
    }

    [Fact]
    public void AnOrphanListCarriesTheRecordsOwnDescriptionAndWhenItWasLastSeen()
    {
        var seen = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var stored = Stored((@"C:\projects\tabkit", Print("tabkit", [OidA])));
        stored[@"C:\projects\tabkit"].LastSeenUtc = seen;

        var orphan = Assert.Single(ManifestIdentity.Orphans(stored, [], [AvailableC], _ => false));

        Assert.Equal("tabkit", orphan.Name);
        Assert.Equal(@"notes for C:\projects\tabkit", orphan.Description);
        Assert.Equal(seen, orphan.LastSeenUtc);
    }

    // ── The wording ────────────────────────────────────────────────

    [Fact]
    public void OneAdoption_IsNamedWithWhereItWent()
    {
        var text = ManifestIdentity.DescribeAdoptions([new ManifestAdoption(@"C:\a\tabkit", @"D:\b\tabkit", "tabkit")]);

        Assert.Contains("tabkit", text);
        Assert.Contains(@"D:\b\tabkit", text);
    }

    [Fact]
    public void SeveralAdoptions_AreCountedRatherThanListed()
    {
        var text = ManifestIdentity.DescribeAdoptions([
            new ManifestAdoption(@"C:\a\one", @"D:\b\one", "one"),
            new ManifestAdoption(@"C:\a\two", @"D:\b\two", "two")]);

        Assert.Contains("2 projects", text);
    }

    [Fact]
    public void NothingAdoptedAndNothingRefused_SaysNothingAtAll()
    {
        Assert.Equal("", ManifestIdentity.DescribeAdoptions([]));
        Assert.Equal("", ManifestIdentity.DescribeRefusals([]));
    }
}
