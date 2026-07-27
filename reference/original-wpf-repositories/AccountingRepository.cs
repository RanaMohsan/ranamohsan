using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;
using PayNex_POS_B1.Services;

namespace PayNex_POS_B1.Repositories;

public class AccountingRepository
{
    public PostingSetup GetSetup()
    {
        using var con = Db.Open();
        return PostingService.GetSetup(con);
    }

    public void SaveSetup(PostingSetup setup)
    {
        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        ValidateAccounts(con, tran, setup);
        using var cmd = new SqlCommand(@"
UPDATE PostingSetup SET CashAccount=@Cash,BankAccount=@Bank,ReceivableAccount=@Receivable,
InventoryAccount=@Inventory,InputTaxAccount=@InputTax,PayableAccount=@Payable,
OutputTaxAccount=@OutputTax,OpeningBalanceAccount=@Opening,SalesAccount=@Sales,
SalesReturnAccount=@SalesReturn,CogsAccount=@Cogs,StockAdjustmentAccount=@Adjustment,
CashierDiscountLimit=@DiscountLimit,BlockNegativeStock=@BlockNegative,CostingMethod=@Costing,
ModifiedAt=SYSUTCDATETIME() WHERE SetupId=1", con, tran);
        cmd.Parameters.AddWithValue("@Cash", setup.CashAccount);
        cmd.Parameters.AddWithValue("@Bank", setup.BankAccount);
        cmd.Parameters.AddWithValue("@Receivable", setup.ReceivableAccount);
        cmd.Parameters.AddWithValue("@Inventory", setup.InventoryAccount);
        cmd.Parameters.AddWithValue("@InputTax", setup.InputTaxAccount);
        cmd.Parameters.AddWithValue("@Payable", setup.PayableAccount);
        cmd.Parameters.AddWithValue("@OutputTax", setup.OutputTaxAccount);
        cmd.Parameters.AddWithValue("@Opening", setup.OpeningBalanceAccount);
        cmd.Parameters.AddWithValue("@Sales", setup.SalesAccount);
        cmd.Parameters.AddWithValue("@SalesReturn", setup.SalesReturnAccount);
        cmd.Parameters.AddWithValue("@Cogs", setup.CogsAccount);
        cmd.Parameters.AddWithValue("@Adjustment", setup.StockAdjustmentAccount);
        cmd.Parameters.AddWithValue("@DiscountLimit", setup.CashierDiscountLimit);
        cmd.Parameters.AddWithValue("@BlockNegative", setup.BlockNegativeStock);
        cmd.Parameters.AddWithValue("@Costing", setup.CostingMethod);
        cmd.ExecuteNonQuery();
        AddAudit(con, tran, "UPDATE_POSTING_SETUP", "PostingSetup", "1", "Posting setup updated.");
        tran.Commit();
    }

    public List<FinancialReportLine> GetTrialBalance(DateTime fromDate, DateTime toDate)
        => ReadFinancialReport(@"
SELECT a.AccountNo,a.AccountName,a.AccountType,
       ISNULL(SUM(g.DebitAmount),0) Debit,ISNULL(SUM(g.CreditAmount),0) Credit,
       ISNULL(SUM(g.DebitAmount-g.CreditAmount),0) Balance
FROM ChartOfAccounts a
LEFT JOIN GLEntries g ON g.AccountId=a.AccountId AND g.PostingDate BETWEEN @FromDate AND @ToDate
WHERE a.IsActive=1
GROUP BY a.AccountNo,a.AccountName,a.AccountType
HAVING SUM(ISNULL(g.DebitAmount,0))<>0 OR SUM(ISNULL(g.CreditAmount,0))<>0
ORDER BY a.AccountNo", fromDate, toDate);

    public List<FinancialReportLine> GetProfitAndLoss(DateTime fromDate, DateTime toDate)
        => ReadFinancialReport(@"
SELECT a.AccountNo,a.AccountName,a.AccountType,
       SUM(g.DebitAmount) Debit,SUM(g.CreditAmount) Credit,
       CASE WHEN a.AccountType='Income' THEN SUM(g.CreditAmount-g.DebitAmount)
            ELSE SUM(g.DebitAmount-g.CreditAmount) END Balance
FROM GLEntries g JOIN ChartOfAccounts a ON a.AccountId=g.AccountId
WHERE g.PostingDate BETWEEN @FromDate AND @ToDate AND a.AccountType IN('Income','Expense')
GROUP BY a.AccountNo,a.AccountName,a.AccountType ORDER BY a.AccountType,a.AccountNo", fromDate, toDate);

    public List<FinancialReportLine> GetBalanceSheet(DateTime asOfDate)
        => ReadFinancialReport(@"
SELECT a.AccountNo,a.AccountName,a.AccountType,
       SUM(g.DebitAmount) Debit,SUM(g.CreditAmount) Credit,
       CASE WHEN a.AccountType IN('Liability','Equity') THEN SUM(g.CreditAmount-g.DebitAmount)
            ELSE SUM(g.DebitAmount-g.CreditAmount) END Balance
FROM GLEntries g JOIN ChartOfAccounts a ON a.AccountId=g.AccountId
WHERE g.PostingDate<=@ToDate AND a.AccountType IN('Asset','Liability','Equity')
GROUP BY a.AccountNo,a.AccountName,a.AccountType ORDER BY a.AccountType,a.AccountNo",
            new DateTime(1900, 1, 1), asOfDate);

    public List<GLEntry> GetAccountLedger(DateTime fromDate, DateTime toDate, string accountTerm)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT g.GLEntryId,g.PostingDate,a.AccountNo,a.AccountName,g.DocumentType,g.DocumentNo,
g.DebitAmount,g.CreditAmount,ISNULL(g.Description,'') Description
FROM GLEntries g JOIN ChartOfAccounts a ON a.AccountId=g.AccountId
WHERE g.PostingDate BETWEEN @FromDate AND @ToDate
AND (@Term='' OR a.AccountNo LIKE @Like OR a.AccountName LIKE @Like)
ORDER BY a.AccountNo,g.PostingDate,g.GLEntryId";
        AddDateParameters(cmd, fromDate, toDate);
        cmd.Parameters.AddWithValue("@Term", accountTerm.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{accountTerm.Trim()}%");
        var list = new List<GLEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GLEntry
            {
                GLEntryId = SqlMap.Int(r, "GLEntryId"), PostingDate = SqlMap.DateTime(r, "PostingDate"),
                AccountNo = SqlMap.String(r, "AccountNo"), AccountName = SqlMap.String(r, "AccountName"),
                DocumentType = SqlMap.String(r, "DocumentType"), DocumentNo = SqlMap.String(r, "DocumentNo"),
                DebitAmount = SqlMap.Decimal(r, "DebitAmount"), CreditAmount = SqlMap.Decimal(r, "CreditAmount"),
                Description = SqlMap.String(r, "Description")
            });
        return list;
    }

    public List<GLEntry> GetCashBankBook(DateTime fromDate, DateTime toDate)
    {
        var setup = GetSetup();
        return GetAccountLedger(fromDate, toDate, "")
            .Where(x => x.AccountNo == setup.CashAccount || x.AccountNo == setup.BankAccount).ToList();
    }

    public List<AgingLine> GetCustomerAging(DateTime asOfDate)
        => ReadAging(@"
SELECT c.CustomerCode Code,c.CustomerName Name,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)<=30 THEN h.BalanceAmount ELSE 0 END) CurrentAmount,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 31 AND 60 THEN h.BalanceAmount ELSE 0 END) Days30,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 61 AND 90 THEN h.BalanceAmount ELSE 0 END) Days60,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 91 AND 120 THEN h.BalanceAmount ELSE 0 END) Days90,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)>120 THEN h.BalanceAmount ELSE 0 END) Over90
FROM SalesInvoiceHeader h JOIN Customers c ON c.CustomerId=h.CustomerId
WHERE h.Status='Posted' AND h.BalanceAmount>0 AND h.InvoiceDate<=@AsOf
GROUP BY c.CustomerCode,c.CustomerName ORDER BY c.CustomerName", asOfDate);

    public List<AgingLine> GetVendorAging(DateTime asOfDate)
        => ReadAging(@"
SELECT v.VendorCode Code,v.VendorName Name,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)<=30 THEN h.BalanceAmount ELSE 0 END) CurrentAmount,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 31 AND 60 THEN h.BalanceAmount ELSE 0 END) Days30,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 61 AND 90 THEN h.BalanceAmount ELSE 0 END) Days60,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 91 AND 120 THEN h.BalanceAmount ELSE 0 END) Days90,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)>120 THEN h.BalanceAmount ELSE 0 END) Over90
FROM PurchaseInvoiceHeader h JOIN Vendors v ON v.VendorId=h.VendorId
WHERE h.Status='Posted' AND h.BalanceAmount>0 AND h.InvoiceDate<=@AsOf
GROUP BY v.VendorCode,v.VendorName ORDER BY v.VendorName", asOfDate);

    public List<StockValuationLine> GetStockValuation()
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT p.ProductCode,p.ProductName,p.StockOnHand Quantity,p.PurchasePrice UnitCost,
ROUND(p.StockOnHand*p.PurchasePrice,2) Value,p.ReorderLevel
FROM Products p WHERE p.IsActive=1 ORDER BY p.ProductName";
        var list = new List<StockValuationLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StockValuationLine
            {
                ProductCode=SqlMap.String(r,"ProductCode"),ProductName=SqlMap.String(r,"ProductName"),
                Quantity=SqlMap.Decimal(r,"Quantity"),UnitCost=SqlMap.Decimal(r,"UnitCost"),
                Value=SqlMap.Decimal(r,"Value"),ReorderLevel=SqlMap.Decimal(r,"ReorderLevel")
            });
        return list;
    }

    public List<TaxReportLine> GetTaxReport(DateTime fromDate, DateTime toDate)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT 'Output GST (Sales)' TaxType,
