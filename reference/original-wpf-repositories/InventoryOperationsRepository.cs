using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Services;
using PayNex_POS_B1.Models;

namespace PayNex_POS_B1.Repositories;

public class InventoryOperationsRepository
{
    public List<LookupOption> GetStores()
    {
        using var con=Db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT StoreId,StoreCode+' - '+StoreName Name FROM Stores WHERE IsActive=1 ORDER BY StoreName";
        var list=new List<LookupOption>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(){Id=Convert.ToInt32(r[0]),Name=Convert.ToString(r[1])!});return list;
    }
    public List<LookupOption> GetProducts(int storeId)
    {
        using var con=Db.Open();using var cmd=con.CreateCommand();cmd.CommandText=@"SELECT p.ProductId,p.ProductCode+' - '+p.ProductName Name,ISNULL(s.Quantity,0) Qty,ISNULL(s.AverageCost,p.PurchasePrice) Cost
FROM Products p LEFT JOIN StockByStore s ON s.ProductId=p.ProductId AND s.StoreId=@StoreId WHERE p.IsActive=1 ORDER BY p.ProductName";cmd.Parameters.AddWithValue("@StoreId",storeId);
        var list=new List<LookupOption>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(){Id=Convert.ToInt32(r[0]),Name=Convert.ToString(r[1])!,Quantity=Convert.ToDecimal(r[2]),Cost=Convert.ToDecimal(r[3])});return list;
    }
    public string Transfer(int fromStore,int toStore,int productId,decimal quantity)
    {
        if(fromStore==toStore)throw new InvalidOperationException("From and To stores must be different.");
        if(quantity<=0)throw new InvalidOperationException("Quantity must be greater than zero.");
        using var con=Db.Open();using var tran=con.BeginTransaction();
        try
        {
            var no=PostingService.NextNumber(con,tran,"TRANSFER");
            decimal cost;
            using(var lockCmd=new SqlCommand("SELECT Quantity,AverageCost FROM StockByStore WITH(UPDLOCK,ROWLOCK) WHERE StoreId=@Store AND ProductId=@Product",con,tran)){lockCmd.Parameters.AddWithValue("@Store",fromStore);lockCmd.Parameters.AddWithValue("@Product",productId);using var r=lockCmd.ExecuteReader();if(!r.Read()||Convert.ToDecimal(r[0])<quantity)throw new InvalidOperationException("Insufficient stock in source store.");cost=Convert.ToDecimal(r[1]);}
            long id;using(var h=new SqlCommand(@"INSERT INTO StockTransfers(TransferNo,FromStoreId,ToStoreId,TransferDate,CreatedBy) OUTPUT INSERTED.TransferId VALUES(@No,@From,@To,@Date,@User)",con,tran)){h.Parameters.AddWithValue("@No",no);h.Parameters.AddWithValue("@From",fromStore);h.Parameters.AddWithValue("@To",toStore);h.Parameters.AddWithValue("@Date",DateTime.Today);h.Parameters.AddWithValue("@User",PosSession.CurrentUser?.UserId??0);id=Convert.ToInt64(h.ExecuteScalar());}
            using(var l=new SqlCommand("INSERT INTO StockTransferLines(TransferId,ProductId,Quantity,UnitCost) VALUES(@Id,@Product,@Qty,@Cost)",con,tran)){l.Parameters.AddWithValue("@Id",id);l.Parameters.AddWithValue("@Product",productId);l.Parameters.AddWithValue("@Qty",quantity);l.Parameters.AddWithValue("@Cost",cost);l.ExecuteNonQuery();}
            using(var u=new SqlCommand(@"UPDATE StockByStore SET Quantity=Quantity-@Qty WHERE StoreId=@From AND ProductId=@Product;
MERGE StockByStore t USING(SELECT @To StoreId,@Product ProductId)s ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=t.Quantity+@Qty
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost)VALUES(@To,@Product,@Qty,@Cost);",con,tran)){u.Parameters.AddWithValue("@Qty",quantity);u.Parameters.AddWithValue("@From",fromStore);u.Parameters.AddWithValue("@To",toStore);u.Parameters.AddWithValue("@Product",productId);u.Parameters.AddWithValue("@Cost",cost);u.ExecuteNonQuery();}
            tran.Commit();return no;
        }catch{tran.Rollback();throw;}
    }
    public string Adjust(int storeId,int productId,decimal countedQty,string disposition,string reason,string batchNo,string serialNo,DateTime? expiry)
    {
        if(PosSession.CurrentUser?.IsManager!=true)throw new InvalidOperationException("Manager approval is required for stock adjustment.");
        using var con=Db.Open();using var tran=con.BeginTransaction();
        try
        {
            var no=PostingService.NextNumber(con,tran,"ADJUSTMENT");decimal systemQty,cost;
            using(var q=new SqlCommand("SELECT Quantity,AverageCost FROM StockByStore WITH(UPDLOCK,ROWLOCK) WHERE StoreId=@Store AND ProductId=@Product",con,tran)){q.Parameters.AddWithValue("@Store",storeId);q.Parameters.AddWithValue("@Product",productId);using var r=q.ExecuteReader();if(!r.Read())throw new InvalidOperationException("Store stock record not found.");systemQty=Convert.ToDecimal(r[0]);cost=Convert.ToDecimal(r[1]);}
            var delta=countedQty-systemQty;long id;
            using(var h=new SqlCommand(@"INSERT INTO StockAdjustments(AdjustmentNo,StoreId,PostingDate,Reason,CreatedBy) OUTPUT INSERTED.AdjustmentId VALUES(@No,@Store,@Date,@Reason,@User)",con,tran)){h.Parameters.AddWithValue("@No",no);h.Parameters.AddWithValue("@Store",storeId);h.Parameters.AddWithValue("@Date",DateTime.Today);h.Parameters.AddWithValue("@Reason",reason);h.Parameters.AddWithValue("@User",PosSession.CurrentUser!.UserId);id=Convert.ToInt64(h.ExecuteScalar());}
            using(var l=new SqlCommand(@"INSERT INTO StockAdjustmentLines(AdjustmentId,ProductId,SystemQuantity,CountedQuantity,UnitCost,Disposition) VALUES(@Id,@Product,@System,@Counted,@Cost,@Disposition);
UPDATE StockByStore SET Quantity=@Counted WHERE StoreId=@Store AND ProductId=@Product;
UPDATE Products SET StockOnHand=StockOnHand+@Delta WHERE ProductId=@Product;
INSERT INTO InventoryLedger(StoreId,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy)
VALUES(@Store,@Product,@Disposition,@No,CASE WHEN @Delta>0 THEN @Delta ELSE 0 END,CASE WHEN @Delta<0 THEN -@Delta ELSE 0 END,@Cost,@Reason,@User);",con,tran)){l.Parameters.AddWithValue("@Id",id);l.Parameters.AddWithValue("@Product",productId);l.Parameters.AddWithValue("@System",systemQty);l.Parameters.AddWithValue("@Counted",countedQty);l.Parameters.AddWithValue("@Cost",cost);l.Parameters.AddWithValue("@Disposition",disposition);l.Parameters.AddWithValue("@Store",storeId);l.Parameters.AddWithValue("@Delta",delta);l.Parameters.AddWithValue("@No",no);l.Parameters.AddWithValue("@Reason",reason);l.Parameters.AddWithValue("@User",PosSession.CurrentUser.UserId);l.ExecuteNonQuery();}
            if(!string.IsNullOrWhiteSpace(batchNo)){using var b=new SqlCommand(@"MERGE ItemBatches t USING(SELECT @Store StoreId,@Product ProductId,@Batch BatchNo,@Serial SerialNo)s ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId AND t.BatchNo=s.BatchNo AND ISNULL(t.SerialNo,'')=ISNULL(s.SerialNo,'')
WHEN MATCHED THEN UPDATE SET Quantity=@Counted,ExpiryDate=@Expiry,Status=@Status
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,BatchNo,SerialNo,ExpiryDate,Quantity,UnitCost,Status)VALUES(@Store,@Product,@Batch,@Serial,@Expiry,@Counted,@Cost,@Status);",con,tran);b.Parameters.AddWithValue("@Store",storeId);b.Parameters.AddWithValue("@Product",productId);b.Parameters.AddWithValue("@Batch",batchNo);b.Parameters.AddWithValue("@Serial",serialNo);b.Parameters.AddWithValue("@Expiry",(object?)expiry?.Date??DBNull.Value);b.Parameters.AddWithValue("@Counted",countedQty);b.Parameters.AddWithValue("@Cost",cost);b.Parameters.AddWithValue("@Status",disposition);b.ExecuteNonQuery();}
            if(delta!=0){var setup=PostingService.GetSetup(con,tran);var value=Math.Abs(Math.Round(delta*cost,2));PostingService.PostBatch(con,tran,DateTime.Today,"Stock Adjustment",no,(int)id,delta>0?[new(setup.InventoryAccount,value,0,"Positive stock adjustment"),new(setup.StockAdjustmentAccount,0,value,"Stock gain")]:[new(setup.StockAdjustmentAccount,value,0,"Stock loss"),new(setup.InventoryAccount,0,value,"Negative stock adjustment")]);}
            tran.Commit();return no;
        }catch{tran.Rollback();throw;}
    }
}
