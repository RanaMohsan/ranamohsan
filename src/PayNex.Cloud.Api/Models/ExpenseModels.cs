namespace PayNex.Cloud.Api.Models;

public sealed record ExpenseUpsertRequest(
    long ExpenseId,
    DateTime ExpenseDate,
    int ExpenseCategoryId,
    string Description,
    decimal Amount,
    int StoreId,
    string? Remarks);

public sealed record ExpenseCategoryUpsertRequest(
    int ExpenseCategoryId,
    string CategoryCode,
    string CategoryName,
    bool IsActive = true);

public sealed record ExpenseReportResult(
    DateTime FromDate,
    DateTime ToDate,
    int BranchId,
    int ExpenseCategoryId,
    string PeriodLabel,
    decimal GrandTotal,
    IReadOnlyList<Dictionary<string, object?>> Rows);
