namespace PayNex.Cloud.Api.Models;

public sealed class BankAccountRequest
{
    public int BankAccountId { get; set; }
    public string BankCode { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string? AccountNumber { get; set; }
    public string? IBAN { get; set; }
    public string? BranchName { get; set; }
    public string Currency { get; set; } = "PKR";
    public string BankType { get; set; } = "Bank";
    public int GLAccountId { get; set; }
    public bool IsActive { get; set; } = true;
}

public readonly record struct PaymentGlDestination(string AccountNo, int? BankAccountId);
