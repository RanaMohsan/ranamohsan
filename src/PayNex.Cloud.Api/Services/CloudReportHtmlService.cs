using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using System.Net;
using System.Text;

namespace PayNex.Cloud.Api.Services;

public sealed class CloudReportHtmlService
{
    public IReadOnlyList<string> GetTemplateNames(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "Reports");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.rdlc").Select(Path.GetFileName).Where(x => x != null).Cast<string>().OrderBy(x => x).ToList();
    }

    public string? GetTemplatePath(IWebHostEnvironment env, string fileName)
    {
        fileName = Path.GetFileName(fileName ?? string.Empty);
        if (!fileName.EndsWith(".rdlc", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(env.ContentRootPath, "Reports", fileName);
        return File.Exists(path) ? path : null;
    }

    public async Task<string> SalesInvoiceHtmlAsync(SqlConnection con, int saleId, string? layout = null)
    {
        await using var h = con.CreateCommand();
        h.CommandText = @"SELECT TOP 1 sh.SaleId,sh.InvoiceNo,sh.SaleDate,sh.SubTotal,sh.DiscountAmount,sh.TaxAmount,sh.GrandTotal,sh.PaidAmount,sh.ChangeAmount,sh.Remarks,c.CustomerCode,c.CustomerName,c.Mobile,c.Email,c.AddressLine,s.StoreName,u.DisplayName UserName
FROM SalesHeader sh
LEFT JOIN Customers c ON c.CustomerId=sh.CustomerId
LEFT JOIN Stores s ON s.StoreId=sh.StoreId
LEFT JOIN Users u ON u.UserId=sh.UserId
WHERE sh.SaleId=@SaleId";
        h.Parameters.AddWithValue("@SaleId", saleId);
        var header = await SqlList.ReadSingleAsync(h);
        if (header.Count == 0) return NotFoundHtml("POS sales invoice not found.");

        await using var l = con.CreateCommand();
        l.CommandText = @"SELECT ProductName,Quantity,UnitPrice,DiscountPercent,DiscountAmount,TaxPercent,TaxAmount,LineTotal,UnitCost FROM SalesLines WHERE SaleId=@SaleId ORDER BY SaleLineId";
        l.Parameters.AddWithValue("@SaleId", saleId);
        var lines = await SqlList.ReadAsync(l);
        return DocumentHtml(con, "Posted Sales Invoice", Val(header,"InvoiceNo"),
            HeaderBlocks(("Posting Date", DateVal(header,"SaleDate")), ("Customer", Val(header,"CustomerName")), ("Mobile", Val(header,"Mobile")), ("Store", Val(header,"StoreName")), ("Cashier", Val(header,"UserName")), ("Status", "Posted")),
            LinesTable(lines, new[]{"ProductName","Quantity","UnitPrice","DiscountAmount","TaxAmount","LineTotal"}),
            TaxSummary(lines), Totals(header), StandardFooter("Received By", "Approved By"), layout);
    }

    public async Task<string> FormalSalesInvoiceHtmlAsync(SqlConnection con, int salesInvoiceId, string? layout = null)
    {
        await using var h = con.CreateCommand();
        h.CommandText = @"SELECT TOP 1 h.SalesInvoiceId,h.InvoiceNo,h.InvoiceDate,h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,h.BalanceAmount,h.Remarks,c.CustomerCode,c.CustomerName,c.Mobile,c.Email,c.AddressLine,s.StoreName,u.DisplayName UserName
FROM SalesInvoiceHeader h
LEFT JOIN Customers c ON c.CustomerId=h.CustomerId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.SalesInvoiceId=@Id";
        h.Parameters.AddWithValue("@Id", salesInvoiceId);
        var header = await SqlList.ReadSingleAsync(h);
        if (header.Count == 0) return NotFoundHtml("Sales invoice not found.");

        await using var l = con.CreateCommand();
        l.CommandText = @"SELECT ProductName,Quantity,UnitPrice,DiscountPercent,DiscountAmount,TaxPercent,TaxAmount,LineTotal,UnitCost FROM SalesInvoiceLines WHERE SalesInvoiceId=@Id ORDER BY SalesInvoiceLineId";
        l.Parameters.AddWithValue("@Id", salesInvoiceId);
        var lines = await SqlList.ReadAsync(l);
        var remarks = string.IsNullOrWhiteSpace(Val(header,"Remarks")) ? string.Empty : $"<div class='remarks'><b>Remarks:</b> {Enc(Val(header,"Remarks"))}</div>";
        return DocumentHtml(con, "Sales Invoice", Val(header,"InvoiceNo"),
            HeaderBlocks(("Invoice Date", DateVal(header,"InvoiceDate")), ("Customer", Val(header,"CustomerName")), ("Customer Code", Val(header,"CustomerCode")), ("Mobile", Val(header,"Mobile")), ("Store", Val(header,"StoreName")), ("Prepared By", Val(header,"UserName"))) + remarks,
            LinesTable(lines, new[]{"ProductName","Quantity","UnitPrice","DiscountPercent","DiscountAmount","TaxPercent","TaxAmount","LineTotal"}),
            TaxSummary(lines), Totals(header), StandardFooter("Customer Signature", "Authorized Signature"), layout);
    }

    public async Task<string> PosReceiptHtmlAsync(SqlConnection con, int saleId, string? layout = null)
        => await CashierSalesInvoiceHtmlAsync(con, saleId, layout);

    /// <summary>Sample-style Sales Invoice used by Cloud + Desktop Cashier POS (identical HTML).</summary>
    public async Task<string> CashierSalesInvoiceHtmlAsync(SqlConnection con, int saleId, string? layout = null)
    {
        await using var h = con.CreateCommand();
        h.CommandText = @"SELECT TOP 1 sh.SaleId,sh.InvoiceNo,sh.SaleDate,sh.SubTotal,sh.DiscountAmount,sh.TaxAmount,sh.GrandTotal,sh.PaidAmount,sh.ChangeAmount,
c.CustomerName,ISNULL(c.AddressLine,'') CustomerAddress,ISNULL(c.Mobile,'') CustomerMobile,
s.StoreName,u.DisplayName UserName
FROM SalesHeader sh
LEFT JOIN Customers c ON c.CustomerId=sh.CustomerId
LEFT JOIN Stores s ON s.StoreId=sh.StoreId
LEFT JOIN Users u ON u.UserId=sh.UserId
WHERE sh.SaleId=@SaleId";
        h.Parameters.AddWithValue("@SaleId", saleId);
        var header = await SqlList.ReadSingleAsync(h);
        if (header.Count == 0) return NotFoundHtml("POS receipt not found.");

        await using var l = con.CreateCommand();
        l.CommandText = @"SELECT ProductName,Quantity,UnitPrice,DiscountAmount,TaxAmount,LineTotal FROM SalesLines WHERE SaleId=@SaleId ORDER BY SaleLineId";
        l.Parameters.AddWithValue("@SaleId", saleId);
        var lines = await SqlList.ReadAsync(l);
        var company = await CompanyAsync(con);

        var saleDate = header.TryGetValue("SaleDate", out var sd) && sd != null && DateTime.TryParse(Convert.ToString(sd), out var dt)
            ? dt
            : DateTime.Now;
        var billNo = Val(header, "InvoiceNo");
        var grand = DecimalVal(header, "GrandTotal");
        var disc = DecimalVal(header, "DiscountAmount");
        var sub = DecimalVal(header, "SubTotal");
        var paid = DecimalVal(header, "PaidAmount");
        var change = DecimalVal(header, "ChangeAmount");
        var totalQty = lines.Sum(r => DecimalVal(r, "Quantity"));

        var sb = new StringBuilder();
        var sr = 1;
        foreach (var row in lines)
        {
            sb.Append("<tr>")
              .Append($"<td class='c'>{sr++}</td>")
              .Append($"<td>{Enc(Val(row, "ProductName"))}</td>")
              .Append($"<td class='r'>{Enc(Qty(row, "Quantity"))}</td>")
              .Append($"<td class='r'>{Enc(Money(row, "UnitPrice"))}</td>")
              .Append($"<td class='r'>{Enc(Money(row, "LineTotal"))}</td>")
              .Append("</tr>");
        }
        if (lines.Count == 0)
            sb.Append("<tr><td colspan='5' class='c muted'>No lines found.</td></tr>");

        var phone = Val(company, "PhoneNo");
        var email = Val(company, "Email");
        var tax = Val(company, "TaxRegistrationNo");
        var contactBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(phone)) contactBits.Add("Ph. " + phone);
        if (!string.IsNullOrWhiteSpace(email)) contactBits.Add(email);
        var contactLine = string.Join(" · ", contactBits);
        var custAddr = Val(header, "CustomerAddress");
        var custMobile = Val(header, "CustomerMobile");
        var custExtra = string.Join(", ", new[] { custAddr, string.IsNullOrWhiteSpace(custMobile) ? "" : "Ph : " + custMobile }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var words = AmountInWords(grand);
        var discLine = disc > 0
            ? $"<div class='row'><span>Bill Discount</span><b>{Enc(disc.ToString("N2"))}</b></div>"
            : "";
        var itemDisc = lines.Sum(r => DecimalVal(r, "DiscountAmount"));
        var itemDiscLine = itemDisc > 0
            ? $"<div class='row muted'><span>Item Discount Inclusive</span><b>{Enc(itemDisc.ToString("N2"))}</b></div>"
            : "";
        var taxHtml = string.IsNullOrWhiteSpace(tax) ? "" : $"<div class=\"center muted\">GSTIN : {Enc(tax)}</div>";

        return $$$"""
<!doctype html><html><head><meta charset="utf-8"><title>Sales Invoice {{{Enc(billNo)}}}</title>
<style>
*{box-sizing:border-box}
body{font-family:'Segoe UI',Arial,sans-serif;background:#f3eee8;margin:0;padding:16px;color:#222}
.sheet{width:80mm;max-width:80mm;margin:auto;background:#fff;padding:12px 10px;border:1px solid #d8cfc6}
.center{text-align:center}.r{text-align:right}.c{text-align:center}
.title{font-size:13px;font-weight:700;margin-bottom:4px;text-decoration:underline}
.company{font-size:15px;font-weight:900;margin:2px 0;line-height:1.2}
.muted{color:#666;font-size:10px;line-height:1.35}
.hr{border:0;border-top:1px solid #333;margin:7px 0}
.meta{font-size:11px;display:flex;justify-content:space-between;gap:6px}
.cust{font-size:11px;line-height:1.35;margin:4px 0}
table{width:100%;border-collapse:collapse;font-size:10.5px;margin-top:4px}
th{font-size:10px;border-bottom:1px solid #333;padding:3px 2px;text-align:left}
td{padding:3px 2px;vertical-align:top;border-bottom:1px dotted #ccc}
.row{display:flex;justify-content:space-between;font-size:11px;padding:2px 0}
.net{font-size:16px;font-weight:900;margin-top:4px}
.words{font-size:10.5px;text-align:center;font-style:italic;margin:6px 0}
.tender{font-size:11px;margin-top:4px}
.tender h4{margin:4px 0;text-align:center;text-decoration:underline;font-size:11px}
.thanks{text-align:center;font-size:11px;margin-top:8px;line-height:1.4}
.brand{text-align:center;font-size:9px;color:#777;margin-top:6px}
.print{display:block;width:80mm;margin:0 auto 8px;background:#6f8fa8;color:#fff;border:0;border-radius:6px;padding:8px;font-weight:700}
@media print{
  body{background:#fff!important;padding:0!important;margin:0!important}
  .sheet{border:0!important;width:74mm!important;max-width:74mm!important;margin:0!important;padding:0!important}
  .print{display:none!important}
  @page{size:80mm auto;margin:3mm}
}
</style></head>
<body>
<button class="print" onclick="window.print()">Print Invoice</button>
<div class="sheet">
  <div class="center title">Sales Invoice</div>
  <div class="center company">{{{Enc(Val(company,"CompanyName"))}}}</div>
  <div class="center muted">{{{Enc(Val(company,"AddressLine"))}}}</div>
  <div class="center muted">{{{Enc(contactLine)}}}</div>
  {{{taxHtml}}}
  <div class="center brand">InterNex Cloud ERP</div>
  <hr class="hr"/>
  <div class="meta"><span>Bill No.: <b>{{{Enc(billNo)}}}</b></span><span>Date : {{{saleDate:dd-MMM-yyyy}}} &nbsp; Time : {{{saleDate:HH:mm:ss}}}</span></div>
  <hr class="hr"/>
  <div class="cust"><b>To,</b> {{{Enc(Val(header,"CustomerName"))}}}<br>{{{Enc(custExtra)}}}</div>
  <hr class="hr"/>
  <table>
    <thead><tr><th class="c" style="width:8%">Sr.</th><th>Description</th><th class="r" style="width:12%">Qty</th><th class="r" style="width:16%">Rate</th><th class="r" style="width:18%">Amount</th></tr></thead>
    <tbody>{{{sb}}}</tbody>
  </table>
  <hr class="hr"/>
  <div class="row"><span>Total Qty</span><b>{{{Enc(totalQty.ToString("N2"))}}}</b></div>
  {{{itemDiscLine}}}
  <div class="row"><span>Gross Amount</span><b>{{{Enc(sub.ToString("N2"))}}}</b></div>
  {{{discLine}}}
  <div class="row net"><span>Net Amount</span><b>{{{Enc(grand.ToString("N2"))}}}</b></div>
  <hr class="hr"/>
  <div class="words">{{{Enc(words)}}}</div>
  <div class="tender">
    <h4>Tender Details</h4>
    <div class="row"><span>Paid Amount</span><b>{{{Enc(paid.ToString("N2"))}}}</b></div>
    <div class="row"><span>Change / Balance</span><b>{{{Enc(change.ToString("N2"))}}}</b></div>
    <div class="muted">Cashier: {{{Enc(Val(header,"UserName"))}}} · Store: {{{Enc(Val(header,"StoreName"))}}}</div>
  </div>
  <hr class="hr"/>
  <div class="thanks">Have a Nice Day<br>Thanks for your Kind Visit</div>
  <div class="brand">Powered by InterNex Cloud ERP</div>
</div>
<script>window.addEventListener('load',function(){ setTimeout(function(){ try{ window.print(); }catch(e){} }, 350); });</script>
</body></html>
""";
    }

    public async Task<string> PurchaseInvoiceHtmlAsync(SqlConnection con, int purchaseInvoiceId, string? layout = null)
    {
        await using var h = con.CreateCommand();
        h.CommandText = @"SELECT TOP 1 ph.PurchaseInvoiceId,ph.InvoiceNo,ph.InvoiceDate,ph.VendorInvoiceNo,ph.SubTotal,ph.DiscountAmount,ph.TaxAmount,ph.GrandTotal,ph.PaidAmount,ph.BalanceAmount,ph.Remarks,v.VendorCode,v.VendorName,v.Mobile,v.Email,v.AddressLine,s.StoreName,u.DisplayName UserName
FROM PurchaseInvoiceHeader ph
LEFT JOIN Vendors v ON v.VendorId=ph.VendorId
LEFT JOIN Stores s ON s.StoreId=ph.StoreId
LEFT JOIN Users u ON u.UserId=ph.UserId
WHERE ph.PurchaseInvoiceId=@Id";
        h.Parameters.AddWithValue("@Id", purchaseInvoiceId);
        var header = await SqlList.ReadSingleAsync(h);
        if (header.Count == 0) return NotFoundHtml("Purchase invoice not found.");

        await using var l = con.CreateCommand();
        l.CommandText = @"SELECT ProductName,Quantity,UnitCost,TaxPercent,TaxAmount,LineTotal FROM PurchaseInvoiceLines WHERE PurchaseInvoiceId=@Id ORDER BY PurchaseInvoiceLineId";
        l.Parameters.AddWithValue("@Id", purchaseInvoiceId);
        var lines = await SqlList.ReadAsync(l);
        var remarks = string.IsNullOrWhiteSpace(Val(header,"Remarks")) ? string.Empty : $"<div class='remarks'><b>Remarks:</b> {Enc(Val(header,"Remarks"))}</div>";
        return DocumentHtml(con, "Posted Purchase Invoice", Val(header,"InvoiceNo"), HeaderBlocks(
            ("Invoice Date", DateVal(header,"InvoiceDate")), ("Vendor", Val(header,"VendorName")), ("Vendor Inv.", Val(header,"VendorInvoiceNo")), ("Mobile", Val(header,"Mobile")), ("Store", Val(header,"StoreName")), ("User", Val(header,"UserName"))) + remarks,
            LinesTable(lines, new[]{"ProductName","Quantity","UnitCost","TaxPercent","TaxAmount","LineTotal"}), TaxSummary(lines), Totals(header), StandardFooter("Prepared By", "Approved By"), layout);
    }

    public async Task<string> CustomerPaymentHtmlAsync(SqlConnection con, int paymentId, string? layout = null)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1 p.PaymentId,p.PaymentNo,p.PaymentDate,p.Amount,p.PaymentMethod,p.ReferenceNo,p.Remarks,c.CustomerCode,c.CustomerName,c.Mobile,c.Email,c.AddressLine,u.DisplayName UserName
FROM CustomerPayments p
LEFT JOIN Customers c ON c.CustomerId=p.CustomerId
LEFT JOIN Users u ON u.UserId=p.CreatedBy
WHERE p.PaymentId=@Id";
        cmd.Parameters.AddWithValue("@Id", paymentId);
        var row = await SqlList.ReadSingleAsync(cmd);
        if (row.Count == 0) return NotFoundHtml("Customer payment not found.");
        var body = HeaderBlocks(("Date", DateVal(row,"PaymentDate")), ("Customer", Val(row,"CustomerName")), ("Amount", Money(row,"Amount")), ("Method", Val(row,"PaymentMethod")), ("Reference", Val(row,"ReferenceNo")), ("Received By", Val(row,"UserName")))
            + $"<div class='remarks'><b>Remarks:</b> {Enc(Val(row,"Remarks"))}</div>";
        return DocumentHtml(con, "Customer Payment Receipt", Val(row,"PaymentNo"), body, "", "", "", StandardFooter("Received By", "Approved By"), layout);
    }

    public async Task<string> VendorPaymentHtmlAsync(SqlConnection con, int paymentId, string? layout = null)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1 p.PaymentId,p.PaymentNo,p.PaymentDate,p.Amount,p.PaymentMethod,p.ReferenceNo,p.Remarks,v.VendorCode,v.VendorName,v.Mobile,v.Email,v.AddressLine,u.DisplayName UserName
FROM VendorPayments p
LEFT JOIN Vendors v ON v.VendorId=p.VendorId
LEFT JOIN Users u ON u.UserId=p.CreatedBy
WHERE p.PaymentId=@Id";
        cmd.Parameters.AddWithValue("@Id", paymentId);
        var row = await SqlList.ReadSingleAsync(cmd);
        if (row.Count == 0) return NotFoundHtml("Vendor payment not found.");
        var body = HeaderBlocks(("Date", DateVal(row,"PaymentDate")), ("Vendor", Val(row,"VendorName")), ("Amount", Money(row,"Amount")), ("Method", Val(row,"PaymentMethod")), ("Reference", Val(row,"ReferenceNo")), ("Paid By", Val(row,"UserName")))
            + $"<div class='remarks'><b>Remarks:</b> {Enc(Val(row,"Remarks"))}</div>";
        return DocumentHtml(con, "Vendor Payment Receipt", Val(row,"PaymentNo"), body, "", "", "", StandardFooter("Prepared By", "Received By"), layout);
    }

    public async Task<string> CustomerLedgerHtmlAsync(SqlConnection con, int customerId, string? layout = null)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1000 le.PostingDate,le.DocumentType,le.DocumentNo,le.DebitAmount,le.CreditAmount,le.BalanceAfter,le.Description,c.CustomerCode,c.CustomerName
