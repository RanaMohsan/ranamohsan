namespace PayNex_POS_B1.Models;

public class CompanyInformation
{
    public int CompanyInformationId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string TaxRegistrationNo { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;
    public byte[]? LogoImage { get; set; }
    public DateTime UpdatedAt { get; set; }
}
