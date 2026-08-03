using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;
using System.Data;

namespace PayNex.Cloud.Api.Endpoints;

public static class CounterAndDayCloseEndpoints
{
    public static IEndpointRouteBuilder MapCounterAndDayCloseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/counters", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int? storeId) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            var sid = storeId is > 0 ? storeId.Value : user.StoreId;
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureTerminalSchemaAsync(con);
            var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT t.TerminalId,t.StoreId,ISNULL(s.StoreCode,s.BranchCode) BranchCode,ISNULL(s.StoreName,s.BranchName) BranchName,
       t.TerminalCode,t.TerminalName,t.IsActive
FROM Terminals t
INNER JOIN Stores s ON s.StoreId=t.StoreId
WHERE t.StoreId=@StoreId
ORDER BY t.TerminalCode, t.TerminalId";
            cmd.Parameters.AddWithValue("@StoreId", sid);
            var counters = await SqlList.ReadAsync(cmd);
            return Results.Ok(new
            {
                storeId = sid,
                maxCounters = tenant?.MaxCounters ?? 0,
                counters
            });
        });

        app.MapPost("/api/counters", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CounterUpsertRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!CanManageCounters(user)) return Results.BadRequest(new { message = "Permission required to manage POS counters." });

            var storeId = request.StoreId > 0 ? request.StoreId : user.StoreId;
            var code = (request.TerminalCode ?? string.Empty).Trim().ToUpperInvariant();
            var name = (request.TerminalName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { message = "Counter code is required." });
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { message = "Counter name is required." });

            var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureTerminalSchemaAsync(con);

            await using (var storeCheck = con.CreateCommand())
            {
                storeCheck.CommandText = "SELECT COUNT(1) FROM Stores WHERE StoreId=@StoreId AND IsActive=1";
                storeCheck.Parameters.AddWithValue("@StoreId", storeId);
                if (Convert.ToInt32(await storeCheck.ExecuteScalarAsync() ?? 0) <= 0)
                    return Results.BadRequest(new { message = "Branch not found or inactive." });
            }

            if (request.TerminalId <= 0 && tenant?.MaxCounters > 0)
            {
                await using var countCmd = con.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(1) FROM Terminals WHERE StoreId=@StoreId AND IsActive=1";
                countCmd.Parameters.AddWithValue("@StoreId", storeId);
                var active = Convert.ToInt32(await countCmd.ExecuteScalarAsync() ?? 0);
                if (active >= tenant.MaxCounters)
                    return Results.BadRequest(new { message = $"Maximum counters for this company is {tenant.MaxCounters}." });
            }

            if (request.TerminalId > 0)
            {
                await using var upd = con.CreateCommand();
                upd.CommandText = @"
UPDATE Terminals SET TerminalCode=@Code,TerminalName=@Name,IsActive=@Active
WHERE TerminalId=@Id AND StoreId=@StoreId;
SELECT @@ROWCOUNT;";
                upd.Parameters.AddWithValue("@Id", request.TerminalId);
                upd.Parameters.AddWithValue("@StoreId", storeId);
                upd.Parameters.AddWithValue("@Code", code);
                upd.Parameters.AddWithValue("@Name", name);
                upd.Parameters.AddWithValue("@Active", request.IsActive);
                if (Convert.ToInt32(await upd.ExecuteScalarAsync() ?? 0) <= 0)
                    return Results.NotFound(new { message = "Counter not found for this branch." });
            }
            else
            {
                await using var ins = con.CreateCommand();
                ins.CommandText = @"
IF EXISTS(SELECT 1 FROM Terminals WHERE StoreId=@StoreId AND TerminalCode=@Code)
    THROW 50030, 'Counter code already exists for this branch.', 1;
INSERT INTO Terminals(StoreId,TerminalCode,TerminalName,IsActive)
OUTPUT INSERTED.TerminalId VALUES(@StoreId,@Code,@Name,@Active);";
                ins.Parameters.AddWithValue("@StoreId", storeId);
                ins.Parameters.AddWithValue("@Code", code);
                ins.Parameters.AddWithValue("@Name", name);
                ins.Parameters.AddWithValue("@Active", request.IsActive);
                await ins.ExecuteScalarAsync();
            }

            await using var list = con.CreateCommand();
            list.CommandText = @"
