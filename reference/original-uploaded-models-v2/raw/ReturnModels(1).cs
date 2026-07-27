using System.Windows.Media;

namespace PayNex_POS_B1.Models;

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

// Row view-models for the colour-formatted matrix tables.
public class MonthlyRowVm
{
    public string MonthLabel { get; set; } = string.Empty;
    public decimal SalesAmount { get; set; }
    public decimal PurchaseAmount { get; set; }
    public decimal ProfitAmount { get; set; }
    public decimal MarginPercent { get; set; }
    public Brush ProfitBrush { get; set; } = Brushes.Transparent;
    public Brush MarginBrush { get; set; } = Brushes.Transparent;
}

public class TopProductRowVm
{
    public int Rank { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
    public Brush AmountBrush { get; set; } = Brushes.Transparent;
}
