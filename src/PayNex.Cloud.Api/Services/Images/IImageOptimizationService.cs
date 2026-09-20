namespace PayNex.Cloud.Api.Services.Images;

public interface IImageOptimizationService
{
    Task<ImageOptimizationResult> OptimizeItemImageAsync(byte[] source, CancellationToken cancellationToken = default);
}
