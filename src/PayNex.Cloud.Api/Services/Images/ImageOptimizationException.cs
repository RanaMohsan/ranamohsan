namespace PayNex.Cloud.Api.Services.Images;

public sealed class ImageOptimizationException : InvalidOperationException
{
    public ImageOptimizationException(string message) : base(message) { }
    public ImageOptimizationException(string message, Exception inner) : base(message, inner) { }
}
