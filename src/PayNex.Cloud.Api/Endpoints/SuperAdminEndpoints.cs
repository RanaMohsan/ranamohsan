using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Security;
using PayNex.Cloud.Api.Services;
using PayNex.Cloud.Api.Validation;

namespace PayNex.Cloud.Api.Endpoints;

public static class SuperAdminEndpoints
{
    public static IEndpointRouteBuilder MapSuperAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapPost("/auth/login", async (SuperAdminService admins, AuthTokenService tokens, SuperAdminLoginRequest request) =>
        {
            var session = await admins.LoginAsync(request);
            if (session == null) return Results.BadRequest(new { message = "Invalid super admin username or password." });
            return Results.Ok(new { token = tokens.CreateSuperAdmin(session), user = session });
        });

        group.MapGet("/tenants", async (HttpContext http, TenantProvisioningService tenants, AuthTokenService tokens) =>
        {
            if (ApiAuth.RequireSuperAdmin(http, tokens) == null) return Results.Unauthorized();
            var list = await tenants.ListTenantsAsync();
            return Results.Ok(list);
        });

        group.MapPost("/tenants", async (HttpContext http, TenantProvisioningService tenants, AuthTokenService tokens, RequestValidationService validator, CreateTenantRequest request) =>
        {
            if (ApiAuth.RequireSuperAdmin(http, tokens) == null) return Results.Unauthorized();
            var validation = validator.ValidateCreateTenant(request);
            if (!validation.Ok) return Results.BadRequest(new { message = validation.Message });
            var result = await tenants.CreateTenantAsync(request);
            return Results.Ok(result);
        });

        group.MapGet("/tenants/{companyCode}", async (HttpContext http, TenantProvisioningService tenants, AuthTokenService tokens, string companyCode) =>
        {
            if (ApiAuth.RequireSuperAdmin(http, tokens) == null) return Results.Unauthorized();
            return Results.Ok(await tenants.GetTenantAsync(companyCode));
        });

        group.MapPut("/tenants/{companyCode}", async (HttpContext http, TenantProvisioningService tenants, AuthTokenService tokens, RequestValidationService validator, string companyCode, TenantCardUpdateRequest request) =>
        {
            if (ApiAuth.RequireSuperAdmin(http, tokens) == null) return Results.Unauthorized();
            var validation = validator.ValidateTenantCardUpdate(request);
            if (!validation.Ok) return Results.BadRequest(new { message = validation.Message });
            await tenants.UpdateTenantCardAsync(companyCode, request);
            return Results.Ok(await tenants.GetTenantAsync(companyCode));
        });

        group.MapPost("/tenants/{companyCode}/sandbox", async (HttpContext http, TenantProvisioningService tenants, AuthTokenService tokens, string companyCode, SandboxCreateRequest request) =>
        {
            if (ApiAuth.RequireSuperAdmin(http, tokens) == null) return Results.Unauthorized();
            var result = await tenants.CreateSandboxAsync(companyCode, request);
            return Results.Ok(new { message = "Sandbox database is ready.", tenant = result });
        });

        group.MapPost("/tenants/{companyCode}/status", async (HttpContext http, TenantProvisioningService tenants, AuthTokenService tokens, string companyCode, TenantStatusUpdateRequest request) =>
        {
            if (ApiAuth.RequireSuperAdmin(http, tokens) == null) return Results.Unauthorized();
            await tenants.UpdateTenantStatusAsync(companyCode, request);
            return Results.Ok(new { message = "Client/company status updated." });
        });

        return app;
    }
}
