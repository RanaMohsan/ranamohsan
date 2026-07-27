using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Text.Json;

namespace PayNex.Cloud.Api.Middleware;

public sealed class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;
    private readonly IWebHostEnvironment _environment;

    public GlobalExceptionHandlingMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlingMiddleware> logger, IWebHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var traceId = context.TraceIdentifier;
            _logger.LogError(ex, "Unhandled request error. TraceId={TraceId} Path={Path}", traceId, context.Request.Path);
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            // Local/development builds should show the real server error so setup issues can be fixed quickly.
            // Production keeps the public message generic and logs the full exception server-side.
            var message = _environment.IsDevelopment()
                ? ex.Message
                : "An unexpected server error occurred.";

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                message,
                traceId
            }));
        }
    }
}
