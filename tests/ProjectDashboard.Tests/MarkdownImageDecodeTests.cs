using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectDashboard.Views.Pages;

namespace ProjectDashboard.Tests;

/// <summary>
/// README/CHANGELOG images are decoded under a bound before they reach layout. The
/// bound caps the LONGER edge only: capping both axes squares every image and decodes
/// ~160x the pixels of an 800-wide badge row, and capping width alone lets a tall
/// source expand to an unbounded height. A source too large to decode safely, or bytes
/// that are not an image at all, must come back null for the caller to render alt text.
/// </summary>
public class MarkdownImageDecodeTests
{
    [Theory]
    [InlineData(400, 40)]   // wide, under the cap
    [InlineData(200, 20)]   // badge-shaped
    [InlineData(64, 64)]    // square, under the cap
    public void SourceUnderTheCap_IsNotResizedAndIsNotUpscaled(int width, int height)
    {
        using var png = Png(width, height);
        var decoded = ProjectDetailPage.DecodeBounded(png);

        Assert.NotNull(decoded);
        Assert.Equal(width, decoded.PixelWidth);
        Assert.Equal(height, decoded.PixelHeight);
    }

    [Fact]
    public void WideSourceOverTheCap_IsWidthCappedAndKeepsItsRatio()
    {
        using var png = Png(1600, 160);
        var decoded = ProjectDetailPage.DecodeBounded(png);

        Assert.NotNull(decoded);
        Assert.Equal(800, decoded.PixelWidth);
        Assert.Equal(80, decoded.PixelHeight);
        AssertRatio(1600.0 / 160.0, decoded);
    }

    [Fact]
    public void TallSourceOverTheCap_IsHeightCappedAndKeepsItsRatio()
    {
        // The shape the width-only cap turned into 800x80000: capping the long edge
        // instead leaves the decoded pixel count in the tens of thousands.
        using var png = Png(40, 4000);
        var decoded = ProjectDetailPage.DecodeBounded(png);

        Assert.NotNull(decoded);
        Assert.Equal(800, decoded.PixelHeight);
        Assert.Equal(8, decoded.PixelWidth);
        AssertRatio(40.0 / 4000.0, decoded);
    }

    [Fact]
    public void SquareSourceOverTheCap_IsCappedOnBothEdgesBecauseTheyAreEqual()
    {
        using var png = Png(2000, 2000);
        var decoded = ProjectDetailPage.DecodeBounded(png);

        Assert.NotNull(decoded);
        Assert.Equal(800, decoded.PixelWidth);
        Assert.Equal(800, decoded.PixelHeight);
    }

    [Fact]
    public void SourceOverThePixelBudget_IsRefusedWithoutDecoding()
    {
        // 8000x8000 = 64M source pixels, past the 50M budget. The budget bounds decode
        // time, which tracks the source rather than the capped output.
        using var png = BlackWhitePng(8000, 8000);
        Assert.Null(ProjectDetailPage.DecodeBounded(png));
    }

    [Fact]
    public void CorruptBytes_ReturnNullRatherThanThrowing()
    {
        using var junk = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03, 0x04]);
        Assert.Null(ProjectDetailPage.DecodeBounded(junk));
    }

    [Fact]
    public void EmptyStream_ReturnsNull()
    {
        using var empty = new MemoryStream();
        Assert.Null(ProjectDetailPage.DecodeBounded(empty));
    }

    [Fact]
    public void DecodedImageIsFrozen_SoOneBitmapCanBeSharedAcrossRenders()
    {
        using var png = Png(120, 30);
        var decoded = ProjectDetailPage.DecodeBounded(png);

        Assert.NotNull(decoded);
        Assert.True(decoded.IsFrozen);
    }

    /// <summary>Decoded ratio must match the source ratio to within one pixel of rounding.</summary>
    private static void AssertRatio(double sourceRatio, BitmapImage decoded)
    {
        var decodedRatio = (double)decoded.PixelWidth / decoded.PixelHeight;
        var tolerance = sourceRatio / Math.Min(decoded.PixelWidth, decoded.PixelHeight);
        Assert.True(Math.Abs(decodedRatio - sourceRatio) <= tolerance,
            $"decoded {decoded.PixelWidth}x{decoded.PixelHeight} ratio {decodedRatio} != source ratio {sourceRatio}");
    }

    private static MemoryStream Png(int width, int height)
    {
        var stride = width * 4;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[stride * height], stride);
        return Encode(source);
    }

    /// <summary>1bpp keeps a very large synthetic source cheap to build and to encode.</summary>
    private static MemoryStream BlackWhitePng(int width, int height)
    {
        var stride = (width + 7) / 8;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.BlackWhite, null,
            new byte[stride * height], stride);
        return Encode(source);
    }

    private static MemoryStream Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}
