using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;
using PayNex_POS_B1.Services;

namespace PayNex_POS_B1.Repositories;

public class PurchaseRepository
{
    public List<Vendor> SearchVendors(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 200 VendorId, VendorCode, VendorName, ISNULL(ContactPerson,'') ContactPerson,
       ISNULL(Mobile,'') Mobile, ISNULL(Email,'') Email, ISNULL(AddressLine,'') AddressLine,
       ISNULL(PaymentTerms,'') PaymentTerms, OpeningBalance, CurrentBalance, IsActive
FROM Vendors
WHERE IsActive = 1 AND (@Term='' OR VendorCode LIKE @Like OR VendorName LIKE @Like OR Mobile LIKE @Like OR ContactPerson LIKE @Like OR Email LIKE @Like)
ORDER BY VendorName";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        return ReadVendors(cmd);
    }

    public List<Vendor> GetVendors() => SearchVendors("");

    public decimal GetVendorBalance(int vendorId)
    {
        using var con = Db.Open();
        using var cmd = new SqlCommand("SELECT ISNULL(CurrentBalance,0) FROM Vendors WHERE VendorId=@VendorId", con);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return Convert.ToDecimal(cmd.ExecuteScalar() ?? 0m);
    }

    public void UpsertVendor(Vendor v)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Vendors WHERE VendorId=@VendorId)
BEGIN
    UPDATE Vendors SET VendorCode=@VendorCode, VendorName=@VendorName, ContactPerson=@ContactPerson,
        Mobile=@Mobile, Email=@Email, AddressLine=@AddressLine, PaymentTerms=@PaymentTerms,
        OpeningBalance=@OpeningBalance,
        CurrentBalance = CASE WHEN @VendorId > 0 THEN CurrentBalance ELSE @OpeningBalance END,
        IsActive=@IsActive
    WHERE VendorId=@VendorId;
END
ELSE
BEGIN
    INSERT INTO Vendors(VendorCode, VendorName, ContactPerson, Mobile, Email, AddressLine, PaymentTerms, OpeningBalance, CurrentBalance, IsActive)
    VALUES(@VendorCode, @VendorName, @ContactPerson, @Mobile, @Email, @AddressLine, @PaymentTerms, @OpeningBalance, @OpeningBalance, @IsActive);
