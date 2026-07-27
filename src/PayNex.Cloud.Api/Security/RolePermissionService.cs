using PayNex.Cloud.Api.Models;
using System.Text.Json;

namespace PayNex.Cloud.Api.Security;

public sealed class RolePermissionService
{
    private static readonly Dictionary<string, string[]> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/api/sales-invoices/post"] = new[] { "sales.postInvoice" },
        ["/api/sales-invoices/draft"] = new[] { "sales.createInvoice", "sales.editInvoice" },
        ["/api/purchase-invoices/draft"] = new[] { "purchase.createInvoice", "purchase.editInvoice" },
        ["/api/sales-quotes"] = new[] { "sales.createInvoice", "sales.editInvoice" },
        ["/api/sales-orders"] = new[] { "sales.createInvoice", "sales.editInvoice" },
        ["/api/sales"] = new[] { "sales.createInvoice" },
        ["/api/returns"] = new[] { "sales.createReturn" },
        ["/api/purchases"] = new[] { "purchase.createInvoice", "purchase.postInvoice" },
        ["/api/inventory/transfer"] = new[] { "inventory.transfer" },
        ["/api/inventory/adjust"] = new[] { "inventory.stockAdjustment" },
        ["/api/product-tax-discounts"] = new[] { "pricing.changeProductDiscount", "pricing.changeProductPrice" },
        ["/api/products"] = new[] { "inventory.createItems", "inventory.editItems" },
        ["/api/customer-payments"] = new[] { "finance.createExpense" },
        ["/api/vendor-payments"] = new[] { "finance.createExpense" },
        ["/api/company"] = new[] { "system.companySettings" },
        ["/api/branches"] = new[] { "system.branchManagement" },
        ["/api/users"] = new[] { "users.createUser", "users.editUser", "users.deleteUser", "users.resetPassword", "users.assignPermissions", "users.promoteCompanySuperAdmin" },
        ["/api/settings"] = new[] { "system.generalConfiguration" },
        ["/api/currencies"] = new[] { "system.generalConfiguration" },
        ["/api/tax-groups"] = new[] { "system.generalConfiguration" },
        ["/api/posting-setup"] = new[] { "system.generalConfiguration" },
        ["/api/backup"] = new[] { "system.backupRestore" },
        ["/api/restore"] = new[] { "system.backupRestore" }
    };

    public bool CanAccess(UserSession user, string method, string path, out string reason)
    {
        reason = string.Empty;
        if (IsAdmin(user)) return true;

        var map = ReadPermissionMap(user);

        if (path.StartsWith("/api/reports/expenses", StringComparison.OrdinalIgnoreCase))
        {
            if (map.TryGetValue("expenses.printReport", out var printAllowed) && printAllowed) return true;
            reason = "Print and Export Expense Report permission is required.";
            return false;
        }

        if (path.StartsWith("/api/expense-report", StringComparison.OrdinalIgnoreCase))
        {
            if (map.TryGetValue("expenses.viewReport", out var reportAllowed) && reportAllowed) return true;
            reason = "View Expense Report permission is required.";
            return false;
        }

        if (path.StartsWith("/api/expense-categories", StringComparison.OrdinalIgnoreCase))
        {
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var keys = new[] { "expenses.view", "expenses.create", "finance.createExpense", "expenses.edit", "expenses.manageCategories", "expenses.viewReport", "expenses.printReport" };
                if (keys.Any(k => map.TryGetValue(k, out var allowed) && allowed)) return true;
            }
            else if (map.TryGetValue("expenses.manageCategories", out var categoryAllowed) && categoryAllowed) return true;
            reason = "Expense Category permission is required.";
            return false;
        }

        if (path.StartsWith("/api/expenses", StringComparison.OrdinalIgnoreCase))
        {
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var keys = new[] { "expenses.view", "expenses.create", "finance.createExpense", "expenses.edit", "expenses.delete", "expenses.viewReport", "expenses.printReport" };
                if (keys.Any(k => map.TryGetValue(k, out var allowed) && allowed)) return true;
                reason = "View Expenses permission is required.";
                return false;
            }
            var required = method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ? "expenses.delete"
                : method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? "expenses.edit" : "expenses.create";
            if (map.TryGetValue(required, out var expenseAllowed) && expenseAllowed) return true;
            if (required == "expenses.create" && map.TryGetValue("finance.createExpense", out var legacyAllowed) && legacyAllowed) return true;
            reason = "You do not have permission to perform this expense operation.";
            return false;
        }

        if (path.StartsWith("/api/configuration-packages", StringComparison.OrdinalIgnoreCase))
        {
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !path.Contains("/export", StringComparison.OrdinalIgnoreCase))
            {
                var packageKeys = new[] { "configurationPackages.view", "configurationPackages.manage", "configurationPackages.export", "configurationPackages.import", "configurationPackages.apply" };
                if (packageKeys.Any(k => map.TryGetValue(k, out var allowed) && allowed)) return true;
                reason = "View Configuration Packages permission is required.";
                return false;
            }
            var required = method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                ? "configurationPackages.export"
                : path.Contains("/validate", StringComparison.OrdinalIgnoreCase)
                    ? "configurationPackages.import"
                    : path.Contains("/apply", StringComparison.OrdinalIgnoreCase)
                        ? "configurationPackages.apply"
                        : "configurationPackages.manage";
            if (map.TryGetValue(required, out var packageAllowed) && packageAllowed) return true;
            reason = "Configuration Package permission is required for this operation.";
            return false;
        }

        if (path.StartsWith("/api/branches/switch", StringComparison.OrdinalIgnoreCase)) return true;

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith("/api/reports", StringComparison.OrdinalIgnoreCase))
            {
                var required = path.Contains("/html", StringComparison.OrdinalIgnoreCase) || path.Contains("templates", StringComparison.OrdinalIgnoreCase) ? "reports.printReports" : "reports.viewReports";
                if (map.TryGetValue(required, out var allowed) && allowed) return true;
                reason = required == "reports.printReports" ? "Print Reports permission is required." : "View Reports permission is required.";
                return false;
            }
            if (path.StartsWith("/api/accounting/report", StringComparison.OrdinalIgnoreCase))
            {
                if (map.TryGetValue("finance.viewFinancialReports", out var allowed) && allowed) return true;
                reason = "View Financial Reports permission is required.";
                return false;
            }
            return true;
        }

        var rule = Rules.FirstOrDefault(x => path.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(rule.Key)) return true;

        if (rule.Value.Any(k => map.TryGetValue(k, out var allowed) && allowed)) return true;

        reason = "You do not have permission to perform this operation.";
        return false;
    }

    private static bool IsAdmin(UserSession user) =>
        user.IsCompanySuperAdmin ||
        user.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
        user.RoleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, bool> ReadPermissionMap(UserSession user)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(user.PermissionsJson ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
