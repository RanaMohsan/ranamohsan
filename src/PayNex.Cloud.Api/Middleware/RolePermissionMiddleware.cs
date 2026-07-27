using PayNex.Cloud.Api.Security;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Middleware;

public sealed class RolePermissionMiddleware
{
    private readonly RequestDelegate _next;

    public RolePermissionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AuthTokenService tokens, RolePermissionService permissions)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;
        var protectedApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase);

        if (protectedApi)
        {
            var user = ApiAuth.RequireUser(context, tokens);
            if (user != null && !permissions.CanAccess(user, method, path, out var reason))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = reason });
                return;
            }
        }

        await _next(context);
    }
}
