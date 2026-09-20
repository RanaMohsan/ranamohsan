using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class DesktopPlatformEndpoints
{
    public static IEndpointRouteBuilder MapDesktopPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/companies/{companyCode}/desktop-app", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, string companyCode) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            return Results.Ok(await desktop.GetDetailAsync(companyCode, includePasswords: true));
        });

        app.MapPost("/api/platform/companies/{companyCode}/desktop-app", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, string companyCode, DesktopAppRegisterRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            var result = await desktop.RegisterAsync(companyCode, request.AppName, request.AppVersion, request.Notes);
            return Results.Ok(new { message = result.Message, appId = result.AppId, companyCode });
        });

        app.MapPut("/api/platform/companies/{companyCode}/desktop-app", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, string companyCode, DesktopAppRegisterRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            var ok = await desktop.SaveAsync(companyCode, request.AppName, request.AppVersion, request.Notes);
            return ok
                ? Results.Ok(new { message = "Desktop app details saved.", companyCode })
                : Results.NotFound(new { message = "Desktop app is not registered for this company." });
        });

        app.MapPost("/api/platform/companies/{companyCode}/desktop-app/block", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, string companyCode, DesktopAppBlockRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            await desktop.SetCompanyBlockedAsync(companyCode, request.IsBlocked, request.Reason);
            return Results.Ok(new
            {
                message = request.IsBlocked ? "Company desktop app access is blocked." : "Company desktop app access is unblocked.",
                isBlocked = request.IsBlocked,
                companyCode
            });
        });

        app.MapPost("/api/platform/companies/{companyCode}/desktop-app/users/{id:int}/access", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, string companyCode, int id, DesktopUserAccessRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            var userName = string.IsNullOrWhiteSpace(request.UserName) ? id.ToString() : request.UserName.Trim();
            await desktop.SetUserAccessAsync(companyCode, id, userName, request.IsAllowed);
            return Results.Ok(new
            {
                message = request.IsAllowed ? "Desktop access granted." : "Desktop access revoked.",
                isAllowed = request.IsAllowed,
                userId = id
            });
        });

        app.MapPost("/api/platform/companies/{companyCode}/desktop-app/users/{id:int}/block", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, string companyCode, int id, DesktopAppBlockRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            var userName = string.IsNullOrWhiteSpace(request.UserName) ? id.ToString() : request.UserName.Trim();
            await desktop.SetUserBlockedAsync(companyCode, id, userName, request.IsBlocked, request.Reason);
            return Results.Ok(new
            {
                message = request.IsBlocked ? "Desktop user is blocked." : "Desktop user is unblocked.",
                isBlocked = request.IsBlocked,
                userId = id
            });
        });

        app.MapGet("/api/users/{id:int}/desktop-access", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, int id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            var (allowed, blocked, reason) = await desktop.GetUserAccessAsync(user.CompanyCode, id);
            var company = await desktop.GetCompanyBlockAsync(user.CompanyCode);
            return Results.Ok(new
            {
                userId = id,
                isAllowed = allowed && !blocked,
                isBlocked = blocked,
                blockReason = reason,
                companyBlocked = company.Blocked
            });
        });

        app.MapPost("/api/users/{id:int}/desktop-access", async (HttpContext http, AuthTokenService tokens, DesktopAccessService desktop, int id, DesktopUserAccessRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsCompanySuperAdmin && !HasPermission(user, "users.editUser") && !HasPermission(user, "users.assignPermissions"))
                return Results.Json(new { message = "You do not have permission to change desktop access." }, statusCode: StatusCodes.Status403Forbidden);
            var userName = string.IsNullOrWhiteSpace(request.UserName) ? id.ToString() : request.UserName.Trim();
            await desktop.SetUserAccessAsync(user.CompanyCode, id, userName, request.IsAllowed);
            return Results.Ok(new
            {
                message = request.IsAllowed ? "Desktop access granted." : "Desktop access revoked.",
                isAllowed = request.IsAllowed,
                userId = id
            });
        });

        return app;
    }

    private static bool HasPermission(UserSession user, string key)
    {
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(user.PermissionsJson ?? "{}")
                ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            return map.TryGetValue(key, out var allowed) && allowed;
        }
        catch
        {
            return false;
        }
    }
}
