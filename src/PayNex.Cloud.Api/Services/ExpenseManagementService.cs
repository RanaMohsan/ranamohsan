using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public sealed class ExpenseManagementService
{
    private readonly ConnectionFactory _db;

    public ExpenseManagementService(ConnectionFactory db) => _db = db;

    public async Task EnsureSchemaAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('ExpenseCategories') IS NULL
BEGIN
 CREATE TABLE ExpenseCategories(
  ExpenseCategoryId INT IDENTITY(1,1) PRIMARY KEY,
  CategoryCode NVARCHAR(20) NOT NULL UNIQUE,
  CategoryName NVARCHAR(100) NOT NULL,
  IsActive BIT NOT NULL DEFAULT 1,
  CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
  UpdatedAt DATETIME2 NULL
 );
END;
IF OBJECT_ID('Expenses') IS NULL
BEGIN
 CREATE TABLE Expenses(
  ExpenseId BIGINT IDENTITY(1,1) PRIMARY KEY,
  ExpenseNo NVARCHAR(40) NOT NULL UNIQUE,
  ExpenseDate DATE NOT NULL,
  ExpenseCategoryId INT NOT NULL,
  Description NVARCHAR(250) NOT NULL,
  Amount DECIMAL(18,2) NOT NULL,
  StoreId INT NOT NULL,
  BranchCode NVARCHAR(30) NULL,
  CreatedBy INT NOT NULL,
  Remarks NVARCHAR(500) NULL,
  CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
  UpdatedBy INT NULL,
  UpdatedAt DATETIME2 NULL,
  IsDeleted BIT NOT NULL DEFAULT 0,
  DeletedBy INT NULL,
  DeletedAt DATETIME2 NULL
 );
 CREATE INDEX IX_Expenses_Date_Branch_Category ON Expenses(ExpenseDate,StoreId,ExpenseCategoryId) INCLUDE(Amount,IsDeleted);
END;
MERGE NumberSeries AS t USING(VALUES('EXPENSE','EXP')) s(SeriesCode,Prefix)
ON t.SeriesCode=s.SeriesCode
WHEN NOT MATCHED THEN INSERT(SeriesCode,Prefix,LastNumber,NumberLength,IncludeDate) VALUES(s.SeriesCode,s.Prefix,0,6,1);
MERGE ExpenseCategories AS t USING(VALUES
 ('GENERAL','General Expense'),('TRAVEL','Travel & Conveyance'),('UTILITIES','Utilities'),('RENT','Rent'),
 ('MARKETING','Marketing'),('MAINTENANCE','Repairs & Maintenance'),('OFFICE','Office Supplies')
) s(CategoryCode,CategoryName) ON t.CategoryCode=s.CategoryCode
WHEN NOT MATCHED THEN INSERT(CategoryCode,CategoryName,IsActive) VALUES(s.CategoryCode,s.CategoryName,1);";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<object> GetLookupsAsync(UserSession user)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        var categories = await SqlList.ReadAsync(con, "SELECT ExpenseCategoryId,CategoryCode,CategoryName,IsActive FROM ExpenseCategories WHERE IsActive=1 ORDER BY CategoryName");
        await using var branchesCmd = con.CreateCommand();
        branchesCmd.CommandText = @"
SELECT s.StoreId,s.StoreCode,s.StoreName,ISNULL(s.BranchCode,s.StoreCode) BranchCode,ISNULL(s.BranchName,s.StoreName) BranchName
FROM Stores s
WHERE s.IsActive=1 AND (@IsAdmin=1 OR s.StoreId=@CurrentStoreId OR EXISTS(SELECT 1 FROM UserBranchAssignments a WHERE a.UserId=@UserId AND a.StoreId=s.StoreId))
ORDER BY ISNULL(s.IsMainBranch,0) DESC,s.StoreName";
        branchesCmd.Parameters.AddWithValue("@IsAdmin", IsAdmin(user));
        branchesCmd.Parameters.AddWithValue("@CurrentStoreId", user.StoreId);
        branchesCmd.Parameters.AddWithValue("@UserId", user.UserId);
        var branches = await SqlList.ReadAsync(branchesCmd);
        return new { categories, branches, currentBranchId = user.StoreId };
    }

    public async Task<List<Dictionary<string, object?>>> ListAsync(UserSession user, DateTime? from, DateTime? to,
        int branchId, int categoryId, string? term, bool ascending = false)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        if (branchId > 0) await EnsureBranchAccessAsync(con, null, user, branchId);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP 10000 e.ExpenseId,e.ExpenseNo,e.ExpenseDate,e.ExpenseCategoryId,c.CategoryCode,c.CategoryName ExpenseCategory,
       e.Description,e.Amount,e.StoreId,ISNULL(e.BranchCode,s.StoreCode) BranchCode,s.StoreName Branch,
       e.CreatedBy,u.DisplayName CreatedByName,u.DisplayName CreatedBy,ISNULL(e.Remarks,'') Remarks,e.CreatedAt,e.UpdatedAt
