namespace PayNex.Cloud.Api.Models;

public sealed class PayNexOptions
{
    public string TokenSecret { get; set; } = "PAYNEX-LOCAL-DEV-TOKEN-SECRET";
    public string MasterDatabaseName { get; set; } = "PayNex_MasterDB";
    public string MasterServerConnection { get; set; } = string.Empty;
    public string MasterDbConnection { get; set; } = string.Empty;
    public string TenantConnectionTemplate { get; set; } = string.Empty;
    public int DefaultSubscriptionDays { get; set; } = 30;

    // Security / deployment-hardening options. Override from environment variables in production.
    public string[] AllowedCorsOrigins { get; set; } = Array.Empty<string>();
    public string SuperAdminUserName { get; set; } = "Mohsin-PayNex";
    public string SuperAdminDisplayName { get; set; } = "Mohsin PayNex Super Admin";
    public string SuperAdminEmail { get; set; } = "mohsin@paynex.local";
    public string SuperAdminBootstrapPassword { get; set; } = "PayNex@123";
    public string SuperAdminCompanyId { get; set; } = "3032720768";
    public int TokenExpiryHours { get; set; } = 12;
    public int AccessTokenExpiryMinutes { get; set; } = 15;
    public int RefreshTokenExpiryDays { get; set; } = 7;
    public int TrustedDeviceDays { get; set; } = 30;
    public int LoginOtpExpiryMinutes { get; set; } = 10;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LoginLockoutMinutes { get; set; } = 15;
    public string BackupFolder { get; set; } = "App_Data/Backups";
    public bool EnablePhysicalSqlBackup { get; set; } = true;
    public string PlatformOwnerEmail { get; set; } = "ranamohsanali3@gmail.com";
    public string PlatformOwnerUserName { get; set; } = "ranamohsanali3@gmail.com";
    public string PlatformOwnerDisplayName { get; set; } = "PayNex Owner";
    public string PlatformOwnerBootstrapPassword { get; set; } = "PayNex@123";
}

public sealed record TenantInfo(Guid TenantId, string CompanyCode, string CompanyName, string Slug, string DatabaseName, string Status, string SubscriptionPlan, DateTime? ExpiryDate, string LicenseStatus = "Active", string DatabaseCreationStatus = "Ready", string ProvisioningStatus = "Completed", string OwnerName = "", string OwnerEmail = "", string OwnerMobile = "", DateTime? TrialStartDate = null, DateTime? TrialEndDate = null, DateTime? RenewalDate = null, DateTime? CreatedAt = null, DateTime? CompanyStartDate = null, DateTime? LicenseExpiryDate = null, bool AllowSandbox = false, string ProductionDatabaseName = "", string SandboxDatabaseName = "", DateTime? SandboxCreatedAt = null, string ActiveEnvironment = "Production", bool AllowMultipleBranches = false, int MaxBranches = 1);

public sealed class TenantStatusUpdateRequest
{
    public string Status { get; set; } = "Active";
    public string LicenseStatus { get; set; } = "Active";
    public DateTime? RenewalDate { get; set; }
    public string? Notes { get; set; }
}

public sealed class TenantCardUpdateRequest
{
    public string CompanyName { get; set; } = string.Empty;
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? OwnerMobile { get; set; }
    public string? SubscriptionPlan { get; set; }
    public string Status { get; set; } = "Active";
    public string LicenseStatus { get; set; } = "Active";
    public DateTime? CompanyStartDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    public DateTime? RenewalDate { get; set; }
    public bool AllowSandbox { get; set; }
    public bool AllowMultipleBranches { get; set; }
    public int MaxBranches { get; set; } = 1;
    public string? Notes { get; set; }
}

public sealed class SandboxCreateRequest
{
    public bool SetAsDefaultEnvironment { get; set; } = false;
}

