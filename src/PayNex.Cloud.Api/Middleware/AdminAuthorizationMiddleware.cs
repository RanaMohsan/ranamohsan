using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Middleware;

public sealed class AdminAuthorizationMiddleware
{
    private readonly RequestDelegate _next;

    public AdminAuthorizationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AuthTokenService tokens)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/admin/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            var admin = ApiAuth.RequireSuperAdmin(context, tokens);
            if (admin == null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "Super Admin authentication is required." });
                return;
            }
        }

        await _next(context);
    }
} 