ISNULL((SELECT SUM(GrandTotal-TaxAmount) FROM SalesHeader WHERE Status='Posted' AND SaleDate BETWEEN @FromDate AND @ToDate),0)
+ISNULL((SELECT SUM(GrandTotal-TaxAmount) FROM SalesInvoiceHeader WHERE Status='Posted' AND InvoiceDate BETWEEN @FromDate AND @ToDate),0) TaxableAmount,
ISNULL((SELECT SUM(TaxAmount) FROM SalesHeader WHERE Status='Posted' AND SaleDate BETWEEN @FromDate AND @ToDate),0)
+ISNULL((SELECT SUM(TaxAmount) FROM SalesInvoiceHeader WHERE Status='Posted' AND InvoiceDate BETWEEN @FromDate AND @ToDate),0) TaxAmount
UNION ALL
SELECT 'Input GST (Purchase)',ISNULL(SUM(GrandTotal-TaxAmount),0),ISNULL(SUM(TaxAmount),0)
FROM PurchaseInvoiceHeader WHERE Status='Posted' AND InvoiceDate BETWEEN @FromDate AND @ToDate";
        AddDateParameters(cmd, fromDate, toDate);
        var list = new List<TaxReportLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TaxReportLine { TaxType=SqlMap.String(r,"TaxType"),TaxableAmount=SqlMap.Decimal(r,"TaxableAmount"),TaxAmount=SqlMap.Decimal(r,"TaxAmount") });
        return list;
    }

    public List<DailyProfitLine> GetDailyProfit(DateTime fromDate, DateTime toDate)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT PostingDate,SUM(Sales) Sales,SUM(Tax) Tax,SUM(Cost) Cost FROM(
SELECT CAST(h.SaleDate AS DATE) PostingDate,SUM(l.LineTotal) Sales,SUM(l.TaxAmount) Tax,SUM(l.Quantity*l.UnitCost) Cost
FROM SalesHeader h JOIN SalesLines l ON l.SaleId=h.SaleId WHERE h.Status='Posted' AND h.SaleDate BETWEEN @FromDate AND @ToDate GROUP BY CAST(h.SaleDate AS DATE)
UNION ALL
SELECT h.InvoiceDate,SUM(l.LineTotal),SUM(l.TaxAmount),SUM(l.Quantity*l.UnitCost)
FROM SalesInvoiceHeader h JOIN SalesInvoiceLines l ON l.SalesInvoiceId=h.SalesInvoiceId WHERE h.Status='Posted' AND h.InvoiceDate BETWEEN @FromDate AND @ToDate GROUP BY h.InvoiceDate
)x GROUP BY PostingDate ORDER BY PostingDate";
        AddDateParameters(cmd, fromDate, toDate);
        var list = new List<DailyProfitLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DailyProfitLine { PostingDate=SqlMap.DateTime(r,"PostingDate"),Sales=SqlMap.Decimal(r,"Sales"),Tax=SqlMap.Decimal(r,"Tax"),Cost=SqlMap.Decimal(r,"Cost") });
        return list;
    }

    public void PostOpeningBalances(DateTime postingDate, decimal cash, decimal bank)
    {
        using var con = Db.Open();
        using var tran = con.BeginTransaction();
        var setup = PostingService.GetSetup(con, tran);
        var lines = new List<PostingLine>();
        if (cash > 0) lines.Add(new(setup.CashAccount,cash,0,"Opening cash"));
        if (bank > 0) lines.Add(new(setup.BankAccount,bank,0,"Opening bank"));
        var stockValue = Scalar(con, tran, "SELECT ISNULL(SUM(StockOnHand*PurchasePrice),0) FROM Products");
        var customer = Scalar(con, tran, "SELECT ISNULL(SUM(OpeningBalance),0) FROM Customers");
        var vendor = Scalar(con, tran, "SELECT ISNULL(SUM(OpeningBalance),0) FROM Vendors");
        if (stockValue > 0) lines.Add(new(setup.InventoryAccount,stockValue,0,"Opening stock"));
        if (customer > 0) lines.Add(new(setup.ReceivableAccount,customer,0,"Customer opening balances"));
        if (vendor > 0) lines.Add(new(setup.PayableAccount,0,vendor,"Vendor opening balances"));
        var debit = lines.Sum(x=>x.Debit);
        var credit = lines.Sum(x=>x.Credit);
        if (debit > credit) lines.Add(new(setup.OpeningBalanceAccount,0,debit-credit,"Opening balance equity"));
        else if (credit > debit) lines.Add(new(setup.OpeningBalanceAccount,credit-debit,0,"Opening balance equity"));
        using(var customerLedger=new SqlCommand(@"
INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
SELECT CustomerId,@Date,'Opening Balance','OPENING-BALANCE',OpeningBalance,0,CurrentBalance,'Customer opening balance',0
FROM Customers WHERE OpeningBalance<>0
AND NOT EXISTS(SELECT 1 FROM CustomerLedgerEntries l WHERE l.CustomerId=Customers.CustomerId AND l.DocumentType='Opening Balance')",con,tran))
        {customerLedger.Parameters.AddWithValue("@Date",postingDate.Date);customerLedger.ExecuteNonQuery();}
        using(var vendorLedger=new SqlCommand(@"
INSERT INTO VendorLedgerEntries(VendorId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
SELECT VendorId,@Date,'Opening Balance','OPENING-BALANCE',0,OpeningBalance,CurrentBalance,'Vendor opening balance',0
FROM Vendors WHERE OpeningBalance<>0
AND NOT EXISTS(SELECT 1 FROM VendorLedgerEntries l WHERE l.VendorId=Vendors.VendorId AND l.DocumentType='Opening Balance')",con,tran))
        {vendorLedger.Parameters.AddWithValue("@Date",postingDate.Date);vendorLedger.ExecuteNonQuery();}
        PostingService.PostBatch(con,tran,postingDate,"Opening Balance","OPENING-BALANCE",0,lines);
        tran.Commit();
    }

    public void ClosePeriod(string name, DateTime fromDate, DateTime toDate)
    {
        if (PosSession.CurrentUser?.IsAdmin != true) throw new InvalidOperationException("Only Admin can close an accounting period.");
        using var con=Db.Open();
        using var cmd=con.CreateCommand();
        cmd.CommandText=@"INSERT INTO AccountingPeriods(PeriodName,StartDate,EndDate,IsClosed,ClosedAt,ClosedBy)
VALUES(@Name,@FromDate,@ToDate,1,SYSUTCDATETIME(),@UserId)";
        cmd.Parameters.AddWithValue("@Name",name);
        cmd.Parameters.AddWithValue("@FromDate",fromDate.Date);
        cmd.Parameters.AddWithValue("@ToDate",toDate.Date);
        cmd.Parameters.AddWithValue("@UserId",PosSession.CurrentUser.UserId);
        cmd.ExecuteNonQuery();
    }

    public List<TaxGroupSetup> GetTaxGroups()
    {
        using var con=Db.Open();using var cmd=con.CreateCommand();cmd.CommandText="SELECT TaxGroupId,TaxGroupName,TaxPercent,IsInclusive FROM TaxGroups ORDER BY TaxGroupName";
        var list=new List<TaxGroupSetup>();using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(){TaxGroupId=SqlMap.Int(r,"TaxGroupId"),TaxGroupName=SqlMap.String(r,"TaxGroupName"),TaxPercent=SqlMap.Decimal(r,"TaxPercent"),IsInclusive=SqlMap.Bool(r,"IsInclusive")});return list;
    }

    public void SaveTaxGroups(IEnumerable<TaxGroupSetup> groups)
    {
        if(PosSession.CurrentUser?.IsAdmin!=true)throw new InvalidOperationException("Only Admin can edit tax setup.");
        using var con=Db.Open();using var tran=con.BeginTransaction();
        foreach(var group in groups)
        {
            using var cmd=new SqlCommand("UPDATE TaxGroups SET TaxGroupName=@Name,TaxPercent=@Percent,IsInclusive=@Inclusive WHERE TaxGroupId=@Id",con,tran);
            cmd.Parameters.AddWithValue("@Name",group.TaxGroupName.Trim());cmd.Parameters.AddWithValue("@Percent",group.TaxPercent);cmd.Parameters.AddWithValue("@Inclusive",group.IsInclusive);cmd.Parameters.AddWithValue("@Id",group.TaxGroupId);cmd.ExecuteNonQuery();
        }
        AddAudit(con,tran,"UPDATE_TAX_SETUP","TaxGroups","ALL","Tax groups updated.");tran.Commit();
    }

    public string ReverseBatch(long batchId, string reason)
    {
        if (PosSession.CurrentUser?.IsManager != true) throw new InvalidOperationException("Manager approval is required for reversal.");
        using var con=Db.Open();using var tran=con.BeginTransaction();
        try
        {
            DateTime date;string originalType;string originalNo;
            using(var h=new SqlCommand("SELECT PostingDate,DocumentType,DocumentNo,ReversedBatchId FROM PostingBatches WITH(UPDLOCK,ROWLOCK) WHERE PostingBatchId=@Id",con,tran))
            {h.Parameters.AddWithValue("@Id",batchId);using var r=h.ExecuteReader();if(!r.Read())throw new InvalidOperationException("Posting batch not found.");if(r["ReversedBatchId"]!=DBNull.Value)throw new InvalidOperationException("This document is already reversed.");date=Convert.ToDateTime(r["PostingDate"]);originalType=Convert.ToString(r["DocumentType"])!;originalNo=Convert.ToString(r["DocumentNo"])!;}
            var lines=new List<PostingLine>();
            using(var q=new SqlCommand(@"SELECT a.AccountNo,g.DebitAmount,g.CreditAmount FROM GLEntries g JOIN ChartOfAccounts a ON a.AccountId=g.AccountId WHERE g.PostingBatchId=@Id",con,tran))
            {q.Parameters.AddWithValue("@Id",batchId);using var r=q.ExecuteReader();while(r.Read())lines.Add(new(Convert.ToString(r[0])!,Convert.ToDecimal(r[2]),Convert.ToDecimal(r[1]),$"Reversal of {originalType} {originalNo}: {reason}"));}
            var no=PostingService.NextNumber(con,tran,"REVERSAL");
            var reversalId=PostingService.PostBatch(con,tran,DateTime.Today,"Reversal",no,(int)Math.Min(batchId,int.MaxValue),lines);
            using(var u=new SqlCommand("UPDATE PostingBatches SET ReversedBatchId=@ReversalId WHERE PostingBatchId=@Id",con,tran)){u.Parameters.AddWithValue("@ReversalId",reversalId);u.Parameters.AddWithValue("@Id",batchId);u.ExecuteNonQuery();}
            AddAudit(con,tran,"REVERSE_POSTING","PostingBatches",batchId.ToString(),$"Reversed {originalType} {originalNo} with {no}. Reason: {reason}");
            tran.Commit();return no;
        }catch{tran.Rollback();throw;}
    }

    public string BackupDatabase(bool systemInitiated=false)
    {
        if(!systemInitiated&&PosSession.CurrentUser?.IsAdmin!=true)throw new InvalidOperationException("Only Admin can create a database backup.");
        using var con=Db.Open();using var cmd=con.CreateCommand();
        var fileName=$"PayNex_POS_B1_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        cmd.CommandText=@"DECLARE @Path nvarchar(4000)=CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultBackupPath'))+@FileName;
DECLARE @Sql nvarchar(max)='BACKUP DATABASE '+QUOTENAME(DB_NAME())+' TO DISK='+QUOTENAME(@Path,'''')+' WITH COPY_ONLY, INIT, CHECKSUM';
EXEC(@Sql);SELECT @Path;";
        cmd.Parameters.AddWithValue("@FileName",fileName);
        return Convert.ToString(cmd.ExecuteScalar())??fileName;
    }

    public void RestoreDatabase(string backupPath)
    {
        if(PosSession.CurrentUser?.IsAdmin!=true)throw new InvalidOperationException("Only Admin can restore a database.");
        if(string.IsNullOrWhiteSpace(backupPath))throw new InvalidOperationException("Enter the SQL Server backup path.");
        var builder=new SqlConnectionStringBuilder(AppConfig.ConnectionString);
        var database=builder.InitialCatalog;
        using var con=Db.OpenMaster();using var cmd=con.CreateCommand();
        cmd.CommandTimeout=0;
        cmd.CommandText=$@"BEGIN TRY
ALTER DATABASE [{database.Replace("]","]]")}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [{database.Replace("]","]]")}] FROM DISK=@Path WITH REPLACE;
ALTER DATABASE [{database.Replace("]","]]")}] SET MULTI_USER;
END TRY
BEGIN CATCH
ALTER DATABASE [{database.Replace("]","]]")}] SET MULTI_USER;
THROW;
END CATCH;";
        cmd.Parameters.AddWithValue("@Path",backupPath.Trim());
        cmd.ExecuteNonQuery();
    }

    private static List<FinancialReportLine> ReadFinancialReport(string sql,DateTime fromDate,DateTime toDate)
    {
        using var con=Db.Open(); using var cmd=con.CreateCommand(); cmd.CommandText=sql; AddDateParameters(cmd,fromDate,toDate);
        var list=new List<FinancialReportLine>(); using var r=cmd.ExecuteReader();
        while(r.Read()) list.Add(new(){AccountNo=SqlMap.String(r,"AccountNo"),AccountName=SqlMap.String(r,"AccountName"),AccountType=SqlMap.String(r,"AccountType"),Debit=SqlMap.Decimal(r,"Debit"),Credit=SqlMap.Decimal(r,"Credit"),Balance=SqlMap.Decimal(r,"Balance")});
        return list;
    }
    private static List<AgingLine> ReadAging(string sql,DateTime asOf)
    {
        using var con=Db.Open(); using var cmd=con.CreateCommand(); cmd.CommandText=sql; cmd.Parameters.AddWithValue("@AsOf",asOf.Date);
        var list=new List<AgingLine>(); using var r=cmd.ExecuteReader();
        while(r.Read()) list.Add(new(){Code=SqlMap.String(r,"Code"),Name=SqlMap.String(r,"Name"),Current=SqlMap.Decimal(r,"CurrentAmount"),Days30=SqlMap.Decimal(r,"Days30"),Days60=SqlMap.Decimal(r,"Days60"),Days90=SqlMap.Decimal(r,"Days90"),Over90=SqlMap.Decimal(r,"Over90")});
        return list;
    }
    private static void AddDateParameters(SqlCommand cmd,DateTime fromDate,DateTime toDate){cmd.Parameters.AddWithValue("@FromDate",fromDate.Date);cmd.Parameters.AddWithValue("@ToDate",toDate.Date);}
    private static decimal Scalar(SqlConnection con,SqlTransaction tran,string sql){using var cmd=new SqlCommand(sql,con,tran);return Convert.ToDecimal(cmd.ExecuteScalar()??0m);}
    private static void ValidateAccounts(SqlConnection con,SqlTransaction tran,PostingSetup s)
    {
        var accounts=new[]{s.CashAccount,s.BankAccount,s.ReceivableAccount,s.InventoryAccount,s.InputTaxAccount,s.PayableAccount,s.OutputTaxAccount,s.OpeningBalanceAccount,s.SalesAccount,s.SalesReturnAccount,s.CogsAccount,s.StockAdjustmentAccount};
        foreach(var account in accounts){using var cmd=new SqlCommand("SELECT COUNT(1) FROM ChartOfAccounts WHERE AccountNo=@No AND IsActive=1",con,tran);cmd.Parameters.AddWithValue("@No",account);if(Convert.ToInt32(cmd.ExecuteScalar())==0)throw new InvalidOperationException($"Account {account} does not exist or is inactive.");}
    }
    private static void AddAudit(SqlConnection con,SqlTransaction tran,string action,string entity,string key,string description){using var cmd=new SqlCommand("INSERT INTO AuditLog(UserId,ActionName,EntityName,EntityKey,Description) VALUES(@User,@Action,@Entity,@Key,@Description)",con,tran);cmd.Parameters.AddWithValue("@User",PosSession.CurrentUser?.UserId??0);cmd.Parameters.AddWithValue("@Action",action);cmd.Parameters.AddWithValue("@Entity",entity);cmd.Parameters.AddWithValue("@Key",key);cmd.Parameters.AddWithValue("@Description",description);cmd.ExecuteNonQuery();}
}
