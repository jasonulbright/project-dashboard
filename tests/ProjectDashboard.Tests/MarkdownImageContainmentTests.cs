using System.IO;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// A rendered README is repository content, and its image sources are attacker-controlled the
/// moment the repository is not the reader's own. A source is opened only where it resolves
/// inside the repository the document came from: a rooted path and a "..\" walk both name any
/// file the user can read, and either would otherwise be read off disk and decoded.
/// </summary>
public class MarkdownImageContainmentTests
{
    private static string Root => Path.Combine(TestEnv.Root, "md-containment", "repo");

    [Theory]
    [InlineData("img.png")]
    [InlineData("sub/dir/img.png")]
    [InlineData(@"sub\dir\img.png")]
    [InlineData("./sub/img.png")]
    [InlineData("sub/../img.png")]
    public void ASourceInsideTheRepository_Resolves(string source)
    {
        var resolved = ProjectDetailPage.ContainedImagePath(Root, source);

        Assert.NotNull(resolved);
        Assert.StartsWith(Root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARootedSourceInsideTheRepository_StillResolves()
    {
        var inside = Path.Combine(Root, "docs", "img.png");

        Assert.Equal(inside, ProjectDetailPage.ContainedImagePath(Root, inside));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("../../outside.png")]
    [InlineData(@"..\outside.png")]
    [InlineData("sub/../../outside.png")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\attacker\share\payload.png")]
    [InlineData("/Windows/win.ini")]
    public void ASourceOutsideTheRepository_IsRefused(string source)
        => Assert.Null(ProjectDetailPage.ContainedImagePath(Root, source));

    /// <summary>
    /// The check is against the root plus its separator: a sibling whose name merely starts
    /// with the root's would pass a plain prefix comparison.
    /// </summary>
    [Fact]
    public void ASiblingDirectoryWithTheRootAsAPrefix_IsRefused()
        => Assert.Null(ProjectDetailPage.ContainedImagePath(Root, "../repo-secrets/img.png"));

    [Fact]
    public void TheRepositoryRootItself_IsNotAnImageInsideIt()
        => Assert.Null(ProjectDetailPage.ContainedImagePath(Root, "."));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0bad")]
    public void AnUnusableSource_IsRefusedWithoutThrowing(string source)
        => Assert.Null(ProjectDetailPage.ContainedImagePath(Root, source));

    [Fact]
    public void AnEmptyBasePath_RefusesEverything()
        => Assert.Null(ProjectDetailPage.ContainedImagePath("", "img.png"));

    /// <summary>A trailing separator on the root must not change which sources are contained.</summary>
    [Fact]
    public void ARootWithATrailingSeparator_ContainsTheSameSources()
    {
        var withSeparator = Root + Path.DirectorySeparatorChar;

        Assert.NotNull(ProjectDetailPage.ContainedImagePath(withSeparator, "sub/img.png"));
        Assert.Null(ProjectDetailPage.ContainedImagePath(withSeparator, "../outside.png"));
    }
}
