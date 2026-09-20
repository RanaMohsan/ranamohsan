using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace PayNex.Cloud.Api.Services.Images;

public sealed class ImageOptimizationService : IImageOptimizationService
{
    private static readonly int[] ShrinkSteps = [600, 540, 480, 420, 360, 320];

    private readonly ItemImageOptions _options;
    private readonly ILogger<ImageOptimizationService> _log;

    public ImageOptimizationService(IOptions<ItemImageOptions> options, ILogger<ImageOptimizationService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<ImageOptimizationResult> OptimizeItemImageAsync(byte[] source, CancellationToken cancellationToken = default)
    {
        if (source == null || source.Length == 0)
            throw new ImageOptimizationException("The selected file is not a valid image.");
        if (source.Length > _options.MaxUploadSizeBytes)
            throw new ImageOptimizationException($"Image cannot exceed {FormatMb(_options.MaxUploadSizeBytes)}.");
        RejectUnsafeSignatures(source);

        ImageInfo info;
        IImageFormat format;
        try
        {
            format = Image.DetectFormat(source) ?? throw new ImageOptimizationException("Please select a JPG, PNG or WebP image.");
            info = Image.Identify(source);
        }
        catch (ImageOptimizationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Item image identify failed.");
            throw new ImageOptimizationException("The selected file is not a valid image.", ex);
        }

        if (!IsAllowedFormat(format))
            throw new ImageOptimizationException("Please select a JPG, PNG or WebP image.");
        if (info.Width <= 0 || info.Height <= 0)
            throw new ImageOptimizationException("The selected file is not a valid image.");
        if (info.Width > _options.MaxDecodedSide || info.Height > _options.MaxDecodedSide)
            throw new ImageOptimizationException("The selected image is too large to process.");
        if ((long)info.Width * info.Height > _options.MaxDecodedPixels)
            throw new ImageOptimizationException("The selected image is too large to process.");

        try
        {
            using var input = new MemoryStream(source, writable: false);
            using var image = await Image.LoadAsync(input, cancellationToken);
            image.Mutate(x => x.AutoOrient());
            StripMetadata(image);

            var originalWidth = image.Width;
            var originalHeight = image.Height;
            if (IsWebp(format)
                && source.Length <= _options.HardMaxOutputSizeBytes
                && originalWidth <= _options.MaxWidth
                && originalHeight <= _options.MaxHeight)
            {
                return new ImageOptimizationResult
                {
                    Data = source,
                    ContentType = "image/webp",
                    OriginalSizeBytes = source.Length,
                    OptimizedSizeBytes = source.Length,
                    Width = originalWidth,
                    Height = originalHeight,
                    Quality = 0,
                    WasResized = false
                };
            }

            if (originalWidth > _options.MaxWidth || originalHeight > _options.MaxHeight)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(_options.MaxWidth, _options.MaxHeight),
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            var wasResized = image.Width != originalWidth || image.Height != originalHeight;
            Candidate? chosen = null;
            var sides = BuildShrinkLadder(Math.Max(image.Width, image.Height));

            foreach (var maxSide in sides)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var frame = image.Clone(ctx => { });
                if (Math.Max(frame.Width, frame.Height) > maxSide)
                {
                    frame.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(maxSide, maxSide),
                        Sampler = KnownResamplers.Lanczos3
                    }));
                    wasResized = true;
                }

                var minBytes = await EncodeWebpAsync(frame, _options.MinimumQuality, cancellationToken);
                if (minBytes.Length > _options.HardMaxOutputSizeBytes)
                    continue;

                long target = _options.HardMaxOutputSizeBytes;
                if (minBytes.Length <= _options.PreferredSmallSizeBytes)
                    target = _options.PreferredSmallSizeBytes;
                else if (minBytes.Length <= _options.PreferredMediumSizeBytes)
                    target = _options.PreferredMediumSizeBytes;

