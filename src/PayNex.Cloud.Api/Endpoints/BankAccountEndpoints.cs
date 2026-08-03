using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;
using System.Data;

namespace PayNex.Cloud.Api.Endpoints;

public static class BankAccountEndpoints
{
    public static IEndpointRouteBuilder MapBankAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/banks", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureBankSchemaAsync(con);
            var banks = await ListBanksAsync(con, activeOnly: false);
            return Results.Ok(new { banks, cashAccount = await GetCashAccountNoAsync(con) });
        });

        app.MapGet("/api/pos/payment-options", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureBankSchemaAsync(con);
            await EnsurePaymentMethodsAsync(con);
            var banks = await ListBanksAsync(con, activeOnly: true);
            var methods = await SqlList.ReadAsync(con, @"
SELECT PaymentMethodId,PaymentMethodName,RequiresReference
FROM PaymentMethods
WHERE IsActive=1 AND PaymentMethodName IN('Cash','Bank','Bank Transfer','Credit')
ORDER BY CASE PaymentMethodName WHEN 'Cash' THEN 1 WHEN 'Bank' THEN 2 WHEN 'Bank Transfer' THEN 2 WHEN 'Credit' THEN 3 ELSE 9 END, PaymentMethodId");
            return Results.Ok(new
            {
                cashAccount = await GetCashAccountNoAsync(con),
                defaultBankAccount = await GetDefaultBankAccountNoAsync(con),
                banks,
                paymentMethods = methods
            });
        });

        app.MapPost("/api/banks", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, BankAccountUpsertRequest request) =>
        {
            var user = ApiAuth.RequireUser(http, tokens);
            if (user == null) return Results.Unauthorized();
            if (!CanManageBanks(user)) return Results.BadRequest(new { message = "Permission required to manage bank accounts." });

            var code = (request.BankCode ?? "").Trim().ToUpperInvariant();
            var name = (request.BankName ?? "").Trim();
            var accountNo = (request.AccountNo ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest(new { message = "Bank code is required." });
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { message = "Bank name is required." });
            if (string.IsNullOrWhiteSpace(accountNo)) return Results.BadRequest(new { message = "G/L account no. is required." });

            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            await EnsureBankSchemaAsync(con);

            await using (var acct = con.CreateCommand())
            {
                acct.CommandText = "SELECT COUNT(1) FROM ChartOfAccounts WHERE AccountNo=@AccountNo AND IsActive=1";
                acct.Parameters.AddWithValue("@AccountNo", accountNo);
                if (Convert.ToInt32(await acct.ExecuteScalarAsync() ?? 0) <= 0)
                    return Results.BadRequest(new { message = $"G/L account {accountNo} was not found. Create it in Chart of Accounts first." });
            }

            if (request.BankAccountId > 0)
            {
                await using var upd = con.CreateCommand();
                upd.CommandText = @"
UPDATE BankAccounts SET BankCode=@Code,BankName=@Name,AccountNo=@AccountNo,IsActive=@Active
WHERE BankAccountId=@Id;
SELECT @@ROWCOUNT;";
                upd.Parameters.AddWithValue("@Id", request.BankAccountId);
                upd.Parameters.AddWithValue("@Code", code);
                upd.Parameters.AddWithValue("@Name", name);
                upd.Parameters.AddWithValue("@AccountNo", accountNo);
                upd.Parameters.AddWithValue("@Active", request.IsActive);
                if (Convert.ToInt32(await upd.ExecuteScalarAsync() ?? 0) <= 0)
                    return Results.NotFound(new { message = "Bank account not found." });
            }
            else
            {
                await using var ins = con.CreateCommand();
                ins.CommandText = @"
IF EXISTS(SELECT 1 FROM BankAccounts WHERE BankCode=@Code) THROW 50040, 'Bank code already exists.', 1;
IF EXISTS(SELECT 1 FROM BankAccounts WHERE AccountNo=@AccountNo) THROW 50041, 'This G/L account is already linked to another bank.', 1;
INSERT INTO BankAccounts(BankCode,BankName,AccountNo,IsActive)
OUTPUT INSERTED.BankAccountId VALUES(@Code,@Name,@AccountNo,@Active);";
                ins.Parameters.AddWithValue("@Code", code);
                ins.Parameters.AddWithValue("@Name", name);
                ins.Parameters.AddWithValue("@AccountNo", accountNo);
                ins.Parameters.AddWithValue("@Active", request.IsActive);
                try { await ins.ExecuteScalarAsync(); }
                catch (SqlException ex) when (ex.Number is 50040 or 50041) { return Results.BadRequest(new { message = ex.Message }); }
            }

            return Results.Ok(new { message = "Bank saved.", banks = await ListBanksAsync(con, activeOnly: false) });
        });

        return app;
    }

    public static async Task EnsureBankSchemaAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('BankAccounts') IS NULL
