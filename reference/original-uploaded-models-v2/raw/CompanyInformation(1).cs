using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayNex_POS_B1.Models;

public class CartLine : INotifyPropertyChanged
{
    private decimal _quantity = 1;
    private decimal _unitPrice;
    private decimal _discountPercent;
    

    public int ProductId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitCost { get; set; }
    public decimal TaxPercent { get; set; }
    public bool TaxInclusive { get; set; }
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

    public decimal Gross => Math.Round(Quantity * UnitPrice, 2);
    public decimal DiscountAmount => Math.Round(Gross * DiscountPercent / 100m, 2);
    public decimal NetAmount => Math.Round(Gross - DiscountAmount, 2);
    public decimal TaxAmount => TaxInclusive
        ? Math.Round(NetAmount - (NetAmount / (1m + TaxPercent / 100m)), 2)
        : Math.Round(NetAmount * TaxPercent / 100m, 2);
    public decimal LineTotal => TaxInclusive ? NetAmount : Math.Round(NetAmount + TaxAmount, 2);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(UnitPrice));
        OnPropertyChanged(nameof(DiscountPercent));
        OnPropertyChanged(nameof(Gross));
        OnPropertyChanged(nameof(DiscountAmount));
        OnPropertyChanged(nameof(NetAmount));
        OnPropertyChanged(nameof(TaxAmount));
        OnPropertyChanged(nameof(LineTotal));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
