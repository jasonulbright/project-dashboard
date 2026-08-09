using System.Diagnostics;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A path that reads as inside the repository is not the same as a read that lands inside it.
/// A reparse point forwards the open wherever it points, and one can be inside a repository
/// without any privilege: a junction needs none to create, and git materializes a symlink blob
/// on checkout. Neither GetFullPath nor a link check on the last component of the path sees a
/// junction in the middle of it, so containment is decided on the resolved paths — and, for the
/// bytes that reach a decode, on the file the render already holds open.
/// </summary>
public class MarkdownImageJunctionTests
{
    /// <summary>
    /// A directory junction, or false when the runner would not make one. mklink /J needs no
    /// elevation and no Developer Mode, so a false here is a broken fixture, not a normal skip.
    /// </summary>
    private static bool TryCreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed record Fixture(string Repo, string Outside);

    private static Fixture NewFixture(string prefix)
    {
        var root = TestEnv.NewDir(prefix);
        var repo = Path.Combine(root, "repo");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.png"), "not really a png");
        File.WriteAllText(Path.Combine(repo, "docs", "inside.png"), "not really a png");
        return new Fixture(repo, outside);
    }

    [Fact]
    public void AJunctionInsideTheRepository_DoesNotForwardASourceOutOfIt()
    {
        var fixture = NewFixture("junction-escape");
        var link = Path.Combine(fixture.Repo, "link");
        Assert.True(TryCreateJunction(link, fixture.Outside), "the fixture could not create a junction");

        // The premise: the source spells a path under the repository and the bytes are readable.
        var lexical = Path.GetFullPath(Path.Combine(fixture.Repo, "link/secret.png"));
        Assert.StartsWith(fixture.Repo, lexical, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(lexical));

        // The open is the authority and is asserted first: it must refuse on the handle's own
        // final path, with no help from the resolver the path check runs.
        Assert.Null(ProjectDetailPage.OpenContainedImage(fixture.Repo, "link/secret.png", out var refused));
        Assert.True(refused);

        Assert.Null(ProjectDetailPage.ContainedImagePath(fixture.Repo, "link/secret.png"));
    }

    [Fact]
    public void AJunctionDeeperInTheSourcePath_IsRefusedToo()
    {
        var fixture = NewFixture("junction-escape-deep");
        var link = Path.Combine(fixture.Repo, "docs", "link");
        Assert.True(TryCreateJunction(link, fixture.Outside), "the fixture could not create a junction");

        Assert.Null(ProjectDetailPage.OpenContainedImage(fixture.Repo, "docs/link/secret.png", out var refused));
        Assert.True(refused);

        Assert.Null(ProjectDetailPage.ContainedImagePath(fixture.Repo, "docs/link/secret.png"));
    }

    /// <summary>
    /// Windows collapses ".." before the filesystem sees the path, so a step back out of a
    /// junction lands beside the junction rather than beside its target. The source names a
    /// file inside the repository and is treated as one.
    /// </summary>
    [Fact]
    public void SteppingBackOutOfAJunction_LandsBesideTheJunctionNotItsTarget()
    {
        var fixture = NewFixture("junction-dotdot");
        var link = Path.Combine(fixture.Repo, "docs", "link");
        Assert.True(TryCreateJunction(link, fixture.Outside), "the fixture could not create a junction");

        var resolved = ProjectDetailPage.ContainedImagePath(fixture.Repo, "docs/link/../secret.png");

        Assert.Equal(Path.Combine(fixture.Repo, "docs", "secret.png"), resolved);
        Assert.False(File.Exists(resolved));
    }