BEGIN
CREATE TABLE BankAccounts(
    BankAccountId INT IDENTITY(1,1) PRIMARY KEY,
    BankCode NVARCHAR(30) NOT NULL,
    BankName NVARCHAR(150) NOT NULL,
    AccountNo NVARCHAR(30) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_BankAccounts_Code UNIQUE(BankCode),
    CONSTRAINT UQ_BankAccounts_AccountNo UNIQUE(AccountNo)
);
END;
IF OBJECT_ID('PaymentLines') IS NOT NULL AND COL_LENGTH('PaymentLines','AccountNo') IS NULL
    ALTER TABLE PaymentLines ADD AccountNo NVARCHAR(30) NULL;
IF OBJECT_ID('PaymentMethods') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM PaymentMethods WHERE PaymentMethodName='Bank')
    INSERT INTO PaymentMethods(PaymentMethodName,RequiresReference,IsActive) VALUES('Bank',1,1);
IF OBJECT_ID('BankAccounts') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM BankAccounts)
BEGIN
    DECLARE @Bank NVARCHAR(30)=(SELECT TOP 1 BankAccount FROM PostingSetup WHERE SetupId=1);
    IF @Bank IS NOT NULL AND EXISTS(SELECT 1 FROM ChartOfAccounts WHERE AccountNo=@Bank)
        INSERT INTO BankAccounts(BankCode,BankName,AccountNo,IsActive)
        VALUES('BANK-01', ISNULL((SELECT TOP 1 AccountName FROM ChartOfAccounts WHERE AccountNo=@Bank),'Main Bank'), @Bank, 1);
