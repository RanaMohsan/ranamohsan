using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;

namespace PayNex_POS_B1.Repositories;

public class ReportRepository
{
    public List<DashboardMetric> GetDashboardMetrics()
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
WITH CurrentMonth AS (
    SELECT DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1) AS MonthStart,
           DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)) AS NextMonthStart
)
SELECT
    ISNULL((SELECT SUM(GrandTotal) FROM SalesHeader sh CROSS JOIN CurrentMonth m WHERE sh.Status='Posted' AND sh.SaleDate >= m.MonthStart AND sh.SaleDate < m.NextMonthStart), 0)
      + ISNULL((SELECT SUM(GrandTotal) FROM SalesInvoiceHeader si CROSS JOIN CurrentMonth m WHERE si.Status='Posted' AND si.InvoiceDate >= m.MonthStart AND si.InvoiceDate < m.NextMonthStart), 0) AS TotalSales,
    ISNULL((SELECT SUM(GrandTotal) FROM PurchaseInvoiceHeader ph CROSS JOIN CurrentMonth m WHERE ph.Status='Posted' AND ph.InvoiceDate >= m.MonthStart AND ph.InvoiceDate < m.NextMonthStart), 0) AS TotalPurchase,
    ISNULL((SELECT SUM(LineTotal - (Quantity * UnitCost))
            FROM SalesLines sl
            INNER JOIN SalesHeader sh ON sh.SaleId = sl.SaleId
            CROSS JOIN CurrentMonth m
            WHERE sh.Status='Posted' AND sh.SaleDate >= m.MonthStart AND sh.SaleDate < m.NextMonthStart), 0)
      + ISNULL((SELECT SUM(LineTotal - (Quantity * UnitCost))
                FROM SalesInvoiceLines sl
                INNER JOIN SalesInvoiceHeader si ON si.SalesInvoiceId = sl.SalesInvoiceId
                CROSS JOIN CurrentMonth m
                WHERE si.Status='Posted' AND si.InvoiceDate >= m.MonthStart AND si.InvoiceDate < m.NextMonthStart), 0) AS TotalProfit,
    ISNULL((SELECT COUNT(1) FROM SalesHeader sh CROSS JOIN CurrentMonth m WHERE sh.Status='Posted' AND sh.SaleDate >= m.MonthStart AND sh.SaleDate < m.NextMonthStart), 0)
      + ISNULL((SELECT COUNT(1) FROM SalesInvoiceHeader si CROSS JOIN CurrentMonth m WHERE si.Status='Posted' AND si.InvoiceDate >= m.MonthStart AND si.InvoiceDate < m.NextMonthStart), 0) AS TotalInvoices,
    ISNULL((SELECT COUNT(1) FROM PurchaseInvoiceHeader ph CROSS JOIN CurrentMonth m WHERE ph.Status='Posted' AND ph.InvoiceDate >= m.MonthStart AND ph.InvoiceDate < m.NextMonthStart), 0) AS TotalPurchases,
    ISNULL((SELECT AVG(v.AmountPerDay) FROM (
        SELECT CAST(sh.SaleDate AS DATE) AS SaleDay, SUM(sh.GrandTotal) AS AmountPerDay
        FROM SalesHeader sh CROSS JOIN CurrentMonth m
        WHERE sh.Status='Posted' AND sh.SaleDate >= m.MonthStart AND sh.SaleDate < m.NextMonthStart
        GROUP BY CAST(sh.SaleDate AS DATE)
        UNION ALL
        SELECT CAST(si.InvoiceDate AS DATE) AS SaleDay, SUM(si.GrandTotal) AS AmountPerDay
        FROM SalesInvoiceHeader si CROSS JOIN CurrentMonth m
        WHERE si.Status='Posted' AND si.InvoiceDate >= m.MonthStart AND si.InvoiceDate < m.NextMonthStart
        GROUP BY CAST(si.InvoiceDate AS DATE)
    ) v), 0) AS AvgSalesPerDay";

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new List<DashboardMetric>();

        var totalSales = SqlMap.Decimal(r, "TotalSales");
        var totalProfit = SqlMap.Decimal(r, "TotalProfit");
        var margin = totalSales == 0 ? 0 : (totalProfit / totalSales) * 100;

        return new List<DashboardMetric>
        {
            new() { Title = "Monthly Sales", Value = $"Rs. {totalSales:N2}", Hint = "Current month sales" },
            new() { Title = "Monthly Purchase", Value = $"Rs. {SqlMap.Decimal(r, "TotalPurchase"):N2}", Hint = "Current month purchases" },
            new() { Title = "Monthly Profit", Value = $"Rs. {totalProfit:N2}", Hint = "Gross profit" },
            new() { Title = "Sales Invoices", Value = SqlMap.Int(r, "TotalInvoices").ToString("N0"), Hint = "Posted sales documents" },
            new() { Title = "Purchase Invoices", Value = SqlMap.Int(r, "TotalPurchases").ToString("N0"), Hint = "Posted purchase documents" },
            new() { Title = "Gross Margin", Value = $"{margin:N1}%", Hint = $"Avg / day: Rs. {SqlMap.Decimal(r, "AvgSalesPerDay"):N2}" }
        };
    }

    public List<MonthlyAnalysisPoint> GetMonthlyAnalysis(int months = 12)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $@"
