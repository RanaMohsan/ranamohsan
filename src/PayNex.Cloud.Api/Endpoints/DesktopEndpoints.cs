using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class DesktopEndpoints
{
    public static IEndpointRouteBuilder MapDesktopEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/desktop/health", () => Results.Ok(new
        {
            status = "OK",
            service = "InterNex Windows Desktop API",
            utc = DateTime.UtcNow,
            message = "Desktop client can connect. Company is resolved automatically from the user credential."
        }));

        app.MapPost("/api/desktop/login", async (HttpContext http, DesktopAuthService desktopAuth, DesktopLoginRequest request) =>
            await desktopAuth.LoginAsync(http, request)).RequireRateLimiting("auth");

        app.MapPost("/api/desktop/verify-otp", async (HttpContext http, DesktopAuthService desktopAuth, VerifyLoginOtpRequest request) =>
            await desktopAuth.VerifyOtpAsync(http, request)).RequireRateLimiting("auth");

        app.MapPost("/api/desktop/resend-otp", async (HttpContext http, DesktopAuthService desktopAuth, ResendLoginOtpRequest request) =>
            await desktopAuth.ResendOtpAsync(http, request)).RequireRateLimiting("auth");

        app.MapPost("/api/desktop/refresh", async (HttpContext http, DesktopAuthService desktopAuth, DesktopRefreshRequest? request) =>
            await desktopAuth.RefreshAsync(http, request)).RequireRateLimiting("auth");

        app.MapGet("/api/desktop/me", (HttpContext http, AuthTokenService tokens, DesktopAuthService desktopAuth) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            return user == null ? Results.Unauthorized() : desktopAuth.Me(user);
        });

        app.MapPost("/api/desktop/logout", async (HttpContext http, AuthTokenService tokens, DesktopAuthService desktopAuth, DesktopLogoutRequest? request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            return await desktopAuth.LogoutAsync(user, request);
        });

        return app;
    }
}