END;";
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task EnsurePaymentMethodsAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('PaymentMethods') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM PaymentMethods WHERE PaymentMethodName='Bank')
    INSERT INTO PaymentMethods(PaymentMethodName,RequiresReference,IsActive) VALUES('Bank',1,1);";
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task EnsurePaymentLineAccountColumnAsync(SqlConnection con, SqlTransaction? tran = null)
    {
        await using var cmd = con.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = @"
IF OBJECT_ID('PaymentLines') IS NOT NULL AND COL_LENGTH('PaymentLines','AccountNo') IS NULL
    ALTER TABLE PaymentLines ADD AccountNo NVARCHAR(30) NULL;";
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<string> ResolvePaymentAccountAsync(SqlConnection con, SqlTransaction tran, SalePaymentRequest pay, Dictionary<string, string> setup)
    {
        var method = (pay.PaymentMethodName ?? "").Trim();
        if (method.Equals("Credit", StringComparison.OrdinalIgnoreCase))
            return setup["ReceivableAccount"];

        var accountNo = (pay.AccountNo ?? "").Trim();
        if (pay.BankAccountId > 0)
        {
            await using var cmd = new SqlCommand("SELECT TOP 1 AccountNo FROM BankAccounts WHERE BankAccountId=@Id AND IsActive=1", con, tran);
            cmd.Parameters.AddWithValue("@Id", pay.BankAccountId);
            var fromBank = Convert.ToString(await cmd.ExecuteScalarAsync());
            if (!string.IsNullOrWhiteSpace(fromBank)) accountNo = fromBank!;
        }

        if (PosSql.IsBankLikePayment(method))
        {
            if (string.IsNullOrWhiteSpace(accountNo))
                accountNo = setup["BankAccount"];
            await ValidateGlAccountAsync(con, tran, accountNo);
            return accountNo;
        }

        return setup["CashAccount"];
    }

    public static async Task<int> ResolvePaymentMethodIdAsync(SqlConnection con, SqlTransaction tran, string paymentMethodName, int requestedId)
    {
        var name = NormalizePaymentMethodName(paymentMethodName);
        await using var byName = new SqlCommand("SELECT TOP 1 PaymentMethodId FROM PaymentMethods WHERE PaymentMethodName=@Name AND IsActive=1", con, tran);
        byName.Parameters.AddWithValue("@Name", name);
        var idObj = await byName.ExecuteScalarAsync();
        if (idObj != null && idObj != DBNull.Value) return Convert.ToInt32(idObj);

        if (requestedId > 0) return requestedId;
        return name.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? 1 : 1;
    }

    public static string NormalizePaymentMethodName(string? name)
    {
        var m = (name ?? "Cash").Trim();
        if (m.Equals("Bank Transfer", StringComparison.OrdinalIgnoreCase) || m.Equals("Card", StringComparison.OrdinalIgnoreCase))
            return "Bank";
        return m;
    }

    private static async Task ValidateGlAccountAsync(SqlConnection con, SqlTransaction tran, string accountNo)
    {
        await using var cmd = new SqlCommand("SELECT COUNT(1) FROM ChartOfAccounts WHERE AccountNo=@AccountNo AND IsActive=1", con, tran);
        cmd.Parameters.AddWithValue("@AccountNo", accountNo);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) <= 0)
            throw new InvalidOperationException($"Bank G/L account {accountNo} was not found.");
    }

    private static async Task<List<Dictionary<string, object?>>> ListBanksAsync(SqlConnection con, bool activeOnly)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = activeOnly
            ? @"SELECT b.BankAccountId,b.BankCode,b.BankName,b.AccountNo,b.IsActive,ISNULL(a.AccountName,'') AccountName
FROM BankAccounts b LEFT JOIN ChartOfAccounts a ON a.AccountNo=b.AccountNo
WHERE b.IsActive=1 ORDER BY b.BankCode"
            : @"SELECT b.BankAccountId,b.BankCode,b.BankName,b.AccountNo,b.IsActive,ISNULL(a.AccountName,'') AccountName
FROM BankAccounts b LEFT JOIN ChartOfAccounts a ON a.AccountNo=b.AccountNo
ORDER BY b.BankCode";
        return await SqlList.ReadAsync(cmd);
    }

    private static async Task<string> GetCashAccountNoAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 CashAccount FROM PostingSetup WHERE SetupId=1";
        return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "1000";
    }

    private static async Task<string> GetDefaultBankAccountNoAsync(SqlConnection con)
    {
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT TOP 1 AccountNo FROM BankAccounts WHERE IsActive=1 ORDER BY BankAccountId";
            var no = Convert.ToString(await cmd.ExecuteScalarAsync());
            if (!string.IsNullOrWhiteSpace(no)) return no!;
        }
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT TOP 1 BankAccount FROM PostingSetup WHERE SetupId=1";
            return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? "1010";
        }
    }

    private static bool CanManageBanks(UserSession user) =>
        user.IsCompanySuperAdmin ||
        user.RoleName.Contains("Admin", StringComparison.OrdinalIgnoreCase) ||
        user.RoleName.Contains("Manager", StringComparison.OrdinalIgnoreCase);
}
