using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class ProfessionalEndpointExtensions
{
    public static IEndpointRouteBuilder MapPayNexProfessionalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/security/permission-matrix", (HttpContext http, AuthTokenService tokens) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            return Results.Ok(new
            {
                roles = new[]
                {
                    new { role = "Admin", permissions = new[] { "All tenant operations", "Accounting setup", "Backup/restore", "Period close", "Approvals" } },
                    new { role = "Manager", permissions = new[] { "Sales", "Purchases", "Inventory operations", "Customers", "Vendors", "Reports" } },
                    new { role = "Cashier", permissions = new[] { "POS sales", "Shift operations", "Customer lookup", "Product lookup" } }
                }
            });
        });

        app.MapGet("/api/approvals", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? status) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT TOP 500 ApprovalRequestId,RequestType,DocumentNo,RequestedBy,RequestedAt,ApprovedBy,ApprovedAt,Status,Remarks
FROM ApprovalRequests WHERE (@Status='' OR Status=@Status) ORDER BY ApprovalRequestId DESC";
            cmd.Parameters.AddWithValue("@Status", (status ?? string.Empty).Trim());
            return Results.Ok(await SqlList.ReadAsync(cmd));
        });

        app.MapPost("/api/approvals/{id:long}/approve", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, long id, ApprovalActionRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            if (!user.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) && !user.RoleName.Equals("Manager", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Manager or Admin role is required." });
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"UPDATE ApprovalRequests SET Status='Approved',ApprovedBy=@UserId,ApprovedAt=SYSUTCDATETIME(),Remarks=@Remarks WHERE ApprovalRequestId=@Id AND Status='Pending'";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows == 0 ? Results.BadRequest(new { message = "Approval request not found or already processed." }) : Results.Ok(new { message = "Approval request approved." });
        });

        app.MapPost("/api/approvals/{id:long}/reject", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, long id, ApprovalActionRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            if (!user.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) && !user.RoleName.Equals("Manager", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Manager or Admin role is required." });
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"UPDATE ApprovalRequests SET Status='Rejected',ApprovedBy=@UserId,ApprovedAt=SYSUTCDATETIME(),Remarks=@Remarks WHERE ApprovalRequestId=@Id AND Status='Pending'";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows == 0 ? Results.BadRequest(new { message = "Approval request not found or already processed." }) : Results.Ok(new { message = "Approval request rejected." });
        });

        app.MapGet("/api/accounting/report/trial-balance", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT a.AccountNo,a.AccountName,a.AccountType,
ISNULL(SUM(CASE WHEN g.PostingDate < @From THEN g.DebitAmount-g.CreditAmount ELSE 0 END),0) OpeningBalance,
ISNULL(SUM(CASE WHEN g.PostingDate BETWEEN @From AND @To THEN g.DebitAmount ELSE 0 END),0) PeriodDebit,
ISNULL(SUM(CASE WHEN g.PostingDate BETWEEN @From AND @To THEN g.CreditAmount ELSE 0 END),0) PeriodCredit,
ISNULL(SUM(CASE WHEN g.PostingDate <= @To THEN g.DebitAmount-g.CreditAmount ELSE 0 END),0) ClosingBalance
FROM ChartOfAccounts a
LEFT JOIN GLEntries g ON g.AccountId=a.AccountId
WHERE a.IsActive=1
GROUP BY a.AccountNo,a.AccountName,a.AccountType
ORDER BY a.AccountNo";
            cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
            cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
            return Results.Ok(await SqlList.ReadAsync(cmd));
        });

        app.MapGet("/api/accounting/report/account-summary", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? asOf) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"SELECT a.AccountType,
SUM(CASE WHEN a.AccountType IN('Liability','Equity','Income') THEN ISNULL(g.CreditAmount-g.DebitAmount,0) ELSE ISNULL(g.DebitAmount-g.CreditAmount,0) END) Balance
FROM ChartOfAccounts a LEFT JOIN GLEntries g ON g.AccountId=a.AccountId AND g.PostingDate<=@AsOf
WHERE a.IsActive=1
GROUP BY a.AccountType
ORDER BY a.AccountType";
            cmd.Parameters.AddWithValue("@AsOf", (asOf ?? DateTime.Today).Date);
            return Results.Ok(await SqlList.ReadAsync(cmd));
        });

        app.MapGet("/api/accounting/report/inventory-valuation-detail", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
        {
            var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            return Results.Ok(await SqlList.ReadAsync(con, @"SELECT s.StoreId,st.StoreName,p.ProductCode,p.ProductName,s.Quantity,s.AverageCost,s.Quantity*s.AverageCost InventoryValue
FROM StockByStore s INNER JOIN Products p ON p.ProductId=s.ProductId INNER JOIN Stores st ON st.StoreId=s.StoreId
ORDER BY st.StoreName,p.ProductName"));
        });

        return app;
    }
}
