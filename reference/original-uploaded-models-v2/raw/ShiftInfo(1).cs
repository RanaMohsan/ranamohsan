using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayNex_POS_B1.Models;

public class SalesInvoiceLine : INotifyPropertyChanged
{
    private decimal _quantity = 1;
    private decimal _unitPrice;
    private decimal _discountPercent;
    private decimal _taxPercent;

    public int SalesInvoiceLineId { get; set; }
    public int SalesInvoiceId { get; set; }
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitCost { get; set; }
    public bool DiscountAllowed { get; set; } = true;
    public decimal StockOnHand { get; set; }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value <= 0 ? 1 : value;
            NotifyTotals();
        }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            _unitPrice = value < 0 ? 0 : value;
            NotifyTotals();
        }
    }

    public decimal DiscountPercent
    {
        get => _discountPercent;
        set
        {
            _discountPercent = value < 0 ? 0 : value > 100 ? 100 : value;
            NotifyTotals();
        }
    }

    public decimal TaxPercent
    {
        get => _taxPercent;
        set
        {
            _taxPercent = value < 0 ? 0 : value;
            NotifyTotals();
        }
    }
    public bool TaxInclusive { get; set; }

    public decimal Gross => Math.Round(Quantity * UnitPrice, 2);
    public decimal DiscountAmount => Math.Round(Gross * DiscountPercent / 100m, 2);
    public decimal NetAmount => Math.Round(Gross - DiscountAmount, 2);
    public decimal TaxAmount => TaxInclusive
        ? Math.Round(NetAmount - (NetAmount / (1m + TaxPercent / 100m)), 2)
        : Math.Round(NetAmount * TaxPercent / 100m, 2);
    public decimal LineTotal => TaxInclusive ? NetAmount : Math.Round(NetAmount + TaxAmount, 2);
    public decimal LineCostTotal => Math.Round(Quantity * UnitCost, 2);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(UnitPrice));
        OnPropertyChanged(nameof(DiscountPercent));
        OnPropertyChanged(nameof(TaxPercent));
        OnPropertyChanged(nameof(Gross));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(NetAmount));
        OnPropertyChanged(nameof(TaxAmount));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(LineCostTotal));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class SalesInvoiceSummary
{
    public int SalesInvoiceId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
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

public class CustomerLedgerEntry
{
    public int CustomerLedgerEntryId { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime PostingDate { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class CustomerPayment
{
    public int PaymentId { get; set; }
    public string PaymentNo { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public string ReferenceNo { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
}
