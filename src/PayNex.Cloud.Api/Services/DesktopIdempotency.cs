using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;

namespace PayNex.Cloud.Api.Services;

public static class DesktopIdempotency
{
    public static string NormalizeClientDocumentId(string? clientDocumentId)
    {
        var value = (clientDocumentId ?? string.Empty).Trim();
        return value.Length > 80 ? value[..80] : value;
    }

    public static async Task EnsureSchemaAsync(SqlConnection con, SqlTransaction? tran = null)
    {
        await EnsureClientDocumentColumnAsync(con, tran, "SalesHeader");
        await EnsureClientDocumentColumnAsync(con, tran, "SalesInvoiceHeader");
        await EnsureClientDocumentColumnAsync(con, tran, "SalesReturnOrderHeader");
        await EnsureClientDocumentColumnAsync(con, tran, "PurchaseInvoiceHeader");
    }

    /// <summary>
    /// Older tenant DBs may not have Sales Return tables yet. SQL Server still compiles
    /// ALTER TABLE even inside IF, so missing objects must be skipped with dynamic SQL.
    /// </summary>
    private static async Task EnsureClientDocumentColumnAsync(SqlConnection con, SqlTransaction? tran, string tableName)
    {
        var quoted = tableName.Replace("]", "]]");
        var indexName = $"UX_{quoted}_ExternalClientDocumentId";
        await ExecAsync(con, tran, $@"
IF OBJECT_ID(N'dbo.{quoted}', N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.{quoted}', N'ExternalClientDocumentId') IS NULL
    EXEC(N'ALTER TABLE dbo.{quoted} ADD ExternalClientDocumentId NVARCHAR(80) NULL');
IF OBJECT_ID(N'dbo.{quoted}', N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.{quoted}', N'ExternalClientDocumentId') IS NOT NULL
AND NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'{indexName}' AND object_id = OBJECT_ID(N'dbo.{quoted}'))
    EXEC(N'CREATE UNIQUE INDEX {indexName} ON dbo.{quoted}(ExternalClientDocumentId) WHERE ExternalClientDocumentId IS NOT NULL');");
    }

    public static async Task<ExistingPosSale?> FindPosSaleAsync(SqlConnection con, SqlTransaction tran, string clientDocumentId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 SaleId, InvoiceNo, GrandTotal, PaidAmount, ChangeAmount,
       (SELECT COUNT(1) FROM SalesLines WHERE SaleId = SalesHeader.SaleId) LineCount
FROM SalesHeader
WHERE ExternalClientDocumentId = @ClientDocumentId", con, tran);
        cmd.Parameters.AddWithValue("@ClientDocumentId", clientDocumentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ExistingPosSale(
            SqlRead.Int(reader, "SaleId"),
            SqlRead.String(reader, "InvoiceNo"),
            SqlRead.Decimal(reader, "GrandTotal"),
            SqlRead.Decimal(reader, "PaidAmount"),
            SqlRead.Decimal(reader, "ChangeAmount"),
            SqlRead.Int(reader, "LineCount"));
    }

    public static async Task<ExistingSalesInvoice?> FindSalesInvoiceAsync(SqlConnection con, SqlTransaction tran, string clientDocumentId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 SalesInvoiceId, InvoiceNo, GrandTotal, PaidAmount, BalanceAmount
FROM SalesInvoiceHeader
WHERE ExternalClientDocumentId = @ClientDocumentId", con, tran);
        cmd.Parameters.AddWithValue("@ClientDocumentId", clientDocumentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ExistingSalesInvoice(
            SqlRead.Int(reader, "SalesInvoiceId"),
            SqlRead.String(reader, "InvoiceNo"),
            SqlRead.Decimal(reader, "GrandTotal"),
            SqlRead.Decimal(reader, "PaidAmount"),
            SqlRead.Decimal(reader, "BalanceAmount"));
    }

    public static async Task<ExistingPurchaseInvoice?> FindPurchaseInvoiceAsync(SqlConnection con, SqlTransaction tran, string clientDocumentId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 PurchaseInvoiceId, InvoiceNo, GrandTotal, PaidAmount, BalanceAmount
FROM PurchaseInvoiceHeader
WHERE ExternalClientDocumentId = @ClientDocumentId", con, tran);
        cmd.Parameters.AddWithValue("@ClientDocumentId", clientDocumentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ExistingPurchaseInvoice(
            SqlRead.Int(reader, "PurchaseInvoiceId"),
            SqlRead.String(reader, "InvoiceNo"),
            SqlRead.Decimal(reader, "GrandTotal"),
            SqlRead.Decimal(reader, "PaidAmount"),
            SqlRead.Decimal(reader, "BalanceAmount"));
    }

    public static async Task<ExistingSalesReturnOrder?> FindSalesReturnOrderAsync(SqlConnection con, SqlTransaction tran, string clientDocumentId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 SalesReturnOrderId, ReturnOrderNo, GrandTotal, Status
FROM SalesReturnOrderHeader
WHERE ExternalClientDocumentId = @ClientDocumentId", con, tran);
        cmd.Parameters.AddWithValue("@ClientDocumentId", clientDocumentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ExistingSalesReturnOrder(
            SqlRead.Int(reader, "SalesReturnOrderId"),
            SqlRead.String(reader, "ReturnOrderNo"),
            SqlRead.Decimal(reader, "GrandTotal"),
            SqlRead.String(reader, "Status"));
    }

    public static async Task<ExistingPayment?> FindCustomerPaymentAsync(SqlConnection con, SqlTransaction tran, string clientDocumentId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 PaymentId, PaymentNo FROM CustomerPayments WHERE ExternalClientDocumentId=@ClientDocumentId", con, tran);
        cmd.Parameters.AddWithValue("@ClientDocumentId", clientDocumentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ExistingPayment(SqlRead.Int(reader, "PaymentId"), SqlRead.String(reader, "PaymentNo"));
    }

    public static async Task<ExistingPayment?> FindVendorPaymentAsync(SqlConnection con, SqlTransaction tran, string clientDocumentId)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 PaymentId, PaymentNo FROM VendorPayments WHERE ExternalClientDocumentId=@ClientDocumentId", con, tran);
        cmd.Parameters.AddWithValue("@ClientDocumentId", clientDocumentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ExistingPayment(SqlRead.Int(reader, "PaymentId"), SqlRead.String(reader, "PaymentNo"));
    }

    private static async Task ExecAsync(SqlConnection con, SqlTransaction? tran, string sql)
    {
        await using var cmd = tran == null ? con.CreateCommand() : new SqlCommand { Connection = con, Transaction = tran };
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public sealed record ExistingPosSale(int SaleId, string InvoiceNo, decimal GrandTotal, decimal Paid, decimal Change, int Lines);
    public sealed record ExistingSalesInvoice(int SalesInvoiceId, string InvoiceNo, decimal GrandTotal, decimal PaidAmount, decimal BalanceAmount);
    public sealed record ExistingPurchaseInvoice(int PurchaseInvoiceId, string InvoiceNo, decimal GrandTotal, decimal PaidAmount, decimal BalanceAmount);
    public sealed record ExistingSalesReturnOrder(int SalesReturnOrderId, string ReturnOrderNo, decimal GrandTotal, string Status);
    public sealed record ExistingPayment(int PaymentId, string PaymentNo);
}
