using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Validation;

public sealed class RequestValidationService
{
    public const string GmailRequiredMessage = "Use a real Gmail address (@gmail.com). Login OTP is sent to that inbox.";

    public static bool IsGmailAddress(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length < 10 || !value.Contains('@') || value.Contains(' ')) return false;
        return value.EndsWith("@gmail.com", StringComparison.Ordinal) || value.EndsWith("@googlemail.com", StringComparison.Ordinal);
    }

    public (bool Ok, string Message) ValidateCreateTenant(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyName)) return (false, "Company name is required.");
        if (request.CompanyName.Trim().Length > 150) return (false, "Company name cannot exceed 150 characters.");
        if (!string.IsNullOrWhiteSpace(request.OwnerEmail) && !IsGmailAddress(request.OwnerEmail)) return (false, "Owner email must be a real Gmail address.");
        if (string.IsNullOrWhiteSpace(request.AdminEmail) || !IsGmailAddress(request.AdminEmail)) return (false, "First administrator Gmail is required. " + GmailRequiredMessage);
        if (request.MaxUsers < 0 || request.MaxBranches < 0 || request.MaxCounters < 0) return (false, "License limits cannot be negative.");
        if (!string.IsNullOrWhiteSpace(request.AdminPassword) && request.AdminPassword.Length < 8) return (false, "Client admin password must be at least 8 characters.");
        return (true, string.Empty);
    }

    public (bool Ok, string Message) ValidateTenantCardUpdate(TenantCardUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyName)) return (false, "Company name is required.");
        if (!string.IsNullOrWhiteSpace(request.OwnerEmail) && !IsGmailAddress(request.OwnerEmail))
            return (false, "Owner email must be a real Gmail address. " + GmailRequiredMessage);
        return (true, string.Empty);
    }

    public (bool Ok, string Message) ValidateRestore(DatabaseRestoreRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BackupReference)) return (false, "Backup reference is required.");
        if (!string.Equals(request.ConfirmText, "RESTORE DATABASE", StringComparison.Ordinal)) return (false, "Type RESTORE DATABASE in ConfirmText to execute restore.");
        return (true, string.Empty);
    }
}