SELECT TerminalId,StoreId,TerminalCode,TerminalName,IsActive
FROM Terminals WHERE StoreId=@StoreId ORDER BY TerminalCode, TerminalId";
            list.Parameters.AddWithValue("@StoreId", storeId);
            return Results.Ok(new { message = "Counter saved.", storeId, counters = await SqlList.ReadAsync(list) });
        });

        app.MapGet("/api/day-closing/preview", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? businessDate) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            var date = (businessDate ?? DateTime.Today).Date;
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureDayClosingSchemaAsync(con);
            var summary = await BuildDaySummaryAsync(con, user.StoreId, user.BranchCode, date);
            await using var closed = con.CreateCommand();
            closed.CommandText = "SELECT TOP 1 DayClosingId,ClosedAt,ClosedByName,Remarks,Status FROM DayClosings WHERE StoreId=@StoreId AND BusinessDate=@BusinessDate";
            closed.Parameters.AddWithValue("@StoreId", user.StoreId);
            closed.Parameters.AddWithValue("@BusinessDate", date);
            var existing = await SqlList.ReadAsync(closed);
            return Results.Ok(new
            {
                businessDate = date.ToString("yyyy-MM-dd"),
                storeId = user.StoreId,
                branchCode = user.BranchCode,
                branchName = user.BranchName,
                isClosed = existing.Count > 0,
                closing = existing.FirstOrDefault(),
                summary
            });
        });

        app.MapPost("/api/day-closing/close", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DayCloseRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!CanCloseDay(user)) return Results.BadRequest(new { message = "Only Admin or Manager can close the business day." });

            var date = (request.BusinessDate ?? DateTime.Today).Date;
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureDayClosingSchemaAsync(con);

            await using (var openShifts = con.CreateCommand())
            {
                openShifts.CommandText = "SELECT COUNT(1) FROM Shifts WHERE StoreId=@StoreId AND Status='Open' AND CAST(OpenedAt AS DATE)=@BusinessDate";
                openShifts.Parameters.AddWithValue("@StoreId", user.StoreId);
                openShifts.Parameters.AddWithValue("@BusinessDate", date);
                if (Convert.ToInt32(await openShifts.ExecuteScalarAsync() ?? 0) > 0)
                    return Results.BadRequest(new { message = "Close all open shifts for this branch/date before Day Closing." });
            }

            await using (var exists = con.CreateCommand())
            {
                exists.CommandText = "SELECT COUNT(1) FROM DayClosings WHERE StoreId=@StoreId AND BusinessDate=@BusinessDate";
                exists.Parameters.AddWithValue("@StoreId", user.StoreId);
                exists.Parameters.AddWithValue("@BusinessDate", date);
                if (Convert.ToInt32(await exists.ExecuteScalarAsync() ?? 0) > 0)
                    return Results.BadRequest(new { message = "This business day is already closed for the current branch." });
            }

            var summary = await BuildDaySummaryAsync(con, user.StoreId, user.BranchCode, date);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
INSERT INTO DayClosings(StoreId,BranchCode,BusinessDate,ClosedAt,ClosedByUserId,ClosedByName,Remarks,Status,
    ShiftCount,InvoiceCount,ReturnCount,TotalSales,TotalTax,TotalDiscount,TotalCost,TotalProfit,TotalRefunds,CashSales,NonCashSales,
    BankSales,OpeningCashBalance,ClosingCashBalance,OpeningBankBalance,ClosingBankBalance)
