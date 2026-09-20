namespace PayNex.Cloud.Api.Models;

public sealed class SalesReturnOrderSaveRequest
{
    public string? ClientDocumentId { get; set; }
    public int SalesReturnOrderId { get; set; }
    public int OriginalSalesInvoiceId { get; set; }
    public DateTime ReturnDate { get; set; } = DateTime.Today;
    public string? Reason { get; set; }
    public string? Remarks { get; set; }
    public List<SalesReturnOrderLineRequest> Lines { get; set; } = new();
}

public sealed class SalesReturnOrderLineRequest
{
    public int SalesInvoiceLineId { get; set; }
    public decimal ReturnQuantity { get; set; }
}
