using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// The decoded-image cache spares a refetch of every badge on a theme flip, but it holds
/// decoded pixels: an entry ranges from a few KB to <c>800 x 800 x 4</c> bytes, so a cap
/// on the entry COUNT bounds nothing — 64 full-size entries retain ~164 MB for the life of
/// the process. The cap is therefore on bytes, the entry dropped is the least recently
/// used, and leaving a project drops that project's images outright.
/// </summary>
[Collection(MarkdownImageCollection.Name)]
public class MarkdownImageCacheTests
{
    /// <summary>Longest edge the decoder allows, so one entry is as large as an entry gets.</summary>
    private const int FullSizeEdge = 800;

    [Fact]
    public void ByteCap_DropsTheLeastRecentlyUsedEntry_AndKeepsTheOneJustRead()
    {
        ProjectDetailPage.ClearRemoteImageCache();

        // Measured, not assumed: the decoder's pixel format decides what one entry costs.
        ProjectDetailPage.CacheRemoteImage("https://host/full-0.png", FullSizeImage());
        var entryBytes = ProjectDetailPage.RemoteImageCacheBytes;
        var fits = (int)(ProjectDetailPage.MaxCachedRemoteImageBytes / entryBytes);
        Assert.InRange(fits, 3, 64);

        for (var i = 1; i < fits; i++)
            ProjectDetailPage.CacheRemoteImage($"https://host/full-{i}.png", FullSizeImage());

        Assert.Equal(fits, ProjectDetailPage.RemoteImageCacheCount);
        Assert.True(ProjectDetailPage.RemoteImageCacheBytes <= ProjectDetailPage.MaxCachedRemoteImageBytes);

        // Reading entry 0 makes entry 1 the least recently used.
        Assert.NotNull(ProjectDetailPage.TakeCachedRemoteImage("https://host/full-0.png"));

        ProjectDetailPage.CacheRemoteImage("https://host/overflow.png", FullSizeImage());

        Assert.True(ProjectDetailPage.RemoteImageCacheBytes <= ProjectDetailPage.MaxCachedRemoteImageBytes,
            $"cache holds {ProjectDetailPage.RemoteImageCacheBytes} bytes past a " +
            $"{ProjectDetailPage.MaxCachedRemoteImageBytes} cap");
        Assert.Equal(fits, ProjectDetailPage.RemoteImageCacheCount);
        Assert.NotNull(ProjectDetailPage.TakeCachedRemoteImage("https://host/full-0.png"));
        Assert.NotNull(ProjectDetailPage.TakeCachedRemoteImage("https://host/overflow.png"));
        Assert.Null(ProjectDetailPage.TakeCachedRemoteImage("https://host/full-1.png"));

        ProjectDetailPage.ClearRemoteImageCache();
    }

    [Fact]
    public void RecachingOneUrl_CountsItsBytesOnce()
    {
        ProjectDetailPage.ClearRemoteImageCache();

        ProjectDetailPage.CacheRemoteImage("https://host/badge.png", Image(200, 20));
        var afterFirst = ProjectDetailPage.RemoteImageCacheBytes;
        ProjectDetailPage.CacheRemoteImage("https://host/badge.png", Image(200, 20));

        Assert.Equal(1, ProjectDetailPage.RemoteImageCacheCount);
        Assert.Equal(afterFirst, ProjectDetailPage.RemoteImageCacheBytes);

        ProjectDetailPage.ClearRemoteImageCache();
    }

    [Fact]
    public void MovingToAnotherProject_ReleasesTheImages_AndReturningToTheSameOneDoesNot()
    {
        ProjectDetailPage.ReleaseRemoteImagesForProject(@"C:\projects\alpha");
        ProjectDetailPage.CacheRemoteImage("https://host/alpha-badge.png", Image(200, 20));
        Assert.Equal(1, ProjectDetailPage.RemoteImageCacheCount);

        // Same project (and the same path in another case) is not a switch.
        ProjectDetailPage.ReleaseRemoteImagesForProject(@"C:\PROJECTS\Alpha");
        Assert.Equal(1, ProjectDetailPage.RemoteImageCacheCount);

        ProjectDetailPage.ReleaseRemoteImagesForProject(@"C:\projects\beta");
        Assert.Equal(0, ProjectDetailPage.RemoteImageCacheCount);
        Assert.Equal(0, ProjectDetailPage.RemoteImageCacheBytes);
    }

    private static BitmapImage FullSizeImage() => Image(FullSizeEdge, FullSizeEdge);

    /// <summary>
    /// A decoded image the size the cache will see: encoded to PNG and taken back through
    /// the app's own decoder, so its retained pixel format is the one the cache measures.
    /// </summary>
    private static BitmapImage Image(int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * 4 * height], width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var decoded = ProjectDetailPage.DecodeBounded(stream);
        Assert.NotNull(decoded);
        return decoded;
    }
}
