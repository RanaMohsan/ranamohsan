namespace PayNex_POS_B1.Models;

public class PaymentLine
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string ReferenceNo { get; set; } = string.Empty;
}
