using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Validation;

public sealed class RequestValidationService
{
    public (bool Ok, string Message) ValidateCreateTenant(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyName)) return (false, "Company name is required.");
        if (request.CompanyName.Trim().Length > 150) return (false, "Company name cannot exceed 150 characters.");
        if (!string.IsNullOrWhiteSpace(request.OwnerEmail) && !request.OwnerEmail.Contains('@')) return (false, "Owner email is invalid.");
        if (!string.IsNullOrWhiteSpace(request.AdminEmail) && !request.AdminEmail.Contains('@')) return (false, "Company Super Admin email is invalid.");
        if (request.MaxUsers < 0 || request.MaxBranches < 0 || request.MaxCounters < 0) return (false, "License limits cannot be negative.");
        if (!string.IsNullOrWhiteSpace(request.AdminPassword) && request.AdminPassword.Length < 8) return (false, "Client admin password must be at least 8 characters.");
        return (true, string.Empty);
    }

    public (bool Ok, string Message) ValidateRestore(DatabaseRestoreRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BackupReference)) return (false, "Backup reference is required.");
        if (!string.Equals(request.ConfirmText, "RESTORE DATABASE", StringComparison.Ordinal)) return (false, "Type RESTORE DATABASE in ConfirmText to execute restore.");
        return (true, string.Empty);
    }
}
