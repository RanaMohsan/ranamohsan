using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Endpoints;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public static class PosSql
{
    public static async Task<int> EnsureOpenShiftAsync(SqlConnection con, SqlTransaction tran, UserSession user)
    {
        await using (var find = new SqlCommand("SELECT TOP 1 ShiftId FROM Shifts WHERE StoreId=@StoreId AND UserId=@UserId AND Status='Open' ORDER BY ShiftId DESC", con, tran))
        {
            find.Parameters.AddWithValue("@StoreId", user.StoreId);
            find.Parameters.AddWithValue("@UserId", user.UserId);
            var existing = await find.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value) return Convert.ToInt32(existing);
        }
        await using (var cmd = new SqlCommand(@"DECLARE @TerminalId INT=(SELECT TOP 1 TerminalId FROM Terminals WHERE StoreId=@StoreId AND IsActive=1 ORDER BY TerminalId);
IF @TerminalId IS NULL
BEGIN
    INSERT INTO Terminals(StoreId,TerminalCode,TerminalName,IsActive) VALUES(@StoreId,CONCAT('COUNTER-',@StoreId),'Counter 01',1);
    SET @TerminalId=SCOPE_IDENTITY();
END;
INSERT INTO Shifts(StoreId,BranchCode,TerminalId,UserId,OpeningCash,Status) OUTPUT INSERTED.ShiftId VALUES(@StoreId,@BranchCode,@TerminalId,@UserId,0,'Open')", con, tran))
        {
            cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
    }

    public static async Task<int> GetWalkInCustomerIdAsync(SqlConnection con, SqlTransaction tran)
    {
        await using var cmd = new SqlCommand(@"
IF EXISTS(SELECT 1 FROM Customers WHERE CustomerCode='WALKIN')
BEGIN
    UPDATE Customers SET CustomerName='Walk-in Customer', IsActive=1 WHERE CustomerCode='WALKIN';
    SELECT TOP 1 CustomerId FROM Customers WHERE CustomerCode='WALKIN' ORDER BY CustomerId;
END
ELSE
BEGIN
    INSERT INTO Customers(CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive)
    OUTPUT INSERTED.CustomerId VALUES('WALKIN','Walk-in Customer','','','',0,0,0,0,1);
END", con, tran);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public static async Task<string> NextNumberAsync(SqlConnection con, SqlTransaction tran, string seriesCode)
    {
        string prefix; long next; int len; bool includeDate;
        await using (var cmd = new SqlCommand("SELECT Prefix,LastNumber+1 NextNumber,NumberLength,IncludeDate FROM NumberSeries WITH(UPDLOCK,HOLDLOCK) WHERE SeriesCode=@Series", con, tran))
        {
            cmd.Parameters.AddWithValue("@Series", seriesCode);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException($"Number series missing: {seriesCode}");
            prefix = SqlRead.String(r,"Prefix"); next = Convert.ToInt64(r["NextNumber"]); len = SqlRead.Int(r,"NumberLength"); includeDate = SqlRead.Bool(r,"IncludeDate");
        }
        await using (var upd = new SqlCommand("UPDATE NumberSeries SET LastNumber=@No WHERE SeriesCode=@Series", con, tran))
        {
            upd.Parameters.AddWithValue("@No", next);
            upd.Parameters.AddWithValue("@Series", seriesCode);
            await upd.ExecuteNonQueryAsync();
        }
        return includeDate ? $"{prefix}-{DateTime.Today:yyyyMMdd}-{next.ToString().PadLeft(len,'0')}" : $"{prefix}-{next.ToString().PadLeft(len,'0')}";
    }

    public static async Task<ProductForSale?> GetProductForSaleAsync(SqlConnection con, SqlTransaction tran, int productId, int storeId = 0)
    {
        await using var cmd = new SqlCommand(@"
SELECT TOP 1 p.ProductId,p.ProductName,p.ProductCode,p.Barcode,p.SalePrice,p.PurchasePrice,ISNULL(sb.Quantity,p.StockOnHand) StockOnHand,ISNULL(t.TaxPercent,0) TaxPercent,ISNULL(t.IsInclusive,0) TaxInclusive,p.DiscountAllowed
FROM Products p
LEFT JOIN TaxGroups t ON t.TaxGroupId=p.TaxGroupId
LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId
WHERE p.ProductId=@ProductId AND p.IsActive=1", con, tran);
        cmd.Parameters.AddWithValue("@ProductId", productId);
        cmd.Parameters.AddWithValue("@StoreId", storeId <= 0 ? 1 : storeId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new ProductForSale(SqlRead.Int(r,"ProductId"), SqlRead.String(r,"ProductName"), SqlRead.String(r,"ProductCode"), SqlRead.String(r,"Barcode"), SqlRead.Decimal(r,"SalePrice"), SqlRead.Decimal(r,"PurchasePrice"), SqlRead.Decimal(r,"StockOnHand"), SqlRead.Decimal(r,"TaxPercent"), SqlRead.Bool(r,"TaxInclusive"), SqlRead.Bool(r,"DiscountAllowed"));
    }

    public static CalculatedSaleLine CalculateLine(ProductForSale p, decimal qty, decimal discountPercent)
    {
        if (qty <= 0) throw new InvalidOperationException("Quantity must be greater than zero.");
        if (!p.DiscountAllowed) discountPercent = 0;
        var gross = Math.Round(qty * p.SalePrice, 2);
        var discount = Math.Round(gross * discountPercent / 100m, 2);
        var taxable = gross - discount;
        decimal tax, total;
        if (p.TaxInclusive)
        {
            tax = p.TaxPercent <= 0 ? 0 : Math.Round(taxable - (taxable / (1 + (p.TaxPercent / 100m))), 2);
            total = taxable;
        }
        else
        {
            tax = Math.Round(taxable * p.TaxPercent / 100m, 2);
            total = taxable + tax;
        }
        return new CalculatedSaleLine(p.ProductId, p.ProductName, qty, p.SalePrice, p.PurchasePrice, discountPercent, gross, discount, p.TaxPercent, tax, total, p.TaxInclusive);
    }

    public static async Task InsertSaleLineAndStockAsync(SqlConnection con, SqlTransaction tran, int saleId, string invoiceNo, UserSession user, CalculatedSaleLine line)
    {
        await using var cmd = new SqlCommand(@"
INSERT INTO SalesLines(SaleId,ProductId,ProductName,Quantity,UnitPrice,DiscountPercent,DiscountAmount,TaxPercent,TaxAmount,LineTotal,UnitCost,TaxInclusive)
VALUES(@SaleId,@ProductId,@ProductName,@Quantity,@UnitPrice,@DiscountPercent,@DiscountAmount,@TaxPercent,@TaxAmount,@LineTotal,@UnitCost,@TaxInclusive);
UPDATE Products SET StockOnHand=StockOnHand-@Quantity WHERE ProductId=@ProductId;
UPDATE StockByStore SET Quantity=Quantity-@Quantity WHERE StoreId=@StoreId AND ProductId=@ProductId;
INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy)
VALUES(@StoreId,@BranchCode,@ProductId,'Sale',@InvoiceNo,0,@Quantity,@UnitCost,'Cloud POS sale',@UserId);", con, tran);
        cmd.Parameters.AddWithValue("@SaleId", saleId);
        cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
        cmd.Parameters.AddWithValue("@ProductName", line.ProductName);
        cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
        cmd.Parameters.AddWithValue("@UnitPrice", line.UnitPrice);
        cmd.Parameters.AddWithValue("@DiscountPercent", line.DiscountPercent);
        cmd.Parameters.AddWithValue("@DiscountAmount", line.DiscountAmount);
        cmd.Parameters.AddWithValue("@TaxPercent", line.TaxPercent);
        cmd.Parameters.AddWithValue("@TaxAmount", line.TaxAmount);
        cmd.Parameters.AddWithValue("@LineTotal", line.LineTotal);
        cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
        cmd.Parameters.AddWithValue("@TaxInclusive", line.TaxInclusive);
        cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
        cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
        cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task PostBasicSalesAccountingAsync(SqlConnection con, SqlTransaction tran, string invoiceNo, int saleId, decimal grandTotal, decimal taxAmount, decimal costTotal, List<SalePaymentRequest> payments, string branchCode = "")
    {
        var setup = await GetPostingSetupAsync(con, tran);
        // Allocate tenders only up to grandTotal. Cash → CashAccount; Bank-like → selected AccountNo (or default Bank); Credit → AR.
        var cashByAccount = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var bankByAccount = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        decimal creditAmount = 0, remaining = grandTotal;

        foreach (var p in payments)
        {
            if (remaining <= 0) break;
            var take = Math.Min(p.Amount, remaining);
            if (take <= 0) continue;
            remaining -= take;
            var method = BankAccountEndpoints.NormalizePaymentMethodName(p.PaymentMethodName);
            if (method.Equals("Credit", StringComparison.OrdinalIgnoreCase))
            {
                creditAmount += take;
                continue;
            }

            var accountNo = await BankAccountEndpoints.ResolvePaymentAccountAsync(con, tran, new SalePaymentRequest
            {
                PaymentMethodId = p.PaymentMethodId,
                PaymentMethodName = method,
                Amount = take,
                ReferenceNo = p.ReferenceNo,
                AccountNo = p.AccountNo,
                BankAccountId = p.BankAccountId
            }, setup);

            if (IsBankLikePayment(method))
            {
                bankByAccount[accountNo] = bankByAccount.GetValueOrDefault(accountNo) + take;
            }
            else
            {
                cashByAccount[accountNo] = cashByAccount.GetValueOrDefault(accountNo) + take;
            }
        }

        var netSales = grandTotal - taxAmount;
        foreach (var kv in cashByAccount)
            await InsertGlAsync(con, tran, kv.Key, DateTime.Today, "POS Sale", invoiceNo, kv.Value, 0, "Cash received", saleId, branchCode);
        foreach (var kv in bankByAccount)
            await InsertGlAsync(con, tran, kv.Key, DateTime.Today, "POS Sale", invoiceNo, kv.Value, 0, "Bank received", saleId, branchCode);
        if (creditAmount > 0) await InsertGlAsync(con, tran, setup["ReceivableAccount"], DateTime.Today, "POS Sale", invoiceNo, creditAmount, 0, "Customer receivable", saleId, branchCode);
        if (netSales > 0) await InsertGlAsync(con, tran, setup["SalesAccount"], DateTime.Today, "POS Sale", invoiceNo, 0, netSales, "Sales revenue", saleId, branchCode);
        if (taxAmount > 0) await InsertGlAsync(con, tran, setup["OutputTaxAccount"], DateTime.Today, "POS Sale", invoiceNo, 0, taxAmount, "Output tax", saleId, branchCode);
        if (costTotal > 0)
        {
            await InsertGlAsync(con, tran, setup["CogsAccount"], DateTime.Today, "POS Sale", invoiceNo, costTotal, 0, "Cost of goods sold", saleId, branchCode);
            await InsertGlAsync(con, tran, setup["InventoryAccount"], DateTime.Today, "POS Sale", invoiceNo, 0, costTotal, "Inventory issued", saleId, branchCode);
        }
    }

    public static bool IsBankLikePayment(string paymentMethod)
    {
        var m = BankAccountEndpoints.NormalizePaymentMethodName(paymentMethod);
        return m.Equals("Bank", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Card", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Bank Transfer", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Wallet", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Cheque", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Check", StringComparison.OrdinalIgnoreCase);
    }

    public static string PaymentGlAccount(Dictionary<string, string> setup, string paymentMethod)
        => IsBankLikePayment(paymentMethod) ? setup["BankAccount"] : setup["CashAccount"];

    private static async Task<Dictionary<string,string>> GetPostingSetupAsync(SqlConnection con, SqlTransaction tran)
    {
        await using var cmd = new SqlCommand("SELECT TOP 1 CashAccount,BankAccount,ReceivableAccount,InventoryAccount,InputTaxAccount,PayableAccount,OutputTaxAccount,SalesAccount,CogsAccount FROM PostingSetup WHERE SetupId=1", con, tran);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("Posting setup is missing.");
        return new Dictionary<string,string>
        {
            ["CashAccount"] = SqlRead.String(r,"CashAccount"),
            ["BankAccount"] = SqlRead.String(r,"BankAccount"),
            ["ReceivableAccount"] = SqlRead.String(r,"ReceivableAccount"),
            ["InventoryAccount"] = SqlRead.String(r,"InventoryAccount"),
            ["InputTaxAccount"] = SqlRead.String(r,"InputTaxAccount"),
            ["PayableAccount"] = SqlRead.String(r,"PayableAccount"),
            ["OutputTaxAccount"] = SqlRead.String(r,"OutputTaxAccount"),
            ["SalesAccount"] = SqlRead.String(r,"SalesAccount"),
            ["CogsAccount"] = SqlRead.String(r,"CogsAccount")
        };
    }

    private static async Task InsertGlAsync(SqlConnection con, SqlTransaction tran, string accountNo, DateTime postingDate, string documentType, string documentNo, decimal debit, decimal credit, string description, int sourceId, string branchCode = "")
    {
        await using (var period = new SqlCommand("IF OBJECT_ID('AccountingPeriods') IS NULL SELECT 0 ELSE SELECT COUNT(1) FROM AccountingPeriods WHERE IsClosed=1 AND @PostingDate BETWEEN StartDate AND EndDate", con, tran))
        {
            period.Parameters.AddWithValue("@PostingDate", postingDate.Date);
            if (Convert.ToInt32(await period.ExecuteScalarAsync()) > 0)
                throw new InvalidOperationException($"Posting date {postingDate:yyyy-MM-dd} is in a closed accounting period.");
        }
        await using var cmd = new SqlCommand(@"
IF COL_LENGTH('GLEntries','BranchCode') IS NOT NULL
BEGIN
    INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId,BranchCode)
    SELECT AccountId,@PostingDate,@DocumentType,@DocumentNo,@Debit,@Credit,@Description,@SourceId,@BranchCode FROM ChartOfAccounts WHERE AccountNo=@AccountNo;
END
ELSE
BEGIN
    INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId)
    SELECT AccountId,@PostingDate,@DocumentType,@DocumentNo,@Debit,@Credit,@Description,@SourceId FROM ChartOfAccounts WHERE AccountNo=@AccountNo;
END", con, tran);
        cmd.Parameters.AddWithValue("@AccountNo", accountNo);
        cmd.Parameters.AddWithValue("@PostingDate", postingDate.Date);
        cmd.Parameters.AddWithValue("@DocumentType", documentType);
        cmd.Parameters.AddWithValue("@DocumentNo", documentNo);
        cmd.Parameters.AddWithValue("@Debit", debit);
        cmd.Parameters.AddWithValue("@Credit", credit);
        cmd.Parameters.AddWithValue("@Description", description);
        cmd.Parameters.AddWithValue("@SourceId", sourceId);
        cmd.Parameters.AddWithValue("@BranchCode", branchCode ?? "");
        if (await cmd.ExecuteNonQueryAsync() == 0) throw new InvalidOperationException($"G/L account missing: {accountNo}");
    }

}