public sealed class EnvironmentSwitchRequest
{
    public string Environment { get; set; } = "Production";
}
public sealed class CreateTenantRequest
{
    public string CompanyName { get; set; } = string.Empty;
    // Optional. If empty, system auto-generates a company ID/code like PNX000001.
    public string? CompanyCode { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }
    public string? OwnerMobile { get; set; }
    public string? SubscriptionPlan { get; set; }
    public int MaxBranches { get; set; } = 1;
    public bool AllowMultipleBranches { get; set; }
    public int MaxUsers { get; set; } = 5;
    public int MaxCounters { get; set; } = 2;
    public string AdminUserName { get; set; } = "admin";
    public string AdminPassword { get; set; } = "Admin@123";
    public string? AdminEmail { get; set; }
    public DateTime? CompanyStartDate { get; set; }
    public DateTime? LicenseExpiryDate { get; set; }
    public bool AllowSandbox { get; set; }
    public bool CreateSandbox { get; set; }
}
public sealed record TenantCreatedResponse(
    Guid TenantId,
    string CompanyCode,
    string CompanyName,
    string DatabaseName,
    string Url,
    string AdminUserName,
    string Message,
    bool CredentialsEmailSent = false,
    string CredentialsEmailFrom = "",
    string CredentialsEmailStatus = "");
public sealed class LoginRequest
{
    // Modern cloud ERP login: users enter only email + password. Company is resolved from CentralUserDirectory.
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;
    public string? Environment { get; set; } = "Production";

    // Legacy compatibility only. The login UI no longer asks for these values.
    public string? CompanyCode { get; set; }
    public string? UserName { get; set; }
}
public sealed class UserUpsertRequest
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Password { get; set; }
    public int RoleId { get; set; }
    public int StoreId { get; set; }
    public List<int> BranchIds { get; set; } = new();
    public Dictionary<string, bool> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsCompanySuperAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ProfileImageBase64 { get; set; }
    public bool RemoveProfileImage { get; set; }
}

public sealed class ResetUserPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class MobileAppRegisterRequest
{
    public string AppName { get; set; } = "PayNex Mobile";
    public string Platform { get; set; } = "Both";
    public string? PackageName { get; set; }
    public string? BundleId { get; set; }
    public string? AppVersion { get; set; }
    public string? Notes { get; set; }
}

public sealed class MobileAppUpdateRequest
{
    public string AppName { get; set; } = "PayNex Mobile";
    public string Platform { get; set; } = "Both";
    public string? PackageName { get; set; }
    public string? BundleId { get; set; }
    public string? AppVersion { get; set; }
    public string? Notes { get; set; }
}

public sealed class MobileAppBlockRequest
{
    public bool IsBlocked { get; set; }
    public string? Reason { get; set; }
}

public sealed class MobileAppUserCreateRequest
{
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string Password { get; set; } = string.Empty;
    public string RoleName { get; set; } = "Mobile User";
}

public sealed class MobileAppUserUpdateRequest
{
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Mobile { get; set; }
    public string Password { get; set; } = string.Empty;
    public string RoleName { get; set; } = "Mobile User";
}

public sealed class MobileAppUserBlockRequest
{
    public bool IsBlocked { get; set; }
    public string? Reason { get; set; }
}

public sealed class MobileLoginRequest
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
}

public sealed class MobileRefreshRequest
{
    public string? RefreshToken { get; set; }
}

