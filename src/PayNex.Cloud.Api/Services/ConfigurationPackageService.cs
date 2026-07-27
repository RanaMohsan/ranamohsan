using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace PayNex.Cloud.Api.Services;

public sealed class ConfigurationPackageService
{
    private const string ExcelMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly string[] ItemHeaders =
    {
        "ItemNo", "Barcode", "Description", "Category", "Brand", "BaseUnitOfMeasure",
        "PurchasePrice", "SalesPrice", "RetailPrice", "TaxPercent", "TaxInclusive",
        "DiscountAllowed", "DiscountPercent", "MinimumStock", "ReorderLevel",
        "OpeningQuantity", "OpeningUnitCost", "Active"
    };
    private static readonly string[] CustomerHeaders =
    {
        "CustomerNo", "Name", "Mobile", "Email", "Address", "CreditLimit", "OpeningBalance", "CurrentBalance", "Active"
    };
    private static readonly string[] VendorHeaders =
    {
        "VendorNo", "Name", "ContactPerson", "Mobile", "Email", "Address", "PaymentTerms", "OpeningBalance", "CurrentBalance", "Active"
    };

    private readonly ConnectionFactory _db;

    public ConfigurationPackageService(ConnectionFactory db) => _db = db;

    public string ContentType => ExcelMime;

    public async Task EnsureSchemaAsync(SqlConnection con, int userId)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('ConfigurationPackages') IS NULL
BEGIN
    CREATE TABLE ConfigurationPackages(
        PackageId BIGINT IDENTITY(1,1) PRIMARY KEY,
        PackageCode NVARCHAR(30) NOT NULL UNIQUE,
        PackageName NVARCHAR(120) NOT NULL,
        Description NVARCHAR(500) NULL,
        IncludeItems BIT NOT NULL DEFAULT 1,
        IncludeCustomers BIT NOT NULL DEFAULT 1,
        IncludeVendors BIT NOT NULL DEFAULT 1,
        IncludeBalances BIT NOT NULL DEFAULT 1,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedBy INT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedBy INT NULL,
        ModifiedAt DATETIME2 NULL
    );
END;
IF OBJECT_ID('ConfigurationPackageImports') IS NULL
BEGIN
    CREATE TABLE ConfigurationPackageImports(
        ImportId BIGINT IDENTITY(1,1) PRIMARY KEY,
        PackageId BIGINT NOT NULL,
        FileName NVARCHAR(260) NOT NULL,
        WorkbookJson NVARCHAR(MAX) NOT NULL,
        ValidationIssuesJson NVARCHAR(MAX) NULL,
        ItemRows INT NOT NULL DEFAULT 0,
        CustomerRows INT NOT NULL DEFAULT 0,
        VendorRows INT NOT NULL DEFAULT 0,
        ValidRows INT NOT NULL DEFAULT 0,
        ErrorRows INT NOT NULL DEFAULT 0,
        Status NVARCHAR(30) NOT NULL DEFAULT 'Validated',
        ImportedBy INT NOT NULL,
        ImportedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        AppliedBy INT NULL,
        AppliedAt DATETIME2 NULL,
        ResultJson NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_ConfigurationPackageImports_PackageId ON ConfigurationPackageImports(PackageId, ImportId DESC);
END;

-- The simplified Configuration Package page always exposes these three master-data packages.
IF EXISTS(SELECT 1 FROM ConfigurationPackages WHERE PackageCode='CUSTOMER')
    UPDATE ConfigurationPackages SET PackageName='Customer',Description='Export, import and validate customer master data.',IncludeItems=0,IncludeCustomers=1,IncludeVendors=0,IncludeBalances=1,IsActive=1 WHERE PackageCode='CUSTOMER';
ELSE
    INSERT INTO ConfigurationPackages(PackageCode,PackageName,Description,IncludeItems,IncludeCustomers,IncludeVendors,IncludeBalances,IsActive,CreatedBy)
    VALUES('CUSTOMER','Customer','Export, import and validate customer master data.',0,1,0,1,1,@SeedUserId);

IF EXISTS(SELECT 1 FROM ConfigurationPackages WHERE PackageCode='VENDOR')
    UPDATE ConfigurationPackages SET PackageName='Vendor',Description='Export, import and validate vendor master data.',IncludeItems=0,IncludeCustomers=0,IncludeVendors=1,IncludeBalances=1,IsActive=1 WHERE PackageCode='VENDOR';
ELSE
    INSERT INTO ConfigurationPackages(PackageCode,PackageName,Description,IncludeItems,IncludeCustomers,IncludeVendors,IncludeBalances,IsActive,CreatedBy)
    VALUES('VENDOR','Vendor','Export, import and validate vendor master data.',0,0,1,1,1,@SeedUserId);

IF EXISTS(SELECT 1 FROM ConfigurationPackages WHERE PackageCode='ITEM')
    UPDATE ConfigurationPackages SET PackageName='Item',Description='Export, import and validate item master data.',IncludeItems=1,IncludeCustomers=0,IncludeVendors=0,IncludeBalances=1,IsActive=1 WHERE PackageCode='ITEM';
ELSE
    INSERT INTO ConfigurationPackages(PackageCode,PackageName,Description,IncludeItems,IncludeCustomers,IncludeVendors,IncludeBalances,IsActive,CreatedBy)
    VALUES('ITEM','Item','Export, import and validate item master data.',1,0,0,1,1,@SeedUserId);
";
        cmd.Parameters.AddWithValue("@SeedUserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Dictionary<string, object?>>> ListAsync(UserSession user, string? term)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT p.PackageId,p.PackageCode,p.PackageName,ISNULL(p.Description,'') Description,
       p.IncludeItems,p.IncludeCustomers,p.IncludeVendors,p.IncludeBalances,p.IsActive,
       p.CreatedAt,p.ModifiedAt,
       ISNULL((SELECT COUNT(1) FROM ConfigurationPackageImports i WHERE i.PackageId=p.PackageId),0) ImportCount,
       (SELECT TOP 1 i.Status FROM ConfigurationPackageImports i WHERE i.PackageId=p.PackageId ORDER BY i.ImportId DESC) LastImportStatus,
       (SELECT TOP 1 i.ImportedAt FROM ConfigurationPackageImports i WHERE i.PackageId=p.PackageId ORDER BY i.ImportId DESC) LastImportAt,
       CASE WHEN p.IncludeItems=1 THEN (SELECT COUNT(1) FROM Products)
            WHEN p.IncludeCustomers=1 THEN (SELECT COUNT(1) FROM Customers)
            WHEN p.IncludeVendors=1 THEN (SELECT COUNT(1) FROM Vendors)
            ELSE 0 END RecordCount
FROM ConfigurationPackages p
WHERE p.PackageCode IN ('CUSTOMER','VENDOR','ITEM')
  AND (@Term='' OR p.PackageCode LIKE @Like OR p.PackageName LIKE @Like OR p.Description LIKE @Like)
ORDER BY CASE p.PackageCode WHEN 'CUSTOMER' THEN 1 WHEN 'VENDOR' THEN 2 WHEN 'ITEM' THEN 3 ELSE 4 END";
        var search = (term ?? string.Empty).Trim();
        cmd.Parameters.AddWithValue("@Term", search);
        cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
        return await SqlList.ReadAsync(cmd);
    }

    public async Task<Dictionary<string, object?>> GetAsync(UserSession user, long packageId)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT PackageId,PackageCode,PackageName,ISNULL(Description,'') Description,
       IncludeItems,IncludeCustomers,IncludeVendors,IncludeBalances,IsActive,CreatedAt,ModifiedAt
FROM ConfigurationPackages WHERE PackageId=@Id";
        cmd.Parameters.AddWithValue("@Id", packageId);
        return await SqlList.ReadSingleAsync(cmd);
    }

    public async Task<ConfigurationPackagePreviewResult> PreviewAsync(UserSession user, long packageId)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        var package = await ReadPackageAsync(con, null, packageId);