OUTPUT INSERTED.DayClosingId
VALUES(@StoreId,@BranchCode,@BusinessDate,SYSUTCDATETIME(),@UserId,@UserName,@Remarks,'Closed',
    @ShiftCount,@InvoiceCount,@ReturnCount,@TotalSales,@TotalTax,@TotalDiscount,@TotalCost,@TotalProfit,@TotalRefunds,@CashSales,@NonCashSales,
    @BankSales,@OpeningCash,@ClosingCash,@OpeningBank,@ClosingBank);";
            cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode ?? "");
            cmd.Parameters.AddWithValue("@BusinessDate", date);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            cmd.Parameters.AddWithValue("@UserName", user.DisplayName ?? user.UserName);
            cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
            cmd.Parameters.AddWithValue("@ShiftCount", summary.ShiftCount);
            cmd.Parameters.AddWithValue("@InvoiceCount", summary.InvoiceCount);
            cmd.Parameters.AddWithValue("@ReturnCount", summary.ReturnCount);
            cmd.Parameters.AddWithValue("@TotalSales", summary.TotalSales);
            cmd.Parameters.AddWithValue("@TotalTax", summary.TotalTax);
            cmd.Parameters.AddWithValue("@TotalDiscount", summary.TotalDiscount);
            cmd.Parameters.AddWithValue("@TotalCost", summary.TotalCost);
            cmd.Parameters.AddWithValue("@TotalProfit", summary.TotalProfit);
            cmd.Parameters.AddWithValue("@TotalRefunds", summary.TotalRefunds);
            cmd.Parameters.AddWithValue("@CashSales", summary.CashSales);
            cmd.Parameters.AddWithValue("@NonCashSales", summary.NonCashSales);
            cmd.Parameters.AddWithValue("@BankSales", summary.BankSales);
            cmd.Parameters.AddWithValue("@OpeningCash", summary.OpeningCashBalance);
            cmd.Parameters.AddWithValue("@ClosingCash", summary.ClosingCashBalance);
            cmd.Parameters.AddWithValue("@OpeningBank", summary.OpeningBankBalance);
            cmd.Parameters.AddWithValue("@ClosingBank", summary.ClosingBankBalance);
            var id = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
            return Results.Ok(new
            {
                message = "Day closed successfully.",
                dayClosingId = id,
                businessDate = date.ToString("yyyy-MM-dd"),
                summary
            });
        });

        app.MapGet("/api/day-closing/report", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? businessDate) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            var date = (businessDate ?? DateTime.Today).Date;
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureDayClosingSchemaAsync(con);
            var summary = await BuildDaySummaryAsync(con, user.StoreId, user.BranchCode, date);

            await using var closeCmd = con.CreateCommand();
            closeCmd.CommandText = @"
SELECT TOP 1 DayClosingId,BusinessDate,ClosedAt,ClosedByName,Remarks,Status,
       ShiftCount,InvoiceCount,ReturnCount,TotalSales,TotalTax,TotalDiscount,TotalCost,TotalProfit,TotalRefunds,CashSales,NonCashSales
FROM DayClosings WHERE StoreId=@StoreId AND BusinessDate=@BusinessDate";
            closeCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            closeCmd.Parameters.AddWithValue("@BusinessDate", date);
            var closingRows = await SqlList.ReadAsync(closeCmd);

            await using var shiftCmd = con.CreateCommand();
            shiftCmd.CommandText = @"
SELECT s.ShiftId,s.TerminalId,ISNULL(t.TerminalName,t.TerminalCode) CounterName,s.UserId,ISNULL(u.DisplayName,u.UserName) Cashier,
       s.OpeningCash,ISNULL(s.ClosingCash,0) ClosingCash,ISNULL(s.DifferenceAmount,0) DifferenceAmount,s.Status,s.OpenedAt,s.ClosedAt,
       ISNULL((SELECT SUM(h.GrandTotal) FROM SalesHeader h WHERE h.ShiftId=s.ShiftId AND h.Status='Posted'),0) ShiftSales,
       ISNULL((SELECT SUM(r.RefundAmount) FROM ReturnHeader r WHERE r.ShiftId=s.ShiftId AND r.Status='Posted'),0) ShiftRefunds
FROM Shifts s
LEFT JOIN Terminals t ON t.TerminalId=s.TerminalId
LEFT JOIN Users u ON u.UserId=s.UserId
WHERE s.StoreId=@StoreId AND CAST(s.OpenedAt AS DATE)=@BusinessDate
ORDER BY s.OpenedAt, s.ShiftId";
            shiftCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            shiftCmd.Parameters.AddWithValue("@BusinessDate", date);
            var shifts = await SqlList.ReadAsync(shiftCmd);

            await using var branchCmd = con.CreateCommand();
            branchCmd.CommandText = @"
