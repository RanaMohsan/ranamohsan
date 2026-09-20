using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public enum SubscriptionAccessPhase
{
    Active = 0,
    GraceWarn = 1,
    PostLocked = 2,
    LoginBlocked = 3,
}

public sealed record SubscriptionAccessState(
    SubscriptionAccessPhase Phase,
    int DaysPast,
    DateTime? ExpiryDate,
    string LicenseStatus,
    string Message)
{
    public bool CanLogin => Phase != SubscriptionAccessPhase.LoginBlocked;
    public bool CanPost => Phase is SubscriptionAccessPhase.Active or SubscriptionAccessPhase.GraceWarn;
    public bool ShouldShowRenewBanner => Phase is SubscriptionAccessPhase.GraceWarn
        or SubscriptionAccessPhase.PostLocked
        or SubscriptionAccessPhase.LoginBlocked;
    public bool IsExpired => DaysPast >= 1;
}

/// <summary>
/// Grace policy after Tenants.ExpiryDate:
/// Day 1+ warn / Expired status, Day 10+ block Post, Day 30+ block login.
/// </summary>
public sealed class SubscriptionAccessService
{
    public const int WarnAfterDays = 1;
    public const int PostLockAfterDays = 10;
    public const int LoginBlockAfterDays = 30;

    private readonly ConnectionFactory _db;

    public SubscriptionAccessService(ConnectionFactory db) => _db = db;

    public SubscriptionAccessState Evaluate(TenantInfo? tenant, DateTime? asOf = null)
    {
        if (tenant == null)
        {
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.LoginBlocked,
                LoginBlockAfterDays,
                null,
                "Unknown",
                "Company subscription could not be verified.");
        }

        return Evaluate(tenant.ExpiryDate, tenant.LicenseStatus, tenant.Status, asOf);
    }

    public SubscriptionAccessState Evaluate(
        DateTime? expiryDate,
        string? licenseStatus,
        string? companyStatus = "Active",
        DateTime? asOf = null)
    {
        var today = (asOf ?? DateTime.Today).Date;
        var status = (companyStatus ?? "Active").Trim();
        var license = (licenseStatus ?? "Active").Trim();

        if (string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Inactive", StringComparison.OrdinalIgnoreCase))
        {
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.LoginBlocked,
                LoginBlockAfterDays,
                expiryDate?.Date,
                license,
                "Company is inactive. Please contact InterNex Super Admin.");
        }

        if (string.Equals(license, "Suspended", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(license, "Blocked", StringComparison.OrdinalIgnoreCase))
        {
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.LoginBlocked,
                LoginBlockAfterDays,
                expiryDate?.Date,
                license,
                $"Company license is {license}. Please renew subscription.");
        }

        if (!expiryDate.HasValue)
        {
            // No expiry on file — treat as active (legacy / owner-managed).
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.Active,
                0,
                null,
                license,
                "Subscription is active.");
        }

        var expiry = expiryDate.Value.Date;
        var daysPast = (today - expiry).Days;

        if (daysPast >= LoginBlockAfterDays)
        {
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.LoginBlocked,
                daysPast,
                expiry,
                "Expired",
                $"Subscription expired on {expiry:yyyy-MM-dd}. Please renew to login.");
        }

        if (daysPast >= PostLockAfterDays)
        {
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.PostLocked,
                daysPast,
                expiry,
                "Expired",
                $"Subscription expired on {expiry:yyyy-MM-dd}. You can save drafts but cannot post until you renew.");
        }

        if (daysPast >= WarnAfterDays)
        {
            return new SubscriptionAccessState(
                SubscriptionAccessPhase.GraceWarn,
                daysPast,
                expiry,
                "Expired",
                $"Your subscription expired on {expiry:yyyy-MM-dd}. Please renew.");
        }

        return new SubscriptionAccessState(
            SubscriptionAccessPhase.Active,
            daysPast,
            expiry,
            string.IsNullOrWhiteSpace(license) ? "Active" : license,
            "Subscription is active.");
    }

    public string LoginBlockedMessage(SubscriptionAccessState state) =>
        state.Phase == SubscriptionAccessPhase.LoginBlocked
            ? state.Message
            : "Company subscription/trial has expired. Please renew subscription.";

    public string PostLockedMessage(SubscriptionAccessState state) =>
        "Subscription expired. You can save drafts but cannot post until renew.";

    public string RenewBannerMessage(SubscriptionAccessState state)
    {
        if (state.ExpiryDate.HasValue)
            return $"Your subscription expired on {state.ExpiryDate:yyyy-MM-dd}. Please renew.";
        return "Your subscription has expired. Please renew.";
    }

    /// <summary>
    /// Mirror grace into Tenants.LicenseStatus for Owner UI:
    /// daysPast &gt;= 1 → Expired; daysPast &gt;= 30 → Blocked.
    /// Does not demote Suspended. Does not upgrade Active when still within term.
    /// </summary>
    public async Task SyncTenantLicenseStatusAsync(string companyCode, SubscriptionAccessState? precomputed = null)
    {
        if (string.IsNullOrWhiteSpace(companyCode)) return;
        var tenant = await _db.GetTenantByCodeAsync(companyCode);
        if (tenant == null) return;

        var state = precomputed ?? Evaluate(tenant);
        string? target = null;
        if (state.DaysPast >= LoginBlockAfterDays)
            target = "Blocked";
        else if (state.DaysPast >= WarnAfterDays)
            target = "Expired";

        if (target == null) return;
        if (string.Equals(tenant.LicenseStatus, "Suspended", StringComparison.OrdinalIgnoreCase))
            return;
        if (string.Equals(tenant.LicenseStatus, target, StringComparison.OrdinalIgnoreCase))
            return;

        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE Tenants
SET LicenseStatus=@Status, UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@Code
  AND LicenseStatus NOT IN ('Suspended')
  AND ISNULL(LicenseStatus,'') <> @Status";
        cmd.Parameters.AddWithValue("@Status", target);
        cmd.Parameters.AddWithValue("@Code", companyCode.Trim());
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Bulk-sync all tenants past expiry (Owner list / EnsureSchema). </summary>
    public async Task SyncAllExpiredTenantsAsync(SqlConnection? existing = null)
    {
        async Task RunAsync(SqlConnection con)
        {
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
UPDATE Tenants
SET LicenseStatus='Blocked', UpdatedAt=SYSUTCDATETIME()
WHERE ExpiryDate IS NOT NULL
  AND ExpiryDate < DATEADD(DAY, -29, CAST(SYSUTCDATETIME() AS DATE))
  AND LicenseStatus NOT IN ('Suspended','Blocked');

UPDATE Tenants
SET LicenseStatus='Expired', UpdatedAt=SYSUTCDATETIME()
WHERE ExpiryDate IS NOT NULL
  AND ExpiryDate < CAST(SYSUTCDATETIME() AS DATE)
  AND ExpiryDate >= DATEADD(DAY, -29, CAST(SYSUTCDATETIME() AS DATE))
  AND LicenseStatus NOT IN ('Suspended','Blocked','Expired');";
            await cmd.ExecuteNonQueryAsync();
        }

        if (existing != null)
        {
            await RunAsync(existing);
            return;
        }

        await using var con = await _db.OpenMasterAsync();
        await RunAsync(con);
    }

    public static bool IsPostingRoute(string method, string path)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            return false;
        var p = path ?? string.Empty;
        return p.Equals("/api/sales-invoices/post", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/purchases", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/pos/sales", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/sales-return-orders/post", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/returns", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/customer-payments", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/api/vendor-payments", StringComparison.OrdinalIgnoreCase);
    }
}
