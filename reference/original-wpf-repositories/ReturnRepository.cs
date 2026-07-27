using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;
using PayNex_POS_B1.Services;

namespace PayNex_POS_B1.Repositories;

public class ReturnRepository
{
    public List<SaleLineForReturn> FindInvoiceLines(string invoiceNo)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT sh.SaleId, sh.InvoiceNo, sl.SaleLineId, sl.ProductId, sl.ProductName,
       sl.Quantity SoldQuantity,
       ISNULL((SELECT SUM(ReturnQuantity) FROM ReturnLines rl WHERE rl.SaleLineId = sl.SaleLineId),0) AlreadyReturnedQuantity,
       sl.UnitPrice, sl.DiscountPercent, sl.TaxPercent, sl.UnitCost, sl.TaxInclusive
FROM SalesHeader sh
INNER JOIN SalesLines sl ON sl.SaleId = sh.SaleId
WHERE sh.InvoiceNo = @InvoiceNo AND sh.Status='Posted'
ORDER BY sl.SaleLineId";
        cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo.Trim());
        var list = new List<SaleLineForReturn>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var line = new SaleLineForReturn
            {
                SaleId = SqlMap.Int(r, "SaleId"),
                InvoiceNo = SqlMap.String(r, "InvoiceNo"),
                SaleLineId = SqlMap.Int(r, "SaleLineId"),
                ProductId = SqlMap.Int(r, "ProductId"),
                ProductName = SqlMap.String(r, "ProductName"),
                SoldQuantity = SqlMap.Decimal(r, "SoldQuantity"),
                AlreadyReturnedQuantity = SqlMap.Decimal(r, "AlreadyReturnedQuantity"),
                UnitPrice = SqlMap.Decimal(r, "UnitPrice"),
                DiscountPercent = SqlMap.Decimal(r, "DiscountPercent"),
                TaxPercent = SqlMap.Decimal(r, "TaxPercent"),
                UnitCost = SqlMap.Decimal(r, "UnitCost"),
                TaxInclusive = SqlMap.Bool(r, "TaxInclusive")
            };
            list.Add(line);
        }
        return list;
    }

    public string PostReturn(string originalInvoiceNo, IReadOnlyList<SaleLineForReturn> lines, string reason)
    {
        if (PosSession.CurrentUser == null) throw new InvalidOperationException("Login required.");
        if (PosSession.CurrentUser.IsManager != true) throw new InvalidOperationException("Manager approval is required to post a return.");
        if (PosSession.CurrentShift == null) throw new InvalidOperationException("Open shift required.");
        var returnLines = lines.Where(x => x.ReturnQuantity > 0).ToList();
        if (returnLines.Count == 0) throw new InvalidOperationException("Enter return quantity first.");

        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        try
        {
            var returnNo = PostingService.NextNumber(con, tran, "RETURN");
            var refund = returnLines.Sum(x => x.RefundAmount);
            int returnId;
            using (var cmd = new SqlCommand(@"
INSERT INTO ReturnHeader(ReturnNo, OriginalInvoiceNo, StoreId, TerminalId, ShiftId, UserId, ReturnDate, RefundAmount, Reason, Status)
OUTPUT INSERTED.ReturnId
VALUES(@ReturnNo,@OriginalInvoiceNo,@StoreId,@TerminalId,@ShiftId,@UserId,SYSUTCDATETIME(),@RefundAmount,@Reason,'Posted')", con, tran))
            {
                cmd.Parameters.AddWithValue("@ReturnNo", returnNo);
                cmd.Parameters.AddWithValue("@OriginalInvoiceNo", originalInvoiceNo);
                cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                cmd.Parameters.AddWithValue("@TerminalId", PosSession.TerminalId);
                cmd.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift.ShiftId);
                cmd.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
                cmd.Parameters.AddWithValue("@RefundAmount", refund);
                cmd.Parameters.AddWithValue("@Reason", reason);
                returnId = Convert.ToInt32(cmd.ExecuteScalar());
            }

            foreach (var line in returnLines)
            {
                using (var cmd = new SqlCommand(@"
INSERT INTO ReturnLines(ReturnId, SaleLineId, ProductId, ProductName, ReturnQuantity, UnitPrice, DiscountAmount, TaxAmount, RefundAmount, UnitCost)
VALUES(@ReturnId,@SaleLineId,@ProductId,@ProductName,@ReturnQuantity,@UnitPrice,@DiscountAmount,@TaxAmount,@RefundAmount,@UnitCost)", con, tran))
                {
                    cmd.Parameters.AddWithValue("@ReturnId", returnId);
                    cmd.Parameters.AddWithValue("@SaleLineId", line.SaleLineId);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.Parameters.AddWithValue("@ProductName", line.ProductName);
                    cmd.Parameters.AddWithValue("@ReturnQuantity", line.ReturnQuantity);
                    cmd.Parameters.AddWithValue("@UnitPrice", line.UnitPrice);
                    cmd.Parameters.AddWithValue("@DiscountAmount", line.DiscountAmount);
                    cmd.Parameters.AddWithValue("@TaxAmount", line.TaxAmount);
                    cmd.Parameters.AddWithValue("@RefundAmount", line.RefundAmount);
                    cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = new SqlCommand("UPDATE Products SET StockOnHand = StockOnHand + @Qty WHERE ProductId=@ProductId", con, tran))
                {
                    cmd.Parameters.AddWithValue("@Qty", line.ReturnQuantity);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.ExecuteNonQuery();
                }
                using (var stockByStore = new SqlCommand("UPDATE StockByStore SET Quantity=Quantity+@Qty WHERE StoreId=@StoreId AND ProductId=@ProductId", con, tran))
                {
                    stockByStore.Parameters.AddWithValue("@Qty", line.ReturnQuantity);
                    stockByStore.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                    stockByStore.Parameters.AddWithValue("@ProductId", line.ProductId);
                    if (stockByStore.ExecuteNonQuery() == 0) throw new InvalidOperationException("Store-wise stock record is missing.");
                }

                using (var cmd = new SqlCommand(@"
INSERT INTO InventoryLedger(StoreId, ProductId, MovementType, SourceDocumentNo, QuantityIn, QuantityOut, UnitCost, Remarks, CreatedBy)
VALUES(@StoreId,@ProductId,'Sales Return',@ReturnNo,@Qty,0,@UnitCost,@Remarks,@CreatedBy)", con, tran))
                {
                    cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
                    cmd.Parameters.AddWithValue("@ProductId", line.ProductId);
                    cmd.Parameters.AddWithValue("@ReturnNo", returnNo);
                    cmd.Parameters.AddWithValue("@Qty", line.ReturnQuantity);
                    cmd.Parameters.AddWithValue("@UnitCost", line.UnitCost);
                    cmd.Parameters.AddWithValue("@Remarks", reason);
                    cmd.Parameters.AddWithValue("@CreatedBy", PosSession.CurrentUser.UserId);
                    cmd.ExecuteNonQuery();
                }
            }

            using (var cash = new SqlCommand(@"
INSERT INTO CashDrawerLedger(ShiftId, EntryType, Amount, ReferenceNo, Remarks, CreatedBy)
VALUES(@ShiftId,'Refund',@Amount,@ReferenceNo,@Remarks,@CreatedBy)", con, tran))
            {
                cash.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift.ShiftId);
                cash.Parameters.AddWithValue("@Amount", refund);
                cash.Parameters.AddWithValue("@ReferenceNo", returnNo);
                cash.Parameters.AddWithValue("@Remarks", $"Return against {originalInvoiceNo}: {reason}");
                cash.Parameters.AddWithValue("@CreatedBy", PosSession.CurrentUser.UserId);
                cash.ExecuteNonQuery();
            }

            var setup = PostingService.GetSetup(con, tran);
            var tax = returnLines.Sum(x => x.TaxAmount);
            var netReturn = refund - tax;
            var returnedCost = returnLines.Sum(x => x.ReturnQuantity * x.UnitCost);
            var postingLines = new List<PostingLine>
            {
                new(setup.SalesReturnAccount, netReturn, 0m, "Sales return"),
                new(setup.CashAccount, 0m, refund, "Customer refund")
            };
            if (tax > 0)
                postingLines.Add(new PostingLine(setup.OutputTaxAccount, tax, 0m, "Output GST reversed"));
            if (returnedCost > 0)
            {
                postingLines.Add(new PostingLine(setup.InventoryAccount, returnedCost, 0m, "Returned inventory"));
                postingLines.Add(new PostingLine(setup.CogsAccount, 0m, returnedCost, "COGS reversed"));
            }
            PostingService.PostBatch(con, tran, DateTime.Today, "POS Return", returnNo, returnId, postingLines);

            using (var audit = new SqlCommand(@"INSERT INTO AuditLog(UserId, ActionName, EntityName, EntityKey, Description) VALUES(@UserId,'POST_RETURN','ReturnHeader',@Key,@Desc)", con, tran))
            {
                audit.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
                audit.Parameters.AddWithValue("@Key", returnId.ToString());
                audit.Parameters.AddWithValue("@Desc", $"Posted return {returnNo}, refund {refund:N2}");
                audit.ExecuteNonQuery();
            }

            tran.Commit();
            return returnNo;
        }
        catch
        {
            tran.Rollback();
            throw;
        }
    }
}
