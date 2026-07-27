namespace PayNex.Cloud.Api.Models;

public sealed record ConfigurationPackageUpsertRequest(
    long PackageId,
    string PackageCode,
    string PackageName,
    string? Description,
    bool IncludeItems,
    bool IncludeCustomers,
    bool IncludeVendors,
    bool IncludeBalances,
    bool IsActive = true);

public sealed record ConfigurationPackageImportRequest(
    string FileName,
    string Base64Content);

public sealed record ConfigurationPackageApplyRequest(long ImportId);

public sealed record ConfigurationPackageValidationIssue(
    string SheetName,
    int RowNumber,
    string FieldName,
    string Message,
    string Severity = "Error");

public sealed record ConfigurationPackageValidationResult(
    long ImportId,
    string FileName,
    int ItemRows,
    int CustomerRows,
    int VendorRows,
    int ValidRows,
    int ErrorRows,
    bool CanApply,
    IReadOnlyList<ConfigurationPackageValidationIssue> Issues);

public sealed record ConfigurationPackageApplyResult(
    long ImportId,
    string PackageCode,
    int ItemsInserted,
    int ItemsUpdated,
    int CustomersInserted,
    int CustomersUpdated,
    int VendorsInserted,
    int VendorsUpdated,
    int BalanceEntriesPosted,
    string Message);

public sealed record ConfigurationPackagePreviewResult(
    long PackageId,
    string PackageCode,
    string PackageName,
    string EntityType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<Dictionary<string, string>> Rows);

internal sealed class ConfigurationPackageWorkbook
{
    public List<Dictionary<string, string>> Items { get; set; } = new();
    public List<Dictionary<string, string>> Customers { get; set; } = new();
    public List<Dictionary<string, string>> Vendors { get; set; } = new();
    public Dictionary<string, List<string>> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
