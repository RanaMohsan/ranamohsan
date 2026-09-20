using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;
using System.Data;

namespace PayNex.Cloud.Api.Endpoints;

public static class InvoiceDraftEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceDraftEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sales-invoices/draft", SaveSalesInvoiceDraftAsync);
        app.MapPost("/api/purchase-invoices/draft", SavePurchaseInvoiceDraftAsync);
        return app;
    }

    private static async Task<IResult> SaveSalesInvoiceDraftAsync(
        HttpContext http,
        ConnectionFactory db,
        AuthTokenService tokens,
        SalesInvoicePostRequest request)
    {
        var user = ApiAuth.RequireUser(http, tokens);
        if (user == null) return Results.Unauthorized();
        user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
        if (request.CustomerId <= 0 || (request.SalesInvoiceId <= 0 && request.Lines.Count == 0))
            return Results.BadRequest(new { message = "Customer and at least one invoice line are required to create the draft." });

        await using var con = await db.OpenTenantAsync(user.DatabaseName);
        await using var ensure = con.CreateCommand();
        ensure.CommandText = @"
IF OBJECT_ID('SalesInvoiceHeader') IS NOT NULL AND COL_LENGTH('SalesInvoiceHeader','ApplicationSource') IS NULL
    ALTER TABLE SalesInvoiceHeader ADD ApplicationSource NVARCHAR(20) NOT NULL CONSTRAINT DF_SalesInvoiceHeader_ApplicationSource DEFAULT 'Cloud';";
        await ensure.ExecuteNonQueryAsync();
        var applicationSource = ApiAuth.ResolveApplicationSource(http, user);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            decimal subTotal = 0, discount = 0, tax = 0, grand = 0;
            var computed = new List<CalculatedSaleLine>();
            foreach (var lineRequest in request.Lines)
            {
                var product = await PosSql.GetProductForSaleAsync(con, tran, lineRequest.ProductId, user.StoreId)
                    ?? throw new InvalidOperationException("Product not found.");
                var effectiveProduct = new ProductForSale(
                    product.ProductId,
                    product.ProductName,
                    product.ProductCode,
                    product.Barcode,
                    lineRequest.UnitPrice <= 0 ? product.SalePrice : lineRequest.UnitPrice,
                    product.PurchasePrice,
                    product.StockOnHand,
                    lineRequest.TaxPercent,
                    product.TaxInclusive,
                    product.DiscountAllowed);
                var line = PosSql.CalculateLine(effectiveProduct, lineRequest.Quantity, lineRequest.DiscountPercent);
                computed.Add(line);
                subTotal += line.Gross;
                discount += line.DiscountAmount;
                tax += line.TaxAmount;
                grand += line.LineTotal;
            }

            var balance = Math.Max(0, grand - request.PaidAmount);
            var invoiceId = request.SalesInvoiceId;
            string invoiceNo;
            if (invoiceId > 0)
            {
                invoiceNo = await GetOpenInvoiceNoAsync(
                    con,
                    tran,
                    "SalesInvoiceHeader",
                    "SalesInvoiceId",
                    invoiceId,
                    user.StoreId,
                    "sales");

                await using var update = new SqlCommand(@"
UPDATE SalesInvoiceHeader
SET CustomerId=@CustomerId,InvoiceDate=@Date,UserId=@UserId,SubTotal=@SubTotal,
    DiscountAmount=@Discount,TaxAmount=@Tax,GrandTotal=@Grand,PaidAmount=@Paid,
    BalanceAmount=@Balance,Remarks=@Remarks,ApplicationSource=@ApplicationSource
WHERE SalesInvoiceId=@Id AND StoreId=@StoreId AND Status='Open';
DELETE FROM SalesInvoiceLines WHERE SalesInvoiceId=@Id;", con, tran);
                update.Parameters.AddWithValue("@Id", invoiceId);
                update.Parameters.AddWithValue("@StoreId", user.StoreId);
                update.Parameters.AddWithValue("@CustomerId", request.CustomerId);
                update.Parameters.AddWithValue("@Date", request.InvoiceDate.Date);
                update.Parameters.AddWithValue("@UserId", user.UserId);
                update.Parameters.AddWithValue("@SubTotal", subTotal);
                update.Parameters.AddWithValue("@Discount", discount);
                update.Parameters.AddWithValue("@Tax", tax);
                update.Parameters.AddWithValue("@Grand", grand);
                update.Parameters.AddWithValue("@Paid", request.PaidAmount);
                update.Parameters.AddWithValue("@Balance", balance);
                update.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
                update.Parameters.AddWithValue("@ApplicationSource", applicationSource);
                await update.ExecuteNonQueryAsync();
            }
            else
            {
                invoiceNo = await PosSql.NextNumberAsync(con, tran, "SALES_INVOICE");
                await using var insert = new SqlCommand(@"
INSERT INTO SalesInvoiceHeader(
    InvoiceNo,CustomerId,InvoiceDate,StoreId,BranchCode,UserId,SubTotal,DiscountAmount,
    TaxAmount,GrandTotal,PaidAmount,BalanceAmount,Status,Remarks,ApplicationSource)
OUTPUT INSERTED.SalesInvoiceId
VALUES(@No,@CustomerId,@Date,@StoreId,@BranchCode,@UserId,@SubTotal,@Discount,
    @Tax,@Grand,@Paid,@Balance,'Open',@Remarks,@ApplicationSource);", con, tran);
                insert.Parameters.AddWithValue("@No", invoiceNo);
                insert.Parameters.AddWithValue("@CustomerId", request.CustomerId);
                insert.Parameters.AddWithValue("@Date", request.InvoiceDate.Date);
                insert.Parameters.AddWithValue("@StoreId", user.StoreId);
                insert.Parameters.AddWithValue("@BranchCode", user.BranchCode);
                insert.Parameters.AddWithValue("@UserId", user.UserId);
                insert.Parameters.AddWithValue("@SubTotal", subTotal);
                insert.Parameters.AddWithValue("@Discount", discount);
                insert.Parameters.AddWithValue("@Tax", tax);
                insert.Parameters.AddWithValue("@Grand", grand);
                insert.Parameters.AddWithValue("@Paid", request.PaidAmount);
                insert.Parameters.AddWithValue("@Balance", balance);
                insert.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
                insert.Parameters.AddWithValue("@ApplicationSource", applicationSource);
                invoiceId = Convert.ToInt32(await insert.ExecuteScalarAsync());
            }

            foreach (var line in computed)
            {
                await using var insertLine = new SqlCommand(@"
INSERT INTO SalesInvoiceLines(
    SalesInvoiceId,ProductId,ProductName,Quantity,UnitPrice,DiscountPercent,DiscountAmount,
    TaxPercent,TaxAmount,LineTotal,UnitCost,TaxInclusive)
VALUES(@Id,@ProductId,@Name,@Qty,@Price,@DiscPct,@Disc,@TaxPct,@Tax,@Total,@Cost,@TaxInclusive);", con, tran);
                insertLine.Parameters.AddWithValue("@Id", invoiceId);
                insertLine.Parameters.AddWithValue("@ProductId", line.ProductId);
                insertLine.Parameters.AddWithValue("@Name", line.ProductName);
                insertLine.Parameters.AddWithValue("@Qty", line.Quantity);
                insertLine.Parameters.AddWithValue("@Price", line.UnitPrice);
                insertLine.Parameters.AddWithValue("@DiscPct", line.DiscountPercent);
                insertLine.Parameters.AddWithValue("@Disc", line.DiscountAmount);
                insertLine.Parameters.AddWithValue("@TaxPct", line.TaxPercent);
                insertLine.Parameters.AddWithValue("@Tax", line.TaxAmount);
                insertLine.Parameters.AddWithValue("@Total", line.LineTotal);
                insertLine.Parameters.AddWithValue("@Cost", line.UnitCost);
                insertLine.Parameters.AddWithValue("@TaxInclusive", line.TaxInclusive);
                await insertLine.ExecuteNonQueryAsync();
            }

            await tran.CommitAsync();
            return Results.Ok(new
            {
                salesInvoiceId = invoiceId,
                invoiceNo,
                status = "Open",
                grandTotal = grand,
                message = "Sales invoice draft saved."
            });
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> SavePurchaseInvoiceDraftAsync(
        HttpContext http,
        ConnectionFactory db,
        AuthTokenService tokens,
        PurchasePostRequest request)
    {
        var user = ApiAuth.RequireUser(http, tokens);
        if (user == null) return Results.Unauthorized();
        user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
        if (request.PurchaseInvoiceId <= 0 && request.VendorId <= 0)
            return Results.BadRequest(new { message = "Vendor is required to create the draft." });

        await using var con = await db.OpenTenantAsync(user.DatabaseName);
        await EnsurePurchaseDraftSchemaAsync(con);
        await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            decimal subTotal = 0, taxAmount = 0;
            var computed = new List<(PurchaseLineRequest Request, ProductForSale Product, decimal TaxAmount, decimal LineTotal)>();
            foreach (var line in request.Lines)
            {
                if (line.Quantity <= 0) throw new InvalidOperationException("Quantity must be greater than zero.");
                if (line.UnitCost < 0) throw new InvalidOperationException("Unit cost cannot be negative.");
                var product = await PosSql.GetProductForSaleAsync(con, tran, line.ProductId, user.StoreId)
                    ?? throw new InvalidOperationException("Product not found.");
                var lineSubTotal = line.Quantity * line.UnitCost;
                var lineTax = Math.Round(lineSubTotal * line.TaxPercent / 100m, 2);
                var lineTotal = lineSubTotal + lineTax;
                subTotal += lineSubTotal;
                taxAmount += lineTax;
                computed.Add((line, product, lineTax, lineTotal));
            }

            var grandTotal = subTotal + taxAmount;
            var balance = Math.Max(0, grandTotal - request.PaidAmount);
            var invoiceId = request.PurchaseInvoiceId;
            string invoiceNo;
            if (invoiceId > 0)
            {
                invoiceNo = await GetOpenInvoiceNoAsync(
                    con,
                    tran,
                    "PurchaseInvoiceHeader",
                    "PurchaseInvoiceId",
                    invoiceId,
                    user.StoreId,
                    "purchase");

                await using var update = new SqlCommand(@"
UPDATE PurchaseInvoiceHeader
SET VendorId=@VendorId,VendorInvoiceNo=@VendorInvoiceNo,InvoiceDate=@Date,UserId=@UserId,
    SubTotal=@SubTotal,DiscountAmount=0,TaxAmount=@Tax,GrandTotal=@Grand,PaidAmount=@Paid,
    BalanceAmount=@Balance,Remarks=@Remarks
WHERE PurchaseInvoiceId=@Id AND StoreId=@StoreId AND Status='Open';
DELETE FROM PurchaseInvoiceLines WHERE PurchaseInvoiceId=@Id;", con, tran);
                update.Parameters.AddWithValue("@Id", invoiceId);
                update.Parameters.AddWithValue("@StoreId", user.StoreId);
                update.Parameters.Add("@VendorId", SqlDbType.Int).Value = request.VendorId > 0 ? request.VendorId : DBNull.Value;
                update.Parameters.AddWithValue("@VendorInvoiceNo", request.VendorInvoiceNo ?? string.Empty);
                update.Parameters.AddWithValue("@Date", request.InvoiceDate.Date);
                update.Parameters.AddWithValue("@UserId", user.UserId);
                update.Parameters.AddWithValue("@SubTotal", subTotal);
                update.Parameters.AddWithValue("@Tax", taxAmount);
                update.Parameters.AddWithValue("@Grand", grandTotal);
                update.Parameters.AddWithValue("@Paid", request.PaidAmount);
                update.Parameters.AddWithValue("@Balance", balance);
                update.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
                await update.ExecuteNonQueryAsync();
            }
            else
            {
                invoiceNo = await PosSql.NextNumberAsync(con, tran, "PURCHASE_INVOICE");
                await using var insert = new SqlCommand(@"
INSERT INTO PurchaseInvoiceHeader(
    InvoiceNo,VendorId,InvoiceDate,VendorInvoiceNo,StoreId,BranchCode,UserId,SubTotal,
    DiscountAmount,TaxAmount,GrandTotal,PaidAmount,BalanceAmount,Status,Remarks)
OUTPUT INSERTED.PurchaseInvoiceId
VALUES(@No,@VendorId,@Date,@VendorInvoiceNo,@StoreId,@BranchCode,@UserId,@SubTotal,
    0,@Tax,@Grand,@Paid,@Balance,'Open',@Remarks);", con, tran);
                insert.Parameters.AddWithValue("@No", invoiceNo);
                insert.Parameters.Add("@VendorId", SqlDbType.Int).Value = request.VendorId > 0 ? request.VendorId : DBNull.Value;
                insert.Parameters.AddWithValue("@Date", request.InvoiceDate.Date);
                insert.Parameters.AddWithValue("@VendorInvoiceNo", request.VendorInvoiceNo ?? string.Empty);
                insert.Parameters.AddWithValue("@StoreId", user.StoreId);
                insert.Parameters.AddWithValue("@BranchCode", user.BranchCode);
                insert.Parameters.AddWithValue("@UserId", user.UserId);
                insert.Parameters.AddWithValue("@SubTotal", subTotal);
                insert.Parameters.AddWithValue("@Tax", taxAmount);
                insert.Parameters.AddWithValue("@Grand", grandTotal);
                insert.Parameters.AddWithValue("@Paid", request.PaidAmount);
                insert.Parameters.AddWithValue("@Balance", balance);
                insert.Parameters.AddWithValue("@Remarks", request.Remarks ?? string.Empty);
                invoiceId = Convert.ToInt32(await insert.ExecuteScalarAsync());
            }

            foreach (var item in computed)
            {
                await using var insertLine = new SqlCommand(@"
INSERT INTO PurchaseInvoiceLines(
    PurchaseInvoiceId,ProductId,ProductName,Quantity,UnitCost,TaxPercent,TaxAmount,LineTotal,TaxInclusive)
VALUES(@Id,@ProductId,@Name,@Qty,@Cost,@TaxPercent,@TaxAmount,@LineTotal,0);", con, tran);
                insertLine.Parameters.AddWithValue("@Id", invoiceId);
                insertLine.Parameters.AddWithValue("@ProductId", item.Product.ProductId);
                insertLine.Parameters.AddWithValue("@Name", item.Product.ProductName);
                insertLine.Parameters.AddWithValue("@Qty", item.Request.Quantity);
                insertLine.Parameters.AddWithValue("@Cost", item.Request.UnitCost);
                insertLine.Parameters.AddWithValue("@TaxPercent", item.Request.TaxPercent);
                insertLine.Parameters.AddWithValue("@TaxAmount", item.TaxAmount);
                insertLine.Parameters.AddWithValue("@LineTotal", item.LineTotal);
                await insertLine.ExecuteNonQueryAsync();
            }

            await tran.CommitAsync();
            return Results.Ok(new
            {
                purchaseInvoiceId = invoiceId,
                invoiceNo,
                status = "Open",
                grandTotal,
                message = "Purchase invoice draft saved."
            });
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task EnsurePurchaseDraftSchemaAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('PurchaseInvoiceHeader') IS NOT NULL
   AND EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('PurchaseInvoiceHeader') AND name='VendorId' AND is_nullable=0)
BEGIN
    ALTER TABLE PurchaseInvoiceHeader ALTER COLUMN VendorId INT NULL;
END;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> GetOpenInvoiceNoAsync(
        SqlConnection con,
        SqlTransaction tran,
        string tableName,
        string idColumn,
        int invoiceId,
        int storeId,
        string invoiceType)
    {
        var sql = $"SELECT InvoiceNo FROM {tableName} WITH(UPDLOCK,HOLDLOCK) WHERE {idColumn}=@Id AND StoreId=@StoreId AND Status='Open'";
        await using var find = new SqlCommand(sql, con, tran);
        find.Parameters.AddWithValue("@Id", invoiceId);
        find.Parameters.AddWithValue("@StoreId", storeId);
        var existingNo = await find.ExecuteScalarAsync();
        if (existingNo == null || existingNo == DBNull.Value)
            throw new InvalidOperationException($"Open {invoiceType} invoice draft was not found or has already been posted.");
        return Convert.ToString(existingNo) ?? string.Empty;
    }
}
