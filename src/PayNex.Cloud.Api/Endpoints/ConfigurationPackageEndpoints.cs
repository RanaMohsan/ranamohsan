using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class ConfigurationPackageEndpoints
{
    public static IEndpointRouteBuilder MapConfigurationPackageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/configuration-packages", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, string? term) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            return Results.Ok(await packages.ListAsync(user, term));
        });

        app.MapGet("/api/configuration-packages/{id:long}", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, long id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            var package = await packages.GetAsync(user, id);
            return package.Count == 0 ? Results.NotFound(new { message = "Configuration package was not found." }) : Results.Ok(package);
        });

        app.MapGet("/api/configuration-packages/{id:long}/records", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, long id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try { return Results.Ok(await packages.PreviewAsync(user, id)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapPost("/api/configuration-packages", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, ConfigurationPackageUpsertRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var id = await packages.SaveAsync(user, request);
                return Results.Ok(new { packageId = id, message = "Configuration package saved." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/api/configuration-packages/{id:long}/export", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, long id, bool? templateOnly) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var file = await packages.ExportAsync(user, id, templateOnly == true);
                return Results.File(file.Content, packages.ContentType, file.FileName);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapPost("/api/configuration-packages/{id:long}/validate", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, long id, ConfigurationPackageImportRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try { return Results.Ok(await packages.ValidateImportAsync(user, id, request)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapPost("/api/configuration-packages/{id:long}/apply", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, long id, ConfigurationPackageApplyRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try { return Results.Ok(await packages.ApplyAsync(user, id, request.ImportId)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/api/configuration-packages/{id:long}/history", async (HttpContext http, AuthTokenService tokens,
            ConfigurationPackageService packages, long id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            return Results.Ok(await packages.HistoryAsync(user, id));
        });

        return app;
    }
}
