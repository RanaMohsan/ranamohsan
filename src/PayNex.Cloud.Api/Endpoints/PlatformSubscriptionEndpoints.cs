using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class PlatformSubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapPlatformSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/subscriptions/plans", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            return Results.Ok(await service.ListPlansAsync());
        });

        app.MapGet("/api/platform/subscriptions/dashboard", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            return Results.Ok(await service.GetDashboardAsync());
        });

        app.MapGet("/api/platform/subscriptions/report", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, string? type, string? status) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            return Results.Ok(await service.GetReportAsync(type, status));
        });

        app.MapGet("/api/platform/subscriptions/report/html", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, string? type, string? status) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            var html = await service.GetReportHtmlAsync(type, status);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/api/platform/subscriptions", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, string? term, string? status, int? planId, int? expiringWithinDays) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            return Results.Ok(await service.ListAsync(term, status, planId, expiringWithinDays));
        });

        app.MapGet("/api/platform/subscriptions/by-company/{companyCode}", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, string companyCode) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            return Results.Ok(await service.GetByCompanyAsync(companyCode));
        });

        app.MapGet("/api/platform/subscriptions/{id:int}", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, int id) =>
        {
            if (DenyUnlessOwner(http, tokens) is { } deny) return deny;
            var row = await service.GetAsync(id);
            if (!row.TryGetValue("subscription", out var sub) || sub is null)
                return Results.NotFound(new { message = "Subscription was not found." });
            if (sub is Dictionary<string, object?> header && header.Count == 0)
                return Results.NotFound(new { message = "Subscription was not found." });
            return Results.Ok(row);
        });

        app.MapPost("/api/platform/subscriptions", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, SubscriptionPostRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            try { return Results.Ok(await service.PostNewAsync(user, request)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapPost("/api/platform/subscriptions/{id:int}/renew", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, int id, SubscriptionRenewRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            try { return Results.Ok(await service.RenewAsync(user, id, request)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapPost("/api/platform/subscriptions/{id:int}/cancel", async (HttpContext http, AuthTokenService tokens, PlatformSubscriptionService service, int id, SubscriptionCancelRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!user.IsPlatformOwner) return Results.Forbid();
            try { return Results.Ok(await service.CancelAsync(user, id, request)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        return app;
    }

    private static IResult? DenyUnlessOwner(HttpContext http, AuthTokenService tokens)
    {
        var user = ApiAuth.RequireUser(http, tokens);
        if (user == null) return Results.Unauthorized();
        if (!user.IsPlatformOwner) return Results.Forbid();
        return null;
    }
}
