namespace PayNex_POS_B1.Models;

public class ShiftInfo
{
    public int ShiftId { get; set; }
    public int StoreId { get; set; }
    public int TerminalId { get; set; }
    public int UserId { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal ClosingCash { get; set; }
    public decimal DifferenceAmount { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
