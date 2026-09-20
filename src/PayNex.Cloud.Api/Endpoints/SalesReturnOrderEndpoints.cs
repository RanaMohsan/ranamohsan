using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Security;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Endpoints;

public static class SalesReturnOrderEndpoints
{
    public static IEndpointRouteBuilder MapSalesReturnOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sales-return-orders", ListAsync);
        app.MapGet("/api/sales-return-orders/source-invoices", SourceInvoicesAsync);
        app.MapGet("/api/sales-return-orders/source-invoices/{invoiceId:int}/lines", SourceLinesAsync);
        app.MapGet("/api/sales-return-orders/{id:int}", GetAsync);
        app.MapGet("/api/sales-return-orders/{id:int}/lines", LinesAsync);
        app.MapPost("/api/sales-return-orders/draft", SaveDraftAsync);
        app.MapPost("/api/sales-return-orders/post", PostAsync);
        app.MapGet("/api/sales-return-orders/{id:int}/report", ReportAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to, string? status)
    {
        var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
        await using var con = await db.OpenTenantAsync(user.DatabaseName); await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1000 h.SalesReturnOrderId,h.ReturnOrderNo,h.OriginalSalesInvoiceId,h.OriginalInvoiceNo,h.ReturnDate,h.Status,h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.CostAmount,h.Reason,h.Remarks,h.PostedAt,c.CustomerCode,c.CustomerName,ISNULL(u.DisplayName,'') PreparedBy
FROM SalesReturnOrderHeader h INNER JOIN Customers c ON c.CustomerId=h.CustomerId LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.StoreId=@StoreId AND h.ReturnDate BETWEEN @From AND @To AND (@Status='' OR (@Status='OpenDraft' AND h.Status IN ('Open','Draft')) OR h.Status=@Status)
ORDER BY h.SalesReturnOrderId DESC";
        cmd.Parameters.AddWithValue("@StoreId", user.StoreId); cmd.Parameters.AddWithValue("@From", (from ?? new DateTime(1900,1,1)).Date); cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date); cmd.Parameters.AddWithValue("@Status", status ?? "");
        return Results.Ok(await SqlList.ReadAsync(cmd));
    }

    private static async Task<IResult> SourceInvoicesAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term)
    {
        var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
        await using var con = await db.OpenTenantAsync(user.DatabaseName); await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 200 h.SalesInvoiceId,h.InvoiceNo,h.InvoiceDate,h.CustomerId,c.CustomerCode,c.CustomerName,h.GrandTotal
FROM SalesInvoiceHeader h INNER JOIN Customers c ON c.CustomerId=h.CustomerId
WHERE h.StoreId=@StoreId AND h.Status='Posted' AND (@Term='' OR h.InvoiceNo LIKE '%'+@Term+'%' OR c.CustomerCode LIKE '%'+@Term+'%' OR c.CustomerName LIKE '%'+@Term+'%')
AND EXISTS(SELECT 1 FROM SalesInvoiceLines l OUTER APPLY(SELECT SUM(rl.ReturnQuantity) Qty FROM SalesReturnOrderLines rl INNER JOIN SalesReturnOrderHeader rh ON rh.SalesReturnOrderId=rl.SalesReturnOrderId WHERE rl.SalesInvoiceLineId=l.SalesInvoiceLineId AND rh.Status='Posted') r WHERE l.SalesInvoiceId=h.SalesInvoiceId AND l.Quantity>ISNULL(r.Qty,0))
ORDER BY h.SalesInvoiceId DESC";
        cmd.Parameters.AddWithValue("@StoreId", user.StoreId); cmd.Parameters.AddWithValue("@Term", (term ?? "").Trim());
        return Results.Ok(await SqlList.ReadAsync(cmd));
    }

    private static async Task<IResult> SourceLinesAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, int invoiceId)
    {
        var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
        await using var con = await db.OpenTenantAsync(user.DatabaseName); await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT l.SalesInvoiceLineId,l.ProductId,p.ProductCode,l.ProductName,l.Quantity SoldQuantity,ISNULL(r.ReturnedQuantity,0) ReturnedQuantity,l.Quantity-ISNULL(r.ReturnedQuantity,0) AvailableToReturn,l.UnitPrice,l.DiscountPercent,l.DiscountAmount,l.TaxPercent,l.TaxAmount,l.LineTotal,l.UnitCost,ISNULL(l.TaxInclusive,0) TaxInclusive
FROM SalesInvoiceLines l INNER JOIN SalesInvoiceHeader h ON h.SalesInvoiceId=l.SalesInvoiceId INNER JOIN Products p ON p.ProductId=l.ProductId
OUTER APPLY(SELECT SUM(rl.ReturnQuantity) ReturnedQuantity FROM SalesReturnOrderLines rl INNER JOIN SalesReturnOrderHeader rh ON rh.SalesReturnOrderId=rl.SalesReturnOrderId WHERE rl.SalesInvoiceLineId=l.SalesInvoiceLineId AND rh.Status='Posted') r
WHERE l.SalesInvoiceId=@Id AND h.StoreId=@StoreId AND h.Status='Posted' AND l.Quantity>ISNULL(r.ReturnedQuantity,0) ORDER BY l.SalesInvoiceLineId";
        cmd.Parameters.AddWithValue("@Id", invoiceId); cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
        return Results.Ok(await SqlList.ReadAsync(cmd));
    }

    private static async Task<IResult> GetAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id)
    {
        var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
        await using var con = await db.OpenTenantAsync(user.DatabaseName); await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1 h.*,c.CustomerCode,c.CustomerName,c.Mobile,c.Email,c.AddressLine,ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy FROM SalesReturnOrderHeader h INNER JOIN Customers c ON c.CustomerId=h.CustomerId LEFT JOIN Stores s ON s.StoreId=h.StoreId LEFT JOIN Users u ON u.UserId=h.UserId WHERE h.SalesReturnOrderId=@Id AND h.StoreId=@StoreId";
        cmd.Parameters.AddWithValue("@Id", id); cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
        var row = await SqlList.ReadSingleAsync(cmd); return row.Count == 0 ? Results.NotFound(new { message="Sales return order not found." }) : Results.Ok(row);
    }

    private static async Task<IResult> LinesAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id)
    {
        var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
        await using var con = await db.OpenTenantAsync(user.DatabaseName); await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand(); cmd.CommandText=@"SELECT l.*,p.ProductCode FROM SalesReturnOrderLines l INNER JOIN SalesReturnOrderHeader h ON h.SalesReturnOrderId=l.SalesReturnOrderId INNER JOIN Products p ON p.ProductId=l.ProductId WHERE l.SalesReturnOrderId=@Id AND h.StoreId=@StoreId ORDER BY l.SalesReturnOrderLineId"; cmd.Parameters.AddWithValue("@Id",id); cmd.Parameters.AddWithValue("@StoreId",user.StoreId);
        return Results.Ok(await SqlList.ReadAsync(cmd));
    }

    private static Task<IResult> SaveDraftAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, SalesReturnOrderSaveRequest request) => SaveAsync(http,db,tokens,request,false);
    private static Task<IResult> PostAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, SalesReturnOrderSaveRequest request) => SaveAsync(http,db,tokens,request,true);

    private static async Task<IResult> SaveAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, SalesReturnOrderSaveRequest request, bool post)
    {
        var user=ApiAuth.RequireUser(http,tokens); if(user==null)return Results.Unauthorized();
        if(request.OriginalSalesInvoiceId<=0)return Results.BadRequest(new{message="Original posted sales invoice is required."});
        var clientDocumentId=DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
        var requested=request.Lines.Where(x=>x.ReturnQuantity>0).GroupBy(x=>x.SalesInvoiceLineId).Select(g=>new SalesReturnOrderLineRequest{SalesInvoiceLineId=g.Key,ReturnQuantity=g.Sum(x=>x.ReturnQuantity)}).ToList();
        if(requested.Count==0)return Results.BadRequest(new{message="At least one positive return quantity is required."});
        await using var con=await db.OpenTenantAsync(user.DatabaseName); await EnsureSchemaAsync(con); await DesktopIdempotency.EnsureSchemaAsync(con); await using var tran=(SqlTransaction)await con.BeginTransactionAsync();
        try
        {
            if(post && !string.IsNullOrWhiteSpace(clientDocumentId))
            {
                var existing=await DesktopIdempotency.FindSalesReturnOrderAsync(con,tran,clientDocumentId);
                if(existing!=null)
                {
                    await tran.CommitAsync();
                    return Results.Ok(new{salesReturnOrderId=existing.SalesReturnOrderId,returnOrderNo=existing.ReturnOrderNo,status=existing.Status,grandTotal=existing.GrandTotal,duplicate=true,message="Desktop sales return order was already posted. Existing InterNex document returned."});
                }
            }
            await using var source=new SqlCommand(@"SELECT TOP 1 h.InvoiceNo,h.CustomerId FROM SalesInvoiceHeader h WHERE h.SalesInvoiceId=@Id AND h.StoreId=@StoreId AND h.Status='Posted'",con,tran); source.Parameters.AddWithValue("@Id",request.OriginalSalesInvoiceId);source.Parameters.AddWithValue("@StoreId",user.StoreId);
            string originalNo; int customerId; await using(var r=await source.ExecuteReaderAsync()){if(!await r.ReadAsync())throw new InvalidOperationException("Original posted sales invoice was not found in the current branch.");originalNo=SqlRead.String(r,"InvoiceNo");customerId=SqlRead.Int(r,"CustomerId");}
            var calculated=new List<CalcLine>(); decimal sub=0,disc=0,tax=0,total=0,cost=0;
            foreach(var req in requested)
            {
                await using var lc=new SqlCommand(@"SELECT l.SalesInvoiceLineId,l.ProductId,l.ProductName,l.Quantity,l.UnitPrice,l.DiscountPercent,l.DiscountAmount,l.TaxPercent,l.TaxAmount,l.LineTotal,l.UnitCost,ISNULL(l.TaxInclusive,0) TaxInclusive,ISNULL((SELECT SUM(rl.ReturnQuantity) FROM SalesReturnOrderLines rl INNER JOIN SalesReturnOrderHeader rh ON rh.SalesReturnOrderId=rl.SalesReturnOrderId WHERE rl.SalesInvoiceLineId=l.SalesInvoiceLineId AND rh.Status='Posted'),0) PostedReturned FROM SalesInvoiceLines l WITH(UPDLOCK,HOLDLOCK) WHERE l.SalesInvoiceId=@InvoiceId AND l.SalesInvoiceLineId=@LineId",con,tran);lc.Parameters.AddWithValue("@InvoiceId",request.OriginalSalesInvoiceId);lc.Parameters.AddWithValue("@LineId",req.SalesInvoiceLineId);
                await using var r=await lc.ExecuteReaderAsync();if(!await r.ReadAsync())throw new InvalidOperationException($"Original invoice line {req.SalesInvoiceLineId} was not found.");var sold=SqlRead.Decimal(r,"Quantity");var returned=SqlRead.Decimal(r,"PostedReturned");if(req.ReturnQuantity>sold-returned)throw new InvalidOperationException($"Return quantity exceeds the remaining returnable quantity for {SqlRead.String(r,"ProductName")}.");
                var ratio=req.ReturnQuantity/sold;var line=new CalcLine(req.SalesInvoiceLineId,SqlRead.Int(r,"ProductId"),SqlRead.String(r,"ProductName"),req.ReturnQuantity,SqlRead.Decimal(r,"UnitPrice"),SqlRead.Decimal(r,"DiscountPercent"),Math.Round(SqlRead.Decimal(r,"DiscountAmount")*ratio,2),SqlRead.Decimal(r,"TaxPercent"),Math.Round(SqlRead.Decimal(r,"TaxAmount")*ratio,2),Math.Round(SqlRead.Decimal(r,"LineTotal")*ratio,2),SqlRead.Decimal(r,"UnitCost"),SqlRead.Bool(r,"TaxInclusive"));calculated.Add(line);sub+=Math.Round(line.UnitPrice*line.Quantity,2);disc+=line.DiscountAmount;tax+=line.TaxAmount;total+=line.LineTotal;cost+=Math.Round(line.UnitCost*line.Quantity,2);
            }
            int id=request.SalesReturnOrderId;string no;
            if(id>0){await using var find=new SqlCommand("SELECT ReturnOrderNo FROM SalesReturnOrderHeader WITH(UPDLOCK,HOLDLOCK) WHERE SalesReturnOrderId=@Id AND StoreId=@StoreId AND Status='Open'",con,tran);find.Parameters.AddWithValue("@Id",id);find.Parameters.AddWithValue("@StoreId",user.StoreId);no=Convert.ToString(await find.ExecuteScalarAsync())??"";if(no=="")throw new InvalidOperationException("Open sales return order was not found or has already been posted.");await using var del=new SqlCommand("DELETE FROM SalesReturnOrderLines WHERE SalesReturnOrderId=@Id",con,tran);del.Parameters.AddWithValue("@Id",id);await del.ExecuteNonQueryAsync();}
            else {no=await PosSql.NextNumberAsync(con,tran,"SALES_RETURN_ORDER");await using var ins=new SqlCommand(@"INSERT INTO SalesReturnOrderHeader(ReturnOrderNo,OriginalSalesInvoiceId,OriginalInvoiceNo,CustomerId,ReturnDate,StoreId,BranchCode,UserId,Status,ExternalClientDocumentId) OUTPUT INSERTED.SalesReturnOrderId VALUES(@No,@InvoiceId,@InvoiceNo,@CustomerId,@Date,@StoreId,@BranchCode,@UserId,'Open',@ClientDocumentId)",con,tran);ins.Parameters.AddWithValue("@No",no);ins.Parameters.AddWithValue("@InvoiceId",request.OriginalSalesInvoiceId);ins.Parameters.AddWithValue("@InvoiceNo",originalNo);ins.Parameters.AddWithValue("@CustomerId",customerId);ins.Parameters.AddWithValue("@Date",request.ReturnDate.Date);ins.Parameters.AddWithValue("@StoreId",user.StoreId);ins.Parameters.AddWithValue("@BranchCode",user.BranchCode);ins.Parameters.AddWithValue("@UserId",user.UserId);ins.Parameters.AddWithValue("@ClientDocumentId",string.IsNullOrWhiteSpace(clientDocumentId)?DBNull.Value:clientDocumentId);id=Convert.ToInt32(await ins.ExecuteScalarAsync());}
            await using(var upd=new SqlCommand(@"UPDATE SalesReturnOrderHeader SET OriginalSalesInvoiceId=@InvoiceId,OriginalInvoiceNo=@InvoiceNo,CustomerId=@CustomerId,ReturnDate=@Date,UserId=@UserId,SubTotal=@Sub,DiscountAmount=@Disc,TaxAmount=@Tax,GrandTotal=@Total,CostAmount=@Cost,Reason=@Reason,Remarks=@Remarks,Status=@Status,PostedAt=CASE WHEN @Status='Posted' THEN SYSUTCDATETIME() ELSE NULL END WHERE SalesReturnOrderId=@Id",con,tran)){upd.Parameters.AddWithValue("@Id",id);upd.Parameters.AddWithValue("@InvoiceId",request.OriginalSalesInvoiceId);upd.Parameters.AddWithValue("@InvoiceNo",originalNo);upd.Parameters.AddWithValue("@CustomerId",customerId);upd.Parameters.AddWithValue("@Date",request.ReturnDate.Date);upd.Parameters.AddWithValue("@UserId",user.UserId);upd.Parameters.AddWithValue("@Sub",sub);upd.Parameters.AddWithValue("@Disc",disc);upd.Parameters.AddWithValue("@Tax",tax);upd.Parameters.AddWithValue("@Total",total);upd.Parameters.AddWithValue("@Cost",cost);upd.Parameters.AddWithValue("@Reason",request.Reason??"");upd.Parameters.AddWithValue("@Remarks",request.Remarks??"");upd.Parameters.AddWithValue("@Status",post?"Posted":"Open");await upd.ExecuteNonQueryAsync();}
            foreach(var l in calculated){await using var ins=new SqlCommand(@"INSERT INTO SalesReturnOrderLines(SalesReturnOrderId,SalesInvoiceLineId,ProductId,ProductName,ReturnQuantity,UnitPrice,DiscountPercent,DiscountAmount,TaxPercent,TaxAmount,LineTotal,UnitCost,TaxInclusive) VALUES(@Id,@Source,@ProductId,@Name,@Qty,@Price,@DiscPct,@Disc,@TaxPct,@Tax,@Total,@Cost,@TaxInclusive)",con,tran);ins.Parameters.AddWithValue("@Id",id);ins.Parameters.AddWithValue("@Source",l.SourceLineId);ins.Parameters.AddWithValue("@ProductId",l.ProductId);ins.Parameters.AddWithValue("@Name",l.ProductName);ins.Parameters.AddWithValue("@Qty",l.Quantity);ins.Parameters.AddWithValue("@Price",l.UnitPrice);ins.Parameters.AddWithValue("@DiscPct",l.DiscountPercent);ins.Parameters.AddWithValue("@Disc",l.DiscountAmount);ins.Parameters.AddWithValue("@TaxPct",l.TaxPercent);ins.Parameters.AddWithValue("@Tax",l.TaxAmount);ins.Parameters.AddWithValue("@Total",l.LineTotal);ins.Parameters.AddWithValue("@Cost",l.UnitCost);ins.Parameters.AddWithValue("@TaxInclusive",l.TaxInclusive);await ins.ExecuteNonQueryAsync();}
            if(post)await PostLedgerAsync(con,tran,user.StoreId,user.BranchCode,user.UserId,id,no,customerId,request.ReturnDate.Date,total,tax,cost,calculated);
            await tran.CommitAsync();return Results.Ok(new{salesReturnOrderId=id,returnOrderNo=no,status=post?"Posted":"Open",grandTotal=total,message=post?"Sales return order posted.":"Sales return order saved as Open."});
        }catch(Exception ex){await tran.RollbackAsync();return Results.BadRequest(new{message=ex.Message});}
    }

    private static async Task PostLedgerAsync(SqlConnection con,SqlTransaction tran,int storeId,string branchCode,int userId,int id,string no,int customerId,DateTime date,decimal total,decimal tax,decimal cost,List<CalcLine> lines)
    {
        foreach(var l in lines){await using var cmd=new SqlCommand(@"UPDATE Products SET StockOnHand=StockOnHand+@Qty WHERE ProductId=@ProductId;MERGE StockByStore AS t USING(SELECT @StoreId StoreId,@ProductId ProductId) s ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId WHEN MATCHED THEN UPDATE SET Quantity=Quantity+@Qty WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@StoreId,@ProductId,@Qty,@Cost);INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy) VALUES(@StoreId,@BranchCode,@ProductId,'Sales Return Order',@No,@Qty,0,@Cost,'Posted sales return order',@UserId)",con,tran);cmd.Parameters.AddWithValue("@Qty",l.Quantity);cmd.Parameters.AddWithValue("@ProductId",l.ProductId);cmd.Parameters.AddWithValue("@StoreId",storeId);cmd.Parameters.AddWithValue("@BranchCode",branchCode??"");cmd.Parameters.AddWithValue("@Cost",l.UnitCost);cmd.Parameters.AddWithValue("@No",no);cmd.Parameters.AddWithValue("@UserId",userId);await cmd.ExecuteNonQueryAsync();}
        await using(var c=new SqlCommand(@"UPDATE Customers SET CurrentBalance=CurrentBalance-@Amount WHERE CustomerId=@CustomerId;DECLARE @Balance DECIMAL(18,2)=(SELECT CurrentBalance FROM Customers WHERE CustomerId=@CustomerId);INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId) VALUES(@CustomerId,@Date,'Sales Return Order',@No,0,@Amount,@Balance,'Posted sales return order',@Id)",con,tran)){c.Parameters.AddWithValue("@Amount",total);c.Parameters.AddWithValue("@CustomerId",customerId);c.Parameters.AddWithValue("@Date",date);c.Parameters.AddWithValue("@No",no);c.Parameters.AddWithValue("@Id",id);await c.ExecuteNonQueryAsync();}
        var setup=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);await using(var s=new SqlCommand("SELECT TOP 1 * FROM PostingSetup WHERE SetupId=1",con,tran))await using(var r=await s.ExecuteReaderAsync()){if(!await r.ReadAsync())throw new InvalidOperationException("G/L Posting Setup is required before posting a sales return order.");foreach(var key in new[]{"SalesReturnAccount","OutputTaxAccount","ReceivableAccount","InventoryAccount","CogsAccount"})setup[key]=SqlRead.String(r,key);}
        var net=total-tax;await InsertGlAsync(con,tran,setup["SalesReturnAccount"],date,no,net,0,"Sales return",id,branchCode);if(tax>0)await InsertGlAsync(con,tran,setup["OutputTaxAccount"],date,no,tax,0,"Output tax reversal",id,branchCode);await InsertGlAsync(con,tran,setup["ReceivableAccount"],date,no,0,total,"Customer receivable credit",id,branchCode);if(cost>0){await InsertGlAsync(con,tran,setup["InventoryAccount"],date,no,cost,0,"Inventory returned",id,branchCode);await InsertGlAsync(con,tran,setup["CogsAccount"],date,no,0,cost,"COGS reversal",id,branchCode);}await using var batch=new SqlCommand("INSERT INTO PostingBatches(DocumentType,DocumentNo,PostingDate,SourceId,TotalDebit,TotalCredit,CreatedBy) VALUES('Sales Return Order',@No,@Date,@Id,@Amount,@Amount,@UserId)",con,tran);batch.Parameters.AddWithValue("@No",no);batch.Parameters.AddWithValue("@Date",date);batch.Parameters.AddWithValue("@Id",id);batch.Parameters.AddWithValue("@Amount",total+cost);batch.Parameters.AddWithValue("@UserId",userId);await batch.ExecuteNonQueryAsync();
    }

    private static async Task InsertGlAsync(SqlConnection con,SqlTransaction tran,string accountNo,DateTime date,string no,decimal debit,decimal credit,string description,int id,string branchCode){await using var cmd=new SqlCommand(@"DECLARE @AccountId INT=(SELECT AccountId FROM ChartOfAccounts WHERE AccountNo=@AccountNo AND IsActive=1);IF @AccountId IS NULL THROW 50001,'A required G/L account is missing or inactive.',1;INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId,BranchCode) VALUES(@AccountId,@Date,'Sales Return Order',@No,@Debit,@Credit,@Description,@Id,@BranchCode)",con,tran);cmd.Parameters.AddWithValue("@AccountNo",accountNo);cmd.Parameters.AddWithValue("@Date",date);cmd.Parameters.AddWithValue("@No",no);cmd.Parameters.AddWithValue("@Debit",debit);cmd.Parameters.AddWithValue("@Credit",credit);cmd.Parameters.AddWithValue("@Description",description);cmd.Parameters.AddWithValue("@Id",id);cmd.Parameters.AddWithValue("@BranchCode",branchCode??"");await cmd.ExecuteNonQueryAsync();}

    private static async Task<IResult> ReportAsync(HttpContext http,ConnectionFactory db,AuthTokenService tokens,int id){var user=ApiAuth.RequireUser(http,tokens);if(user==null)return Results.Unauthorized();await using var con=await db.OpenTenantAsync(user.DatabaseName);await EnsureSchemaAsync(con);await using var h=con.CreateCommand();h.CommandText="SELECT h.*,c.CustomerCode,c.CustomerName,c.AddressLine FROM SalesReturnOrderHeader h INNER JOIN Customers c ON c.CustomerId=h.CustomerId WHERE h.SalesReturnOrderId=@Id AND h.StoreId=@StoreId";h.Parameters.AddWithValue("@Id",id);h.Parameters.AddWithValue("@StoreId",user.StoreId);var header=await SqlList.ReadSingleAsync(h);if(header.Count==0)return Results.NotFound();await using var l=con.CreateCommand();l.CommandText="SELECT l.*,p.ProductCode FROM SalesReturnOrderLines l INNER JOIN Products p ON p.ProductId=l.ProductId WHERE l.SalesReturnOrderId=@Id ORDER BY l.SalesReturnOrderLineId";l.Parameters.AddWithValue("@Id",id);var lines=await SqlList.ReadAsync(l);string E(object? x)=>System.Net.WebUtility.HtmlEncode(Convert.ToString(x,CultureInfo.InvariantCulture));var rows=string.Join("",lines.Select(x=>$"<tr><td>{E(x["ProductCode"])}</td><td>{E(x["ProductName"])}</td><td class='n'>{E(x["ReturnQuantity"])}</td><td class='n'>{E(x["UnitPrice"])}</td><td class='n'>{E(x["LineTotal"])}</td></tr>"));var html=$"<!doctype html><html><head><meta charset='utf-8'><title>{E(header["ReturnOrderNo"])}</title><style>body{{font:14px Segoe UI;margin:36px;color:#222}}h1{{color:#0067b8}}table{{width:100%;border-collapse:collapse;margin-top:24px}}th,td{{padding:8px;border-bottom:1px solid #ddd;text-align:left}}.n{{text-align:right}}.total{{font-size:18px;font-weight:700}}@media print{{button{{display:none}}}}</style></head><body><button onclick='print()'>Print</button><h1>Sales Return Order</h1><p><b>No.:</b> {E(header["ReturnOrderNo"])} &nbsp; <b>Status:</b> {E(header["Status"])}<br><b>Original Invoice:</b> {E(header["OriginalInvoiceNo"])} &nbsp; <b>Date:</b> {E(header["ReturnDate"])}<br><b>Customer:</b> {E(header["CustomerCode"])} - {E(header["CustomerName"])}</p><table><thead><tr><th>Item No.</th><th>Description</th><th class='n'>Return Qty</th><th class='n'>Unit Price</th><th class='n'>Amount</th></tr></thead><tbody>{rows}</tbody></table><p class='total'>Total Credit: {E(header["GrandTotal"])}</p><p>Reason: {E(header["Reason"])}</p></body></html>";return Results.Content(html,"text/html",Encoding.UTF8);}

    private static async Task EnsureSchemaAsync(SqlConnection con){await using var cmd=con.CreateCommand();cmd.CommandText=@"IF OBJECT_ID('SalesReturnOrderHeader') IS NULL BEGIN CREATE TABLE SalesReturnOrderHeader(SalesReturnOrderId INT IDENTITY(1,1) PRIMARY KEY,ReturnOrderNo NVARCHAR(30) NOT NULL UNIQUE,OriginalSalesInvoiceId INT NOT NULL,OriginalInvoiceNo NVARCHAR(30) NOT NULL,CustomerId INT NOT NULL,ReturnDate DATE NOT NULL,StoreId INT NOT NULL,BranchCode NVARCHAR(30) NULL,UserId INT NOT NULL,SubTotal DECIMAL(18,2) NOT NULL DEFAULT 0,DiscountAmount DECIMAL(18,2) NOT NULL DEFAULT 0,TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,GrandTotal DECIMAL(18,2) NOT NULL DEFAULT 0,CostAmount DECIMAL(18,2) NOT NULL DEFAULT 0,Reason NVARCHAR(250) NULL,Remarks NVARCHAR(250) NULL,Status NVARCHAR(20) NOT NULL DEFAULT 'Open',PostedAt DATETIME2 NULL,CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),ExternalClientDocumentId NVARCHAR(80) NULL);CREATE TABLE SalesReturnOrderLines(SalesReturnOrderLineId INT IDENTITY(1,1) PRIMARY KEY,SalesReturnOrderId INT NOT NULL FOREIGN KEY REFERENCES SalesReturnOrderHeader(SalesReturnOrderId),SalesInvoiceLineId INT NOT NULL,ProductId INT NOT NULL,ProductName NVARCHAR(200) NOT NULL,ReturnQuantity DECIMAL(18,3) NOT NULL,UnitPrice DECIMAL(18,2) NOT NULL,DiscountPercent DECIMAL(9,2) NOT NULL DEFAULT 0,DiscountAmount DECIMAL(18,2) NOT NULL DEFAULT 0,TaxPercent DECIMAL(9,2) NOT NULL DEFAULT 0,TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,LineTotal DECIMAL(18,2) NOT NULL DEFAULT 0,UnitCost DECIMAL(18,2) NOT NULL DEFAULT 0,TaxInclusive BIT NOT NULL DEFAULT 0);CREATE INDEX IX_SalesReturnOrderLines_Source ON SalesReturnOrderLines(SalesInvoiceLineId);END IF COL_LENGTH('dbo.SalesReturnOrderHeader','ExternalClientDocumentId') IS NULL ALTER TABLE dbo.SalesReturnOrderHeader ADD ExternalClientDocumentId NVARCHAR(80) NULL; IF NOT EXISTS(SELECT 1 FROM NumberSeries WHERE SeriesCode='SALES_RETURN_ORDER') INSERT INTO NumberSeries(SeriesCode,Prefix,LastNumber,NumberLength,IncludeDate) VALUES('SALES_RETURN_ORDER','SRO',0,6,1);";await cmd.ExecuteNonQueryAsync();}
    private sealed record CalcLine(int SourceLineId,int ProductId,string ProductName,decimal Quantity,decimal UnitPrice,decimal DiscountPercent,decimal DiscountAmount,decimal TaxPercent,decimal TaxAmount,decimal LineTotal,decimal UnitCost,bool TaxInclusive);
}
