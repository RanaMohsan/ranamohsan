using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayNex_POS_B1.Models;

public class SaleLineForReturn : INotifyPropertyChanged
{
    private decimal _returnQuantity;

    public int SaleId { get; set; }
    public int SaleLineId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal SoldQuantity { get; set; }
    public decimal AlreadyReturnedQuantity { get; set; }
    public decimal AvailableToReturn => SoldQuantity - AlreadyReturnedQuantity;
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal UnitCost { get; set; }

    public decimal ReturnQuantity
    {
        get => _returnQuantity;
        set
        {
            _returnQuantity = value < 0 ? 0 : value > AvailableToReturn ? AvailableToReturn : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefundAmount));
        }
    }

    public decimal Gross => Math.Round(ReturnQuantity * UnitPrice, 2);
    public decimal DiscountAmount => Math.Round(Gross * DiscountPercent / 100m, 2);
    public decimal NetAmount => Math.Round(Gross - DiscountAmount, 2);
    public decimal TaxAmount => TaxInclusive
        ? Math.Round(NetAmount - (NetAmount / (1m + TaxPercent / 100m)), 2)
        : Math.Round(NetAmount * TaxPercent / 100m, 2);
    public decimal RefundAmount => TaxInclusive ? NetAmount : Math.Round(NetAmount + TaxAmount, 2);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
