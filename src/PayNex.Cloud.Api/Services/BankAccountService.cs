using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public sealed class BankAccountService
{
    private const string SelectSql = @"
SELECT b.BankAccountId,b.BankCode,b.BankName,ISNULL(b.AccountNumber,'') AccountNumber,ISNULL(b.IBAN,'') IBAN,
ISNULL(b.BranchName,'') BranchName,b.Currency,b.BankType,b.GLAccountId,a.AccountNo GLAccountNo,a.AccountName GLAccountName,
ISNULL((SELECT SUM(le.DebitAmount-le.CreditAmount) FROM BankLedgerEntries le WHERE le.BankAccountId=b.BankAccountId),0) Balance,
b.IsActive,b.CreatedAt,b.UpdatedAt
FROM BankAccounts b
INNER JOIN ChartOfAccounts a ON a.AccountId=b.GLAccountId";

    private readonly ConnectionFactory _db;
    public BankAccountService(ConnectionFactory db) => _db = db;

    public static async Task EnsureSchemaAsync(SqlConnection con, SqlTransaction? tran = null)
    {
        async Task ExecAsync(string sql)
        {
            await using var cmd = con.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        await ExecAsync(@"
IF OBJECT_ID('BankAccounts') IS NULL
BEGIN
    CREATE TABLE BankAccounts(
        BankAccountId INT IDENTITY(1,1) PRIMARY KEY,
        BankCode NVARCHAR(30) NOT NULL UNIQUE,
        BankName NVARCHAR(150) NOT NULL,
        AccountNumber NVARCHAR(50) NULL,
        IBAN NVARCHAR(50) NULL,
        BranchName NVARCHAR(150) NULL,
        Currency NVARCHAR(10) NOT NULL CONSTRAINT DF_BankAccounts_Currency DEFAULT 'PKR',
        BankType NVARCHAR(20) NOT NULL CONSTRAINT DF_BankAccounts_BankType DEFAULT 'Bank',
        GLAccountId INT NOT NULL REFERENCES ChartOfAccounts(AccountId),
        IsActive BIT NOT NULL CONSTRAINT DF_BankAccounts_IsActive DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_BankAccounts_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL
    );
END");
        await UpgradeBankAccountsTableAsync(ExecAsync);
        await ExecAsync(@"
IF OBJECT_ID('BankLedgerEntries') IS NULL
BEGIN
    CREATE TABLE BankLedgerEntries(
        BankLedgerEntryId INT IDENTITY(1,1) PRIMARY KEY,
        BankAccountId INT NOT NULL REFERENCES BankAccounts(BankAccountId),
        PostingDate DATE NOT NULL,
        DocumentType NVARCHAR(50) NOT NULL,
        DocumentNo NVARCHAR(50) NOT NULL,
        DebitAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_BankLedger_Debit DEFAULT 0,
        CreditAmount DECIMAL(18,2) NOT NULL CONSTRAINT DF_BankLedger_Credit DEFAULT 0,
        Description NVARCHAR(250) NULL,
        SourceId INT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_BankLedger_CreatedAt DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_BankLedgerEntries_BankAccountId ON BankLedgerEntries(BankAccountId, PostingDate, BankLedgerEntryId);
END");
        await ExecAsync("IF OBJECT_ID('CustomerPayments') IS NOT NULL AND COL_LENGTH('CustomerPayments','BankAccountId') IS NULL ALTER TABLE CustomerPayments ADD BankAccountId INT NULL;");
        await ExecAsync("IF OBJECT_ID('CustomerPayments') IS NOT NULL AND COL_LENGTH('CustomerPayments','SalesInvoiceId') IS NULL ALTER TABLE CustomerPayments ADD SalesInvoiceId INT NULL;");
        await ExecAsync("IF OBJECT_ID('VendorPayments') IS NOT NULL AND COL_LENGTH('VendorPayments','BankAccountId') IS NULL ALTER TABLE VendorPayments ADD BankAccountId INT NULL;");
        await ExecAsync("IF OBJECT_ID('VendorPayments') IS NOT NULL AND COL_LENGTH('VendorPayments','PurchaseInvoiceId') IS NULL ALTER TABLE VendorPayments ADD PurchaseInvoiceId INT NULL;");
        await ExecAsync("IF OBJECT_ID('CustomerPayments') IS NOT NULL AND COL_LENGTH('CustomerPayments','ExternalClientDocumentId') IS NULL ALTER TABLE CustomerPayments ADD ExternalClientDocumentId NVARCHAR(80) NULL;");
        await ExecAsync("IF OBJECT_ID('VendorPayments') IS NOT NULL AND COL_LENGTH('VendorPayments','ExternalClientDocumentId') IS NULL ALTER TABLE VendorPayments ADD ExternalClientDocumentId NVARCHAR(80) NULL;");
        await ExecAsync(@"
IF OBJECT_ID('CustomerPayments') IS NOT NULL AND COL_LENGTH('CustomerPayments','ExternalClientDocumentId') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CustomerPayments_ExternalClientDocumentId' AND object_id = OBJECT_ID('CustomerPayments'))
    CREATE UNIQUE INDEX UX_CustomerPayments_ExternalClientDocumentId ON CustomerPayments(ExternalClientDocumentId) WHERE ExternalClientDocumentId IS NOT NULL;");
        await ExecAsync(@"
IF OBJECT_ID('VendorPayments') IS NOT NULL AND COL_LENGTH('VendorPayments','ExternalClientDocumentId') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_VendorPayments_ExternalClientDocumentId' AND object_id = OBJECT_ID('VendorPayments'))
    CREATE UNIQUE INDEX UX_VendorPayments_ExternalClientDocumentId ON VendorPayments(ExternalClientDocumentId) WHERE ExternalClientDocumentId IS NOT NULL;");
    }

    private static async Task UpgradeBankAccountsTableAsync(Func<string, Task> exec)
    {
        if (exec == null) throw new ArgumentNullException(nameof(exec));
        await exec("IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNumber') IS NULL ALTER TABLE BankAccounts ADD AccountNumber NVARCHAR(50) NULL;");
        await exec("IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','IBAN') IS NULL ALTER TABLE BankAccounts ADD IBAN NVARCHAR(50) NULL;");
        await exec("IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','BranchName') IS NULL ALTER TABLE BankAccounts ADD BranchName NVARCHAR(150) NULL;");
        await exec(@"IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','Currency') IS NULL
    ALTER TABLE BankAccounts ADD Currency NVARCHAR(10) NOT NULL CONSTRAINT DF_BankAccounts_Currency DEFAULT 'PKR';");
        await exec(@"IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','BankType') IS NULL
    ALTER TABLE BankAccounts ADD BankType NVARCHAR(20) NOT NULL CONSTRAINT DF_BankAccounts_BankType DEFAULT 'Bank';");
        await exec("IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NULL ALTER TABLE BankAccounts ADD GLAccountId INT NULL;");
        await exec(@"IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','CreatedAt') IS NULL
    ALTER TABLE BankAccounts ADD CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_BankAccounts_CreatedAt DEFAULT SYSUTCDATETIME();");
        await exec("IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','UpdatedAt') IS NULL ALTER TABLE BankAccounts ADD UpdatedAt DATETIME2 NULL;");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
    EXEC('UPDATE b SET b.GLAccountId=a.AccountId
FROM BankAccounts b
INNER JOIN ChartOfAccounts a ON a.AccountNo=b.AccountNo
WHERE b.GLAccountId IS NULL');");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
    EXEC('UPDATE BankAccounts SET GLAccountId=(
        SELECT TOP 1 AccountId FROM ChartOfAccounts
        WHERE IsActive=1 AND (AccountNo=''1010'' OR AccountName LIKE ''%Bank%'')
        ORDER BY CASE WHEN AccountNo=''1010'' THEN 0 ELSE 1 END, AccountId
    ) WHERE GLAccountId IS NULL');");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
    EXEC('UPDATE BankAccounts SET GLAccountId=(SELECT TOP 1 AccountId FROM ChartOfAccounts ORDER BY AccountId)
WHERE GLAccountId IS NULL');");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNumber') IS NOT NULL
AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
    EXEC('UPDATE b SET AccountNumber=b.AccountNo
FROM BankAccounts b
WHERE ISNULL(b.AccountNumber,'''')=''''
AND NOT EXISTS (SELECT 1 FROM ChartOfAccounts a WHERE a.AccountNo=b.AccountNo)');");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('BankAccounts') AND name='GLAccountId' AND is_nullable=1)
AND NOT EXISTS (SELECT 1 FROM BankAccounts WHERE GLAccountId IS NULL)
    ALTER TABLE BankAccounts ALTER COLUMN GLAccountId INT NOT NULL;");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','GLAccountId') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys fk
    INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
    INNER JOIN sys.columns c ON c.object_id=fkc.parent_object_id AND c.column_id=fkc.parent_column_id
    WHERE fk.parent_object_id=OBJECT_ID('BankAccounts') AND c.name='GLAccountId'
)
    ALTER TABLE BankAccounts ADD CONSTRAINT FK_BankAccounts_ChartOfAccounts FOREIGN KEY (GLAccountId) REFERENCES ChartOfAccounts(AccountId);");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('BankAccounts') AND name='AccountNo' AND is_nullable=0)
    ALTER TABLE BankAccounts ALTER COLUMN AccountNo NVARCHAR(50) NULL;");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.default_object_id=dc.object_id WHERE dc.parent_object_id=OBJECT_ID('BankAccounts') AND c.name='AccountNo')
    ALTER TABLE BankAccounts ADD CONSTRAINT DF_BankAccounts_AccountNo DEFAULT '' FOR AccountNo;");
        await exec(@"
IF OBJECT_ID('BankAccounts') IS NOT NULL AND COL_LENGTH('BankAccounts','AccountNo') IS NOT NULL
    EXEC('UPDATE BankAccounts SET AccountNo=ISNULL(NULLIF(LTRIM(RTRIM(AccountNumber)),''''), ISNULL(AccountNo,'''')) WHERE AccountNo IS NULL OR LTRIM(RTRIM(AccountNo))=''''');");
    }

    public static bool IsBankPaymentMethod(string? paymentMethod)
    {
        var method = (paymentMethod ?? string.Empty).Trim();
        return method.Equals("Bank", StringComparison.OrdinalIgnoreCase)
            || method.Equals("Bank Transfer", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<Dictionary<string, object?>>> ListAsync(UserSession user, bool includeInactive, string? term)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = SelectSql + @"
WHERE (@IncludeInactive=1 OR b.IsActive=1)
AND (@Term='' OR b.BankCode LIKE @Like OR b.BankName LIKE @Like OR ISNULL(b.AccountNumber,'') LIKE @Like OR a.AccountNo LIKE @Like OR a.AccountName LIKE @Like)
ORDER BY b.BankCode";
        var search = (term ?? string.Empty).Trim();
        cmd.Parameters.AddWithValue("@IncludeInactive", includeInactive);
        cmd.Parameters.AddWithValue("@Term", search);
        cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
        return await SqlList.ReadAsync(cmd);
    }

    public async Task<Dictionary<string, object?>> GetAsync(UserSession user, int id)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = SelectSql + " WHERE b.BankAccountId=@Id";
        cmd.Parameters.AddWithValue("@Id", id);
        return await SqlList.ReadSingleAsync(cmd);
    }

    public async Task<List<Dictionary<string, object?>>> LedgerAsync(UserSession user, int id)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var exists = con.CreateCommand();
        exists.CommandText = "SELECT COUNT(1) FROM BankAccounts WHERE BankAccountId=@Id";
        exists.Parameters.AddWithValue("@Id", id);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
            return new List<Dictionary<string, object?>>();

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT le.BankLedgerEntryId,le.BankAccountId,le.PostingDate,le.DocumentType,le.DocumentNo,le.DebitAmount,le.CreditAmount,
SUM(le.DebitAmount-le.CreditAmount) OVER (ORDER BY le.PostingDate, le.BankLedgerEntryId ROWS UNBOUNDED PRECEDING) BalanceAfter,
ISNULL(le.Description,'') Description,le.SourceId,le.CreatedAt
FROM BankLedgerEntries le
WHERE le.BankAccountId=@Id
ORDER BY le.PostingDate, le.BankLedgerEntryId";
        cmd.Parameters.AddWithValue("@Id", id);
        return await SqlList.ReadAsync(cmd);
    }

    public async Task<int> CreateAsync(UserSession user, BankAccountRequest request)
    {
        var payload = Normalize(request);
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await ValidateLinkedGlAsync(con, null, payload.GLAccountId);
        await EnsureUniqueCodeAsync(con, null, payload.BankCode, 0);
        var hasLegacyAccountNo = await HasLegacyAccountNoColumnAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = hasLegacyAccountNo
            ? @"INSERT INTO BankAccounts(BankCode,BankName,AccountNumber,AccountNo,IBAN,BranchName,Currency,BankType,GLAccountId,IsActive)
OUTPUT INSERTED.BankAccountId
VALUES(@BankCode,@BankName,@AccountNumber,@AccountNumber,@IBAN,@BranchName,@Currency,@BankType,@GLAccountId,@IsActive)"
            : @"INSERT INTO BankAccounts(BankCode,BankName,AccountNumber,IBAN,BranchName,Currency,BankType,GLAccountId,IsActive)
OUTPUT INSERTED.BankAccountId
VALUES(@BankCode,@BankName,@AccountNumber,@IBAN,@BranchName,@Currency,@BankType,@GLAccountId,@IsActive)";
        AddUpsertParameters(cmd, payload);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task UpdateAsync(UserSession user, int id, BankAccountRequest request)
    {
        var payload = Normalize(request);
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await ValidateLinkedGlAsync(con, null, payload.GLAccountId);
        await EnsureUniqueCodeAsync(con, null, payload.BankCode, id);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = (await HasLegacyAccountNoColumnAsync(con))
            ? @"UPDATE BankAccounts
SET BankCode=@BankCode,BankName=@BankName,AccountNumber=@AccountNumber,AccountNo=@AccountNumber,IBAN=@IBAN,BranchName=@BranchName,
    Currency=@Currency,BankType=@BankType,GLAccountId=@GLAccountId,IsActive=@IsActive,UpdatedAt=SYSUTCDATETIME()
WHERE BankAccountId=@Id"
            : @"UPDATE BankAccounts
SET BankCode=@BankCode,BankName=@BankName,AccountNumber=@AccountNumber,IBAN=@IBAN,BranchName=@BranchName,
    Currency=@Currency,BankType=@BankType,GLAccountId=@GLAccountId,IsActive=@IsActive,UpdatedAt=SYSUTCDATETIME()
WHERE BankAccountId=@Id";
        AddUpsertParameters(cmd, payload);
        cmd.Parameters.AddWithValue("@Id", id);
        if (await cmd.ExecuteNonQueryAsync() == 0)
            throw new InvalidOperationException("Bank account was not found.");
    }

    public async Task DeleteAsync(UserSession user, int id)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using (var ledger = con.CreateCommand())
        {
            ledger.CommandText = "SELECT COUNT(1) FROM BankLedgerEntries WHERE BankAccountId=@Id";
            ledger.Parameters.AddWithValue("@Id", id);
            if (Convert.ToInt32(await ledger.ExecuteScalarAsync()) > 0)
                throw new InvalidOperationException("This bank account has transactions and cannot be deleted.");
        }
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM BankAccounts WHERE BankAccountId=@Id";
        cmd.Parameters.AddWithValue("@Id", id);
        if (await cmd.ExecuteNonQueryAsync() == 0)
            throw new InvalidOperationException("Bank account was not found.");
    }

    public static async Task<PaymentGlDestination> ResolvePaymentGlAsync(
        SqlConnection con, SqlTransaction? tran, string? paymentMethod, int? bankAccountId, string cashAccountNo)
    {
        if (!IsBankPaymentMethod(paymentMethod))
            return new PaymentGlDestination(cashAccountNo, null);

        if (bankAccountId is null or <= 0)
            throw new InvalidOperationException("Select a bank account for Bank payments.");

        await using var cmd = con.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = @"
SELECT TOP 1 b.BankAccountId,b.IsActive,a.AccountNo,a.IsActive GlActive
FROM BankAccounts b
INNER JOIN ChartOfAccounts a ON a.AccountId=b.GLAccountId
WHERE b.BankAccountId=@Id";
        cmd.Parameters.AddWithValue("@Id", bankAccountId.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Selected bank account was not found.");
        if (!reader.GetBoolean(reader.GetOrdinal("IsActive")))
            throw new InvalidOperationException("Inactive bank accounts cannot be used on payments.");
        if (!reader.GetBoolean(reader.GetOrdinal("GlActive")))
            throw new InvalidOperationException("The linked G/L account is inactive.");
        return new PaymentGlDestination(reader.GetString(reader.GetOrdinal("AccountNo")), bankAccountId.Value);
    }

    public static async Task InsertBankLedgerAsync(
        SqlConnection con, SqlTransaction tran, int bankAccountId, DateTime postingDate,
        string documentType, string documentNo, decimal debit, decimal credit, string description, int sourceId)
    {
        if (debit == 0 && credit == 0) return;
        await using var cmd = new SqlCommand(@"
INSERT INTO BankLedgerEntries(BankAccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId)
VALUES(@BankAccountId,@PostingDate,@DocumentType,@DocumentNo,@Debit,@Credit,@Description,@SourceId)", con, tran);
        cmd.Parameters.AddWithValue("@BankAccountId", bankAccountId);
        cmd.Parameters.AddWithValue("@PostingDate", postingDate.Date);
        cmd.Parameters.AddWithValue("@DocumentType", documentType);
        cmd.Parameters.AddWithValue("@DocumentNo", documentNo);
        cmd.Parameters.AddWithValue("@Debit", debit);
        cmd.Parameters.AddWithValue("@Credit", credit);
        cmd.Parameters.AddWithValue("@Description", description ?? "");
        cmd.Parameters.AddWithValue("@SourceId", sourceId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ApplySalesInvoicePaymentAsync(
        SqlConnection con, SqlTransaction tran, int customerId, int salesInvoiceId, decimal amount)
    {
        await using var cmd = new SqlCommand(@"
SELECT CustomerId,Status,GrandTotal,PaidAmount,BalanceAmount
FROM SalesInvoiceHeader WITH (UPDLOCK, HOLDLOCK)
WHERE SalesInvoiceId=@Id", con, tran);
        cmd.Parameters.AddWithValue("@Id", salesInvoiceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Sales invoice was not found.");
        if (reader.GetInt32(reader.GetOrdinal("CustomerId")) != customerId)
            throw new InvalidOperationException("Sales invoice does not belong to the selected customer.");
        if (!string.Equals(reader.GetString(reader.GetOrdinal("Status")), "Posted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only posted sales invoices can be applied.");
        var grand = reader.GetDecimal(reader.GetOrdinal("GrandTotal"));
        var paid = reader.GetDecimal(reader.GetOrdinal("PaidAmount"));
        var remaining = grand - paid;
        if (remaining <= 0)
            throw new InvalidOperationException("This sales invoice has no remaining balance.");
        if (amount > remaining)
            throw new InvalidOperationException($"Payment amount cannot exceed the remaining invoice balance of {remaining:0.00}.");
        await reader.CloseAsync();

        await using var update = new SqlCommand(@"
UPDATE SalesInvoiceHeader
SET PaidAmount=PaidAmount+@Amount, BalanceAmount=GrandTotal-(PaidAmount+@Amount)
WHERE SalesInvoiceId=@Id", con, tran);
        update.Parameters.AddWithValue("@Amount", amount);
        update.Parameters.AddWithValue("@Id", salesInvoiceId);
        await update.ExecuteNonQueryAsync();
    }

    public static async Task ApplyPurchaseInvoicePaymentAsync(
        SqlConnection con, SqlTransaction tran, int vendorId, int purchaseInvoiceId, decimal amount)
    {
        await using var cmd = new SqlCommand(@"
SELECT VendorId,Status,GrandTotal,PaidAmount,BalanceAmount
FROM PurchaseInvoiceHeader WITH (UPDLOCK, HOLDLOCK)
WHERE PurchaseInvoiceId=@Id", con, tran);
        cmd.Parameters.AddWithValue("@Id", purchaseInvoiceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Purchase invoice was not found.");
        var invoiceVendorId = reader.IsDBNull(reader.GetOrdinal("VendorId")) ? 0 : reader.GetInt32(reader.GetOrdinal("VendorId"));
        if (invoiceVendorId != vendorId)
            throw new InvalidOperationException("Purchase invoice does not belong to the selected vendor.");
        if (!string.Equals(reader.GetString(reader.GetOrdinal("Status")), "Posted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only posted purchase invoices can be applied.");
        var grand = reader.GetDecimal(reader.GetOrdinal("GrandTotal"));
        var paid = reader.GetDecimal(reader.GetOrdinal("PaidAmount"));
        var remaining = grand - paid;
        if (remaining <= 0)
            throw new InvalidOperationException("This purchase invoice has no remaining balance.");
        if (amount > remaining)
            throw new InvalidOperationException($"Payment amount cannot exceed the remaining invoice balance of {remaining:0.00}.");
        await reader.CloseAsync();

        await using var update = new SqlCommand(@"
UPDATE PurchaseInvoiceHeader
SET PaidAmount=PaidAmount+@Amount, BalanceAmount=GrandTotal-(PaidAmount+@Amount)
WHERE PurchaseInvoiceId=@Id", con, tran);
        update.Parameters.AddWithValue("@Amount", amount);
        update.Parameters.AddWithValue("@Id", purchaseInvoiceId);
        await update.ExecuteNonQueryAsync();
    }

    private static BankAccountRequest Normalize(BankAccountRequest request)
    {
        var code = (request.BankCode ?? string.Empty).Trim();
        var name = (request.BankName ?? string.Empty).Trim();
        var currency = (request.Currency ?? string.Empty).Trim();
        var type = (request.BankType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Bank Code is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Bank Name is required.");
        if (request.GLAccountId <= 0) throw new InvalidOperationException("Linked G/L Account is required.");
        if (string.IsNullOrWhiteSpace(currency)) currency = "PKR";
        if (string.IsNullOrWhiteSpace(type)) type = "Bank";
        if (!type.Equals("Bank", StringComparison.OrdinalIgnoreCase) && !type.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bank Type must be Bank or Cash.");
        return new BankAccountRequest
        {
            BankAccountId = request.BankAccountId,
            BankCode = code,
            BankName = name,
            AccountNumber = (request.AccountNumber ?? string.Empty).Trim(),
            IBAN = (request.IBAN ?? string.Empty).Trim(),
            BranchName = (request.BranchName ?? string.Empty).Trim(),
            Currency = currency,
            BankType = type.Equals("Cash", StringComparison.OrdinalIgnoreCase) ? "Cash" : "Bank",
            GLAccountId = request.GLAccountId,
            IsActive = request.IsActive
        };
    }

    private static async Task ValidateLinkedGlAsync(SqlConnection con, SqlTransaction? tran, int glAccountId)
    {
        await using var cmd = con.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "SELECT TOP 1 AccountType,IsActive FROM ChartOfAccounts WHERE AccountId=@Id";
        cmd.Parameters.AddWithValue("@Id", glAccountId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Linked G/L account was not found.");
        var accountType = reader.GetString(reader.GetOrdinal("AccountType"));
        var isActive = reader.GetBoolean(reader.GetOrdinal("IsActive"));
        if (!isActive) throw new InvalidOperationException("Linked G/L account must be active.");
        if (!accountType.Equals("Asset", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Linked G/L account must be an Asset account.");
    }

    private static async Task<bool> HasLegacyAccountNoColumnAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN COL_LENGTH('BankAccounts','AccountNo') IS NULL THEN 0 ELSE 1 END";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private static async Task EnsureUniqueCodeAsync(SqlConnection con, SqlTransaction? tran, string bankCode, int excludeId)
    {
        await using var cmd = con.CreateCommand();
        cmd.Transaction = tran;
        cmd.CommandText = "SELECT COUNT(1) FROM BankAccounts WHERE BankCode=@Code AND BankAccountId<>@Id";
        cmd.Parameters.AddWithValue("@Code", bankCode);
        cmd.Parameters.AddWithValue("@Id", excludeId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0)
            throw new InvalidOperationException("Bank Code already exists.");
    }

    private static void AddUpsertParameters(SqlCommand cmd, BankAccountRequest request)
    {
        cmd.Parameters.AddWithValue("@BankCode", request.BankCode);
        cmd.Parameters.AddWithValue("@BankName", request.BankName);
        cmd.Parameters.AddWithValue("@AccountNumber", request.AccountNumber ?? "");
        cmd.Parameters.AddWithValue("@IBAN", request.IBAN ?? "");
        cmd.Parameters.AddWithValue("@BranchName", request.BranchName ?? "");
        cmd.Parameters.AddWithValue("@Currency", request.Currency);
        cmd.Parameters.AddWithValue("@BankType", request.BankType);
        cmd.Parameters.AddWithValue("@GLAccountId", request.GLAccountId);
        cmd.Parameters.AddWithValue("@IsActive", request.IsActive);
    }
}
