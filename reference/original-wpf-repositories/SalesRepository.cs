using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;
using PayNex_POS_B1.Services;

namespace PayNex_POS_B1.Repositories;

public class SalesRepository
{
    public SaleResult PostSale(Customer customer, IReadOnlyList<CartLine> lines, IReadOnlyList<PaymentLine> payments, string remarks = "")
    {
        if (PosSession.CurrentUser == null) throw new InvalidOperationException("Login required.");
        if (PosSession.CurrentShift == null) throw new InvalidOperationException("Open shift first.");
        if (lines.Count == 0) throw new InvalidOperationException("Cart is empty.");

        var subTotal = lines.Sum(x => x.Gross);
        var discount = lines.Sum(x => x.DiscountAmount);
        var tax = lines.Sum(x => x.TaxAmount);
        var grandTotal = lines.Sum(x => x.LineTotal);
        var paid = payments.Sum(x => x.Amount);
        var change = paid - grandTotal;
        if (paid < grandTotal) throw new InvalidOperationException("Paid amount is less than grand total.");

        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        try
        {
            foreach (var productGroup in lines.GroupBy(x => x.ProductId))
            {
                var requiredQty = productGroup.Sum(x => x.Quantity);
                var productName = productGroup.First().ProductName;
                using var stockCmd = new SqlCommand("SELECT StockOnHand FROM Products WITH (UPDLOCK, ROWLOCK) WHERE ProductId=@ProductId", con, tran);
                stockCmd.Parameters.AddWithValue("@ProductId", productGroup.Key);
                var stock = Convert.ToDecimal(stockCmd.ExecuteScalar());
                if (stock < requiredQty)
                    throw new InvalidOperationException($"Stock not available for {productName}. Available: {stock:N0}");
            }

            var invoiceNo = PostingService.NextNumber(con, tran, "POS");
            int saleId;
            using (var cmd = new SqlCommand(@"
INSERT INTO SalesHeader(InvoiceNo, StoreId, TerminalId, ShiftId, UserId, CustomerId, SubTotal, DiscountAmount, TaxAmount, GrandTotal, PaidAmount, ChangeAmount, Status, Remarks)
OUTPUT INSERTED.SaleId
VALUES(@InvoiceNo, @StoreId, @TerminalId, @ShiftId, @UserId, @CustomerId, @SubTotal, @DiscountAmount, @TaxAmount, @GrandTotal, @PaidAmount, @ChangeAmount, 'Posted', @Remarks)", con, tran))
            {
                cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
                cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                cmd.Parameters.AddWithValue("@TerminalId", PosSession.TerminalId);
                cmd.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift.ShiftId);
                cmd.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
                cmd.Parameters.AddWithValue("@CustomerId", customer.CustomerId);
                cmd.Parameters.AddWithValue("@SubTotal", subTotal);
                cmd.Parameters.AddWithValue("@DiscountAmount", discount);
                cmd.Parameters.AddWithValue("@TaxAmount", tax);
                cmd.Parameters.AddWithValue("@GrandTotal", grandTotal);
                cmd.Parameters.AddWithValue("@PaidAmount", paid);
                cmd.Parameters.AddWithValue("@ChangeAmount", change);
                cmd.Parameters.AddWithValue("@Remarks", remarks);
                saleId = Convert.ToInt32(cmd.ExecuteScalar());
            }

            foreach (var line in lines)
            {
                using (var cmd = new SqlCommand(@"
INSERT INTO SalesLines(SaleId, ProductId, ProductName, Quantity, UnitPrice, DiscountPercent, DiscountAmount, TaxPercent, TaxAmount, LineTotal, UnitCost, TaxInclusive)
VALUES(@SaleId, @ProductId, @ProductName, @Quantity, @UnitPrice, @DiscountPercent, @DiscountAmount, @TaxPercent, @TaxAmount, @LineTotal, @UnitCost, @TaxInclusive)", con, tran))
                {
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
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqlCommand("UPDATE Products SET StockOnHand = StockOnHand - @Qty WHERE ProductId=@ProductId", con, tran))
                {
                    cmd.Parameters.AddWithValue("@Qty", line.Quantity);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.ExecuteNonQuery();
                }
                using (var stockByStore = new SqlCommand("UPDATE StockByStore SET Quantity=Quantity-@Qty WHERE StoreId=@StoreId AND ProductId=@ProductId", con, tran))
                {
                    stockByStore.Parameters.AddWithValue("@Qty", line.Quantity);
                    stockByStore.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                    stockByStore.Parameters.AddWithValue("@ProductId", line.ProductId);
                    if (stockByStore.ExecuteNonQuery() == 0) throw new InvalidOperationException("Store-wise stock record is missing.");
                }

                using (var cmd = new SqlCommand(@"
INSERT INTO InventoryLedger(StoreId, ProductId, MovementType, SourceDocumentNo, QuantityIn, QuantityOut, UnitCost, Remarks, CreatedBy)
VALUES(@StoreId, @ProductId, 'Sale', @DocNo, 0, @Qty, @UnitCost, 'POS Sale', @CreatedBy)", con, tran))
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

            foreach (var payment in payments.Where(x => x.Amount > 0))
            {
                using var cmd = new SqlCommand(@"
INSERT INTO PaymentLines(SaleId, PaymentMethodId, Amount, ReferenceNo)
VALUES(@SaleId, @PaymentMethodId, @Amount, @ReferenceNo)", con, tran);
                cmd.Parameters.AddWithValue("@SaleId", saleId);
                cmd.Parameters.AddWithValue("@PaymentMethodId", payment.PaymentMethodId);
                cmd.Parameters.AddWithValue("@Amount", payment.Amount);
                cmd.Parameters.AddWithValue("@ReferenceNo", payment.ReferenceNo);
                cmd.ExecuteNonQuery();
            }

            var setup = PostingService.GetSetup(con, tran);
            var postingLines = new List<PostingLine>();
            var amountToAllocate = grandTotal;
            decimal creditSaleAmount = 0;
            foreach (var payment in payments.Where(x => x.Amount > 0))
            {
                var appliedAmount = Math.Min(payment.Amount, amountToAllocate);
                if (appliedAmount <= 0) break;
                var accountNo = PostingService.PaymentAccount(setup, payment.PaymentMethodName);
                postingLines.Add(new PostingLine(accountNo, appliedAmount, 0m, $"{payment.PaymentMethodName} received"));
                if (payment.PaymentMethodName.Equals("Credit", StringComparison.OrdinalIgnoreCase))
                    creditSaleAmount += appliedAmount;
                amountToAllocate -= appliedAmount;
            }

            var netSales = grandTotal - tax;
            postingLines.Add(new PostingLine(setup.SalesAccount, 0m, netSales, "POS sales revenue"));
            if (tax > 0)
                postingLines.Add(new PostingLine(setup.OutputTaxAccount, 0m, tax, "Output GST"));

            var costTotal = lines.Sum(x => x.Quantity * x.UnitCost);
            if (costTotal > 0)
            {
                postingLines.Add(new PostingLine(setup.CogsAccount, costTotal, 0m, "Cost of goods sold"));
                postingLines.Add(new PostingLine(setup.InventoryAccount, 0m, costTotal, "Inventory issued"));
            }
            PostingService.PostBatch(con, tran, DateTime.Today, "POS Sale", invoiceNo, saleId, postingLines);

            if (creditSaleAmount > 0)
            {
                using (var creditCheck = new SqlCommand("SELECT CurrentBalance,CreditLimit FROM Customers WITH(UPDLOCK,ROWLOCK) WHERE CustomerId=@CustomerId", con, tran))
                {
                    creditCheck.Parameters.AddWithValue("@CustomerId", customer.CustomerId);
                    using var reader = creditCheck.ExecuteReader();
                    if (!reader.Read()) throw new InvalidOperationException("Customer not found.");
                    var currentBalance = Convert.ToDecimal(reader["CurrentBalance"]);
                    var creditLimit = Convert.ToDecimal(reader["CreditLimit"]);
                    if (currentBalance + creditSaleAmount > creditLimit)
                        throw new InvalidOperationException($"Customer credit limit exceeded. Limit: {creditLimit:N2}, Current: {currentBalance:N2}.");
                }
                decimal balanceAfter;
                using (var balance = new SqlCommand(@"
UPDATE Customers SET CurrentBalance=CurrentBalance+@Amount
OUTPUT INSERTED.CurrentBalance WHERE CustomerId=@CustomerId", con, tran))
                {
                    balance.Parameters.AddWithValue("@Amount", creditSaleAmount);
                    balance.Parameters.AddWithValue("@CustomerId", customer.CustomerId);
                    balanceAfter = Convert.ToDecimal(balance.ExecuteScalar());
                }
                using var ledger = new SqlCommand(@"
INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@CustomerId,@Date,'POS Credit Sale',@DocumentNo,@Amount,0,@BalanceAfter,'POS credit sale',@SourceId)", con, tran);
                ledger.Parameters.AddWithValue("@CustomerId", customer.CustomerId);
                ledger.Parameters.AddWithValue("@Date", DateTime.Today);
                ledger.Parameters.AddWithValue("@DocumentNo", invoiceNo);
                ledger.Parameters.AddWithValue("@Amount", creditSaleAmount);
                ledger.Parameters.AddWithValue("@BalanceAfter", balanceAfter);
                ledger.Parameters.AddWithValue("@SourceId", saleId);
                ledger.ExecuteNonQuery();
            }

            using (var audit = new SqlCommand(@"INSERT INTO AuditLog(UserId, ActionName, EntityName, EntityKey, Description) VALUES(@UserId,'POST_SALE','SalesHeader',@Key,@Desc)", con, tran))
            {
                audit.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
                audit.Parameters.AddWithValue("@Key", saleId.ToString());
                audit.Parameters.AddWithValue("@Desc", $"Posted invoice {invoiceNo}, total {grandTotal:N2}");
                audit.ExecuteNonQuery();
            }

            tran.Commit();
            return new SaleResult { SaleId = saleId, InvoiceNo = invoiceNo, GrandTotal = grandTotal, PaidAmount = paid, ChangeAmount = change };
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }

    public int HoldSale(Customer customer, IReadOnlyList<CartLine> lines)
    {
        if (PosSession.CurrentUser == null || PosSession.CurrentShift == null) throw new InvalidOperationException("Open shift required.");
        if (lines.Count == 0) throw new InvalidOperationException("Cart is empty.");
        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        try
        {
            var holdNo = PostingService.NextNumber(con, tran, "HOLD");
            int holdId;
            using (var cmd = new SqlCommand(@"
INSERT INTO HoldSalesHeader(HoldNo, StoreId, TerminalId, ShiftId, UserId, CustomerId, SubTotal, DiscountAmount, TaxAmount, GrandTotal)
OUTPUT INSERTED.HoldId
VALUES(@HoldNo,@StoreId,@TerminalId,@ShiftId,@UserId,@CustomerId,@SubTotal,@Discount,@Tax,@Total)", con, tran))
            {
                cmd.Parameters.AddWithValue("@HoldNo", holdNo);
                cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                cmd.Parameters.AddWithValue("@TerminalId", PosSession.TerminalId);
                cmd.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift.ShiftId);
                cmd.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
                cmd.Parameters.AddWithValue("@CustomerId", customer.CustomerId);
                cmd.Parameters.AddWithValue("@SubTotal", lines.Sum(x => x.Gross));
                cmd.Parameters.AddWithValue("@Discount", lines.Sum(x => x.DiscountAmount));
                cmd.Parameters.AddWithValue("@Tax", lines.Sum(x => x.TaxAmount));
                cmd.Parameters.AddWithValue("@Total", lines.Sum(x => x.LineTotal));
                holdId = Convert.ToInt32(cmd.ExecuteScalar());
            }
            foreach (var line in lines)
            {
                using var cmd = new SqlCommand(@"
INSERT INTO HoldSalesLines(HoldId, ProductId, ProductName, Barcode, Quantity, UnitPrice, DiscountPercent, TaxPercent, UnitCost, TaxInclusive)
VALUES(@HoldId,@ProductId,@ProductName,@Barcode,@Quantity,@UnitPrice,@DiscountPercent,@TaxPercent,@UnitCost,@TaxInclusive)", con, tran);
                cmd.Parameters.AddWithValue("@HoldId", holdId);
                cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                cmd.Parameters.AddWithValue("@ProductName", line.ProductName);
                cmd.Parameters.AddWithValue("@Barcode", line.Barcode);
                cmd.Parameters.AddWithValue("@Quantity", line.Quantity);
                cmd.Parameters.AddWithValue("@UnitPrice", line.UnitPrice);
                cmd.Parameters.AddWithValue("@DiscountPercent", line.DiscountPercent);
                cmd.Parameters.AddWithValue("@TaxPercent", line.TaxPercent);
                cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                cmd.Parameters.AddWithValue("@TaxInclusive", line.TaxInclusive);
                cmd.ExecuteNonQuery();
            }
            tran.Commit();
            return holdId;
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }

    public List<CartLine> RecallLatestHold(out int customerId)
    {
        customerId = 1;
        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        try
        {
            int holdId;
            using (var cmd = new SqlCommand(@"SELECT TOP 1 HoldId, CustomerId FROM HoldSalesHeader WHERE ShiftId=@ShiftId ORDER BY HoldId DESC", con, tran))
            {
                cmd.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift?.ShiftId ?? 0);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return new List<CartLine>();
                holdId = Convert.ToInt32(r["HoldId"]);
                customerId = Convert.ToInt32(r["CustomerId"]);
            }
            var list = new List<CartLine>();
            using (var cmd = new SqlCommand("SELECT * FROM HoldSalesLines WHERE HoldId=@HoldId", con, tran))
            {
                cmd.Parameters.AddWithValue("@HoldId", holdId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new CartLine
                    {
                        ProductId = Convert.ToInt32(r["ProductId"]),
                        ProductName = Convert.ToString(r["ProductName"]) ?? string.Empty,
                        Barcode = Convert.ToString(r["Barcode"]) ?? string.Empty,
                        Quantity = Convert.ToDecimal(r["Quantity"]),
                        UnitPrice = Convert.ToDecimal(r["UnitPrice"]),
                        DiscountPercent = Convert.ToDecimal(r["DiscountPercent"]),
                        TaxPercent = Convert.ToDecimal(r["TaxPercent"]),
                        TaxInclusive = Convert.ToBoolean(r["TaxInclusive"]),
                        UnitCost = Convert.ToDecimal(r["UnitCost"])
                    });
                }
            }
            using (var cmd = new SqlCommand("DELETE FROM HoldSalesLines WHERE HoldId=@HoldId; DELETE FROM HoldSalesHeader WHERE HoldId=@HoldId;", con, tran))
            {
                cmd.Parameters.AddWithValue("@HoldId", holdId);
                cmd.ExecuteNonQuery();
            }
            tran.Commit();
            return list;
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }
}
