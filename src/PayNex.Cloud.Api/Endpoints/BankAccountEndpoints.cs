using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class BankAccountEndpoints
{
    public static IEndpointRouteBuilder MapBankAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bank-accounts", async (HttpContext http, AuthTokenService tokens, BankAccountService banks, bool? includeInactive, string? term) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            return Results.Ok(await banks.ListAsync(user, includeInactive == true, term));
        });

        app.MapGet("/api/bank-accounts/{id:int}", async (HttpContext http, AuthTokenService tokens, BankAccountService banks, int id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            var row = await banks.GetAsync(user, id);
            return row.Count == 0 ? Results.NotFound(new { message = "Bank account was not found." }) : Results.Ok(row);
        });

        app.MapGet("/api/bank-accounts/{id:int}/ledger", async (HttpContext http, AuthTokenService tokens, BankAccountService banks, int id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            var header = await banks.GetAsync(user, id);
            if (header.Count == 0) return Results.NotFound(new { message = "Bank account was not found." });
            return Results.Ok(await banks.LedgerAsync(user, id));
        });

        app.MapPost("/api/bank-accounts", async (HttpContext http, AuthTokenService tokens, BankAccountService banks, BankAccountRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var id = await banks.CreateAsync(user, request);
                return Results.Ok(new { bankAccountId = id, message = "Bank account saved." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = UniqueMessage(ex) }); }
        });

        app.MapPut("/api/bank-accounts/{id:int}", async (HttpContext http, AuthTokenService tokens, BankAccountService banks, int id, BankAccountRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                await banks.UpdateAsync(user, id, request);
                return Results.Ok(new { bankAccountId = id, message = "Bank account saved." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = UniqueMessage(ex) }); }
        });

        app.MapDelete("/api/bank-accounts/{id:int}", async (HttpContext http, AuthTokenService tokens, BankAccountService banks, int id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                await banks.DeleteAsync(user, id);
                return Results.Ok(new { message = "Bank account deleted." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        return app;
    }

    private static string UniqueMessage(SqlException ex) =>
        ex.Number is 2627 or 2601 ? "Bank Code already exists." : ex.Message;
}
