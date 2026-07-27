namespace PayNex_POS_B1.Models;

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
