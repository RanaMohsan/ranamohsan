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
    {
        await using var h = con.CreateCommand();
        h.CommandText = @"SELECT TOP 1 sh.SaleId,sh.InvoiceNo,sh.SaleDate,sh.SubTotal,sh.DiscountAmount,sh.TaxAmount,sh.GrandTotal,sh.PaidAmount,sh.ChangeAmount,c.CustomerName,s.StoreName,u.DisplayName UserName
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
        var logo = LogoHtml(company, true);
        var sb = new StringBuilder();
        foreach (var row in lines)
        {
            sb.Append($"<div class='r-line'><div>{Enc(Val(row,"ProductName"))}</div><div>{Enc(Qty(row,"Quantity"))} x {Enc(Money(row,"UnitPrice"))}</div><b>{Enc(Money(row,"LineTotal"))}</b></div>");
        }
        if (lines.Count == 0) sb.Append("<div class='r-line muted'>No lines found.</div>");
        return $$$"""
<!doctype html><html><head><meta charset="utf-8"><title>Receipt {{{Enc(Val(header,"InvoiceNo"))}}}</title>
<style>
body{font-family:'Segoe UI',Arial,sans-serif;background:#eef2f7;margin:0;padding:18px;color:#111827}.receipt{width:78mm;margin:auto;background:#fff;padding:14px;border:1px solid #d0d5dd}.center{text-align:center}.receipt-logo{width:52px;height:52px;object-fit:contain;margin:0 auto 5px;display:block}.company{font-size:16px;font-weight:900;color:#004578}.muted{color:#667085;font-size:11px}.meta{border-top:1px dashed #9aa4b2;border-bottom:1px dashed #9aa4b2;margin:10px 0;padding:7px 0;font-size:11px}.meta div{display:flex;justify-content:space-between}.r-line{border-bottom:1px dashed #d0d5dd;padding:6px 0;font-size:11.5px}.r-line b{display:block;text-align:right;font-size:12px}.totals{margin-top:8px;border-top:1px solid #111827}.totals div{display:flex;justify-content:space-between;padding:3px 0;font-size:12px}.totals .grand{font-size:15px;font-weight:900;border-top:1px dashed #9aa4b2;margin-top:4px;padding-top:6px}.thanks{border-top:1px dashed #9aa4b2;margin-top:10px;padding-top:8px;font-size:11px}.print{display:block;width:78mm;margin:0 auto 8px;background:#004578;color:white;border:0;border-radius:4px;padding:8px;font-weight:700}@media print{body{background:white;margin:0;padding:0}.receipt{border:0;width:76mm}.print{display:none}@page{size:80mm auto;margin:3mm}}
</style></head><body><button class="print" onclick="window.print()">Print Receipt</button><div class="receipt"><div class="center">{{{logo}}}<div class="company">{{{Enc(Val(company,"CompanyName"))}}}</div><div class="muted">{{{Enc(Val(company,"AddressLine"))}}}</div><div class="muted">{{{Enc(Val(company,"PhoneNo"))}}}</div><b>COUNTER SALES RECEIPT</b></div><div class="meta"><div><span>No.</span><b>{{{Enc(Val(header,"InvoiceNo"))}}}</b></div><div><span>Date</span><span>{{{Enc(DateVal(header,"SaleDate"))}}}</span></div><div><span>Store</span><span>{{{Enc(Val(header,"StoreName"))}}}</span></div><div><span>Cashier</span><span>{{{Enc(Val(header,"UserName"))}}}</span></div></div>{{{sb}}}<div class="totals"><div><span>Subtotal</span><b>{{{Enc(Money(header,"SubTotal"))}}}</b></div><div><span>Discount</span><b>{{{Enc(Money(header,"DiscountAmount"))}}}</b></div><div><span>Tax</span><b>{{{Enc(Money(header,"TaxAmount"))}}}</b></div><div class="grand"><span>Total</span><b>{{{Enc(Money(header,"GrandTotal"))}}}</b></div><div><span>Paid</span><b>{{{Enc(Money(header,"PaidAmount"))}}}</b></div><div><span>Change</span><b>{{{Enc(Money(header,"ChangeAmount"))}}}</b></div></div><div class="thanks center">Thank you for shopping with us.<br>Powered by PayNex Cloud</div></div></body></html>
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
        return $$$"""
<!doctype html><html><head><meta charset="utf-8"><title>{{{Enc(title)}}} {{{Enc(docNo)}}}</title>
<style>
:root{--blue:#004578;--blue2:#0078d4;--ink:#172033;--muted:#667085;--line:#d8e0e8;--soft:#f4f8fc}body{font-family:'Segoe UI',Arial,sans-serif;color:var(--ink);margin:0;background:#eef2f7;padding:22px}.toolbar{text-align:right;margin:0 auto 10px;max-width:980px}.print{background:var(--blue);color:white;border:0;border-radius:4px;padding:8px 14px;font-weight:700}.doc{max-width:980px;margin:auto;background:white;padding:30px 34px;border:1px solid #d0d5dd;box-shadow:0 10px 28px rgba(16,24,40,.12)}.doc.layout-bc{border-top:8px solid var(--blue2)}.doc.layout-compact{padding:20px 24px}.doc-head{display:grid;grid-template-columns:1fr 300px;gap:24px;border-bottom:3px solid var(--blue);padding-bottom:16px;margin-bottom:18px}.brand-block{display:grid;grid-template-columns:72px 1fr;gap:14px;align-items:center}.company-logo{width:66px;height:66px;border:1px solid var(--line);border-radius:6px;object-fit:contain;background:#fff}.logo-placeholder{width:66px;height:66px;border:1px solid var(--line);border-radius:6px;background:var(--soft);display:grid;place-items:center;color:var(--blue);font-weight:900}.company{font-size:24px;font-weight:900;color:var(--blue);letter-spacing:-.02em}.muted{color:var(--muted);font-size:12px}.doc-title{text-align:right}.doc-title h1{font-size:25px;margin:0;color:#111827}.doc-no{display:inline-block;background:#edf6ff;border:1px solid #bad7f0;border-radius:999px;padding:5px 10px;margin-top:6px;color:var(--blue);font-weight:800}.layout-tag{display:inline-block;margin-top:7px;padding:3px 8px;border-radius:999px;background:#f2f4f7;color:#475467;font-size:10px;font-weight:800}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:9px;margin:14px 0}.box{border:1px solid var(--line);border-radius:5px;padding:8px 10px;background:#fbfcfe}.label{font-size:10px;color:var(--muted);text-transform:uppercase;font-weight:800;letter-spacing:.06em}.value{font-weight:800;margin-top:3px;color:var(--ink)}.remarks{border:1px solid var(--line);background:#fbfcfe;border-radius:5px;padding:9px;margin:10px 0}table{width:100%;border-collapse:collapse;margin-top:14px}th,td{border-bottom:1px solid var(--line);text-align:left;padding:7px 8px;font-size:12px;vertical-align:top}th{background:#eef4fb;color:#344054;text-transform:uppercase;font-size:10.5px;letter-spacing:.04em}td:nth-last-child(-n+4),th:nth-last-child(-n+4){text-align:right}.layout-compact th,.layout-compact td{font-size:10.5px;padding:5px 6px}.report-bottom{display:grid;grid-template-columns:1fr 340px;gap:22px;margin-top:16px}.report-bottom:empty{display:none}.tax-summary,.total{border:1px solid var(--line);border-radius:5px;overflow:hidden;background:#fff}.tax-summary h3{margin:0;background:#f7fafc;padding:8px 10px;font-size:12px}.tax-summary div,.total div{display:flex;justify-content:space-between;border-bottom:1px solid var(--line);padding:7px 10px;font-size:12px}.total div:last-child,.tax-summary div:last-child{border-bottom:0}.total .grand{background:#eef4fb;color:var(--blue);font-size:15px;font-weight:900}.footer-note{margin-top:18px;border-top:1px solid var(--line);padding-top:10px;color:var(--muted);font-size:11px}.sign-row{display:flex;justify-content:space-between;margin-top:58px}.sign-row span{border-top:1px solid #111827;padding-top:7px;width:220px;text-align:center;font-size:12px}@media(max-width:760px){.doc-head,.brand-block,.grid,.report-bottom{grid-template-columns:1fr}.doc-title{text-align:left}}@media print{body{background:white;margin:0;padding:0}.toolbar{display:none}.doc{box-shadow:none;border:0;padding:14mm 12mm;max-width:none}.doc.layout-bc{border-top:0}.doc-head{break-inside:avoid}@page{size:A4;margin:8mm}}
</style></head><body><div class="toolbar"><button class="print" onclick="window.print()">Print</button></div><div class="doc layout-{{{cleanLayout}}}"><div class="doc-head"><div class="brand-block">{{{logo}}}<div><div class="company">{{{Enc(Val(company,"CompanyName"))}}}</div><div class="muted">{{{Enc(Val(company,"AddressLine"))}}}</div><div class="muted">{{{Enc(Val(company,"PhoneNo"))}}} {{{Enc(Val(company,"Email"))}}}</div><div class="muted">Tax Reg: {{{Enc(Val(company,"TaxRegistrationNo"))}}}</div></div></div><div class="doc-title"><h1>{{{Enc(title)}}}</h1><div class="doc-no">{{{Enc(docNo)}}}</div><div class="layout-tag">{{{Enc(layoutName)}}}</div><div class="muted">Printed: {{{DateTime.Now:yyyy-MM-dd HH:mm}}}</div></div></div>{{{headerHtml}}}{{{tableHtml}}}<div class="report-bottom"><div>{{{taxHtml}}}</div><div>{{{totalsHtml}}}</div></div>{{{footerHtml}}}<div class="footer-note">This is a system generated document from PayNex Cloud. Verify posting accounts and tax setup before statutory submission.</div></div></body></html>
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
}
