using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public static class LicenseGuard
{
    public static bool IsLicensePostingBlocked(TenantInfo? tenant)
    {
        if (tenant == null) return true;
        var status = (tenant.LicenseStatus ?? string.Empty).Trim();
        if (status.Equals("Expired", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("Suspended", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!status.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
            !status.Equals("Trial", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(status))
            return true;

        var expiry = tenant.LicenseExpiryDate ?? tenant.ExpiryDate;
        return expiry.HasValue && expiry.Value.Date < DateTime.Today;
    }

    public static async Task<IResult?> BlockPostingIfLicenseExpiredAsync(ConnectionFactory db, UserSession user)
    {
        if (user.IsPlatformOwner || string.IsNullOrWhiteSpace(user.CompanyCode) || string.IsNullOrWhiteSpace(user.DatabaseName))
            return null;
        var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
        if (!IsLicensePostingBlocked(tenant)) return null;
        return Results.Json(new
        {
            message = "Company license has expired or is inactive. You can save documents as Draft only. Posting is blocked until the license is renewed.",
            code = "LICENSE_EXPIRED_POST_BLOCKED",
            postingBlocked = true
        }, statusCode: StatusCodes.Status403Forbidden);
    }
}