WITH n AS (
    SELECT 0 AS n
    UNION ALL
    SELECT n + 1 FROM n WHERE n + 1 < {months}
),
months AS (
    SELECT DATEFROMPARTS(YEAR(DATEADD(MONTH, -n, GETDATE())), MONTH(DATEADD(MONTH, -n, GETDATE())), 1) AS MonthStart
    FROM n
)
SELECT
    MonthStart,
    FORMAT(MonthStart, 'MMM yy') AS MonthLabel,
    ISNULL((SELECT SUM(GrandTotal) FROM SalesHeader sh WHERE sh.Status='Posted' AND sh.SaleDate >= MonthStart AND sh.SaleDate < DATEADD(MONTH, 1, MonthStart)), 0)
      + ISNULL((SELECT SUM(GrandTotal) FROM SalesInvoiceHeader si WHERE si.Status='Posted' AND si.InvoiceDate >= MonthStart AND si.InvoiceDate < DATEADD(MONTH, 1, MonthStart)), 0) AS SalesAmount,
    ISNULL((SELECT SUM(GrandTotal) FROM PurchaseInvoiceHeader ph WHERE ph.Status='Posted' AND ph.InvoiceDate >= MonthStart AND ph.InvoiceDate < DATEADD(MONTH, 1, MonthStart)), 0) AS PurchaseAmount,
    ISNULL((SELECT SUM(LineTotal - (Quantity * UnitCost))
            FROM SalesLines sl
            INNER JOIN SalesHeader sh ON sh.SaleId = sl.SaleId
            WHERE sh.Status='Posted' AND sh.SaleDate >= MonthStart AND sh.SaleDate < DATEADD(MONTH, 1, MonthStart)), 0)
      + ISNULL((SELECT SUM(LineTotal - (Quantity * UnitCost))
                FROM SalesInvoiceLines sl
                INNER JOIN SalesInvoiceHeader si ON si.SalesInvoiceId = sl.SalesInvoiceId
                WHERE si.Status='Posted' AND si.InvoiceDate >= MonthStart AND si.InvoiceDate < DATEADD(MONTH, 1, MonthStart)), 0) AS ProfitAmount
FROM months
ORDER BY MonthStart
OPTION (MAXRECURSION 100);";

        var list = new List<MonthlyAnalysisPoint>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MonthlyAnalysisPoint
            {
                MonthStart = r.GetDateTime(r.GetOrdinal("MonthStart")),
                MonthLabel = SqlMap.String(r, "MonthLabel"),
                SalesAmount = SqlMap.Decimal(r, "SalesAmount"),
                PurchaseAmount = SqlMap.Decimal(r, "PurchaseAmount"),
                ProfitAmount = SqlMap.Decimal(r, "ProfitAmount")
            });
        }
        return list;
    }

    public List<TopProductReport> GetTopProducts()
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 10 ProductName, SUM(Quantity) Quantity, SUM(LineTotal) Amount
FROM (
    SELECT sl.ProductName, sl.Quantity, sl.LineTotal
    FROM SalesLines sl
    INNER JOIN SalesHeader sh ON sh.SaleId = sl.SaleId
    WHERE sh.Status='Posted'
    UNION ALL
    SELECT sl.ProductName, sl.Quantity, sl.LineTotal
    FROM SalesInvoiceLines sl
    INNER JOIN SalesInvoiceHeader sh ON sh.SalesInvoiceId = sl.SalesInvoiceId
    WHERE sh.Status='Posted'
) x
GROUP BY ProductName
ORDER BY SUM(LineTotal) DESC";
        var list = new List<TopProductReport>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TopProductReport
            {
                ProductName = SqlMap.String(r, "ProductName"),
                Quantity = SqlMap.Decimal(r, "Quantity"),
                Amount = SqlMap.Decimal(r, "Amount")
            });
        }
        return list;
    }
}
