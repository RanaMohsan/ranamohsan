namespace PayNex_POS_B1.Models;

public class SaleResult
{
    public int SaleId { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal ChangeAmount { get; set; }
}
