using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayNex.Cloud.Api.Services.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PayNex.Cloud.Api.Tests;

public class ImageOptimizationServiceTests
{
    private const int HardMax = 30 * 1024;

    private static ImageOptimizationService CreateService(ItemImageOptions? options = null) =>
        new(Options.Create(options ?? new ItemImageOptions()), NullLogger<ImageOptimizationService>.Instance);

    [Fact]
    public async Task LargeJpeg_FitsHardMaxAndMaxDimension()
    {
        var jpeg = CreateJpeg(2000, 1500, noise: true, quality: 90);
        Assert.True(jpeg.Length > 50_000);
        var result = await CreateService().OptimizeItemImageAsync(jpeg);
        AssertSuccess(result, 2000, 1500);
    }

    [Fact]
    public async Task LargePng_FitsHardMax()
    {
        var png = CreatePng(1600, 1200, transparent: false, noise: true);
        var result = await CreateService().OptimizeItemImageAsync(png);
        AssertSuccess(result, 1600, 1200);
    }

    [Fact]
    public async Task TransparentPng_FitsHardMax()
    {
        var png = CreatePng(800, 800, transparent: true, noise: false);
        var result = await CreateService().OptimizeItemImageAsync(png);
        AssertSuccess(result, 800, 800);
    }

    [Fact]
    public async Task SmallJpeg_IsNotUpscaled()
    {
        var jpeg = CreateJpeg(250, 250, noise: false, quality: 80);
        var result = await CreateService().OptimizeItemImageAsync(jpeg);
        AssertSuccess(result, 250, 250);
        Assert.True(result.Width <= 250);
        Assert.True(result.Height <= 250);
    }

    [Fact]
    public async Task PortraitImage_PreservesAspectRatio()
    {
        var jpeg = CreateJpeg(900, 1600, noise: true, quality: 85);
        var result = await CreateService().OptimizeItemImageAsync(jpeg);
        AssertSuccess(result, 900, 1600);
        Assert.True(result.Height >= result.Width);
        var original = 900d / 1600d;
        var actual = result.Width / (double)result.Height;
        Assert.InRange(actual, original - 0.03, original + 0.03);
    }

    [Fact]
    public async Task LandscapeImage_PreservesAspectRatio()
    {
        var jpeg = CreateJpeg(1600, 900, noise: true, quality: 85);
        var result = await CreateService().OptimizeItemImageAsync(jpeg);
        AssertSuccess(result, 1600, 900);
        Assert.True(result.Width >= result.Height);
    }

    [Fact]
    public async Task SquareImage_StaysSquare()
    {
        var jpeg = CreateJpeg(1200, 1200, noise: true, quality: 85);
        var result = await CreateService().OptimizeItemImageAsync(jpeg);
        AssertSuccess(result, 1200, 1200);
        Assert.Equal(result.Width, result.Height);
    }