SELECT s.StoreId, ISNULL(s.StoreCode,s.BranchCode) BranchCode, ISNULL(s.StoreName,s.BranchName) BranchName,
  ISNULL((SELECT SUM(h.GrandTotal) FROM SalesHeader h WHERE h.StoreId=s.StoreId AND h.Status='Posted' AND CAST(h.SaleDate AS DATE)=@D),0) TotalSales,
  ISNULL((SELECT SUM(h.TaxAmount) FROM SalesHeader h WHERE h.StoreId=s.StoreId AND h.Status='Posted' AND CAST(h.SaleDate AS DATE)=@D),0) TotalTax,
  ISNULL((SELECT SUM(sl.UnitCost*sl.Quantity) FROM SalesLines sl INNER JOIN SalesHeader h ON h.SaleId=sl.SaleId WHERE h.StoreId=s.StoreId AND h.Status='Posted' AND CAST(h.SaleDate AS DATE)=@D),0)
  - ISNULL((SELECT SUM(rl.UnitCost*rl.ReturnQuantity) FROM ReturnLines rl INNER JOIN ReturnHeader rh ON rh.ReturnId=rl.ReturnId WHERE rh.StoreId=s.StoreId AND rh.Status='Posted' AND CAST(rh.ReturnDate AS DATE)=@D),0) TotalCost,
  ISNULL((SELECT SUM(r.RefundAmount) FROM ReturnHeader r WHERE r.StoreId=s.StoreId AND r.Status='Posted' AND CAST(r.ReturnDate AS DATE)=@D),0) TotalRefunds,
  ISNULL((SELECT COUNT(1) FROM SalesHeader h WHERE h.StoreId=s.StoreId AND h.Status='Posted' AND CAST(h.SaleDate AS DATE)=@D),0) InvoiceCount,
  ISNULL((SELECT COUNT(1) FROM Shifts sh WHERE sh.StoreId=s.StoreId AND CAST(sh.OpenedAt AS DATE)=@D),0) ShiftCount,
  CASE WHEN EXISTS(SELECT 1 FROM DayClosings d WHERE d.StoreId=s.StoreId AND d.BusinessDate=@D AND d.Status='Closed') THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END IsClosed
FROM Stores s
WHERE s.IsActive=1
ORDER BY ISNULL(s.StoreCode,s.BranchCode), s.StoreId";
            branchCmd.Parameters.AddWithValue("@D", date);
            var branches = await SqlList.ReadAsync(branchCmd);

            await BankAccountEndpoints.EnsureBankSchemaAsync(con);
            var bankBalances = new List<object>();
            await using (var bankCmd = con.CreateCommand())
            {
                bankCmd.CommandText = "SELECT BankAccountId,BankCode,BankName,AccountNo FROM BankAccounts WHERE IsActive=1 ORDER BY BankCode";
                await using var br = await bankCmd.ExecuteReaderAsync();
                var banks = new List<(int Id, string Code, string Name, string AccountNo)>();
                while (await br.ReadAsync())
                    banks.Add((Convert.ToInt32(br["BankAccountId"]), Convert.ToString(br["BankCode"]) ?? "", Convert.ToString(br["BankName"]) ?? "", Convert.ToString(br["AccountNo"]) ?? ""));
                await br.CloseAsync();
                foreach (var b in banks)
                {
                    var bal = await GetGlDayBalanceAsync(con, b.AccountNo, date);
                    bankBalances.Add(new
                    {
                        bankAccountId = b.Id,
                        bankCode = b.Code,
                        bankName = b.Name,
                        accountNo = b.AccountNo,
                        openingBalance = bal.Opening,
                        inflow = bal.DayDebit,
                        outflow = bal.DayCredit,
                        closingBalance = bal.Closing
                    });
                }
            }

            return Results.Ok(new
            {
                companyName = user.CompanyName,
                branchCode = user.BranchCode,
                branchName = user.BranchName,
                businessDate = date.ToString("yyyy-MM-dd"),
                generatedAt = DateTime.Now,
                isClosed = closingRows.Count > 0,
                closing = closingRows.FirstOrDefault(),
                summary,
                shifts,
                branches,
                bankBalances
            });
        });

        app.MapGet("/api/day-closing/status", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? businessDate) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            var date = (businessDate ?? DateTime.Today).Date;
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureDayClosingSchemaAsync(con);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM DayClosings WHERE StoreId=@StoreId AND BusinessDate=@BusinessDate AND Status='Closed'";
            cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            cmd.Parameters.AddWithValue("@BusinessDate", date);
            var closed = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
            return Results.Ok(new { businessDate = date.ToString("yyyy-MM-dd"), isClosed = closed, storeId = user.StoreId });
        });

        return app;
    }

    public static async Task EnsureDayClosingSchemaAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('DayClosings') IS NULL