END";
        cmd.Parameters.AddWithValue("@VendorId", v.VendorId);
        cmd.Parameters.AddWithValue("@VendorCode", v.VendorCode.Trim());
        cmd.Parameters.AddWithValue("@VendorName", v.VendorName.Trim());
        cmd.Parameters.AddWithValue("@ContactPerson", v.ContactPerson.Trim());
        cmd.Parameters.AddWithValue("@Mobile", v.Mobile.Trim());
        cmd.Parameters.AddWithValue("@Email", v.Email.Trim());
        cmd.Parameters.AddWithValue("@AddressLine", v.AddressLine.Trim());
        cmd.Parameters.AddWithValue("@PaymentTerms", v.PaymentTerms.Trim());
        cmd.Parameters.AddWithValue("@OpeningBalance", v.OpeningBalance);
        cmd.Parameters.AddWithValue("@IsActive", v.IsActive);
        cmd.ExecuteNonQuery();
    }

    public string PostPurchaseInvoice(int vendorId, DateTime invoiceDate, string vendorInvoiceNo, IReadOnlyList<PurchaseInvoiceLine> lines, string remarks = "")
    {
        if (PosSession.CurrentUser == null) throw new InvalidOperationException("Login required.");
        if (vendorId <= 0) throw new InvalidOperationException("Select vendor.");
        if (lines.Count == 0) throw new InvalidOperationException("Add at least one item.");
        if (lines.Any(x => x.Quantity <= 0 || x.UnitCost < 0)) throw new InvalidOperationException("Quantity and unit cost must be valid.");

        var tax = lines.Sum(x => x.TaxAmount);
        var grandTotal = lines.Sum(x => x.LineTotal);
        var subTotal = grandTotal - tax;

        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        try
        {
            var invoiceNo = PostingService.NextNumber(con, tran, "PURCHASE_INVOICE");
            int invoiceId;
            using (var cmd = new SqlCommand(@"
INSERT INTO PurchaseInvoiceHeader(InvoiceNo, VendorId, InvoiceDate, VendorInvoiceNo, StoreId, UserId, SubTotal, DiscountAmount, TaxAmount, GrandTotal, PaidAmount, BalanceAmount, Status, Remarks)
OUTPUT INSERTED.PurchaseInvoiceId
VALUES(@InvoiceNo, @VendorId, @InvoiceDate, @VendorInvoiceNo, @StoreId, @UserId, @SubTotal, 0, @TaxAmount, @GrandTotal, 0, @GrandTotal, 'Posted', @Remarks)", con, tran))
            {
                cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
                cmd.Parameters.AddWithValue("@VendorId", vendorId);
                cmd.Parameters.AddWithValue("@InvoiceDate", invoiceDate.Date);
                cmd.Parameters.AddWithValue("@VendorInvoiceNo", vendorInvoiceNo.Trim());
                cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                cmd.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
                cmd.Parameters.AddWithValue("@SubTotal", subTotal);
                cmd.Parameters.AddWithValue("@TaxAmount", tax);
                cmd.Parameters.AddWithValue("@GrandTotal", grandTotal);
                cmd.Parameters.AddWithValue("@Remarks", remarks.Trim());
                invoiceId = Convert.ToInt32(cmd.ExecuteScalar());
            }

            foreach (var line in lines)
            {
                using (var cmd = new SqlCommand(@"
INSERT INTO PurchaseInvoiceLines(PurchaseInvoiceId, ProductId, ProductName, Quantity, UnitCost, TaxPercent, TaxAmount, LineTotal, TaxInclusive)
VALUES(@PurchaseInvoiceId, @ProductId, @ProductName, @Quantity, @UnitCost, @TaxPercent, @TaxAmount, @LineTotal, @TaxInclusive)", con, tran))
                {
                    cmd.Parameters.AddWithValue("@PurchaseInvoiceId", invoiceId);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.Parameters.AddWithValue("@ProductName", line.ProductName);
                    cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
                    cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                    cmd.Parameters.AddWithValue("@TaxPercent", line.TaxPercent);
                    cmd.Parameters.AddWithValue("@TaxAmount", line.TaxAmount);
                    cmd.Parameters.AddWithValue("@LineTotal", line.LineTotal);
                    cmd.Parameters.AddWithValue("@TaxInclusive", line.TaxInclusive);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqlCommand(@"
UPDATE Products SET
PurchasePrice=CASE WHEN StockOnHand+@Qty=0 THEN @UnitCost
ELSE ROUND(((StockOnHand*PurchasePrice)+(@Qty*@UnitCost))/(StockOnHand+@Qty),2) END,
StockOnHand=StockOnHand+@Qty
WHERE ProductId=@ProductId", con, tran))
                {
                    cmd.Parameters.AddWithValue("@Qty", line.Quantity);
                    cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.ExecuteNonQuery();
                }
                using (var stockByStore = new SqlCommand(@"
MERGE StockByStore AS t USING(SELECT @StoreId StoreId,@ProductId ProductId) s
ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=t.Quantity+@Qty,
AverageCost=CASE WHEN t.Quantity+@Qty=0 THEN @UnitCost ELSE ((t.Quantity*t.AverageCost)+(@Qty*@UnitCost))/(t.Quantity+@Qty) END
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@StoreId,@ProductId,@Qty,@UnitCost);", con, tran))
                {
                    stockByStore.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                    stockByStore.Parameters.AddWithValue("@ProductId", line.ProductId);
                    stockByStore.Parameters.AddWithValue("@Qty", line.Quantity);
                    stockByStore.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                    stockByStore.ExecuteNonQuery();
                }

                using (var cmd = new SqlCommand(@"
INSERT INTO InventoryLedger(StoreId, ProductId, MovementType, SourceDocumentNo, QuantityIn, QuantityOut, UnitCost, Remarks, CreatedBy)
VALUES(@StoreId, @ProductId, 'Purchase', @DocNo, @Qty, 0, @UnitCost, 'Purchase Invoice', @CreatedBy)", con, tran))
                {
                    cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.Parameters.AddWithValue("@DocNo", invoiceNo);
                    cmd.Parameters.AddWithValue("@Qty", line.Quantity);
                    cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                    cmd.Parameters.AddWithValue("@CreatedBy", PosSession.CurrentUser.UserId);
                    cmd.ExecuteNonQuery();
                }
            }

            var balanceAfter = UpdateVendorBalance(con, tran, vendorId, grandTotal);
            InsertVendorLedger(con, tran, vendorId, invoiceDate.Date, "Purchase Invoice", invoiceNo, 0m, grandTotal, balanceAfter, "Posted purchase invoice", invoiceId);
            var setup = PostingService.GetSetup(con, tran);
            var glLines = new List<PostingLine>
            {
                new(setup.InventoryAccount, subTotal, 0m, "Inventory purchase"),
                new(setup.PayableAccount, 0m, grandTotal, "Vendor payable")
            };
            if (tax > 0)
                glLines.Add(new PostingLine(setup.InputTaxAccount, tax, 0m, "Input GST"));
            PostingService.PostBatch(con, tran, invoiceDate.Date, "Purchase Invoice", invoiceNo, invoiceId, glLines);
            InsertAudit(con, tran, "POST_PURCHASE_INVOICE", "PurchaseInvoiceHeader", invoiceId.ToString(), $"Posted purchase invoice {invoiceNo}, total {grandTotal:N2}");

            tran.Commit();
            return invoiceNo;
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }

    public List<PurchaseInvoiceSummary> GetPostedPurchaseInvoices(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 200 h.PurchaseInvoiceId, h.InvoiceNo, h.VendorId, v.VendorName, ISNULL(h.VendorInvoiceNo,'') VendorInvoiceNo,
       h.InvoiceDate, h.PostedAt, h.SubTotal, h.DiscountAmount, h.TaxAmount, h.GrandTotal, h.PaidAmount, h.BalanceAmount, h.Status, ISNULL(h.Remarks,'') Remarks
FROM PurchaseInvoiceHeader h
JOIN Vendors v ON v.VendorId = h.VendorId
WHERE h.Status='Posted' AND (@Term='' OR h.InvoiceNo LIKE @Like OR v.VendorName LIKE @Like OR h.VendorInvoiceNo LIKE @Like)
ORDER BY h.PurchaseInvoiceId DESC";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        return ReadInvoices(cmd);
    }

    public List<PurchaseInvoiceLine> GetPurchaseInvoiceLines(int purchaseInvoiceId)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT l.PurchaseInvoiceLineId, l.PurchaseInvoiceId, l.ProductId, p.ProductCode, l.ProductName,
       l.Quantity, l.UnitCost, l.TaxPercent, l.TaxInclusive
FROM PurchaseInvoiceLines l
JOIN Products p ON p.ProductId = l.ProductId
WHERE l.PurchaseInvoiceId=@PurchaseInvoiceId
ORDER BY l.PurchaseInvoiceLineId";
        cmd.Parameters.AddWithValue("@PurchaseInvoiceId", purchaseInvoiceId);
        var list = new List<PurchaseInvoiceLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PurchaseInvoiceLine
            {
                PurchaseInvoiceLineId = SqlMap.Int(r, "PurchaseInvoiceLineId"),
                PurchaseInvoiceId = SqlMap.Int(r, "PurchaseInvoiceId"),
                ProductId = SqlMap.Int(r, "ProductId"),
                ProductCode = SqlMap.String(r, "ProductCode"),
                ProductName = SqlMap.String(r, "ProductName"),
                Quantity = SqlMap.Decimal(r, "Quantity"),
                UnitCost = SqlMap.Decimal(r, "UnitCost"),
                TaxPercent = SqlMap.Decimal(r, "TaxPercent"),
                TaxInclusive = SqlMap.Bool(r, "TaxInclusive")
            });
        }
        return list;
    }

    public VendorPayment PostVendorPayment(int vendorId, DateTime paymentDate, decimal amount, string paymentMethod, string referenceNo, string remarks)
    {
        if (PosSession.CurrentUser == null) throw new InvalidOperationException("Login required.");
        if (vendorId <= 0) throw new InvalidOperationException("Select vendor.");
        if (amount <= 0) throw new InvalidOperationException("Payment amount must be greater than zero.");

        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        try
        {
            var currentBalance = GetVendorBalance(con, tran, vendorId);
            if (amount > currentBalance) throw new InvalidOperationException($"Payment cannot be more than vendor balance. Balance: Rs. {currentBalance:N2}");

            var paymentNo = PostingService.NextNumber(con, tran, "VENDOR_PAYMENT");
            int paymentId;
            using (var cmd = new SqlCommand(@"
INSERT INTO VendorPayments(PaymentNo, VendorId, PaymentDate, Amount, PaymentMethod, ReferenceNo, Remarks, CreatedBy)
OUTPUT INSERTED.PaymentId
VALUES(@PaymentNo, @VendorId, @PaymentDate, @Amount, @PaymentMethod, @ReferenceNo, @Remarks, @CreatedBy)", con, tran))
            {
                cmd.Parameters.AddWithValue("@PaymentNo", paymentNo);
                cmd.Parameters.AddWithValue("@VendorId", vendorId);
                cmd.Parameters.AddWithValue("@PaymentDate", paymentDate.Date);
                cmd.Parameters.AddWithValue("@Amount", amount);
                cmd.Parameters.AddWithValue("@PaymentMethod", paymentMethod.Trim());
                cmd.Parameters.AddWithValue("@ReferenceNo", referenceNo.Trim());
                cmd.Parameters.AddWithValue("@Remarks", remarks.Trim());
                cmd.Parameters.AddWithValue("@CreatedBy", PosSession.CurrentUser.UserId);
                paymentId = Convert.ToInt32(cmd.ExecuteScalar());
            }

            ApplyPaymentToOldInvoices(con, tran, vendorId, amount);
            var balanceAfter = UpdateVendorBalance(con, tran, vendorId, -amount);
            InsertVendorLedger(con, tran, vendorId, paymentDate.Date, "Payment", paymentNo, amount, 0m, balanceAfter, "Vendor payment", paymentId);
            var setup = PostingService.GetSetup(con, tran);
            PostingService.PostBatch(con, tran, paymentDate.Date, "Vendor Payment", paymentNo, paymentId,
            [
                new PostingLine(setup.PayableAccount, amount, 0m, "Vendor payable settled"),
                new PostingLine(PostingService.PaymentAccount(setup, paymentMethod), 0m, amount, "Cash/Bank paid to vendor")
            ]);
            InsertAudit(con, tran, "POST_VENDOR_PAYMENT", "VendorPayments", paymentId.ToString(), $"Posted vendor payment {paymentNo}, amount {amount:N2}");
            var vendorName = GetVendorName(con, tran, vendorId);

            tran.Commit();
            return new VendorPayment
            {
                PaymentId = paymentId,
                PaymentNo = paymentNo,
                VendorId = vendorId,
                VendorName = vendorName,
                PaymentDate = paymentDate.Date,
                Amount = amount,
                PaymentMethod = paymentMethod,
                ReferenceNo = referenceNo,
                Remarks = remarks
            };
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }

    public List<VendorPayment> GetVendorPayments(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 200 p.PaymentId, p.PaymentNo, p.VendorId, v.VendorName, p.PaymentDate, p.Amount,
       p.PaymentMethod, ISNULL(p.ReferenceNo,'') ReferenceNo, ISNULL(p.Remarks,'') Remarks
FROM VendorPayments p
JOIN Vendors v ON v.VendorId = p.VendorId
WHERE @Term='' OR p.PaymentNo LIKE @Like OR v.VendorName LIKE @Like OR p.ReferenceNo LIKE @Like
ORDER BY p.PaymentId DESC";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        var list = new List<VendorPayment>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VendorPayment
            {
                PaymentId = SqlMap.Int(r, "PaymentId"),
                PaymentNo = SqlMap.String(r, "PaymentNo"),
                VendorId = SqlMap.Int(r, "VendorId"),
                VendorName = SqlMap.String(r, "VendorName"),
                PaymentDate = SqlMap.DateTime(r, "PaymentDate"),
                Amount = SqlMap.Decimal(r, "Amount"),
                PaymentMethod = SqlMap.String(r, "PaymentMethod"),
                ReferenceNo = SqlMap.String(r, "ReferenceNo"),
                Remarks = SqlMap.String(r, "Remarks")
            });
        }
        return list;
    }

    public List<VendorLedgerEntry> GetVendorLedger(int vendorId = 0)
        => GetVendorLedger(vendorId, new DateTime(1900, 1, 1), new DateTime(9999, 12, 31), true);

    public List<VendorLedgerEntry> GetVendorLedger(int vendorId, DateTime fromDate, DateTime toDate, bool newestFirst = true)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        var sort = newestFirst ? "DESC" : "ASC";
        cmd.CommandText = $@"
SELECT TOP 5000 l.VendorLedgerEntryId, l.VendorId, v.VendorName, l.PostingDate, l.DocumentType, l.DocumentNo,
       l.DebitAmount, l.CreditAmount, l.BalanceAfter, ISNULL(l.Description,'') Description
FROM VendorLedgerEntries l
JOIN Vendors v ON v.VendorId = l.VendorId
WHERE (@VendorId = 0 OR l.VendorId = @VendorId)
  AND l.PostingDate >= @FromDate
  AND l.PostingDate < @ToDateExclusive
ORDER BY l.PostingDate {sort}, l.VendorLedgerEntryId {sort}";
        var toDateExclusive = toDate.Date >= new DateTime(9999, 12, 31) ? toDate.Date : toDate.Date.AddDays(1);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@FromDate", fromDate.Date);
        cmd.Parameters.AddWithValue("@ToDateExclusive", toDateExclusive);
        var list = new List<VendorLedgerEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new VendorLedgerEntry
            {
                VendorLedgerEntryId = SqlMap.Int(r, "VendorLedgerEntryId"),
                VendorId = SqlMap.Int(r, "VendorId"),
                VendorName = SqlMap.String(r, "VendorName"),
                PostingDate = SqlMap.DateTime(r, "PostingDate"),
                DocumentType = SqlMap.String(r, "DocumentType"),
                DocumentNo = SqlMap.String(r, "DocumentNo"),
                DebitAmount = SqlMap.Decimal(r, "DebitAmount"),
                CreditAmount = SqlMap.Decimal(r, "CreditAmount"),
                BalanceAfter = SqlMap.Decimal(r, "BalanceAfter"),
                Description = SqlMap.String(r, "Description")
            });
        }
        return list;
    }

    public List<ChartAccount> SearchAccounts(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 200 AccountId, AccountNo, AccountName, AccountType, NormalBalance, IsSystem, IsActive
FROM ChartOfAccounts
WHERE IsActive = 1 AND (@Term='' OR AccountNo LIKE @Like OR AccountName LIKE @Like OR AccountType LIKE @Like)
ORDER BY AccountNo";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        return ReadAccounts(cmd);
    }

    public void UpsertAccount(ChartAccount a)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM ChartOfAccounts WHERE AccountId=@AccountId)
BEGIN
    UPDATE ChartOfAccounts SET AccountNo=@AccountNo, AccountName=@AccountName, AccountType=@AccountType,
        NormalBalance=@NormalBalance, IsActive=@IsActive
    WHERE AccountId=@AccountId AND IsSystem=0;
END
ELSE
BEGIN
    INSERT INTO ChartOfAccounts(AccountNo, AccountName, AccountType, NormalBalance, IsSystem, IsActive)
    VALUES(@AccountNo, @AccountName, @AccountType, @NormalBalance, 0, @IsActive);
END";
        cmd.Parameters.AddWithValue("@AccountId", a.AccountId);
        cmd.Parameters.AddWithValue("@AccountNo", a.AccountNo.Trim());
        cmd.Parameters.AddWithValue("@AccountName", a.AccountName.Trim());
        cmd.Parameters.AddWithValue("@AccountType", a.AccountType.Trim());
        cmd.Parameters.AddWithValue("@NormalBalance", a.NormalBalance.Trim());
        cmd.Parameters.AddWithValue("@IsActive", a.IsActive);
        cmd.ExecuteNonQuery();
    }

    public List<GLEntry> GetGLEntries(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 400 g.GLEntryId, g.PostingDate, a.AccountNo, a.AccountName, g.DocumentType, g.DocumentNo,
       g.DebitAmount, g.CreditAmount, ISNULL(g.Description,'') Description, g.PostingBatchId
FROM GLEntries g
JOIN ChartOfAccounts a ON a.AccountId = g.AccountId
WHERE @Term='' OR a.AccountNo LIKE @Like OR a.AccountName LIKE @Like OR g.DocumentNo LIKE @Like OR g.DocumentType LIKE @Like
ORDER BY g.GLEntryId DESC";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        var list = new List<GLEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new GLEntry
            {
                GLEntryId = SqlMap.Int(r, "GLEntryId"),
                PostingDate = SqlMap.DateTime(r, "PostingDate"),
                AccountNo = SqlMap.String(r, "AccountNo"),
                AccountName = SqlMap.String(r, "AccountName"),
                DocumentType = SqlMap.String(r, "DocumentType"),
                DocumentNo = SqlMap.String(r, "DocumentNo"),
                DebitAmount = SqlMap.Decimal(r, "DebitAmount"),
                CreditAmount = SqlMap.Decimal(r, "CreditAmount"),
                Description = SqlMap.String(r, "Description"),
                PostingBatchId = r["PostingBatchId"] == DBNull.Value ? null : Convert.ToInt64(r["PostingBatchId"])
            });
        }
        return list;
    }

    private static decimal GetVendorBalance(SqlConnection con, SqlTransaction tran, int vendorId)
    {
        using var cmd = new SqlCommand("SELECT ISNULL(CurrentBalance,0) FROM Vendors WITH (UPDLOCK, ROWLOCK) WHERE VendorId=@VendorId", con, tran);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return Convert.ToDecimal(cmd.ExecuteScalar() ?? 0m);
    }

    private static string GetVendorName(SqlConnection con, SqlTransaction tran, int vendorId)
    {
        using var cmd = new SqlCommand("SELECT VendorName FROM Vendors WHERE VendorId=@VendorId", con, tran);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static decimal UpdateVendorBalance(SqlConnection con, SqlTransaction tran, int vendorId, decimal delta)
    {
        using var cmd = new SqlCommand("UPDATE Vendors SET CurrentBalance = CurrentBalance + @Delta OUTPUT INSERTED.CurrentBalance WHERE VendorId=@VendorId", con, tran);
        cmd.Parameters.AddWithValue("@Delta", delta);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        return Convert.ToDecimal(cmd.ExecuteScalar());
    }

    private static void ApplyPaymentToOldInvoices(SqlConnection con, SqlTransaction tran, int vendorId, decimal amount)
    {
        var remaining = amount;
        var invoices = new List<(int Id, decimal Balance)>();
        using (var cmd = new SqlCommand(@"
SELECT PurchaseInvoiceId, BalanceAmount
FROM PurchaseInvoiceHeader WITH (UPDLOCK, ROWLOCK)
WHERE VendorId=@VendorId AND Status='Posted' AND BalanceAmount > 0
ORDER BY InvoiceDate, PurchaseInvoiceId", con, tran))
        {
            cmd.Parameters.AddWithValue("@VendorId", vendorId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) invoices.Add((Convert.ToInt32(r["PurchaseInvoiceId"]), Convert.ToDecimal(r["BalanceAmount"])));
        }

        foreach (var invoice in invoices)
        {
            if (remaining <= 0) break;
            var apply = Math.Min(invoice.Balance, remaining);
            using var cmd = new SqlCommand(@"
UPDATE PurchaseInvoiceHeader
SET PaidAmount = PaidAmount + @Apply, BalanceAmount = BalanceAmount - @Apply
WHERE PurchaseInvoiceId=@PurchaseInvoiceId", con, tran);
            cmd.Parameters.AddWithValue("@Apply", apply);
            cmd.Parameters.AddWithValue("@PurchaseInvoiceId", invoice.Id);
            cmd.ExecuteNonQuery();
            remaining -= apply;
        }
    }

    private static void InsertVendorLedger(SqlConnection con, SqlTransaction tran, int vendorId, DateTime date, string docType, string docNo, decimal debit, decimal credit, decimal balanceAfter, string description, int sourceId)
    {
        using var cmd = new SqlCommand(@"
INSERT INTO VendorLedgerEntries(VendorId, PostingDate, DocumentType, DocumentNo, DebitAmount, CreditAmount, BalanceAfter, Description, SourceId)
VALUES(@VendorId, @PostingDate, @DocumentType, @DocumentNo, @DebitAmount, @CreditAmount, @BalanceAfter, @Description, @SourceId)", con, tran);
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        cmd.Parameters.AddWithValue("@PostingDate", date);
        cmd.Parameters.AddWithValue("@DocumentType", docType);
        cmd.Parameters.AddWithValue("@DocumentNo", docNo);
        cmd.Parameters.AddWithValue("@DebitAmount", debit);
        cmd.Parameters.AddWithValue("@CreditAmount", credit);
        cmd.Parameters.AddWithValue("@BalanceAfter", balanceAfter);
        cmd.Parameters.AddWithValue("@Description", description);
        cmd.Parameters.AddWithValue("@SourceId", sourceId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertGLEntry(SqlConnection con, SqlTransaction tran, DateTime date, string docType, string docNo, string accountNo, decimal debit, decimal credit, string description, int sourceId)
    {
        var accountId = GetAccountId(con, tran, accountNo);
        using var cmd = new SqlCommand(@"
INSERT INTO GLEntries(AccountId, PostingDate, DocumentType, DocumentNo, DebitAmount, CreditAmount, Description, SourceId)
VALUES(@AccountId, @PostingDate, @DocumentType, @DocumentNo, @DebitAmount, @CreditAmount, @Description, @SourceId)", con, tran);
        cmd.Parameters.AddWithValue("@AccountId", accountId);
        cmd.Parameters.AddWithValue("@PostingDate", date);
        cmd.Parameters.AddWithValue("@DocumentType", docType);
        cmd.Parameters.AddWithValue("@DocumentNo", docNo);
        cmd.Parameters.AddWithValue("@DebitAmount", debit);
        cmd.Parameters.AddWithValue("@CreditAmount", credit);
        cmd.Parameters.AddWithValue("@Description", description);
        cmd.Parameters.AddWithValue("@SourceId", sourceId);
        cmd.ExecuteNonQuery();
    }

    private static int GetAccountId(SqlConnection con, SqlTransaction tran, string accountNo)
    {
        using var cmd = new SqlCommand("SELECT AccountId FROM ChartOfAccounts WHERE AccountNo=@AccountNo", con, tran);
        cmd.Parameters.AddWithValue("@AccountNo", accountNo);
        var result = cmd.ExecuteScalar();
        if (result == null) throw new InvalidOperationException($"Chart of account {accountNo} not found. Open Settings/Database seed or create it first.");
        return Convert.ToInt32(result);
    }

    private static void InsertAudit(SqlConnection con, SqlTransaction tran, string action, string entity, string key, string description)
    {
        using var audit = new SqlCommand(@"INSERT INTO AuditLog(UserId, ActionName, EntityName, EntityKey, Description) VALUES(@UserId,@Action,@Entity,@Key,@Desc)", con, tran);
        audit.Parameters.AddWithValue("@UserId", PosSession.CurrentUser?.UserId ?? 0);
        audit.Parameters.AddWithValue("@Action", action);
        audit.Parameters.AddWithValue("@Entity", entity);
        audit.Parameters.AddWithValue("@Key", key);
        audit.Parameters.AddWithValue("@Desc", description);
        audit.ExecuteNonQuery();
    }

    private static List<Vendor> ReadVendors(SqlCommand cmd)
    {
        var list = new List<Vendor>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Vendor
            {
                VendorId = SqlMap.Int(r, "VendorId"),
                VendorCode = SqlMap.String(r, "VendorCode"),
                VendorName = SqlMap.String(r, "VendorName"),
                ContactPerson = SqlMap.String(r, "ContactPerson"),
                Mobile = SqlMap.String(r, "Mobile"),
                Email = SqlMap.String(r, "Email"),
                AddressLine = SqlMap.String(r, "AddressLine"),
                PaymentTerms = SqlMap.String(r, "PaymentTerms"),
                OpeningBalance = SqlMap.Decimal(r, "OpeningBalance"),
                CurrentBalance = SqlMap.Decimal(r, "CurrentBalance"),
                IsActive = SqlMap.Bool(r, "IsActive")
            });
        }
        return list;
    }

    private static List<PurchaseInvoiceSummary> ReadInvoices(SqlCommand cmd)
    {
        var list = new List<PurchaseInvoiceSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PurchaseInvoiceSummary
            {
                PurchaseInvoiceId = SqlMap.Int(r, "PurchaseInvoiceId"),
                InvoiceNo = SqlMap.String(r, "InvoiceNo"),
                VendorId = SqlMap.Int(r, "VendorId"),
                VendorName = SqlMap.String(r, "VendorName"),
                VendorInvoiceNo = SqlMap.String(r, "VendorInvoiceNo"),
                InvoiceDate = SqlMap.DateTime(r, "InvoiceDate"),
                PostedAt = SqlMap.DateTime(r, "PostedAt"),
                SubTotal = SqlMap.Decimal(r, "SubTotal"),
                DiscountAmount = SqlMap.Decimal(r, "DiscountAmount"),
                TaxAmount = SqlMap.Decimal(r, "TaxAmount"),
                GrandTotal = SqlMap.Decimal(r, "GrandTotal"),
                PaidAmount = SqlMap.Decimal(r, "PaidAmount"),
                BalanceAmount = SqlMap.Decimal(r, "BalanceAmount"),
                Status = SqlMap.String(r, "Status"),
                Remarks = SqlMap.String(r, "Remarks")
            });
        }
        return list;
    }

    private static List<ChartAccount> ReadAccounts(SqlCommand cmd)
    {
        var list = new List<ChartAccount>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ChartAccount
            {
                AccountId = SqlMap.Int(r, "AccountId"),
                AccountNo = SqlMap.String(r, "AccountNo"),
                AccountName = SqlMap.String(r, "AccountName"),
                AccountType = SqlMap.String(r, "AccountType"),
                NormalBalance = SqlMap.String(r, "NormalBalance"),
                IsSystem = SqlMap.Bool(r, "IsSystem"),
                IsActive = SqlMap.Bool(r, "IsActive")
            });
        }
        return list;
    }
}