        if (package.IncludeItems)
        {
            var rows = await ExportItemsAsync(con, user.StoreId);
            return new ConfigurationPackagePreviewResult(package.PackageId, package.PackageCode, package.PackageName,
                "Item", ItemHeaders, rows);
        }
        if (package.IncludeCustomers)
        {
            var rows = await ExportCustomersAsync(con);
            return new ConfigurationPackagePreviewResult(package.PackageId, package.PackageCode, package.PackageName,
                "Customer", CustomerHeaders, rows);
        }
        if (package.IncludeVendors)
        {
            var rows = await ExportVendorsAsync(con);
            return new ConfigurationPackagePreviewResult(package.PackageId, package.PackageCode, package.PackageName,
                "Vendor", VendorHeaders, rows);
        }

        throw new InvalidOperationException("This package does not contain a supported master-data table.");
    }

    public async Task<long> SaveAsync(UserSession user, ConfigurationPackageUpsertRequest request)
    {
        var code = NormalizeCode(request.PackageCode);
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Package code is required.");
        if (string.IsNullOrWhiteSpace(request.PackageName)) throw new InvalidOperationException("Package name is required.");
        if (!request.IncludeItems && !request.IncludeCustomers && !request.IncludeVendors)
            throw new InvalidOperationException("Select at least one table: Item, Customer or Vendor.");

        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF @PackageId>0 AND EXISTS(SELECT 1 FROM ConfigurationPackages WHERE PackageId=@PackageId)
BEGIN
    IF EXISTS(SELECT 1 FROM ConfigurationPackages WHERE PackageCode=@Code AND PackageId<>@PackageId)
        THROW 50001, 'Package code already exists.', 1;
    UPDATE ConfigurationPackages
       SET PackageCode=@Code,PackageName=@Name,Description=@Description,
           IncludeItems=@Items,IncludeCustomers=@Customers,IncludeVendors=@Vendors,
           IncludeBalances=@Balances,IsActive=@Active,ModifiedBy=@UserId,ModifiedAt=SYSUTCDATETIME()
     WHERE PackageId=@PackageId;
    SELECT @PackageId;
END
ELSE
BEGIN
    INSERT INTO ConfigurationPackages(PackageCode,PackageName,Description,IncludeItems,IncludeCustomers,IncludeVendors,IncludeBalances,IsActive,CreatedBy)
    OUTPUT INSERTED.PackageId VALUES(@Code,@Name,@Description,@Items,@Customers,@Vendors,@Balances,@Active,@UserId);
END";
        cmd.Parameters.AddWithValue("@PackageId", request.PackageId);
        cmd.Parameters.AddWithValue("@Code", code);
        cmd.Parameters.AddWithValue("@Name", request.PackageName.Trim());
        cmd.Parameters.AddWithValue("@Description", request.Description ?? string.Empty);
        cmd.Parameters.AddWithValue("@Items", request.IncludeItems);
        cmd.Parameters.AddWithValue("@Customers", request.IncludeCustomers);
        cmd.Parameters.AddWithValue("@Vendors", request.IncludeVendors);
        cmd.Parameters.AddWithValue("@Balances", request.IncludeBalances);
        cmd.Parameters.AddWithValue("@Active", request.IsActive);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<(byte[] Content, string FileName)> ExportAsync(UserSession user, long packageId, bool templateOnly)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        var package = await ReadPackageAsync(con, null, packageId);
        var workbook = new ConfigurationPackageWorkbook();
        if (!templateOnly)
        {
            if (package.IncludeItems) workbook.Items.AddRange(await ExportItemsAsync(con, user.StoreId));
            if (package.IncludeCustomers) workbook.Customers.AddRange(await ExportCustomersAsync(con));
            if (package.IncludeVendors) workbook.Vendors.AddRange(await ExportVendorsAsync(con));
        }
        var bytes = CreateWorkbook(package, workbook, templateOnly);
        var suffix = templateOnly ? "Template" : "Data";
        return (bytes, $"{package.PackageCode}_{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    public async Task<ConfigurationPackageValidationResult> ValidateImportAsync(UserSession user, long packageId, ConfigurationPackageImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName) || !request.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select an Excel .xlsx package file.");
        var raw = request.Base64Content ?? string.Empty;
        if (raw.Contains(',')) raw = raw[(raw.IndexOf(',') + 1)..];
        byte[] content;
        try { content = Convert.FromBase64String(raw); }
        catch { throw new InvalidOperationException("The selected file is not valid Base64 content."); }
        if (content.Length == 0) throw new InvalidOperationException("The selected file is empty.");
        if (content.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Package file cannot exceed 20 MB.");

        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        var package = await ReadPackageAsync(con, null, packageId);
        ConfigurationPackageWorkbook workbook;
        try { workbook = ParseWorkbook(content); }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidOperationException("The selected file is not a valid or supported Excel .xlsx package.", ex);
        }
        var issues = ValidateWorkbook(package, workbook);
        issues.AddRange(await ValidateDatabaseConflictsAsync(con, workbook));
        issues = issues.Take(500).ToList();
        var totalRows = workbook.Items.Count + workbook.Customers.Count + workbook.Vendors.Count;
        var errorRows = issues.Where(x => x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.SheetName + ":" + x.RowNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var validRows = Math.Max(0, totalRows - errorRows);
        var canApply = issues.All(x => !x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)) && totalRows > 0;
        var status = canApply ? "Validated" : "Validation Failed";
        var workbookJson = JsonSerializer.Serialize(workbook);
        var issuesJson = JsonSerializer.Serialize(issues);

        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO ConfigurationPackageImports(PackageId,FileName,WorkbookJson,ValidationIssuesJson,ItemRows,CustomerRows,VendorRows,ValidRows,ErrorRows,Status,ImportedBy)
OUTPUT INSERTED.ImportId
VALUES(@PackageId,@FileName,@Workbook,@Issues,@Items,@Customers,@Vendors,@Valid,@Errors,@Status,@UserId)";
        cmd.Parameters.AddWithValue("@PackageId", packageId);
        cmd.Parameters.AddWithValue("@FileName", Path.GetFileName(request.FileName));
        cmd.Parameters.AddWithValue("@Workbook", workbookJson);
        cmd.Parameters.AddWithValue("@Issues", issuesJson);
        cmd.Parameters.AddWithValue("@Items", workbook.Items.Count);
        cmd.Parameters.AddWithValue("@Customers", workbook.Customers.Count);
        cmd.Parameters.AddWithValue("@Vendors", workbook.Vendors.Count);
        cmd.Parameters.AddWithValue("@Valid", validRows);
        cmd.Parameters.AddWithValue("@Errors", errorRows);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        var importId = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        return new ConfigurationPackageValidationResult(importId, Path.GetFileName(request.FileName), workbook.Items.Count,
            workbook.Customers.Count, workbook.Vendors.Count, validRows, errorRows, canApply, issues);
    }

    public async Task<ConfigurationPackageApplyResult> ApplyAsync(UserSession user, long packageId, long importId)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            var package = await ReadPackageAsync(con, tran, packageId);
            string workbookJson;
            string importStatus;
            await using (var load = new SqlCommand(@"
SELECT WorkbookJson,Status FROM ConfigurationPackageImports WITH(UPDLOCK,HOLDLOCK)
WHERE ImportId=@ImportId AND PackageId=@PackageId", con, tran))
            {
                load.Parameters.AddWithValue("@ImportId", importId);
                load.Parameters.AddWithValue("@PackageId", packageId);
                await using var reader = await load.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw new InvalidOperationException("Validated import package was not found.");
                workbookJson = Convert.ToString(reader["WorkbookJson"]) ?? string.Empty;
                importStatus = Convert.ToString(reader["Status"]) ?? string.Empty;
            }
            if (!importStatus.Equals("Validated", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(importStatus.Equals("Applied", StringComparison.OrdinalIgnoreCase)
                    ? "This import has already been saved."
                    : "Resolve validation errors before saving the data.");

            var workbook = JsonSerializer.Deserialize<ConfigurationPackageWorkbook>(workbookJson)
                ?? throw new InvalidOperationException("Stored package workbook could not be read.");
            var issues = ValidateWorkbook(package, workbook);
            if (issues.Any(x => x.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Validation failed. Import and validate the Excel file again.");

            var setup = package.IncludeBalances ? await ReadPostingSetupAsync(con, tran) : new Dictionary<string, string>();
            var documentNo = BuildDocumentNo(package.PackageCode, importId);
            int itemsInserted = 0, itemsUpdated = 0, customersInserted = 0, customersUpdated = 0, vendorsInserted = 0, vendorsUpdated = 0, balanceEntries = 0;
            decimal inventoryValueDelta = 0, customerBalanceDelta = 0, vendorBalanceDelta = 0;

            if (package.IncludeItems)
            {
                foreach (var row in workbook.Items)
                {
                    var itemResult = await ApplyItemAsync(con, tran, user, row, package.IncludeBalances, documentNo);
                    if (itemResult.Inserted) itemsInserted++; else itemsUpdated++;
                    inventoryValueDelta += itemResult.ValueDelta;
                    if (itemResult.BalanceChanged) balanceEntries++;
                }
            }
            if (package.IncludeCustomers)
            {
                foreach (var row in workbook.Customers)
                {
                    var customerResult = await ApplyCustomerAsync(con, tran, user, row, package.IncludeBalances, documentNo);
                    if (customerResult.Inserted) customersInserted++; else customersUpdated++;
                    customerBalanceDelta += customerResult.BalanceDelta;
                    if (customerResult.BalanceChanged) balanceEntries++;
                }
            }
            if (package.IncludeVendors)
            {
                foreach (var row in workbook.Vendors)
                {
                    var vendorResult = await ApplyVendorAsync(con, tran, user, row, package.IncludeBalances, documentNo);
                    if (vendorResult.Inserted) vendorsInserted++; else vendorsUpdated++;
                    vendorBalanceDelta += vendorResult.BalanceDelta;
                    if (vendorResult.BalanceChanged) balanceEntries++;
                }
            }

            if (package.IncludeBalances)
            {
                await PostBalancePairAsync(con, tran, setup, documentNo, "Package Item Balance", inventoryValueDelta,
                    setup["InventoryAccount"], setup["OpeningBalanceAccount"], user, importId);
                await PostBalancePairAsync(con, tran, setup, documentNo, "Package Customer Balance", customerBalanceDelta,
                    setup["ReceivableAccount"], setup["OpeningBalanceAccount"], user, importId);
                await PostLiabilityBalancePairAsync(con, tran, setup, documentNo, "Package Vendor Balance", vendorBalanceDelta,
                    setup["PayableAccount"], setup["OpeningBalanceAccount"], user, importId);
            }

            var result = new ConfigurationPackageApplyResult(importId, package.PackageCode, itemsInserted, itemsUpdated,
                customersInserted, customersUpdated, vendorsInserted, vendorsUpdated, balanceEntries,
                "Validated data saved successfully.");
            await using (var update = new SqlCommand(@"
UPDATE ConfigurationPackageImports SET Status='Applied',AppliedBy=@UserId,AppliedAt=SYSUTCDATETIME(),ResultJson=@Result
WHERE ImportId=@ImportId AND PackageId=@PackageId", con, tran))
            {
                update.Parameters.AddWithValue("@UserId", user.UserId);
                update.Parameters.AddWithValue("@Result", JsonSerializer.Serialize(result));
                update.Parameters.AddWithValue("@ImportId", importId);
                update.Parameters.AddWithValue("@PackageId", packageId);
                await update.ExecuteNonQueryAsync();
            }
            await tran.CommitAsync();
            return result;
        }
        catch
        {
            await tran.RollbackAsync();
            throw;
        }
    }

    public async Task<List<Dictionary<string, object?>>> HistoryAsync(UserSession user, long packageId)
    {
        await using var con = await _db.OpenTenantAsync(user.DatabaseName);
        await EnsureSchemaAsync(con, user.UserId);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 100 ImportId,FileName,ItemRows,CustomerRows,VendorRows,ValidRows,ErrorRows,Status,ImportedAt,AppliedAt,
       ISNULL(ValidationIssuesJson,'[]') ValidationIssuesJson,ISNULL(ResultJson,'') ResultJson
FROM ConfigurationPackageImports WHERE PackageId=@PackageId ORDER BY ImportId DESC";
        cmd.Parameters.AddWithValue("@PackageId", packageId);
        return await SqlList.ReadAsync(cmd);
    }

    private static async Task<List<Dictionary<string, string>>> ExportItemsAsync(SqlConnection con, int storeId)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT p.ProductCode ItemNo,p.Barcode,p.ProductName Description,ISNULL(c.CategoryName,'') Category,ISNULL(b.BrandName,'') Brand,
       p.UnitOfMeasure BaseUnitOfMeasure,p.PurchasePrice,p.SalePrice SalesPrice,p.RetailPrice,
       ISNULL(t.TaxPercent,0) TaxPercent,ISNULL(t.IsInclusive,0) TaxInclusive,p.DiscountAllowed,
       ISNULL(p.ProductDiscountPercent,0) DiscountPercent,p.MinStockLevel MinimumStock,p.ReorderLevel,
       ISNULL(s.Quantity,p.StockOnHand) OpeningQuantity,ISNULL(s.AverageCost,p.PurchasePrice) OpeningUnitCost,p.IsActive Active
FROM Products p
LEFT JOIN Categories c ON c.CategoryId=p.CategoryId
LEFT JOIN Brands b ON b.BrandId=p.BrandId
LEFT JOIN TaxGroups t ON t.TaxGroupId=p.TaxGroupId
LEFT JOIN StockByStore s ON s.ProductId=p.ProductId AND s.StoreId=@StoreId
ORDER BY p.ProductCode";
        cmd.Parameters.AddWithValue("@StoreId", storeId);
        return await ReadStringRowsAsync(cmd, ItemHeaders);
    }

    private static async Task<List<Dictionary<string, string>>> ExportCustomersAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT CustomerCode CustomerNo,CustomerName Name,ISNULL(Mobile,'') Mobile,ISNULL(Email,'') Email,
       ISNULL(AddressLine,'') Address,CreditLimit,OpeningBalance,CurrentBalance,IsActive Active
FROM Customers ORDER BY CustomerCode";
        return await ReadStringRowsAsync(cmd, CustomerHeaders);
    }

    private static async Task<List<Dictionary<string, string>>> ExportVendorsAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT VendorCode VendorNo,VendorName Name,ISNULL(ContactPerson,'') ContactPerson,ISNULL(Mobile,'') Mobile,
       ISNULL(Email,'') Email,ISNULL(AddressLine,'') Address,ISNULL(PaymentTerms,'') PaymentTerms,OpeningBalance,CurrentBalance,IsActive Active
FROM Vendors ORDER BY VendorCode";
        return await ReadStringRowsAsync(cmd, VendorHeaders);
    }

    private static async Task<List<Dictionary<string, string>>> ReadStringRowsAsync(SqlCommand cmd, IReadOnlyList<string> headers)
    {
        var list = new List<Dictionary<string, string>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                var value = reader[header];
                row[header] = value == DBNull.Value ? string.Empty : value switch
                {
                    bool b => b ? "TRUE" : "FALSE",
                    decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
                    double d => d.ToString("0.####", CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                };
            }
            list.Add(row);
        }
        return list;
    }

    private static List<ConfigurationPackageValidationIssue> ValidateWorkbook(PackageDefinition package, ConfigurationPackageWorkbook workbook)
    {
        var issues = new List<ConfigurationPackageValidationIssue>();
        if (package.IncludeItems) ValidateSheet("Items", ItemHeaders, new[] { "ItemNo", "Description" }, workbook.Items, workbook, issues);
        else if (workbook.Items.Count > 0) issues.Add(new("Items", 1, "Sheet", "Items are not enabled in this package."));
        if (package.IncludeCustomers) ValidateSheet("Customers", CustomerHeaders, new[] { "CustomerNo", "Name" }, workbook.Customers, workbook, issues);
        else if (workbook.Customers.Count > 0) issues.Add(new("Customers", 1, "Sheet", "Customers are not enabled in this package."));
        if (package.IncludeVendors) ValidateSheet("Vendors", VendorHeaders, new[] { "VendorNo", "Name" }, workbook.Vendors, workbook, issues);
        else if (workbook.Vendors.Count > 0) issues.Add(new("Vendors", 1, "Sheet", "Vendors are not enabled in this package."));
        return issues.Take(500).ToList();
    }

    private static async Task<List<ConfigurationPackageValidationIssue>> ValidateDatabaseConflictsAsync(SqlConnection con, ConfigurationPackageWorkbook workbook)
    {
        var issues = new List<ConfigurationPackageValidationIssue>();
        var barcodeOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT ProductCode,Barcode FROM Products WHERE ISNULL(Barcode,'')<>''";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var barcode = Convert.ToString(reader["Barcode"]) ?? string.Empty;
                if (!barcodeOwners.ContainsKey(barcode))
                    barcodeOwners[barcode] = Convert.ToString(reader["ProductCode"]) ?? string.Empty;
            }
        }
        for (var i = 0; i < workbook.Items.Count; i++)
        {
            var row = workbook.Items[i];
            var itemNo = Value(row, "ItemNo").Trim();
            var barcode = string.IsNullOrWhiteSpace(Value(row, "Barcode")) ? itemNo : Value(row, "Barcode").Trim();
            if (barcodeOwners.TryGetValue(barcode, out var owner) && !owner.Equals(itemNo, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("Items", i + 2, "Barcode", $"Barcode '{barcode}' already belongs to item '{owner}'."));
        }
        return issues;
    }

    private static void ValidateSheet(string sheetName, IReadOnlyList<string> expectedHeaders, IReadOnlyList<string> requiredFields,
        List<Dictionary<string, string>> rows, ConfigurationPackageWorkbook workbook, List<ConfigurationPackageValidationIssue> issues)
    {
        if (!workbook.Headers.TryGetValue(sheetName, out var actualHeaders))
        {
            issues.Add(new(sheetName, 1, "Sheet", $"Required worksheet '{sheetName}' is missing."));
            return;
        }
        foreach (var header in expectedHeaders.Where(h => !actualHeaders.Contains(h, StringComparer.OrdinalIgnoreCase)))
            issues.Add(new(sheetName, 1, header, $"Required column '{header}' is missing."));

        var keyField = requiredFields[0];
        var duplicates = rows.Select((row, index) => new { Key = Value(row, keyField), Row = index + 2 })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var duplicate in duplicates)
            foreach (var entry in duplicate)
                issues.Add(new(sheetName, entry.Row, keyField, $"Duplicate {keyField} '{entry.Key}' in workbook."));
        if (sheetName == "Items")
        {
            var duplicateBarcodes = rows.Select((row, index) => new { Key = Value(row, "Barcode"), Row = index + 2 })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
            foreach (var duplicate in duplicateBarcodes)
                foreach (var entry in duplicate)
                    issues.Add(new(sheetName, entry.Row, "Barcode", $"Duplicate Barcode '{entry.Key}' in workbook."));
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNo = i + 2;
            foreach (var field in requiredFields)
                if (string.IsNullOrWhiteSpace(Value(row, field))) issues.Add(new(sheetName, rowNo, field, $"{field} is required."));

            var lengths = sheetName switch
            {
                "Items" => new Dictionary<string, int> { ["ItemNo"] = 30, ["Barcode"] = 50, ["Description"] = 200, ["BaseUnitOfMeasure"] = 20 },
                "Customers" => new Dictionary<string, int> { ["CustomerNo"] = 30, ["Name"] = 150, ["Mobile"] = 30, ["Email"] = 100, ["Address"] = 250 },
                "Vendors" => new Dictionary<string, int> { ["VendorNo"] = 30, ["Name"] = 150, ["ContactPerson"] = 150, ["Mobile"] = 30, ["Email"] = 100, ["Address"] = 250 },
                _ => new Dictionary<string, int>()
            };
            foreach (var limit in lengths)
                if (Value(row, limit.Key).Length > limit.Value)
                    issues.Add(new(sheetName, rowNo, limit.Key, $"{limit.Key} cannot exceed {limit.Value} characters."));

            var decimals = sheetName switch
            {
                "Items" => new[] { "PurchasePrice", "SalesPrice", "RetailPrice", "TaxPercent", "DiscountPercent", "MinimumStock", "ReorderLevel", "OpeningQuantity", "OpeningUnitCost" },
                "Customers" => new[] { "CreditLimit", "OpeningBalance", "CurrentBalance" },
                "Vendors" => new[] { "OpeningBalance", "CurrentBalance" },
                _ => Array.Empty<string>()
            };
            foreach (var field in decimals)
                if (!string.IsNullOrWhiteSpace(Value(row, field)) && !TryDecimal(Value(row, field), out _))
                    issues.Add(new(sheetName, rowNo, field, $"'{Value(row, field)}' is not a valid number."));
            var booleans = sheetName == "Items" ? new[] { "TaxInclusive", "DiscountAllowed", "Active" } : new[] { "Active" };
            foreach (var field in booleans)
                if (!string.IsNullOrWhiteSpace(Value(row, field)) && !TryBool(Value(row, field), out _))
                    issues.Add(new(sheetName, rowNo, field, "Use TRUE/FALSE, YES/NO or 1/0."));

            if (TryDecimal(Value(row, "OpeningQuantity"), out var qty) && qty < 0)
                issues.Add(new(sheetName, rowNo, "OpeningQuantity", "Opening quantity cannot be negative."));
            if (TryDecimal(Value(row, "OpeningBalance"), out var balance) && balance < 0)
                issues.Add(new(sheetName, rowNo, "OpeningBalance", "Opening balance cannot be negative."));
            if (TryDecimal(Value(row, "DiscountPercent"), out var discount) && (discount < 0 || discount > 100))
                issues.Add(new(sheetName, rowNo, "DiscountPercent", "Discount percent must be between 0 and 100."));
            var nonNegative = sheetName switch
            {
                "Items" => new[] { "PurchasePrice", "SalesPrice", "RetailPrice", "TaxPercent", "MinimumStock", "ReorderLevel", "OpeningUnitCost" },
                "Customers" => new[] { "CreditLimit" },
                _ => Array.Empty<string>()
            };
            foreach (var field in nonNegative)
                if (TryDecimal(Value(row, field), out var number) && number < 0)
                    issues.Add(new(sheetName, rowNo, field, $"{field} cannot be negative."));
        }
    }

    private static byte[] CreateWorkbook(PackageDefinition package, ConfigurationPackageWorkbook workbook, bool templateOnly)
    {
        var sheets = new List<SheetData>
        {
            new("Instructions", new[] { "Package Configuration Instructions", "Value" }, new List<Dictionary<string, string>>
            {
                InstructionRow("Package Code", package.PackageCode),
                InstructionRow("Package Name", package.PackageName),
                InstructionRow("Mode", templateOnly ? "Blank import template" : "Exported company data"),
                InstructionRow("Balances", package.IncludeBalances ? "Opening/balance columns will be applied" : "Balance columns are informational and will not be applied"),
                InstructionRow("How to import", "Keep worksheet and column names unchanged. Import the file, validate it, and then choose Save."),
                InstructionRow("Boolean values", "Use TRUE/FALSE, YES/NO or 1/0."),
                InstructionRow("Customer/Vendor balances", "CurrentBalance is exported for reference. Only OpeningBalance is applied during import."),
                InstructionRow("Important", "Saving validated balances posts only the difference from the existing opening balance or stock.")
            })
        };
        if (package.IncludeItems) sheets.Add(new("Items", ItemHeaders, workbook.Items));
        if (package.IncludeCustomers) sheets.Add(new("Customers", CustomerHeaders, workbook.Customers));
        if (package.IncludeVendors) sheets.Add(new("Vendors", VendorHeaders, workbook.Vendors));
        return XlsxCodec.Write(sheets);
    }

    private static ConfigurationPackageWorkbook ParseWorkbook(byte[] content) => XlsxCodec.Read(content);

    private static Dictionary<string, string> InstructionRow(string instruction, string value)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Package Configuration Instructions"] = instruction,
            ["Value"] = value
        };
    }

    private static async Task<PackageDefinition> ReadPackageAsync(SqlConnection con, SqlTransaction? tran, long packageId)
    {
        await using var cmd = new SqlCommand(@"
SELECT PackageId,PackageCode,PackageName,ISNULL(Description,'') Description,IncludeItems,IncludeCustomers,IncludeVendors,IncludeBalances,IsActive
FROM ConfigurationPackages WHERE PackageId=@Id", con, tran);
        cmd.Parameters.AddWithValue("@Id", packageId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Configuration package was not found.");
        if (!Convert.ToBoolean(reader["IsActive"])) throw new InvalidOperationException("Configuration package is inactive.");
        return new PackageDefinition(Convert.ToInt64(reader["PackageId"]), Convert.ToString(reader["PackageCode"]) ?? string.Empty,
            Convert.ToString(reader["PackageName"]) ?? string.Empty, Convert.ToString(reader["Description"]) ?? string.Empty,
            Convert.ToBoolean(reader["IncludeItems"]), Convert.ToBoolean(reader["IncludeCustomers"]),
            Convert.ToBoolean(reader["IncludeVendors"]), Convert.ToBoolean(reader["IncludeBalances"]));
    }

    private static async Task<ItemApplyResult> ApplyItemAsync(SqlConnection con, SqlTransaction tran, UserSession user,
        Dictionary<string, string> row, bool includeBalances, string documentNo)
    {
        var code = Value(row, "ItemNo").Trim();
        var barcode = string.IsNullOrWhiteSpace(Value(row, "Barcode")) ? code : Value(row, "Barcode").Trim();
        var name = Value(row, "Description").Trim();
        var categoryId = await EnsureLookupAsync(con, tran, "Categories", "CategoryId", "CategoryName", Value(row, "Category"));
        var brandId = await EnsureLookupAsync(con, tran, "Brands", "BrandId", "BrandName", Value(row, "Brand"));
        var taxPercent = DecimalValue(row, "TaxPercent");
        var taxInclusive = BoolValue(row, "TaxInclusive", false);
        var taxGroupId = await EnsureTaxGroupAsync(con, tran, taxPercent, taxInclusive);
        var purchasePrice = DecimalValue(row, "PurchasePrice");
        var openingCost = DecimalValue(row, "OpeningUnitCost", purchasePrice);
        if (openingCost <= 0) openingCost = purchasePrice;
        var openingQty = DecimalValue(row, "OpeningQuantity");

        int productId;
        bool inserted;
        decimal oldQty = 0, oldCost = 0;
        await using (var find = new SqlCommand(@"
SELECT TOP 1 p.ProductId,ISNULL(s.Quantity,p.StockOnHand) Quantity,ISNULL(s.AverageCost,p.PurchasePrice) AverageCost
FROM Products p LEFT JOIN StockByStore s ON s.ProductId=p.ProductId AND s.StoreId=@StoreId
WHERE p.ProductCode=@Code", con, tran))
        {
            find.Parameters.AddWithValue("@Code", code);
            find.Parameters.AddWithValue("@StoreId", user.StoreId);
            await using var reader = await find.ExecuteReaderAsync();
            inserted = !await reader.ReadAsync();
            if (!inserted)
            {
                productId = Convert.ToInt32(reader["ProductId"]);
                oldQty = Convert.ToDecimal(reader["Quantity"]);
                oldCost = Convert.ToDecimal(reader["AverageCost"]);
            }
            else productId = 0;
        }

        await using (var cmd = new SqlCommand(@"
IF @ProductId=0
BEGIN
 INSERT INTO Products(ProductCode,Barcode,ProductName,CategoryId,BrandId,UnitOfMeasure,PurchasePrice,SalePrice,RetailPrice,TaxGroupId,DiscountAllowed,ProductDiscountPercent,MinStockLevel,ReorderLevel,StockOnHand,IsActive)
 OUTPUT INSERTED.ProductId
 VALUES(@Code,@Barcode,@Name,@CategoryId,@BrandId,@Uom,@Purchase,@Sales,@Retail,@TaxGroupId,@DiscountAllowed,@DiscountPercent,@MinimumStock,@ReorderLevel,0,@Active);
END
ELSE
BEGIN
 IF EXISTS(SELECT 1 FROM Products WHERE Barcode=@Barcode AND ProductId<>@ProductId) THROW 50010, 'Barcode already belongs to another item.', 1;
 UPDATE Products SET Barcode=@Barcode,ProductName=@Name,CategoryId=@CategoryId,BrandId=@BrandId,UnitOfMeasure=@Uom,
 PurchasePrice=@Purchase,SalePrice=@Sales,RetailPrice=@Retail,TaxGroupId=@TaxGroupId,DiscountAllowed=@DiscountAllowed,
 ProductDiscountPercent=@DiscountPercent,MinStockLevel=@MinimumStock,ReorderLevel=@ReorderLevel,IsActive=@Active
 WHERE ProductId=@ProductId;
 SELECT @ProductId;
END", con, tran))
        {
            cmd.Parameters.AddWithValue("@ProductId", productId);
            cmd.Parameters.AddWithValue("@Code", code);
            cmd.Parameters.AddWithValue("@Barcode", barcode);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@CategoryId", categoryId.HasValue ? (object)categoryId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@BrandId", brandId.HasValue ? (object)brandId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@Uom", string.IsNullOrWhiteSpace(Value(row, "BaseUnitOfMeasure")) ? "PCS" : Value(row, "BaseUnitOfMeasure").Trim());
            cmd.Parameters.AddWithValue("@Purchase", purchasePrice);
            cmd.Parameters.AddWithValue("@Sales", DecimalValue(row, "SalesPrice"));
            cmd.Parameters.AddWithValue("@Retail", DecimalValue(row, "RetailPrice", DecimalValue(row, "SalesPrice")));
            cmd.Parameters.AddWithValue("@TaxGroupId", taxGroupId);
            cmd.Parameters.AddWithValue("@DiscountAllowed", BoolValue(row, "DiscountAllowed", true));
            cmd.Parameters.AddWithValue("@DiscountPercent", DecimalValue(row, "DiscountPercent"));
            cmd.Parameters.AddWithValue("@MinimumStock", DecimalValue(row, "MinimumStock"));
            cmd.Parameters.AddWithValue("@ReorderLevel", DecimalValue(row, "ReorderLevel"));
            cmd.Parameters.AddWithValue("@Active", BoolValue(row, "Active", true));
            productId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        decimal valueDelta = 0;
        var balanceChanged = false;
        if (includeBalances)
        {
            var quantityDelta = openingQty - oldQty;
            valueDelta = Math.Round((openingQty * openingCost) - (oldQty * oldCost), 2);
            balanceChanged = quantityDelta != 0 || valueDelta != 0;
            await using var stock = new SqlCommand(@"
MERGE StockByStore AS t USING(SELECT @StoreId StoreId,@ProductId ProductId) s
ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=@Quantity,AverageCost=@Cost
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@StoreId,@ProductId,@Quantity,@Cost);
UPDATE Products SET StockOnHand=StockOnHand+@QuantityDelta,PurchasePrice=@Cost WHERE ProductId=@ProductId;
IF @QuantityDelta<>0
INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy)
VALUES(@StoreId,@BranchCode,@ProductId,'Package Balance',@DocumentNo,CASE WHEN @QuantityDelta>0 THEN @QuantityDelta ELSE 0 END,
CASE WHEN @QuantityDelta<0 THEN ABS(@QuantityDelta) ELSE 0 END,@Cost,'Configuration package item balance',@UserId);", con, tran);
            stock.Parameters.AddWithValue("@StoreId", user.StoreId);
            stock.Parameters.AddWithValue("@BranchCode", user.BranchCode ?? string.Empty);
            stock.Parameters.AddWithValue("@ProductId", productId);
            stock.Parameters.AddWithValue("@Quantity", openingQty);
            stock.Parameters.AddWithValue("@QuantityDelta", quantityDelta);
            stock.Parameters.AddWithValue("@Cost", openingCost);
            stock.Parameters.AddWithValue("@DocumentNo", documentNo);
            stock.Parameters.AddWithValue("@UserId", user.UserId);
            await stock.ExecuteNonQueryAsync();
        }
        return new ItemApplyResult(inserted, balanceChanged, valueDelta);
    }

    private static async Task<PartyApplyResult> ApplyCustomerAsync(SqlConnection con, SqlTransaction tran, UserSession user,
        Dictionary<string, string> row, bool includeBalances, string documentNo)
    {
        var code = Value(row, "CustomerNo").Trim();
        int id;
        bool inserted;
        decimal oldOpening = 0;
        await using (var find = new SqlCommand("SELECT TOP 1 CustomerId,OpeningBalance FROM Customers WHERE CustomerCode=@Code", con, tran))
        {
            find.Parameters.AddWithValue("@Code", code);
            await using var reader = await find.ExecuteReaderAsync();
            inserted = !await reader.ReadAsync();
            if (inserted) id = 0;
            else { id = Convert.ToInt32(reader["CustomerId"]); oldOpening = Convert.ToDecimal(reader["OpeningBalance"]); }
        }
        var opening = DecimalValue(row, "OpeningBalance");
        var delta = includeBalances ? opening - oldOpening : 0;
        await using (var cmd = new SqlCommand(@"
IF @Id=0
BEGIN
 INSERT INTO Customers(CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive)
 OUTPUT INSERTED.CustomerId VALUES(@Code,@Name,@Mobile,@Email,@Address,@CreditLimit,0,@Opening,@Opening,@Active);
END
ELSE
BEGIN
 UPDATE Customers SET CustomerName=@Name,Mobile=@Mobile,Email=@Email,AddressLine=@Address,CreditLimit=@CreditLimit,
 OpeningBalance=CASE WHEN @IncludeBalances=1 THEN @Opening ELSE OpeningBalance END,
 CurrentBalance=CASE WHEN @IncludeBalances=1 THEN CurrentBalance+@Delta ELSE CurrentBalance END,IsActive=@Active WHERE CustomerId=@Id;
 SELECT @Id;
END", con, tran))
        {
            cmd.Parameters.AddWithValue("@Id", id); cmd.Parameters.AddWithValue("@Code", code); cmd.Parameters.AddWithValue("@Name", Value(row, "Name").Trim());
            cmd.Parameters.AddWithValue("@Mobile", Value(row, "Mobile")); cmd.Parameters.AddWithValue("@Email", Value(row, "Email")); cmd.Parameters.AddWithValue("@Address", Value(row, "Address"));
            cmd.Parameters.AddWithValue("@CreditLimit", DecimalValue(row, "CreditLimit")); cmd.Parameters.AddWithValue("@Opening", includeBalances ? opening : 0);
            cmd.Parameters.AddWithValue("@Delta", delta); cmd.Parameters.AddWithValue("@IncludeBalances", includeBalances); cmd.Parameters.AddWithValue("@Active", BoolValue(row, "Active", true));
            id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        if (includeBalances && delta != 0)
        {
            await using var ledger = new SqlCommand(@"
DECLARE @Balance DECIMAL(18,2)=(SELECT CurrentBalance FROM Customers WHERE CustomerId=@Id);
INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@Id,CAST(GETDATE() AS DATE),'Package Opening Balance',@DocumentNo,CASE WHEN @Delta>0 THEN @Delta ELSE 0 END,
CASE WHEN @Delta<0 THEN ABS(@Delta) ELSE 0 END,@Balance,'Configuration package customer balance',@Id);", con, tran);
            ledger.Parameters.AddWithValue("@Id", id); ledger.Parameters.AddWithValue("@Delta", delta); ledger.Parameters.AddWithValue("@DocumentNo", documentNo);
            await ledger.ExecuteNonQueryAsync();
        }
        return new PartyApplyResult(inserted, delta != 0, delta);
    }

    private static async Task<PartyApplyResult> ApplyVendorAsync(SqlConnection con, SqlTransaction tran, UserSession user,
        Dictionary<string, string> row, bool includeBalances, string documentNo)
    {
        var code = Value(row, "VendorNo").Trim();
        int id;
        bool inserted;
        decimal oldOpening = 0;
        await using (var find = new SqlCommand("SELECT TOP 1 VendorId,OpeningBalance FROM Vendors WHERE VendorCode=@Code", con, tran))
        {
            find.Parameters.AddWithValue("@Code", code);
            await using var reader = await find.ExecuteReaderAsync();
            inserted = !await reader.ReadAsync();
            if (inserted) id = 0;
            else { id = Convert.ToInt32(reader["VendorId"]); oldOpening = Convert.ToDecimal(reader["OpeningBalance"]); }
        }
        var opening = DecimalValue(row, "OpeningBalance");
        var delta = includeBalances ? opening - oldOpening : 0;
        await using (var cmd = new SqlCommand(@"
IF @Id=0
BEGIN
 INSERT INTO Vendors(VendorCode,VendorName,ContactPerson,Mobile,Email,AddressLine,PaymentTerms,OpeningBalance,CurrentBalance,IsActive)
 OUTPUT INSERTED.VendorId VALUES(@Code,@Name,@Contact,@Mobile,@Email,@Address,@Terms,@Opening,@Opening,@Active);
END
ELSE
BEGIN
 UPDATE Vendors SET VendorName=@Name,ContactPerson=@Contact,Mobile=@Mobile,Email=@Email,AddressLine=@Address,PaymentTerms=@Terms,
 OpeningBalance=CASE WHEN @IncludeBalances=1 THEN @Opening ELSE OpeningBalance END,
 CurrentBalance=CASE WHEN @IncludeBalances=1 THEN CurrentBalance+@Delta ELSE CurrentBalance END,IsActive=@Active WHERE VendorId=@Id;
 SELECT @Id;
END", con, tran))
        {
            cmd.Parameters.AddWithValue("@Id", id); cmd.Parameters.AddWithValue("@Code", code); cmd.Parameters.AddWithValue("@Name", Value(row, "Name").Trim());
            cmd.Parameters.AddWithValue("@Contact", Value(row, "ContactPerson")); cmd.Parameters.AddWithValue("@Mobile", Value(row, "Mobile")); cmd.Parameters.AddWithValue("@Email", Value(row, "Email"));
            cmd.Parameters.AddWithValue("@Address", Value(row, "Address")); cmd.Parameters.AddWithValue("@Terms", Value(row, "PaymentTerms"));
            cmd.Parameters.AddWithValue("@Opening", includeBalances ? opening : 0); cmd.Parameters.AddWithValue("@Delta", delta); cmd.Parameters.AddWithValue("@IncludeBalances", includeBalances);
            cmd.Parameters.AddWithValue("@Active", BoolValue(row, "Active", true)); id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        if (includeBalances && delta != 0)
        {
            await using var ledger = new SqlCommand(@"
DECLARE @Balance DECIMAL(18,2)=(SELECT CurrentBalance FROM Vendors WHERE VendorId=@Id);
INSERT INTO VendorLedgerEntries(VendorId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@Id,CAST(GETDATE() AS DATE),'Package Opening Balance',@DocumentNo,CASE WHEN @Delta<0 THEN ABS(@Delta) ELSE 0 END,
CASE WHEN @Delta>0 THEN @Delta ELSE 0 END,@Balance,'Configuration package vendor balance',@Id);", con, tran);
            ledger.Parameters.AddWithValue("@Id", id); ledger.Parameters.AddWithValue("@Delta", delta); ledger.Parameters.AddWithValue("@DocumentNo", documentNo);
            await ledger.ExecuteNonQueryAsync();
        }
        return new PartyApplyResult(inserted, delta != 0, delta);
    }

    private static async Task<int?> EnsureLookupAsync(SqlConnection con, SqlTransaction tran, string table, string idColumn, string nameColumn, string value)
    {
        value = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Categories", "CategoryId", "CategoryName", "Brands", "BrandId", "BrandName" };
        if (!safe.Contains(table) || !safe.Contains(idColumn) || !safe.Contains(nameColumn)) throw new InvalidOperationException("Unsafe item lookup mapping.");
        await using var cmd = new SqlCommand($@"
DECLARE @Id INT=(SELECT TOP 1 [{idColumn}] FROM [{table}] WHERE [{nameColumn}]=@Name);
IF @Id IS NULL BEGIN INSERT INTO [{table}]([{nameColumn}]) VALUES(@Name); SET @Id=SCOPE_IDENTITY(); END;
SELECT @Id;", con, tran);
        cmd.Parameters.AddWithValue("@Name", value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> EnsureTaxGroupAsync(SqlConnection con, SqlTransaction tran, decimal percent, bool inclusive)
    {
        await using var cmd = new SqlCommand(@"
DECLARE @Id INT=(SELECT TOP 1 TaxGroupId FROM TaxGroups WHERE TaxPercent=@Percent AND ISNULL(IsInclusive,0)=@Inclusive ORDER BY TaxGroupId);
IF @Id IS NULL
BEGIN
 INSERT INTO TaxGroups(TaxGroupName,TaxPercent,IsInclusive,IsActive) VALUES(CONCAT('TAX ',FORMAT(@Percent,'0.##'),CASE WHEN @Inclusive=1 THEN '% Inclusive' ELSE '%' END),@Percent,@Inclusive,1);
 SET @Id=SCOPE_IDENTITY();
END;
SELECT @Id;", con, tran);
        cmd.Parameters.AddWithValue("@Percent", percent);
        cmd.Parameters.AddWithValue("@Inclusive", inclusive);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<Dictionary<string, string>> ReadPostingSetupAsync(SqlConnection con, SqlTransaction tran)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 InventoryAccount,ReceivableAccount,PayableAccount,OpeningBalanceAccount FROM PostingSetup WHERE SetupId=1", con, tran);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Posting setup is required before importing balances.");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["InventoryAccount"] = Convert.ToString(reader["InventoryAccount"]) ?? string.Empty,
            ["ReceivableAccount"] = Convert.ToString(reader["ReceivableAccount"]) ?? string.Empty,
            ["PayableAccount"] = Convert.ToString(reader["PayableAccount"]) ?? string.Empty,
            ["OpeningBalanceAccount"] = Convert.ToString(reader["OpeningBalanceAccount"]) ?? string.Empty
        };
    }

    private static async Task PostBalancePairAsync(SqlConnection con, SqlTransaction tran, Dictionary<string, string> setup,
        string documentNo, string documentType, decimal delta, string assetAccount, string equityAccount, UserSession user, long sourceId)
    {
        if (delta == 0) return;
        if (delta > 0)
        {
            await InsertGlAsync(con, tran, assetAccount, documentType, documentNo, delta, 0, user, sourceId);
            await InsertGlAsync(con, tran, equityAccount, documentType, documentNo, 0, delta, user, sourceId);
        }
        else
        {
            await InsertGlAsync(con, tran, equityAccount, documentType, documentNo, Math.Abs(delta), 0, user, sourceId);
            await InsertGlAsync(con, tran, assetAccount, documentType, documentNo, 0, Math.Abs(delta), user, sourceId);
        }
    }

    private static async Task PostLiabilityBalancePairAsync(SqlConnection con, SqlTransaction tran, Dictionary<string, string> setup,
        string documentNo, string documentType, decimal delta, string liabilityAccount, string equityAccount, UserSession user, long sourceId)
    {
        if (delta == 0) return;
        if (delta > 0)
        {
            await InsertGlAsync(con, tran, equityAccount, documentType, documentNo, delta, 0, user, sourceId);
            await InsertGlAsync(con, tran, liabilityAccount, documentType, documentNo, 0, delta, user, sourceId);
        }
        else
        {
            await InsertGlAsync(con, tran, liabilityAccount, documentType, documentNo, Math.Abs(delta), 0, user, sourceId);
            await InsertGlAsync(con, tran, equityAccount, documentType, documentNo, 0, Math.Abs(delta), user, sourceId);
        }
    }

    private static async Task InsertGlAsync(SqlConnection con, SqlTransaction tran, string accountNo, string documentType,
        string documentNo, decimal debit, decimal credit, UserSession user, long sourceId)
    {
        await using (var period = new SqlCommand("SELECT COUNT(1) FROM AccountingPeriods WHERE IsClosed=1 AND CAST(GETDATE() AS DATE) BETWEEN StartDate AND EndDate", con, tran))
            if (Convert.ToInt32(await period.ExecuteScalarAsync()) > 0) throw new InvalidOperationException("Today's posting date is in a closed accounting period.");
        await using var cmd = new SqlCommand(@"
INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId,BranchCode)
SELECT AccountId,CAST(GETDATE() AS DATE),@DocumentType,@DocumentNo,@Debit,@Credit,'Configuration package balance import',@SourceId,@BranchCode
FROM ChartOfAccounts WHERE AccountNo=@AccountNo AND IsActive=1;
IF @@ROWCOUNT=0 THROW 50020, 'A required G/L posting account is missing or inactive.', 1;", con, tran);
        cmd.Parameters.AddWithValue("@AccountNo", accountNo); cmd.Parameters.AddWithValue("@DocumentType", documentType); cmd.Parameters.AddWithValue("@DocumentNo", documentNo);
        cmd.Parameters.AddWithValue("@Debit", debit); cmd.Parameters.AddWithValue("@Credit", credit); cmd.Parameters.AddWithValue("@SourceId", sourceId > int.MaxValue ? int.MaxValue : (int)sourceId);
        cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode ?? string.Empty); await cmd.ExecuteNonQueryAsync();
    }

    private static string BuildDocumentNo(string packageCode, long importId)
    {
        var value = $"PKG-{NormalizeCode(packageCode)}-{importId}";
        return value.Length <= 50 ? value : value[..50];
    }

    private static string NormalizeCode(string? value)
    {
        var code = new string((value ?? string.Empty).Trim().ToUpperInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return code.Length <= 30 ? code : code[..30];
    }

    private static string Value(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
    private static decimal DecimalValue(Dictionary<string, string> row, string key, decimal defaultValue = 0) => TryDecimal(Value(row, key), out var value) ? value : defaultValue;
    private static bool BoolValue(Dictionary<string, string> row, string key, bool defaultValue) => TryBool(Value(row, key), out var value) ? value : defaultValue;
    private static bool TryDecimal(string? text, out decimal value) => decimal.TryParse((text ?? string.Empty).Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    private static bool TryBool(string? text, out bool value)
    {
        var normalized = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized is "TRUE" or "YES" or "Y" or "1") { value = true; return true; }
        if (normalized is "FALSE" or "NO" or "N" or "0") { value = false; return true; }
        value = false; return false;
    }

    private sealed record PackageDefinition(long PackageId, string PackageCode, string PackageName, string Description,
        bool IncludeItems, bool IncludeCustomers, bool IncludeVendors, bool IncludeBalances);
    private sealed record ItemApplyResult(bool Inserted, bool BalanceChanged, decimal ValueDelta);
    private sealed record PartyApplyResult(bool Inserted, bool BalanceChanged, decimal BalanceDelta);
    private sealed record SheetData(string Name, IReadOnlyList<string> Headers, List<Dictionary<string, string>> Rows);

    private static class XlsxCodec
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static byte[] Write(IReadOnlyList<SheetData> sheets)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                WriteEntry(archive, "[Content_Types].xml", ContentTypes(sheets.Count));
                WriteEntry(archive, "_rels/.rels", new XDocument(new XElement(PackageRel + "Relationships",
                    new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "xl/workbook.xml")))));
                WriteEntry(archive, "xl/workbook.xml", Workbook(sheets));
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
                WriteEntry(archive, "xl/styles.xml", Styles());
                for (var i = 0; i < sheets.Count; i++) WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]));
            }
            return output.ToArray();
        }

        public static ConfigurationPackageWorkbook Read(byte[] bytes)
        {
            var result = new ConfigurationPackageWorkbook();
            using var stream = new MemoryStream(bytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            if (archive.Entries.Count > 100 || archive.Entries.Sum(x => x.Length) > 150L * 1024 * 1024)
                throw new InvalidOperationException("The Excel package is too large or contains too many internal files.");
            var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidOperationException("Excel workbook.xml is missing.");
            var relEntry = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidOperationException("Excel workbook relationships are missing.");
            var workbook = Load(workbookEntry);
            var relationships = Load(relEntry).Root?.Elements(PackageRel + "Relationship")
                .ToDictionary(x => (string?)x.Attribute("Id") ?? string.Empty, x => (string?)x.Attribute("Target") ?? string.Empty)
                ?? new Dictionary<string, string>();
            var sharedStrings = ReadSharedStrings(archive);
            foreach (var sheet in workbook.Descendants(Main + "sheet"))
            {
                var name = (string?)sheet.Attribute("name") ?? string.Empty;
                var relId = (string?)sheet.Attribute(Rel + "id") ?? string.Empty;
                if (!relationships.TryGetValue(relId, out var target)) continue;
                var path = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
                path = NormalizeZipPath(path);
                var entry = archive.GetEntry(path);
                if (entry == null) continue;
                var (headers, rows) = ReadWorksheet(entry, sharedStrings);
                result.Headers[name] = headers;
                if (name.Equals("Items", StringComparison.OrdinalIgnoreCase)) result.Items.AddRange(rows);
                else if (name.Equals("Customers", StringComparison.OrdinalIgnoreCase)) result.Customers.AddRange(rows);
                else if (name.Equals("Vendors", StringComparison.OrdinalIgnoreCase)) result.Vendors.AddRange(rows);
            }
            return result;
        }

        private static XDocument Worksheet(SheetData sheet)
        {
            var sheetData = new XElement(Main + "sheetData");
            var headerRow = new XElement(Main + "row", new XAttribute("r", 1));
            for (var i = 0; i < sheet.Headers.Count; i++) headerRow.Add(Cell(i + 1, 1, sheet.Headers[i], 1));
            sheetData.Add(headerRow);
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var row = new XElement(Main + "row", new XAttribute("r", r + 2));
                for (var c = 0; c < sheet.Headers.Count; c++)
                    row.Add(Cell(c + 1, r + 2, sheet.Rows[r].TryGetValue(sheet.Headers[c], out var value) ? value : string.Empty, 0));
                sheetData.Add(row);
            }
            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(Main + "worksheet",
                    new XElement(Main + "sheetViews", new XElement(Main + "sheetView", new XAttribute("workbookViewId", 0),
                        new XElement(Main + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
                    new XElement(Main + "cols", sheet.Headers.Select((_, i) => new XElement(Main + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", i is 2 or 4 ? 28 : 16), new XAttribute("customWidth", 1)))),
                    sheetData,
                    new XElement(Main + "autoFilter", new XAttribute("ref", $"A1:{ColumnName(sheet.Headers.Count)}{Math.Max(1, sheet.Rows.Count + 1)}"))));
        }

        private static XElement Cell(int column, int row, string? value, int style)
        {
            value ??= string.Empty;
            return new XElement(Main + "c", new XAttribute("r", ColumnName(column) + row), new XAttribute("t", "inlineStr"), new XAttribute("s", style),
                new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
        }

        private static XDocument Workbook(IReadOnlyList<SheetData> sheets) => new(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(Main + "workbook", new XAttribute(XNamespace.Xmlns + "r", Rel),
                new XElement(Main + "sheets", sheets.Select((s, i) => new XElement(Main + "sheet", new XAttribute("name", SafeSheetName(s.Name)), new XAttribute("sheetId", i + 1), new XAttribute(Rel + "id", "rId" + (i + 1)))))));

        private static XDocument WorkbookRelationships(int count) => new(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(PackageRel + "Relationships",
                Enumerable.Range(1, count).Select(i => new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId" + i), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"), new XAttribute("Target", $"worksheets/sheet{i}.xml")))
                    .Append(new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId" + (count + 1)), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"), new XAttribute("Target", "styles.xml")))));

        private static XDocument ContentTypes(int count) => new(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types") + "Types",
                new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types") + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types") + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types") + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types") + "Override", new XAttribute("PartName", "/xl/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
                Enumerable.Range(1, count).Select(i => new XElement(XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types") + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{i}.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));

        private static XDocument Styles() => new(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Main + "styleSheet",
            new XElement(Main + "fonts", new XAttribute("count", 2),
                new XElement(Main + "font", new XElement(Main + "sz", new XAttribute("val", 11)), new XElement(Main + "name", new XAttribute("val", "Calibri"))),
                new XElement(Main + "font", new XElement(Main + "b"), new XElement(Main + "color", new XAttribute("rgb", "FFFFFFFF")), new XElement(Main + "sz", new XAttribute("val", 11)), new XElement(Main + "name", new XAttribute("val", "Calibri")))),
            new XElement(Main + "fills", new XAttribute("count", 3),
                new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "gray125"))),
                new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "solid"), new XElement(Main + "fgColor", new XAttribute("rgb", "FF005A7A")), new XElement(Main + "bgColor", new XAttribute("indexed", 64))))),
            new XElement(Main + "borders", new XAttribute("count", 1), new XElement(Main + "border", new XElement(Main + "left"), new XElement(Main + "right"), new XElement(Main + "top"), new XElement(Main + "bottom"), new XElement(Main + "diagonal"))),
            new XElement(Main + "cellStyleXfs", new XAttribute("count", 1), new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0))),
            new XElement(Main + "cellXfs", new XAttribute("count", 2),
                new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0)),
                new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 1), new XAttribute("fillId", 2), new XAttribute("borderId", 0), new XAttribute("xfId", 0), new XAttribute("applyFont", 1), new XAttribute("applyFill", 1)))));

        private static (List<string> Headers, List<Dictionary<string, string>> Rows) ReadWorksheet(ZipArchiveEntry entry, IReadOnlyList<string> sharedStrings)
        {
            var document = Load(entry);
            var rawRows = new List<Dictionary<int, string>>();
            foreach (var row in document.Descendants(Main + "row"))
            {
                if (rawRows.Count >= 100_000) throw new InvalidOperationException("A package worksheet cannot exceed 100,000 rows.");
                var values = new Dictionary<int, string>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? string.Empty;
                    var column = ColumnIndex(reference);
                    if (column <= 0) continue;
                    var type = (string?)cell.Attribute("t") ?? string.Empty;
                    string value;
                    if (type == "inlineStr") value = string.Concat(cell.Descendants(Main + "t").Select(x => x.Value));
                    else
                    {
                        value = cell.Element(Main + "v")?.Value ?? string.Empty;
                        if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count) value = sharedStrings[index];
                        else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                    }
                    values[column] = value.Trim();
                }
                rawRows.Add(values);
            }
            if (rawRows.Count == 0) return (new List<string>(), new List<Dictionary<string, string>>());
            var maxColumn = rawRows[0].Keys.DefaultIfEmpty(0).Max();
            var headers = Enumerable.Range(1, maxColumn).Select(i => rawRows[0].TryGetValue(i, out var h) ? h.Trim() : string.Empty).ToList();
            var rows = new List<Dictionary<string, string>>();
            foreach (var raw in rawRows.Skip(1))
            {
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var hasValue = false;
                for (var i = 0; i < headers.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(headers[i])) continue;
                    var value = raw.TryGetValue(i + 1, out var v) ? v : string.Empty;
                    row[headers[i]] = value;
                    if (!string.IsNullOrWhiteSpace(value)) hasValue = true;
                }
                if (hasValue) rows.Add(row);
            }
            return (headers.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(), rows);
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return new List<string>();
            return Load(entry).Descendants(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList();
        }

        private static XDocument Load(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return XDocument.Load(stream, LoadOptions.None);
        }

        private static void WriteEntry(ZipArchive archive, string path, XDocument document)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            document.Save(writer, SaveOptions.DisableFormatting);
        }

        private static string ColumnName(int index)
        {
            var name = string.Empty;
            while (index > 0) { index--; name = (char)('A' + index % 26) + name; index /= 26; }
            return name;
        }

        private static int ColumnIndex(string reference)
        {
            var index = 0;
            foreach (var c in reference.TakeWhile(char.IsLetter)) index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            return index;
        }

        private static string SafeSheetName(string value)
        {
            var safe = new string((value ?? "Sheet").Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            if (string.IsNullOrWhiteSpace(safe)) safe = "Sheet";
            return safe.Length <= 31 ? safe : safe[..31];
        }

        private static string NormalizeZipPath(string path)
        {
            var stack = new Stack<string>();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "..") { if (stack.Count > 0) stack.Pop(); }
                else if (part != ".") stack.Push(part);
            }
            return string.Join("/", stack.Reverse());
        }
    }
}
