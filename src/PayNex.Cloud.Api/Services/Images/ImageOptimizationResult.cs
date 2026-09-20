namespace PayNex.Cloud.Api.Services.Images;

public sealed class ImageOptimizationResult
{
    public required byte[] Data { get; init; }
    public string ContentType { get; init; } = "image/webp";
    public long OriginalSizeBytes { get; init; }
    public long OptimizedSizeBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Quality { get; init; }
    public bool WasResized { get; init; }

    public string SummaryMessage =>
        $"Image optimized: {FormatSize(OriginalSizeBytes)} → {FormatSize(OptimizedSizeBytes)}";

    public string Details => $"{Width} × {Height} • {(ContentType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? "WebP" : ContentType.Replace("image/", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant())}";

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        var kb = bytes / 1024d;
        if (kb < 1024)
            return kb >= 100 ? $"{kb:0} KB" : $"{kb:0.#} KB";
        var mb = kb / 1024d;
        return mb >= 10 ? $"{mb:0.#} MB" : $"{mb:0.##} MB";
    }
}
