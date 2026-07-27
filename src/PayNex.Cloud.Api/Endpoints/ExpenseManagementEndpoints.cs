using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class ExpenseManagementEndpoints
{
    public static IEndpointRouteBuilder MapExpenseManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/expenses/lookups", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            return Results.Ok(await expenses.GetLookupsAsync(user));
        });

        app.MapGet("/api/expenses", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses,
            DateTime? from, DateTime? to, int? branchId, int? categoryId, string? term) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try { return Results.Ok(await expenses.ListAsync(user, from, to, branchId ?? 0, categoryId ?? 0, term)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/api/expenses/{id:long}", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses, long id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            var row = await expenses.GetAsync(user, id);
            return row.Count == 0 ? Results.NotFound(new { message = "Expense record was not found." }) : Results.Ok(row);
        });

        app.MapPost("/api/expenses", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses, ExpenseUpsertRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var id = await expenses.CreateAsync(user, request);
                return Results.Ok(new { expenseId = id, message = "Expense created successfully." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapPut("/api/expenses/{id:long}", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses, long id, ExpenseUpsertRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                await expenses.UpdateAsync(user, id, request);
                return Results.Ok(new { expenseId = id, message = "Expense updated successfully." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapDelete("/api/expenses/{id:long}", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses, long id) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                await expenses.DeleteAsync(user, id);
                return Results.Ok(new { message = "Expense deleted successfully." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/api/expense-categories", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            return Results.Ok(await expenses.GetLookupsAsync(user));
        });

        app.MapPost("/api/expense-categories", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses, ExpenseCategoryUpsertRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var id = await expenses.SaveCategoryAsync(user, request);
                return Results.Ok(new { expenseCategoryId = id, message = "Expense category saved." });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (SqlException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/api/expense-report", async (HttpContext http, AuthTokenService tokens, ExpenseManagementService expenses,
            DateTime? from, DateTime? to, string? month, int? year, int? branchId, int? categoryId) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var range = ExpenseManagementService.ResolveReportRange(from, to, month, year);
                var rows = await expenses.ListAsync(user, range.From, range.To, branchId ?? 0, categoryId ?? 0, null, true);
                var total = rows.Sum(x => x.TryGetValue("Amount", out var amount) && amount != null ? Convert.ToDecimal(amount) : 0m);
                return Results.Ok(new ExpenseReportResult(range.From, range.To, branchId ?? 0, categoryId ?? 0, range.Label, total, rows));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/api/reports/expenses/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens,
            ExpenseManagementService expenses, CloudReportHtmlService reports, DateTime? from, DateTime? to, string? month,
            int? year, int? branchId, int? categoryId, string? layout) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            try
            {
                var range = ExpenseManagementService.ResolveReportRange(from, to, month, year);
                var rows = await expenses.ListAsync(user, range.From, range.To, branchId ?? 0, categoryId ?? 0, null, true);
                await using var con = await db.OpenTenantAsync(user.DatabaseName);
                return Results.Content(reports.ExpenseReportHtml(con, rows, range.From, range.To, branchId ?? 0, categoryId ?? 0, range.Label, layout), "text/html; charset=utf-8");
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        return app;
    }
}
