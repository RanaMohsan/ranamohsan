using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Security;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Middleware;

public sealed class RolePermissionMiddleware
{
    private readonly RequestDelegate _next;

    public RolePermissionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        AuthTokenService tokens,
        RolePermissionService permissions,
        ConnectionFactory db,
        SubscriptionAccessService subscriptionAccess)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;
        var protectedApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/platform", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/mobile/login", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/mobile/verify-otp", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/mobile/resend-otp", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/mobile/health", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/mobile/refresh", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/desktop/login", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/desktop/verify-otp", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/desktop/resend-otp", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/desktop/health", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/desktop/refresh", StringComparison.OrdinalIgnoreCase);

        if (protectedApi)
        {
            var user = ApiAuth.RequireUser(context, tokens);
            if (user != null && !permissions.CanAccess(user, method, path, out var reason))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = reason });
                return;
            }

            if (user != null &&
                !user.IsPlatformOwner &&
                SubscriptionAccessService.IsPostingRoute(method, path))
            {
                var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
                var access = subscriptionAccess.Evaluate(tenant);
                if (!access.CanPost)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "SUBSCRIPTION_POST_LOCKED",
                        message = subscriptionAccess.PostLockedMessage(access),
                        expiryDate = access.ExpiryDate?.ToString("yyyy-MM-dd"),
                        daysPast = access.DaysPast,
                        phase = access.Phase.ToString(),
                    });
                    return;
                }
            }
        }

        await _next(context);
    }
}
