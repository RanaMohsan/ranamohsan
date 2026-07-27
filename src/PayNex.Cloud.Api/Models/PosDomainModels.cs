using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PayNex.Cloud.Api.Models;

// Cloud-safe model classes adapted from the original WPF POS models.
// WPF-only UI types such as ImageSource and Brush were replaced with API-friendly scalar properties.

public class PostingSetup
{
    public string CashAccount { get; set; } = "1000";
    public string BankAccount { get; set; } = "1010";
    public string ReceivableAccount { get; set; } = "1100";
    public string InventoryAccount { get; set; } = "1200";
    public string InputTaxAccount { get; set; } = "1300";
    public string PayableAccount { get; set; } = "2000";
    public string OutputTaxAccount { get; set; } = "2100";
    public string OpeningBalanceAccount { get; set; } = "3000";
    public string SalesAccount { get; set; } = "4000";
    public string SalesReturnAccount { get; set; } = "4010";
    public string CogsAccount { get; set; } = "5000";
    public string StockAdjustmentAccount { get; set; } = "5100";
    public decimal CashierDiscountLimit { get; set; } = 5m;
    public bool BlockNegativeStock { get; set; } = true;
    public string CostingMethod { get; set; } = "Average";
}

public record PostingLine(string AccountNo, decimal Debit, decimal Credit, string Description);

public class FinancialReportLine
{
    public string AccountNo { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
}

public class AgingLine
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Current { get; set; }
    public decimal Days30 { get; set; }
    public decimal Days60 { get; set; }
    public decimal Days90 { get; set; }
    public decimal Over90 { get; set; }
    public decimal Total => Current + Days30 + Days60 + Days90 + Over90;
}

public class StockValuationLine
{
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Value { get; set; }
    public decimal ReorderLevel { get; set; }
    public string Status => Quantity <= ReorderLevel ? "REORDER" : "OK";
}

public class TaxReportLine
{
    public string TaxType { get; set; } = string.Empty;
    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
}

public class DailyProfitLine
{
    public DateTime PostingDate { get; set; }
    public decimal Sales { get; set; }
    public decimal Tax { get; set; }
    public decimal Cost { get; set; }
    public decimal GrossProfit => Sales - Tax - Cost;
}

public class LookupOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Cost { get; set; }
    public string DisplayName => Quantity == 0 ? Name : $"{Name} (Qty: {Quantity:N3})";
}

public class TaxGroupSetup
{
    public int TaxGroupId { get; set; }
    public string TaxGroupName { get; set; } = string.Empty;
    public decimal TaxPercent { get; set; }
    public bool IsInclusive { get; set; }
}

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
        set { _quantity = value <= 0 ? 1 : value; NotifyTotals(); }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set { _unitPrice = value < 0 ? 0 : value; NotifyTotals(); }
    }

    public decimal DiscountPercent
    {
        get => _discountPercent;
        set { _discountPercent = value < 0 ? 0 : value > 100 ? 100 : value; NotifyTotals(); }
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
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class CompanyInformation
{
    public int CompanyInformationId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string TaxRegistrationNo { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;
    public byte[]? LogoImage { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Customer
{
    public int CustomerId { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public decimal CreditLimit { get; set; }
    public decimal LoyaltyPoints { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => $"{CustomerCode} - {CustomerName}";
    public override string ToString() => DisplayName;
}

public enum DynamicFieldType
{
    Text,
    Number,
    Decimal,
    CheckBox
}

public class DynamicField
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DynamicFieldType FieldType { get; set; }
    public object? Value { get; set; }
    public bool IsRequired { get; set; }
}

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

public class PaymentLine
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
}

public class Product
{
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string BrandName { get; set; } = string.Empty;
    public string UnitOfMeasure { get; set; } = "PCS";
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal RetailPrice { get; set; }
    public decimal TaxPercent { get; set; }
    public bool TaxInclusive { get; set; }
    public bool DiscountAllowed { get; set; }
    public decimal MinStockLevel { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal StockOnHand { get; set; }
    public string ImagePath { get; set; } = string.Empty;
    public byte[]? ProductImage { get; set; }
    public bool IsActive { get; set; } = true;
    public bool HasImage => (ProductImage != null && ProductImage.Length > 0) || !string.IsNullOrWhiteSpace(ImagePath);
    public string? ProductImageBase64 => ProductImage == null || ProductImage.Length == 0 ? null : Convert.ToBase64String(ProductImage);
}

public class DashboardMetric
{
    public string Title { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Hint { get; set; } = string.Empty;
}

public class TopProductReport
{
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
}

public class MonthlyAnalysisPoint
{
    public DateTime MonthStart { get; set; }
    public string MonthLabel { get; set; } = string.Empty;
    public decimal SalesAmount { get; set; }
    public decimal PurchaseAmount { get; set; }
    public decimal ProfitAmount { get; set; }
    public decimal MarginPercent => SalesAmount == 0 ? 0 : (ProfitAmount / SalesAmount) * 100;
}

public class MonthlyChartBarGroup
{
    public string MonthLabel { get; set; } = string.Empty;
    public decimal SalesAmount { get; set; }
    public decimal PurchaseAmount { get; set; }
    public decimal ProfitAmount { get; set; }
    public double SalesHeight { get; set; }
    public double PurchaseHeight { get; set; }
    public double ProfitHeight { get; set; }
    public string SalesText => $"Rs. {SalesAmount:N0}";
    public string PurchaseText => $"Rs. {PurchaseAmount:N0}";
    public string ProfitText => $"Rs. {ProfitAmount:N0}";
    public string MarginText => $"{(SalesAmount == 0 ? 0 : (ProfitAmount / SalesAmount) * 100):N1}%";
}

public class MonthlyRowVm
{
    public string MonthLabel { get; set; } = string.Empty;
    public decimal SalesAmount { get; set; }
    public decimal PurchaseAmount { get; set; }
    public decimal ProfitAmount { get; set; }
    public decimal MarginPercent { get; set; }
    public string ProfitClass { get; set; } = string.Empty;
    public string MarginClass { get; set; } = string.Empty;
}

public class TopProductRowVm
{
    public int Rank { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public string AmountClass { get; set; } = string.Empty;
}

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

public class SaleResult
{
    public int SaleId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal ChangeAmount { get; set; }
}

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

    public decimal Quantity { get => _quantity; set { _quantity = value <= 0 ? 1 : value; NotifyTotals(); } }
    public decimal UnitPrice { get => _unitPrice; set { _unitPrice = value < 0 ? 0 : value; NotifyTotals(); } }
    public decimal DiscountPercent { get => _discountPercent; set { _discountPercent = value < 0 ? 0 : value > 100 ? 100 : value; NotifyTotals(); } }
    public decimal TaxPercent { get => _taxPercent; set { _taxPercent = value < 0 ? 0 : value; NotifyTotals(); } }
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
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

public class ShiftInfo
{
    public int ShiftId { get; set; }
    public int StoreId { get; set; }
    public int TerminalId { get; set; }
    public int UserId { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal ClosingCash { get; set; }
    public decimal DifferenceAmount { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public class UserAccount
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public int StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public bool IsAdmin => RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase);
    public bool IsManager => RoleName.Equals("Manager", StringComparison.OrdinalIgnoreCase) || IsAdmin;
}