    [Fact]
    public void AJunctionThatStaysInsideTheRepository_StillResolves()
    {
        var fixture = NewFixture("junction-inside");
        var inner = Path.Combine(fixture.Repo, "assets");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "logo.png"), "not really a png");

        var link = Path.Combine(fixture.Repo, "shortcut");
        Assert.True(TryCreateJunction(link, inner), "the fixture could not create a junction");

        Assert.NotNull(ProjectDetailPage.ContainedImagePath(fixture.Repo, "shortcut/logo.png"));

        using var opened = ProjectDetailPage.OpenContainedImage(fixture.Repo, "shortcut/logo.png", out var refused);
        Assert.NotNull(opened);
        Assert.False(refused);
        Assert.Equal("not really a png", new StreamReader(opened).ReadToEnd());
    }

    /// <summary>
    /// A repository reached through a junction is an ordinary repository; resolving both sides
    /// is what keeps its own files contained.
    /// </summary>
    [Fact]
    public void ARepositoryReachedThroughAJunction_ContainsItsOwnFiles()
    {
        var fixture = NewFixture("junction-root");
        var alias = Path.Combine(TestEnv.NewDir("junction-root-alias"), "repo");
        Assert.True(TryCreateJunction(alias, fixture.Repo), "the fixture could not create a junction");

        Assert.NotNull(ProjectDetailPage.ContainedImagePath(alias, "docs/inside.png"));
        Assert.Null(ProjectDetailPage.ContainedImagePath(alias, "../outside/secret.png"));

        using var opened = ProjectDetailPage.OpenContainedImage(alias, "docs/inside.png", out var insideRefused);
        Assert.NotNull(opened);
        Assert.False(insideRefused);

        Assert.Null(ProjectDetailPage.OpenContainedImage(alias, "../outside/secret.png", out var outsideRefused));
        Assert.True(outsideRefused);
    }

    [Fact]
    public void AnOrdinaryRepositoryWithNoLinks_IsUnaffected()
    {
        var fixture = NewFixture("junction-none");

        Assert.NotNull(ProjectDetailPage.ContainedImagePath(fixture.Repo, "docs/inside.png"));
        // A source that does not exist still resolves: the resolver stops where the path does.
        Assert.NotNull(ProjectDetailPage.ContainedImagePath(fixture.Repo, "docs/missing/deeper.png"));
        Assert.Null(ProjectDetailPage.ContainedImagePath(fixture.Repo, "../outside/secret.png"));

        using var opened = ProjectDetailPage.OpenContainedImage(fixture.Repo, "docs/inside.png", out var insideRefused);
        Assert.NotNull(opened);
        Assert.False(insideRefused);
    }

    /// <summary>
    /// A source naming nothing is not a refusal: the line stays markdown text, as it did before
    /// any of this, while a source that resolves outside the repository is refused outright.
    /// Reporting one as the other would put the "image not loaded" line under every typo.
    /// </summary>
    [Fact]
    public void ASourceNamingNoFile_IsReportedAsNothingToOpenRatherThanARefusal()
    {
        var fixture = NewFixture("junction-missing");

        Assert.Null(ProjectDetailPage.OpenContainedImage(fixture.Repo, "docs/missing.png", out var missingRefused));
        Assert.False(missingRefused);

        Assert.Null(ProjectDetailPage.OpenContainedImage(fixture.Repo, "../outside/secret.png", out var outsideRefused));
        Assert.True(outsideRefused);
    }

    /// <summary>
    /// The decode reads the stream this returns, and the stream is the file the containment check
    /// cleared — the check runs on the open handle, not on a path reopened afterwards. While the
    /// stream is held the file cannot be replaced or deleted, so there is no window in which a
    /// reparse point could be swapped in between the decision and the bytes.
    /// </summary>
    [Fact]
    public void TheOpenedImage_IsTheFileTheCheckCleared()
    {
        var fixture = NewFixture("junction-pinned");
        var inside = Path.Combine(fixture.Repo, "docs", "inside.png");

        using var opened = ProjectDetailPage.OpenContainedImage(fixture.Repo, "docs/inside.png", out var refused);

        Assert.NotNull(opened);
        Assert.False(refused);
        Assert.Throws<IOException>(() => File.Delete(inside));
        Assert.Throws<IOException>(() => File.Move(inside, inside + ".moved"));
        Assert.Equal("not really a png", new StreamReader(opened).ReadToEnd());
    }

    /// <summary>
    /// A second open by path would put back the window the handle closes, whatever the first one
    /// decided. The page opens an image source exactly once — the stream the check cleared — and
    /// reads image bytes through no other route.
    /// </summary>
    [Fact]
    public void TheRenderer_ReadsImageBytesOnlyFromTheCheckedHandle()
    {
        var source = RepoSource.Read("src/ProjectDashboard/Views/Pages/ProjectDetailPage.xaml.cs");

        Assert.DoesNotContain("File.OpenRead", source);
        Assert.DoesNotContain("File.ReadAllBytes", source);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"new FileStream\("));
    }
}