FROM Expenses e
INNER JOIN ExpenseCategories c ON c.ExpenseCategoryId=e.ExpenseCategoryId
INNER JOIN Stores s ON s.StoreId=e.StoreId
LEFT JOIN Users u ON u.UserId=e.CreatedBy
WHERE e.IsDeleted=0
  AND e.ExpenseDate>=@From AND e.ExpenseDate<=@To
  AND (@BranchId=0 OR e.StoreId=@BranchId)
  AND (@CategoryId=0 OR e.ExpenseCategoryId=@CategoryId)
  AND (@Term='' OR e.ExpenseNo LIKE @Like OR e.Description LIKE @Like OR e.Remarks LIKE @Like OR c.CategoryName LIKE @Like)
  AND (@IsAdmin=1 OR e.StoreId=@CurrentStoreId OR EXISTS(SELECT 1 FROM UserBranchAssignments a WHERE a.UserId=@UserId AND a.StoreId=e.StoreId))
ORDER BY e.ExpenseDate {(ascending ? "ASC" : "DESC")},e.ExpenseId {(ascending ? "ASC" : "DESC")}";
        cmd.Parameters.AddWithValue("@From", (from ?? new DateTime(1900, 1, 1)).Date);
        cmd.Parameters.AddWithValue("@To", (to ?? new DateTime(9999, 12, 31)).Date);
        cmd.Parameters.AddWithValue("@BranchId", branchId);
        cmd.Parameters.AddWithValue("@CategoryId", categoryId);
        var search = (term ?? string.Empty).Trim();
        cmd.Parameters.AddWithValue("@Term", search);
        cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
        cmd.Parameters.AddWithValue("@IsAdmin", IsAdmin(user));
        cmd.Parameters.AddWithValue("@CurrentStoreId", user.StoreId);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        return await SqlList.ReadAsync(cmd);
    }

    public async Task<Dictionary<string, object?>> GetAsync(UserSession user, long expenseId)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 e.ExpenseId,e.ExpenseNo,e.ExpenseDate,e.ExpenseCategoryId,c.CategoryCode,c.CategoryName ExpenseCategory,
       e.Description,e.Amount,e.StoreId,ISNULL(e.BranchCode,s.StoreCode) BranchCode,s.StoreName Branch,
       e.CreatedBy,u.DisplayName CreatedByName,u.DisplayName CreatedBy,ISNULL(e.Remarks,'') Remarks,e.CreatedAt,e.UpdatedAt