BEGIN
CREATE TABLE DayClosings(
    DayClosingId BIGINT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL,
    BranchCode NVARCHAR(30) NULL,
    BusinessDate DATE NOT NULL,
    ClosedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ClosedByUserId INT NOT NULL,
    ClosedByName NVARCHAR(150) NULL,
    Remarks NVARCHAR(500) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Closed',
    ShiftCount INT NOT NULL DEFAULT 0,
    InvoiceCount INT NOT NULL DEFAULT 0,
    ReturnCount INT NOT NULL DEFAULT 0,
    TotalSales DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalTax DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalDiscount DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalCost DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalProfit DECIMAL(18,2) NOT NULL DEFAULT 0,
    TotalRefunds DECIMAL(18,2) NOT NULL DEFAULT 0,
    CashSales DECIMAL(18,2) NOT NULL DEFAULT 0,
    NonCashSales DECIMAL(18,2) NOT NULL DEFAULT 0,
    CONSTRAINT UQ_DayClosings_StoreDate UNIQUE(StoreId, BusinessDate)
);
END;
IF COL_LENGTH('DayClosings','BankSales') IS NULL ALTER TABLE DayClosings ADD BankSales DECIMAL(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('DayClosings','OpeningCashBalance') IS NULL ALTER TABLE DayClosings ADD OpeningCashBalance DECIMAL(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('DayClosings','ClosingCashBalance') IS NULL ALTER TABLE DayClosings ADD ClosingCashBalance DECIMAL(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('DayClosings','OpeningBankBalance') IS NULL ALTER TABLE DayClosings ADD OpeningBankBalance DECIMAL(18,2) NOT NULL DEFAULT 0;
IF COL_LENGTH('DayClosings','ClosingBankBalance') IS NULL ALTER TABLE DayClosings ADD ClosingBankBalance DECIMAL(18,2) NOT NULL DEFAULT 0;
IF OBJECT_ID('Terminals') IS NULL
BEGIN
CREATE TABLE Terminals(
    TerminalId INT IDENTITY(1,1) PRIMARY KEY,
    StoreId INT NOT NULL,
    TerminalCode NVARCHAR(30) NOT NULL,
    TerminalName NVARCHAR(100) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Terminals_StoreCode UNIQUE(StoreId, TerminalCode)
);
END;";
        await cmd.ExecuteNonQueryAsync();
        await BankAccountEndpoints.EnsureBankSchemaAsync(con);
    }

    public static async Task EnsureTerminalSchemaAsync(SqlConnection con)
    {
        await EnsureDayClosingSchemaAsync(con);
    }

    public static async Task<bool> IsBusinessDayClosedAsync(SqlConnection con, SqlTransaction? tran, int storeId, DateTime businessDate)
    {
        await EnsureDayClosingSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "SELECT COUNT(1) FROM DayClosings WHERE StoreId=@StoreId AND BusinessDate=@BusinessDate AND Status='Closed'";
        cmd.Parameters.AddWithValue("@StoreId", storeId);
        cmd.Parameters.AddWithValue("@BusinessDate", businessDate.Date);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    private static bool CanManageCounters(UserSession user) =>
        user.IsCompanySuperAdmin ||
        user.RoleName.Contains("Admin", StringComparison.OrdinalIgnoreCase) ||
        user.RoleName.Contains("Manager", StringComparison.OrdinalIgnoreCase);

    private static bool CanCloseDay(UserSession user) => CanManageCounters(user);

    private sealed record DaySummary(
        int ShiftCount, int InvoiceCount, int ReturnCount,
        decimal TotalSales, decimal TotalTax, decimal TotalDiscount,
        decimal TotalCost, decimal TotalProfit, decimal TotalRefunds,
        decimal CashSales, decimal NonCashSales, decimal BankSales,
        decimal OpeningCashBalance, decimal ClosingCashBalance,
        decimal OpeningBankBalance, decimal ClosingBankBalance,
        decimal CashInflow, decimal CashOutflow, decimal BankInflow, decimal BankOutflow);

    private static async Task<DaySummary> BuildDaySummaryAsync(SqlConnection con, int storeId, string? branchCode, DateTime businessDate)
    {
        await BankAccountEndpoints.EnsureBankSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
DECLARE @Sales DECIMAL(18,2)=ISNULL((SELECT SUM(GrandTotal) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND CAST(SaleDate AS DATE)=@D),0);
DECLARE @Tax DECIMAL(18,2)=ISNULL((SELECT SUM(TaxAmount) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND CAST(SaleDate AS DATE)=@D),0);
DECLARE @Disc DECIMAL(18,2)=ISNULL((SELECT SUM(DiscountAmount) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND CAST(SaleDate AS DATE)=@D),0);
DECLARE @Cost DECIMAL(18,2)=ISNULL((SELECT SUM(sl.UnitCost*sl.Quantity) FROM SalesLines sl INNER JOIN SalesHeader sh ON sh.SaleId=sl.SaleId WHERE sh.StoreId=@StoreId AND sh.Status='Posted' AND CAST(sh.SaleDate AS DATE)=@D),0);
DECLARE @Refunds DECIMAL(18,2)=ISNULL((SELECT SUM(RefundAmount) FROM ReturnHeader WHERE StoreId=@StoreId AND Status='Posted' AND CAST(ReturnDate AS DATE)=@D),0);
DECLARE @ReturnCost DECIMAL(18,2)=ISNULL((SELECT SUM(rl.UnitCost*rl.ReturnQuantity) FROM ReturnLines rl INNER JOIN ReturnHeader rh ON rh.ReturnId=rl.ReturnId WHERE rh.StoreId=@StoreId AND rh.Status='Posted' AND CAST(rh.ReturnDate AS DATE)=@D),0);
DECLARE @Cash DECIMAL(18,2)=ISNULL((SELECT SUM(pl.Amount) FROM PaymentLines pl INNER JOIN SalesHeader sh ON sh.SaleId=pl.SaleId INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId WHERE sh.StoreId=@StoreId AND sh.Status='Posted' AND CAST(sh.SaleDate AS DATE)=@D AND pm.PaymentMethodName='Cash'),0);
DECLARE @Bank DECIMAL(18,2)=ISNULL((SELECT SUM(pl.Amount) FROM PaymentLines pl INNER JOIN SalesHeader sh ON sh.SaleId=pl.SaleId INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId WHERE sh.StoreId=@StoreId AND sh.Status='Posted' AND CAST(sh.SaleDate AS DATE)=@D AND pm.PaymentMethodName IN('Bank','Bank Transfer','Card','Wallet','Cheque','Check')),0);
DECLARE @Invoices INT=ISNULL((SELECT COUNT(1) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND CAST(SaleDate AS DATE)=@D),0);
DECLARE @Returns INT=ISNULL((SELECT COUNT(1) FROM ReturnHeader WHERE StoreId=@StoreId AND Status='Posted' AND CAST(ReturnDate AS DATE)=@D),0);
DECLARE @Shifts INT=ISNULL((SELECT COUNT(1) FROM Shifts WHERE StoreId=@StoreId AND CAST(OpenedAt AS DATE)=@D),0);
DECLARE @CashAcc NVARCHAR(30)=(SELECT TOP 1 CashAccount FROM PostingSetup WHERE SetupId=1);
SELECT @Shifts ShiftCount,@Invoices InvoiceCount,@Returns ReturnCount,
       @Sales TotalSales,@Tax TotalTax,@Disc TotalDiscount,
       (@Cost-@ReturnCost) TotalCost,
       ((@Sales-@Tax-@Disc)-(@Cost-@ReturnCost)-(@Refunds)) TotalProfit,
       @Refunds TotalRefunds,@Cash CashSales,@Bank BankSales,(@Sales-@Cash) NonCashSales,
       @CashAcc CashAccountNo;";
        cmd.Parameters.AddWithValue("@StoreId", storeId);
        cmd.Parameters.AddWithValue("@D", businessDate.Date);
        string cashAccountNo = "1000";
        DaySummary baseSummary;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync())
                return new DaySummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            cashAccountNo = Convert.ToString(r["CashAccountNo"]) ?? "1000";
            baseSummary = new DaySummary(
                Convert.ToInt32(r["ShiftCount"]),
                Convert.ToInt32(r["InvoiceCount"]),
                Convert.ToInt32(r["ReturnCount"]),
                Convert.ToDecimal(r["TotalSales"]),
                Convert.ToDecimal(r["TotalTax"]),
                Convert.ToDecimal(r["TotalDiscount"]),
                Convert.ToDecimal(r["TotalCost"]),
                Convert.ToDecimal(r["TotalProfit"]),
                Convert.ToDecimal(r["TotalRefunds"]),
                Convert.ToDecimal(r["CashSales"]),
                Convert.ToDecimal(r["NonCashSales"]),
                Convert.ToDecimal(r["BankSales"]),
                0, 0, 0, 0, 0, 0, 0, 0);
        }

        var cashBal = await GetGlDayBalanceAsync(con, cashAccountNo, businessDate.Date);
        var bankAccounts = await ListActiveBankGlAccountsAsync(con);
        decimal openBank = 0, closeBank = 0, bankIn = 0, bankOut = 0;
        foreach (var acct in bankAccounts)
        {
            var b = await GetGlDayBalanceAsync(con, acct, businessDate.Date);
            openBank += b.Opening;
            closeBank += b.Closing;
            bankIn += b.DayDebit;
            bankOut += b.DayCredit;
        }
        if (bankAccounts.Count == 0)
        {
            var defaultBank = await GetPostingBankAccountAsync(con);
            var b = await GetGlDayBalanceAsync(con, defaultBank, businessDate.Date);
            openBank = b.Opening; closeBank = b.Closing; bankIn = b.DayDebit; bankOut = b.DayCredit;
        }

        return baseSummary with
        {
            OpeningCashBalance = cashBal.Opening,
            ClosingCashBalance = cashBal.Closing,
            CashInflow = cashBal.DayDebit,
            CashOutflow = cashBal.DayCredit,
            OpeningBankBalance = openBank,
            ClosingBankBalance = closeBank,
            BankInflow = bankIn,
            BankOutflow = bankOut
        };
    }

    private static async Task<string> GetPostingBankAccountAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 BankAccount FROM PostingSetup WHERE SetupId=1";
        return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "1010";
    }

    private static async Task<List<string>> ListActiveBankGlAccountsAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT AccountNo FROM BankAccounts WHERE IsActive=1 ORDER BY BankCode";
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var no = Convert.ToString(r[0]);
            if (!string.IsNullOrWhiteSpace(no)) list.Add(no!);
        }
        return list;
    }

    private sealed record GlDayBalance(decimal Opening, decimal DayDebit, decimal DayCredit, decimal Closing);

    private static async Task<GlDayBalance> GetGlDayBalanceAsync(SqlConnection con, string accountNo, DateTime businessDate)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
