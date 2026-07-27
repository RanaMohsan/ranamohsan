namespace PayNex_POS_B1.Models;

public class Vendor
{
    public int VendorId { get; set; }
    public string VendorCode { get; set; } = string.Empty;
    public string VendorName { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public string PaymentTerms { get; set; } = string.Empty;
    public decimal OpeningBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => $"{VendorCode} - {VendorName}";
}

public class ChartAccount
{
    public int AccountId { get; set; }
    public string AccountNo { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string NormalBalance { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => $"{AccountNo} - {AccountName}";
}

public class PurchaseInvoiceLine
{
    public int PurchaseInvoiceLineId { get; set; }
    public int PurchaseInvoiceId { get; set; }
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TaxPercent { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal LineSubTotal => Math.Round(Quantity * UnitCost, 2);
    public decimal TaxAmount => TaxInclusive
        ? Math.Round(LineSubTotal - (LineSubTotal / (1m + TaxPercent / 100m)), 2)
        : Math.Round(LineSubTotal * TaxPercent / 100m, 2);
    public decimal LineTotal => TaxInclusive ? LineSubTotal : LineSubTotal + TaxAmount;
}

public class PurchaseInvoiceSummary
{
    public int PurchaseInvoiceId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public string VendorInvoiceNo { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public DateTime PostedAt { get; set; }
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
}

public class VendorLedgerEntry
{
    public int VendorLedgerEntryId { get; set; }
    public int VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public DateTime PostingDate { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class GLEntry
{
    public int GLEntryId { get; set; }
    public DateTime PostingDate { get; set; }
    public string AccountNo { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public string Description { get; set; } = string.Empty;
    public long? PostingBatchId { get; set; }
}

public class VendorPayment
{
    public int PaymentId { get; set; }
    public string PaymentNo { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public string ReferenceNo { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
}
