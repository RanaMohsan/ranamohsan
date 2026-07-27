using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Middleware;

public sealed class AuditTrailMiddleware
{
    private readonly RequestDelegate _next;

    public AuditTrailMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AuthTokenService tokens, AuditTrailService audit)
    {
        await _next(context);

        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            context.Response.StatusCode >= 400)
        {
            return;
        }

        var user = ApiAuth.RequireUser(context, tokens);
        if (user == null) return;

        try
        {
            await audit.WriteAsync(user, $"{method}_{path}".Replace('/', '_').Trim('_').ToUpperInvariant(), path, null, "API mutation completed successfully.", new
            {
                method,
                path,
                statusCode = context.Response.StatusCode,
                utc = DateTime.UtcNow
            });
        }
        catch
        {
            // Never fail the business request because audit write failed.
        }
    }
}
