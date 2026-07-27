namespace PayNex_POS_B1.Models;

public class Customer
{
    public int CustomerId { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public decimal CreditLimit { get; set; }
    public decimal LoyaltyPoints { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => $"{CustomerCode} - {CustomerName}";
    public override string ToString() => DisplayName;
}