FROM Expenses e
INNER JOIN ExpenseCategories c ON c.ExpenseCategoryId=e.ExpenseCategoryId
INNER JOIN Stores s ON s.StoreId=e.StoreId
LEFT JOIN Users u ON u.UserId=e.CreatedBy
WHERE e.ExpenseId=@Id AND e.IsDeleted=0
  AND (@IsAdmin=1 OR e.StoreId=@CurrentStoreId OR EXISTS(SELECT 1 FROM UserBranchAssignments a WHERE a.UserId=@UserId AND a.StoreId=e.StoreId))";
        cmd.Parameters.AddWithValue("@Id", expenseId);
        cmd.Parameters.AddWithValue("@IsAdmin", IsAdmin(user));
        cmd.Parameters.AddWithValue("@CurrentStoreId", user.StoreId);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        return await SqlList.ReadSingleAsync(cmd);
    }

    public async Task<long> CreateAsync(UserSession user, ExpenseUpsertRequest request)
    {
        ValidateExpense(request);
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            var storeId = request.StoreId <= 0 ? user.StoreId : request.StoreId;
            await EnsureBranchAccessAsync(con, tran, user, storeId);
            await EnsureCategoryAsync(con, tran, request.ExpenseCategoryId);
            var expenseNo = await NextNumberAsync(con, tran);
            await using var cmd = new SqlCommand(@"
INSERT INTO Expenses(ExpenseNo,ExpenseDate,ExpenseCategoryId,Description,Amount,StoreId,BranchCode,CreatedBy,Remarks)
OUTPUT INSERTED.ExpenseId
SELECT @No,@Date,@CategoryId,@Description,@Amount,s.StoreId,ISNULL(s.BranchCode,s.StoreCode),@UserId,@Remarks FROM Stores s WHERE s.StoreId=@StoreId AND s.IsActive=1", con, tran);
            cmd.Parameters.AddWithValue("@No", expenseNo);
            AddExpenseParameters(cmd, user, request, storeId);
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar == null) throw new InvalidOperationException("Selected branch was not found or is inactive.");
            var id = Convert.ToInt64(scalar);
            await tran.CommitAsync();
            return id;
        }
        catch { await tran.RollbackAsync(); throw; }
    }

    public async Task UpdateAsync(UserSession user, long expenseId, ExpenseUpsertRequest request)
    {
        ValidateExpense(request);
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            await EnsureExpenseAccessAsync(con, tran, user, expenseId);
            var storeId = request.StoreId <= 0 ? user.StoreId : request.StoreId;
            await EnsureBranchAccessAsync(con, tran, user, storeId);
            await EnsureCategoryAsync(con, tran, request.ExpenseCategoryId);
            await using var cmd = new SqlCommand(@"
UPDATE e SET ExpenseDate=@Date,ExpenseCategoryId=@CategoryId,Description=@Description,Amount=@Amount,
       StoreId=@StoreId,BranchCode=ISNULL(s.BranchCode,s.StoreCode),Remarks=@Remarks,UpdatedBy=@UserId,UpdatedAt=SYSUTCDATETIME()
FROM Expenses e INNER JOIN Stores s ON s.StoreId=@StoreId
WHERE e.ExpenseId=@Id AND e.IsDeleted=0;
IF @@ROWCOUNT=0 THROW 50001, 'Expense record was not found.', 1;", con, tran);
            cmd.Parameters.AddWithValue("@Id", expenseId);
            AddExpenseParameters(cmd, user, request, storeId);
            await cmd.ExecuteNonQueryAsync();
            await tran.CommitAsync();
        }
        catch { await tran.RollbackAsync(); throw; }
    }

    public async Task DeleteAsync(UserSession user, long expenseId)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            await EnsureExpenseAccessAsync(con, tran, user, expenseId);
            await using var cmd = new SqlCommand(@"
UPDATE Expenses SET IsDeleted=1,DeletedBy=@UserId,DeletedAt=SYSUTCDATETIME() WHERE ExpenseId=@Id AND IsDeleted=0;
IF @@ROWCOUNT=0 THROW 50002, 'Expense record was not found or is already deleted.', 1;", con, tran);
            cmd.Parameters.AddWithValue("@Id", expenseId);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            await cmd.ExecuteNonQueryAsync();
            await tran.CommitAsync();
        }
        catch { await tran.RollbackAsync(); throw; }
    }

    public async Task<int> SaveCategoryAsync(UserSession user, ExpenseCategoryUpsertRequest request)
    {
        var code = NormalizeCategoryCode(request.CategoryCode);
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Expense category code is required.");
        if (string.IsNullOrWhiteSpace(request.CategoryName)) throw new InvalidOperationException("Expense category name is required.");
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF @Id>0 AND EXISTS(SELECT 1 FROM ExpenseCategories WHERE ExpenseCategoryId=@Id)
BEGIN
 IF EXISTS(SELECT 1 FROM ExpenseCategories WHERE CategoryCode=@Code AND ExpenseCategoryId<>@Id) THROW 50003, 'Expense category code already exists.', 1;
 UPDATE ExpenseCategories SET CategoryCode=@Code,CategoryName=@Name,IsActive=@Active,UpdatedAt=SYSUTCDATETIME() WHERE ExpenseCategoryId=@Id;
 SELECT @Id;
END
ELSE
BEGIN
 INSERT INTO ExpenseCategories(CategoryCode,CategoryName,IsActive) OUTPUT INSERTED.ExpenseCategoryId VALUES(@Code,@Name,@Active);
END";
        cmd.Parameters.AddWithValue("@Id", request.ExpenseCategoryId);
        cmd.Parameters.AddWithValue("@Code", code);
        cmd.Parameters.AddWithValue("@Name", request.CategoryName.Trim());
        cmd.Parameters.AddWithValue("@Active", request.IsActive);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public static (DateTime From, DateTime To, string Label) ResolveReportRange(DateTime? from, DateTime? to, string? month, int? year)
    {
        if (!string.IsNullOrWhiteSpace(month) && DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var monthStart))
            return (monthStart.Date, monthStart.AddMonths(1).AddDays(-1).Date, monthStart.ToString("MMMM yyyy"));
        if (year.HasValue && year.Value >= 1900 && year.Value <= 9999)
            return (new DateTime(year.Value, 1, 1), new DateTime(year.Value, 12, 31), "Year " + year.Value);
        var start = (from ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)).Date;
        var end = (to ?? DateTime.Today).Date;
        if (end < start) throw new InvalidOperationException("To Date cannot be before From Date.");
        return (start, end, $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}");
    }

    private static void ValidateExpense(ExpenseUpsertRequest request)
    {
        if (request.ExpenseDate == default) throw new InvalidOperationException("Expense date is required.");
        if (request.ExpenseCategoryId <= 0) throw new InvalidOperationException("Expense category is required.");
        if (string.IsNullOrWhiteSpace(request.Description)) throw new InvalidOperationException("Expense description is required.");
        if (request.Description.Trim().Length > 250) throw new InvalidOperationException("Expense description cannot exceed 250 characters.");
        if (request.Amount <= 0) throw new InvalidOperationException("Expense amount must be greater than zero.");
        if ((request.Remarks ?? string.Empty).Length > 500) throw new InvalidOperationException("Remarks cannot exceed 500 characters.");
    }

    private static void AddExpenseParameters(SqlCommand cmd, UserSession user, ExpenseUpsertRequest request, int storeId)
    {
        cmd.Parameters.AddWithValue("@Date", request.ExpenseDate.Date);
        cmd.Parameters.AddWithValue("@CategoryId", request.ExpenseCategoryId);
        cmd.Parameters.AddWithValue("@Description", request.Description.Trim());
        cmd.Parameters.AddWithValue("@Amount", request.Amount);
        cmd.Parameters.AddWithValue("@StoreId", storeId);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
    }

    private static async Task<string> NextNumberAsync(SqlConnection con, SqlTransaction tran)
    {
        string prefix; long next; int length; bool includeDate;
        await using (var cmd = new SqlCommand("SELECT Prefix,LastNumber+1 NextNumber,NumberLength,IncludeDate FROM NumberSeries WITH(UPDLOCK,HOLDLOCK) WHERE SeriesCode='EXPENSE'", con, tran))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Expense number series is missing.");
            prefix = Convert.ToString(reader["Prefix"]) ?? "EXP";
            next = Convert.ToInt64(reader["NextNumber"]);
            length = Convert.ToInt32(reader["NumberLength"]);
            includeDate = Convert.ToBoolean(reader["IncludeDate"]);
        }
        await using (var update = new SqlCommand("UPDATE NumberSeries SET LastNumber=@Next WHERE SeriesCode='EXPENSE'", con, tran))
        {
            update.Parameters.AddWithValue("@Next", next);
            await update.ExecuteNonQueryAsync();
        }
        return includeDate ? $"{prefix}-{DateTime.Today:yyyyMMdd}-{next.ToString().PadLeft(length, '0')}" : $"{prefix}-{next.ToString().PadLeft(length, '0')}";
    }

    private static async Task EnsureCategoryAsync(SqlConnection con, SqlTransaction tran, int categoryId)
    {
        await using var cmd = new SqlCommand("SELECT COUNT(1) FROM ExpenseCategories WHERE ExpenseCategoryId=@Id AND IsActive=1", con, tran);
        cmd.Parameters.AddWithValue("@Id", categoryId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0) throw new InvalidOperationException("Selected expense category was not found or is inactive.");
    }

    private static async Task EnsureBranchAccessAsync(SqlConnection con, SqlTransaction? tran, UserSession user, int storeId)
    {
        await using var cmd = new SqlCommand(@"
SELECT COUNT(1) FROM Stores s WHERE s.StoreId=@StoreId AND s.IsActive=1
AND (@IsAdmin=1 OR s.StoreId=@CurrentStoreId OR EXISTS(SELECT 1 FROM UserBranchAssignments a WHERE a.UserId=@UserId AND a.StoreId=s.StoreId))", con, tran);
        cmd.Parameters.AddWithValue("@StoreId", storeId);
        cmd.Parameters.AddWithValue("@IsAdmin", IsAdmin(user));
        cmd.Parameters.AddWithValue("@CurrentStoreId", user.StoreId);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0) throw new InvalidOperationException("You do not have access to the selected branch.");
    }

    private static async Task EnsureExpenseAccessAsync(SqlConnection con, SqlTransaction tran, UserSession user, long expenseId)
    {
        await using var cmd = new SqlCommand(@"
SELECT COUNT(1) FROM Expenses e WHERE e.ExpenseId=@Id AND e.IsDeleted=0
AND (@IsAdmin=1 OR e.StoreId=@CurrentStoreId OR EXISTS(SELECT 1 FROM UserBranchAssignments a WHERE a.UserId=@UserId AND a.StoreId=e.StoreId))", con, tran);
        cmd.Parameters.AddWithValue("@Id", expenseId);
        cmd.Parameters.AddWithValue("@IsAdmin", IsAdmin(user));
        cmd.Parameters.AddWithValue("@CurrentStoreId", user.StoreId);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0) throw new InvalidOperationException("Expense record was not found or you do not have branch access.");
    }

    private static bool IsAdmin(UserSession user) => user.IsCompanySuperAdmin ||
        user.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
        user.RoleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCategoryCode(string? value)
    {
        var code = new string((value ?? string.Empty).Trim().ToUpperInvariant().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return code.Length <= 20 ? code : code[..20];
    }
}
