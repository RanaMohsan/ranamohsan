namespace PayNex.Cloud.Api.Services.Images;

public sealed class ItemImageOptions
{
    public const string SectionName = "ItemImage";

    public long MaxUploadSizeBytes { get; set; } = 25L * 1024 * 1024;
    public long HardMaxOutputSizeBytes { get; set; } = 30L * 1024;
    public long PreferredSmallSizeBytes { get; set; } = 10L * 1024;
    public long PreferredMediumSizeBytes { get; set; } = 20L * 1024;
    public int MaxWidth { get; set; } = 600;
    public int MaxHeight { get; set; } = 600;
    public int MinDimension { get; set; } = 300;
    public int StartingQuality { get; set; } = 82;
    public int MinimumQuality { get; set; } = 58;
    public int MaximumQuality { get; set; } = 85;
    public int MaxDecodedSide { get; set; } = 8192;
    public long MaxDecodedPixels { get; set; } = 40_000_000;
}