                chosen = await SearchHighestQualityAsync(frame, target, cancellationToken);
                if (chosen != null)
                    break;
            }

            if (chosen == null || chosen.Data.Length > _options.HardMaxOutputSizeBytes)
                throw new ImageOptimizationException("Image could not be optimized. Please try another image.");

            return new ImageOptimizationResult
            {
                Data = chosen.Data,
                ContentType = "image/webp",
                OriginalSizeBytes = source.Length,
                OptimizedSizeBytes = chosen.Data.Length,
                Width = chosen.Width,
                Height = chosen.Height,
                Quality = chosen.Quality,
                WasResized = wasResized || chosen.Width != originalWidth || chosen.Height != originalHeight
            };
        }
        catch (ImageOptimizationException)
        {
            throw;
        }
        catch (UnknownImageFormatException ex)
        {
            throw new ImageOptimizationException("Please select a JPG, PNG or WebP image.", ex);
        }
        catch (InvalidImageContentException ex)
        {
            throw new ImageOptimizationException("The selected file is not a valid image.", ex);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Item image optimization failed.");
            throw new ImageOptimizationException("Image could not be optimized. Please try another image.", ex);
        }
    }

    private async Task<Candidate?> SearchHighestQualityAsync(Image frame, long targetBytes, CancellationToken ct)
    {
        var low = _options.MinimumQuality;
        var high = _options.MaximumQuality;
        Candidate? best = null;

        var start = Math.Clamp(_options.StartingQuality, low, high);
        var startBytes = await EncodeWebpAsync(frame, start, ct);
        if (startBytes.Length <= targetBytes)
        {
            best = new Candidate(startBytes, start, frame.Width, frame.Height);
            low = start + 1;
        }
        else
        {
            high = start - 1;
        }

        while (low <= high)
        {
            ct.ThrowIfCancellationRequested();
            var mid = (low + high + 1) / 2;
            if (mid > high) mid = high;
            var encoded = await EncodeWebpAsync(frame, mid, ct);
            if (encoded.Length <= targetBytes)
            {
                best = new Candidate(encoded, mid, frame.Width, frame.Height);
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (best == null)
        {
            var fallback = await EncodeWebpAsync(frame, _options.MinimumQuality, ct);
            if (fallback.Length <= _options.HardMaxOutputSizeBytes)
                best = new Candidate(fallback, _options.MinimumQuality, frame.Width, frame.Height);
        }

        return best;
    }

    public static string DetectRasterContentType(byte[] data)
    {
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return "image/webp";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
            return "image/png";
        return "image/png";
    }

    public static byte[] DecodeBase64Payload(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ImageOptimizationException("The selected file is not a valid image.");
        var raw = source.Contains(',') ? source[(source.IndexOf(',') + 1)..] : source;
        try
        {
            return Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new ImageOptimizationException("Invalid item image file.", ex);
        }
    }

    private static async Task<byte[]> EncodeWebpAsync(Image image, int quality, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var encoder = new WebpEncoder
        {
            Quality = Math.Clamp(quality, 1, 100),
            FileFormat = WebpFileFormatType.Lossy,
            Method = WebpEncodingMethod.Level4,
            SkipMetadata = true
        };
        await image.SaveAsync(ms, encoder, ct);
        return ms.ToArray();
    }

    private int[] BuildShrinkLadder(int currentMaxSide)
    {
        var list = new List<int>();
        foreach (var step in ShrinkSteps)
        {
            if (step <= currentMaxSide && step >= _options.MinDimension)
                list.Add(step);
        }
        if (list.Count == 0 || list[0] != currentMaxSide)
            list.Insert(0, currentMaxSide);
        return list.Distinct().OrderByDescending(x => x).ToArray();
    }

    private static void StripMetadata(Image image)
    {
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;
        image.Metadata.CicpProfile = null;
    }

    private static void RejectUnsafeSignatures(byte[] source)
    {
        if (LooksLike(source, "<svg") || LooksLike(source, "<?xml") || LooksLike(source, "<html") || LooksLike(source, "<!do"))
            throw new ImageOptimizationException("Please select a JPG, PNG or WebP image.");
        if (source.Length >= 2 && source[0] == (byte)'M' && source[1] == (byte)'Z')
            throw new ImageOptimizationException("Please select a JPG, PNG or WebP image.");
    }

    private static bool LooksLike(byte[] source, string ascii)
    {
        if (source.Length < ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
        {
            var c = (char)source[i];
            if (char.ToLowerInvariant(c) != ascii[i]) return false;
        }
        return true;
    }

    private static bool IsAllowedFormat(IImageFormat format) =>
        format is JpegFormat or PngFormat or WebpFormat
        || format.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
        || format.Name.Equals("PNG", StringComparison.OrdinalIgnoreCase)
        || format.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebp(IImageFormat format) =>
        format is WebpFormat || format.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase);

    private static string FormatMb(long bytes)
    {
        var mb = bytes / (1024d * 1024d);
        return mb >= 10 ? $"{mb:0} MB" : $"{mb:0.#} MB";
    }

    private sealed record Candidate(byte[] Data, int Quality, int Width, int Height);
}