DECLARE @Aid INT=(SELECT TOP 1 AccountId FROM ChartOfAccounts WHERE AccountNo=@AccountNo);
DECLARE @Open DECIMAL(18,2)=ISNULL((SELECT SUM(DebitAmount-CreditAmount) FROM GLEntries WHERE AccountId=@Aid AND PostingDate<@D),0);
DECLARE @Dr DECIMAL(18,2)=ISNULL((SELECT SUM(DebitAmount) FROM GLEntries WHERE AccountId=@Aid AND PostingDate=@D),0);
DECLARE @Cr DECIMAL(18,2)=ISNULL((SELECT SUM(CreditAmount) FROM GLEntries WHERE AccountId=@Aid AND PostingDate=@D),0);
SELECT @Open OpeningBalance,@Dr DayDebit,@Cr DayCredit,(@Open+@Dr-@Cr) ClosingBalance;";
        cmd.Parameters.AddWithValue("@AccountNo", accountNo);
        cmd.Parameters.AddWithValue("@D", businessDate.Date);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return new GlDayBalance(0, 0, 0, 0);
        return new GlDayBalance(
            Convert.ToDecimal(r["OpeningBalance"]),
            Convert.ToDecimal(r["DayDebit"]),
            Convert.ToDecimal(r["DayCredit"]),
            Convert.ToDecimal(r["ClosingBalance"]));
    }
}