    [Fact]
    public async Task CorruptedBytes_ThrowInvalidImage()
    {
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0x01, 0x02, 0x03, 0x04 };
        var ex = await Assert.ThrowsAsync<ImageOptimizationException>(() => CreateService().OptimizeItemImageAsync(data));
        Assert.Contains("valid image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyBytes_AreRejected()
    {
        var ex = await Assert.ThrowsAsync<ImageOptimizationException>(() => CreateService().OptimizeItemImageAsync([]));
        Assert.Contains("valid image", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedSvg_IsRejected()
    {
        var svg = "<svg xmlns='http://www.w3.org/2000/svg'><rect width='10' height='10'/></svg>"u8.ToArray();
        var ex = await Assert.ThrowsAsync<ImageOptimizationException>(() => CreateService().OptimizeItemImageAsync(svg));
        Assert.Contains("JPG, PNG or WebP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedGif_IsRejected()
    {
        using var image = new Image<Rgba32>(32, 32);
        Fill(image, noise: false, transparent: false);
        using var ms = new MemoryStream();
        await image.SaveAsync(ms, new GifEncoder());
        var ex = await Assert.ThrowsAsync<ImageOptimizationException>(() => CreateService().OptimizeItemImageAsync(ms.ToArray()));
        Assert.Contains("JPG, PNG or WebP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverUploadLimit_IsRejected()
    {
        var huge = new byte[26 * 1024 * 1024];
        huge[0] = 0xFF; huge[1] = 0xD8; huge[2] = 0xFF;
        var ex = await Assert.ThrowsAsync<ImageOptimizationException>(() => CreateService().OptimizeItemImageAsync(huge));
        Assert.Contains("25 MB", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExifOrientation_IsAutoOriented()
    {
        using var image = new Image<Rgba32>(80, 160);
        Fill(image, noise: false, transparent: false);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 90 });
        var result = await CreateService().OptimizeItemImageAsync(ms.ToArray());
        AssertSuccess(result, 160, 80);
        Assert.True(result.Width >= result.Height);
    }

    [Fact]
    public async Task HighlyDetailedImage_StillFitsHardMax()
    {
        var png = CreatePng(900, 900, transparent: false, noise: true);
        var result = await CreateService().OptimizeItemImageAsync(png);
        AssertSuccess(result, 900, 900);
    }

    [Fact]
    public async Task SmallWebpWithinLimits_IsReturnedAsIs()
    {
        var webp = CreateWebp(400, 300, quality: 70);
        Assert.True(webp.Length <= HardMax);
        var result = await CreateService().OptimizeItemImageAsync(webp);
        Assert.Equal(webp, result.Data);
        Assert.Equal("image/webp", result.ContentType);
        Assert.False(result.WasResized);
        Assert.Equal(400, result.Width);
        Assert.Equal(300, result.Height);
    }

    [Fact]
    public async Task OutputCanBeDecodedAsWebp()
    {
        var jpeg = CreateJpeg(640, 480, noise: true, quality: 88);
        var result = await CreateService().OptimizeItemImageAsync(jpeg);
        AssertSuccess(result, 640, 480);
        Assert.Equal("image/webp", ImageOptimizationService.DetectRasterContentType(result.Data));
    }

    [Fact]
    public void DetectRasterContentType_SniffsMagicBytes()
    {
        Assert.Equal("image/jpeg", ImageOptimizationService.DetectRasterContentType(CreateJpeg(16, 16, noise: false, quality: 80)));
        Assert.Equal("image/png", ImageOptimizationService.DetectRasterContentType(CreatePng(16, 16, transparent: false, noise: false)));
        Assert.Equal("image/webp", ImageOptimizationService.DetectRasterContentType(CreateWebp(16, 16, quality: 70)));
    }

    private static void AssertSuccess(ImageOptimizationResult result, int originalWidth, int originalHeight)
    {
        Assert.True(result.Data.Length <= HardMax, $"optimized {result.Data.Length} bytes");
        Assert.Equal("image/webp", result.ContentType);
        Assert.True(result.Width <= 600);
        Assert.True(result.Height <= 600);
        Assert.True(result.Width <= originalWidth);
        Assert.True(result.Height <= originalHeight);
        using var decoded = Image.Load(result.Data);
        Assert.True(decoded.Metadata.DecodedImageFormat is WebpFormat);
        Assert.Equal(result.Width, decoded.Width);
        Assert.Equal(result.Height, decoded.Height);
        Assert.Equal("image/webp", ImageOptimizationService.DetectRasterContentType(result.Data));
    }

    private static byte[] CreateJpeg(int width, int height, bool noise, int quality)
    {
        using var image = new Image<Rgba32>(width, height);
        Fill(image, noise, transparent: false);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private static byte[] CreatePng(int width, int height, bool transparent, bool noise)
    {
        using var image = new Image<Rgba32>(width, height);
        Fill(image, noise, transparent);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] CreateWebp(int width, int height, int quality)
    {
        using var image = new Image<Rgba32>(width, height);
        Fill(image, noise: false, transparent: false);
        using var ms = new MemoryStream();
        image.Save(ms, new WebpEncoder { Quality = quality, FileFormat = WebpFileFormatType.Lossy });
        return ms.ToArray();
    }

    private static void Fill(Image<Rgba32> image, bool noise, bool transparent)
    {
        var rng = new Random(42);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (noise)
                    {
                        var a = (byte)(transparent && ((x + y) % 7 == 0) ? 0 : 255);
                        row[x] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), a);
                    }
                    else
                    {
                        var a = (byte)(transparent && x < 20 ? 0 : 255);
                        row[x] = new Rgba32(40, 90, 180, a);
                    }
                }
            }
        });
    }
}