public sealed class ProductTaxDiscountUpdateRequest
{
    public int ProductId { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
}
public sealed record SuperAdminLoginRequest(string UserName, string Password, string? CompanyCode = null);
public sealed record PlatformCompanySwitchRequest(string CompanyCode, string? Environment = null);
public sealed record SuperAdminSession(int SuperAdminUserId, string UserName, string DisplayName, string Email, string RoleName);
public sealed record ApprovalActionRequest(string? Remarks);
public sealed record DatabaseRestoreRequest(string BackupReference, string ConfirmText, string? Remarks);

public sealed record UserSession(string CompanyCode, string CompanyName, string DatabaseName, int UserId, string UserName, string DisplayName, int RoleId, string RoleName, int StoreId, string StoreName, string Environment = "Production", int BranchId = 1, string BranchCode = "MAIN", string BranchName = "Main Branch", bool AllowMultipleBranches = false, string Email = "", bool IsCompanySuperAdmin = false, bool IsPlatformOwner = false, string PermissionsJson = "{}", string SessionId = "");

public sealed class ProductUpsertRequest
{
    public int ProductId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string UnitOfMeasure { get; set; } = "PCS";
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal RetailPrice { get; set; }
    public decimal StockOnHand { get; set; }
    public bool DiscountAllowed { get; set; } = true;
    public decimal MinStockLevel { get; set; }
    public decimal ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public decimal TaxPercent { get; set; }
    public string? ImageBase64 { get; set; }
    public string? ImagePath { get; set; }
}
public sealed record CustomerUpsertRequest(int CustomerId, string CustomerCode, string CustomerName, string? Mobile, string? Email, string? AddressLine, decimal CreditLimit, decimal OpeningBalance, bool IsActive);
public sealed record VendorUpsertRequest(int VendorId, string VendorCode, string VendorName, string? ContactPerson, string? Mobile, string? Email, string? AddressLine, string? PaymentTerms, decimal OpeningBalance, bool IsActive);

public sealed class CurrencyUpsertRequest
{
    public int CurrencyId { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string CurrencyName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public int DecimalPlaces { get; set; } = 2;
    public decimal ExchangeRate { get; set; } = 1m;
    public bool IsBase { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed record SaleLineRequest(int ProductId, decimal Quantity, decimal DiscountPercent, decimal? UnitPrice);
public sealed record SalePaymentRequest(int PaymentMethodId, string PaymentMethodName, decimal Amount, string? ReferenceNo);
public sealed class SalePostRequest
{
    public int CustomerId { get; set; }
    public string? Remarks { get; set; }
    public List<SaleLineRequest> Lines { get; set; } = new();
    public List<SalePaymentRequest> Payments { get; set; } = new();
}

public sealed record PurchaseLineRequest(int ProductId, decimal Quantity, decimal UnitCost, decimal TaxPercent);
public sealed class PurchasePostRequest
{
    public int PurchaseInvoiceId { get; set; }
    public int VendorId { get; set; }
    public DateTime InvoiceDate { get; set; } = DateTime.Today;
    public string? VendorInvoiceNo { get; set; }
    public decimal PaidAmount { get; set; }
    public string? Remarks { get; set; }
    public List<PurchaseLineRequest> Lines { get; set; } = new();
}


public sealed class BranchUpsertRequest
{
    public int BranchId { get; set; }
    public string BranchCode { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string? AddressLine { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class BranchSwitchRequest
{
    public int BranchId { get; set; }
}

public sealed record ProductForSale(int ProductId, string ProductName, string ProductCode, string Barcode, decimal SalePrice, decimal PurchasePrice, decimal StockOnHand, decimal TaxPercent, bool TaxInclusive, bool DiscountAllowed);
public sealed record CalculatedSaleLine(int ProductId, string ProductName, decimal Quantity, decimal UnitPrice, decimal UnitCost, decimal DiscountPercent, decimal Gross, decimal DiscountAmount, decimal TaxPercent, decimal TaxAmount, decimal LineTotal, bool TaxInclusive);

public sealed class CompanyInformationUpdateRequest
{
    public string CompanyName { get; set; } = string.Empty;
    public string? AddressLine { get; set; }
    public string? PhoneNo { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? TaxRegistrationNo { get; set; }
    public string? LogoPath { get; set; }
    public string? LogoBase64 { get; set; }
    public bool RemoveLogo { get; set; }
}

public sealed record OpenShiftRequest(decimal OpeningCash, int TerminalId = 1);
public sealed record CloseShiftRequest(decimal ClosingCash, string? Remarks);

public sealed record ReturnLineRequest(int SaleLineId, decimal ReturnQuantity);
public sealed class ReturnPostRequest
{
    public string OriginalInvoiceNo { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public List<ReturnLineRequest> Lines { get; set; } = new();
}

public sealed class CustomerPaymentPostRequest
{
    public int CustomerId { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Today;
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
}

public sealed class VendorPaymentPostRequest
{
    public int VendorId { get; set; }
    public DateTime PaymentDate { get; set; } = DateTime.Today;
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
}

public sealed class ChartAccountUpsertRequest
{
    public int AccountId { get; set; }
    public string AccountNo { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountType { get; set; } = "Asset";
    public string NormalBalance { get; set; } = "Debit";
    public bool IsActive { get; set; } = true;
}

public sealed class ReverseGlDocumentRequest
{
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNo { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public sealed class CashDrawerRequest
{
    public string EntryType { get; set; } = "Cash In";
    public decimal Amount { get; set; }
    public string? Remarks { get; set; }
}

public sealed class InventoryTransferRequest
{
    public int FromStoreId { get; set; }
    public int ToStoreId { get; set; }
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class InventoryAdjustmentRequest
{
    public int StoreId { get; set; }
    public int ProductId { get; set; }
    public decimal CountedQuantity { get; set; }
    public string Disposition { get; set; } = "Adjustment";
    public string? Reason { get; set; }
    public string? BatchNo { get; set; }
    public string? SerialNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
}


public sealed class SalesInvoicePostRequest
{
    public int SalesInvoiceId { get; set; }
    public int CustomerId { get; set; }
    public DateTime InvoiceDate { get; set; } = DateTime.Today;
    public decimal PaidAmount { get; set; }
    public string? Remarks { get; set; }
    public List<SalesInvoiceLinePostRequest> Lines { get; set; } = new();
}
public sealed record SalesInvoiceLinePostRequest(int ProductId, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent);

public sealed class SaveSalesDocumentRequest
{
    public int CustomerId { get; set; }
    public DateTime DocumentDate { get; set; } = DateTime.Today;
    public decimal TotalAmount { get; set; }
    public string? Remarks { get; set; }
    public object? Payload { get; set; }
}

public sealed class TaxGroupUpsertRequest
{
    public int TaxGroupId { get; set; }
    public string TaxGroupName { get; set; } = string.Empty;
    public decimal TaxPercent { get; set; }
    public bool IsInclusive { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class PostingSetupUpdateRequest
{
    public string CashAccount { get; set; } = "1000";
    public string BankAccount { get; set; } = "1010";
    public string ReceivableAccount { get; set; } = "1100";
    public string PayableAccount { get; set; } = "2000";
    public string InventoryAccount { get; set; } = "1200";
    public string SalesAccount { get; set; } = "4000";
    public string CogsAccount { get; set; } = "5000";
    public string SalesReturnAccount { get; set; } = "4010";
    public string InputTaxAccount { get; set; } = "1300";
    public string OutputTaxAccount { get; set; } = "2100";
    public string OpeningBalanceAccount { get; set; } = "3000";
    public string StockAdjustmentAccount { get; set; } = "5100";
    public decimal CashierDiscountLimit { get; set; } = 5m;
    public string CostingMethod { get; set; } = "Average";
    public bool BlockNegativeStock { get; set; } = true;
}

public sealed class OpeningBalancePostRequest
{
    public DateTime PostingDate { get; set; } = DateTime.Today;
    public decimal Cash { get; set; }
    public decimal Bank { get; set; }
    public string? Remarks { get; set; }
}

public sealed class CloseAccountingPeriodRequest
{
    public string PeriodName { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime EndDate { get; set; } = DateTime.Today;
}

public sealed class HoldSaleRequest
{
    public int CustomerId { get; set; }
    public string? Remarks { get; set; }
    public List<SaleLineRequest> Lines { get; set; } = new();
}

public sealed class BackupActionRequest
{
    public string? Remarks { get; set; }
}

public sealed class RestoreActionRequest
{
    public string BackupReference { get; set; } = string.Empty;
    public string? Remarks { get; set; }
}

public sealed class VerifyLoginOtpRequest
{
    public string ChallengeId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public sealed class ResendLoginOtpRequest
{
    public string ChallengeId { get; set; } = string.Empty;
}

public sealed class OwnerEmailSecuritySettingsRequest
{
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "PayNex Cloud ERP";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string? SmtpPassword { get; set; }
    public bool ClearStoredPassword { get; set; }
    public bool EnableSsl { get; set; } = true;
    public bool ReturnDevOtp { get; set; }
    public int LoginOtpExpiryMinutes { get; set; } = 10;
    public int TrustedDeviceDays { get; set; } = 30;
}

public sealed class OwnerEmailTestRequest
{
    public string ToEmail { get; set; } = string.Empty;
}

public sealed record EffectiveEmailSecuritySettings(
    string FromEmail,
    string FromName,
    string SmtpHost,
    int SmtpPort,
    string SmtpUser,
    string SmtpPassword,
    bool EnableSsl,
    bool ReturnDevOtp,
    int LoginOtpExpiryMinutes,
    int TrustedDeviceDays,
    bool IsDatabaseConfigured);

public sealed record EmailDeliveryResult(bool Sent, bool SmtpConfigured, bool ReturnDevOtp, string FromEmail, string Message);

public sealed record LoginChallengeCreated(
    string ChallengeId,
    string MaskedEmail,
    int ExpiresInSeconds,
    string? DevOtp,
    EmailDeliveryResult Delivery);

public sealed record VerifiedLoginChallenge(UserSession PendingSession, string Email, string CompanyCode);

public sealed record RefreshTokenIssue(string PlainToken, DateTimeOffset ExpiresAt);

public sealed record RefreshTokenRotation(UserSession Session, RefreshTokenIssue NewToken);

public sealed record AuthenticatedLoginResponse(string Token, UserSession User, bool Authenticated = true, bool RequiresVerification = false);