FROM CustomerLedgerEntries le INNER JOIN Customers c ON c.CustomerId=le.CustomerId
WHERE (@CustomerId=0 OR le.CustomerId=@CustomerId)
ORDER BY le.PostingDate,le.CustomerLedgerEntryId";
        cmd.Parameters.AddWithValue("@CustomerId", customerId);
        var rows = await SqlList.ReadAsync(cmd);
        var docNo = customerId == 0 ? "All Customers" : rows.FirstOrDefault() is { } r ? Val(r,"CustomerName") : "Selected Customer";
        var header = HeaderBlocks(("Customer", docNo), ("Printed On", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("Status", rows.Count == 0 ? "No entries found" : "Entries found"));
        return DocumentHtml(con, "Customer Ledger Entries", docNo, header, LinesTable(rows, new[]{"PostingDate","DocumentType","DocumentNo","DebitAmount","CreditAmount","BalanceAfter","Description"}), "", "", "", layout);
    }

    public async Task<string> VendorLedgerHtmlAsync(SqlConnection con, int vendorId, string? layout = null)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1000 le.PostingDate,le.DocumentType,le.DocumentNo,le.DebitAmount,le.CreditAmount,le.BalanceAfter,le.Description,v.VendorCode,v.VendorName
FROM VendorLedgerEntries le INNER JOIN Vendors v ON v.VendorId=le.VendorId
WHERE (@VendorId=0 OR le.VendorId=@VendorId)
ORDER BY le.PostingDate,le.VendorLedgerEntryId";
        cmd.Parameters.AddWithValue("@VendorId", vendorId);
        var rows = await SqlList.ReadAsync(cmd);
        var docNo = vendorId == 0 ? "All Vendors" : rows.FirstOrDefault() is { } r ? Val(r,"VendorName") : "Selected Vendor";
        var header = HeaderBlocks(("Vendor", docNo), ("Printed On", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("Status", rows.Count == 0 ? "No entries found" : "Entries found"));
        return DocumentHtml(con, "Vendor Ledger Entries", docNo, header, LinesTable(rows, new[]{"PostingDate","DocumentType","DocumentNo","DebitAmount","CreditAmount","BalanceAfter","Description"}), "", "", "", layout);
    }

    public string ExpenseReportHtml(SqlConnection con, List<Dictionary<string, object?>> rows, DateTime from, DateTime to,
        int branchId, int categoryId, string periodLabel, string? layout = null)
    {
        var branch = branchId == 0 ? "All Branches" : rows.FirstOrDefault() is { } branchRow ? Val(branchRow, "Branch") : "Selected Branch";
        var category = categoryId == 0 ? "All Categories" : rows.FirstOrDefault() is { } categoryRow ? Val(categoryRow, "ExpenseCategory") : "Selected Category";
        var grandTotal = rows.Sum(x => DecimalVal(x, "Amount"));
        var header = HeaderBlocks(
            ("Period", periodLabel),
            ("From Date", from.ToString("yyyy-MM-dd")),
            ("To Date", to.ToString("yyyy-MM-dd")),
            ("Branch", branch),
            ("Expense Category", category),
            ("Records", rows.Count.ToString()));
        var totals = $"<div class='total'><div class='grand'><span>Grand Total of Expenses</span><b>{grandTotal:N2}</b></div></div>";
        return DocumentHtml(con, "Expense Management Report", periodLabel, header,
            LinesTable(rows, new[] { "ExpenseDate", "ExpenseCategory", "Description", "Amount", "Branch", "CreatedBy", "Remarks" }),
            "", totals, StandardFooter("Prepared By", "Approved By"), layout);
    }

    private static async Task<Dictionary<string, object?>> CompanyAsync(SqlConnection con)
    {
        return await SqlList.ReadSingleAsync(con, "SELECT TOP 1 CompanyName,AddressLine,PhoneNo,Email,Website,TaxRegistrationNo,LogoPath,LogoImage FROM CompanyInformation ORDER BY CompanyInformationId");
    }

    private static string DocumentHtml(SqlConnection con, string title, string docNo, string headerHtml, string tableHtml, string taxHtml, string totalsHtml, string footerHtml = "", string? layout = null)
    {
        var company = CompanyAsync(con).GetAwaiter().GetResult();
        var cleanLayout = CleanLayout(layout);
        var layoutName = cleanLayout == "compact" ? "Compact A4" : cleanLayout == "standard" ? "Standard A4" : "Professional A4";
        var logo = LogoHtml(company, false);
        var bottom = string.IsNullOrWhiteSpace(taxHtml) && string.IsNullOrWhiteSpace(totalsHtml)
            ? ""
            : $"<div class='report-bottom'><div>{taxHtml}</div><div>{totalsHtml}</div></div>";
        return $$$"""
<!doctype html><html><head><meta charset="utf-8"><title>{{{Enc(title)}}} {{{Enc(docNo)}}}</title>
<link rel="stylesheet" href="/css/report-print.css?v=a4-fit-20260817">
</head><body><div class="toolbar"><button class="print" onclick="window.print()">Print</button></div><div class="doc layout-{{{cleanLayout}}} layout-sheet"><div class="doc-head"><div class="brand-block">{{{logo}}}<div><div class="company">{{{Enc(Val(company,"CompanyName"))}}}</div><div class="muted">{{{Enc(Val(company,"AddressLine"))}}}</div><div class="muted">{{{Enc(Val(company,"PhoneNo"))}}} {{{Enc(Val(company,"Email"))}}}</div><div class="muted">Tax Reg: {{{Enc(Val(company,"TaxRegistrationNo"))}}}</div></div></div><div class="doc-title"><h1>{{{Enc(title)}}}</h1><div class="doc-no">{{{Enc(docNo)}}}</div><div class="layout-tag">{{{Enc(layoutName)}}}</div><div class="muted">Printed: {{{DateTime.Now:yyyy-MM-dd HH:mm}}}</div></div></div>{{{headerHtml}}}{{{tableHtml}}}{{{bottom}}}{{{footerHtml}}}<div class="footer-note">This is a system generated document from InterNex Cloud. Verify posting accounts and tax setup before statutory submission.</div></div></body></html>
""";
    }

    private static string HeaderBlocks(params (string Label, string Value)[] items)
    {
        var sb = new StringBuilder("<div class='grid'>");
        foreach (var i in items)
            sb.Append($"<div class='box'><div class='label'>{Enc(i.Label)}</div><div class='value'>{Enc(i.Value)}</div></div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string LinesTable(List<Dictionary<string, object?>> rows, string[] cols)
    {
        var sb = new StringBuilder("<table><thead><tr>");
        foreach (var c in cols) sb.Append($"<th>{Enc(Pretty(c))}</th>");
        sb.Append("</tr></thead><tbody>");
        if (rows.Count == 0) sb.Append($"<tr><td colspan='{cols.Length}' class='muted'>No lines found.</td></tr>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var c in cols)
            {
                var v = IsMoney(c) ? Money(row,c) : c.Contains("Date") ? DateVal(row,c) : c.Equals("Quantity", StringComparison.OrdinalIgnoreCase) ? Qty(row,c) : Val(row,c);
                sb.Append($"<td>{Enc(v)}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string TaxSummary(List<Dictionary<string, object?>> rows)
    {
        var tax = rows.Sum(x => DecimalVal(x,"TaxAmount"));
        var taxable = rows.Sum(x => DecimalVal(x,"LineTotal") - DecimalVal(x,"TaxAmount"));
        return $"<div class='tax-summary'><h3>Tax Summary</h3><div><span>Taxable Amount</span><b>{taxable:N2}</b></div><div><span>Tax Amount</span><b>{tax:N2}</b></div></div>";
    }

    private static string Totals(Dictionary<string, object?> row)
    {
        string[] keys = { "SubTotal", "DiscountAmount", "TaxAmount", "GrandTotal", "PaidAmount", "BalanceAmount", "ChangeAmount" };
        var sb = new StringBuilder("<div class='total'>");
        foreach (var k in keys)
        {
            if (!row.ContainsKey(k)) continue;
            var css = k == "GrandTotal" ? " class='grand'" : "";
            sb.Append($"<div{css}><span>{Enc(Pretty(k))}</span><b>{Enc(Money(row,k))}</b></div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string LogoHtml(Dictionary<string, object?> company, bool receipt)
    {
        if (company.TryGetValue("LogoImage", out var logoObj) && logoObj is byte[] bytes && bytes.Length > 0)
        {
            var cls = receipt ? "receipt-logo" : "company-logo";
            return $"<img class='{cls}' src='data:image/png;base64,{Convert.ToBase64String(bytes)}' alt='Company Logo'>";
        }
        var name = Val(company, "CompanyName");
        var initials = string.Join("", name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0].ToString())).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(initials)) initials = "PN";
        if (receipt) return string.Empty;
        return $"<div class='logo-placeholder'>{Enc(initials)}</div>";
    }

    private static string CleanLayout(string? layout)
    {
        var l = (layout ?? "bc").Trim().ToLowerInvariant();
        return l is "standard" or "compact" ? l : "bc";
    }

    private static string StandardFooter(string left, string right) => $"<div class='sign-row'><span>{Enc(left)}</span><span>{Enc(right)}</span></div>";
    private static string NotFoundHtml(string msg) => $"<html><body><h2>{Enc(msg)}</h2></body></html>";
    private static string Val(Dictionary<string, object?> r, string key) => r.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) ?? "" : "";
    private static decimal DecimalVal(Dictionary<string, object?> r, string key) => r.TryGetValue(key, out var v) && v != null && decimal.TryParse(Convert.ToString(v), out var d) ? d : 0m;
    private static string DateVal(Dictionary<string, object?> r, string key) => r.TryGetValue(key, out var v) && v != null && DateTime.TryParse(Convert.ToString(v), out var d) ? d.ToString("yyyy-MM-dd") : Val(r,key);
    private static string Money(Dictionary<string, object?> r, string key) => DecimalVal(r,key).ToString("N2");
    private static string Qty(Dictionary<string, object?> r, string key) => DecimalVal(r,key).ToString("N2");
    private static string Pretty(string s) => string.Concat(s.Select((ch,i)=> i>0 && char.IsUpper(ch) ? " "+ch : ch.ToString()));
    private static bool IsMoney(string c) => c.Contains("Amount") || c.Contains("Total") || c.Contains("Price") || c.Contains("Debit") || c.Contains("Credit") || c.Contains("Balance") || c.Contains("Cost");
    private static string Enc(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

    private static string AmountInWords(decimal amount)
    {
        var whole = (long)Math.Floor(Math.Abs(amount));
        var paise = (int)Math.Round((Math.Abs(amount) - whole) * 100m, MidpointRounding.AwayFromZero);
        if (paise == 100) { whole++; paise = 0; }
        var words = whole == 0 ? "Zero" : ConvertWholeNumber(whole);
        var result = "Rupee " + words;
        if (paise > 0) result += " and " + ConvertWholeNumber(paise) + " Paisa";
        return result + " Only";
    }

    private static string ConvertWholeNumber(long n)
    {
        if (n == 0) return "Zero";
        string[] ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
        string[] tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };
        string WordsUnderThousand(long x)
        {
            var parts = new List<string>();
            if (x >= 100) { parts.Add(ones[x / 100] + " Hundred"); x %= 100; }
            if (x >= 20) { parts.Add(tens[x / 10] + (x % 10 == 0 ? "" : " " + ones[x % 10])); }
            else if (x > 0) parts.Add(ones[x]);
            return string.Join(" ", parts);
        }
        var scales = new (long Value, string Name)[] { (1_000_000_000, "Billion"), (1_000_000, "Million"), (1_000, "Thousand") };
        var chunks = new List<string>();
        foreach (var (value, name) in scales)
        {
            if (n < value) continue;
            var q = n / value;
            chunks.Add(WordsUnderThousand(q) + " " + name);
            n %= value;
        }
        if (n > 0) chunks.Add(WordsUnderThousand(n));
        return string.Join(" ", chunks);
    }
}
