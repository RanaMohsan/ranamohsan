using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;
using PayNex_POS_B1.Services;

namespace PayNex_POS_B1.Repositories;

public class ShiftRepository
{
    public ShiftInfo? GetOpenShift(int userId, int terminalId)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 ShiftId, StoreId, TerminalId, UserId, OpeningCash, ISNULL(ExpectedCash,0) ExpectedCash,
       ISNULL(ClosingCash,0) ClosingCash, ISNULL(DifferenceAmount,0) DifferenceAmount, Status, OpenedAt, ClosedAt
FROM Shifts
WHERE UserId=@UserId AND TerminalId=@TerminalId AND Status='Open'
ORDER BY ShiftId DESC";
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@TerminalId", terminalId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return Map(r);
    }

    public ShiftInfo OpenShift(decimal openingCash)
    {
        if (PosSession.CurrentUser == null) throw new InvalidOperationException("Login required.");
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO Shifts(StoreId, TerminalId, UserId, OpeningCash, Status)
OUTPUT INSERTED.ShiftId
VALUES(@StoreId, @TerminalId, @UserId, @OpeningCash, 'Open');";
        cmd.Parameters.AddWithValue("@StoreId", PosSession.StoreId);
        cmd.Parameters.AddWithValue("@TerminalId", PosSession.TerminalId);
        cmd.Parameters.AddWithValue("@UserId", PosSession.CurrentUser.UserId);
        cmd.Parameters.AddWithValue("@OpeningCash", openingCash);
        var id = Convert.ToInt32(cmd.ExecuteScalar());
        return GetById(id)!;
    }

    public ShiftInfo? GetById(int id)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT ShiftId, StoreId, TerminalId, UserId, OpeningCash, ISNULL(ExpectedCash,0) ExpectedCash,
       ISNULL(ClosingCash,0) ClosingCash, ISNULL(DifferenceAmount,0) DifferenceAmount, Status, OpenedAt, ClosedAt
FROM Shifts WHERE ShiftId=@ShiftId";
        cmd.Parameters.AddWithValue("@ShiftId", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public decimal GetExpectedCash(int shiftId)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT s.OpeningCash
+ ISNULL((SELECT SUM(pl.Amount) FROM PaymentLines pl INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId INNER JOIN SalesHeader sh ON sh.SaleId=pl.SaleId WHERE sh.ShiftId=s.ShiftId AND pm.PaymentMethodName='Cash' AND sh.Status='Posted'),0)
+ ISNULL((SELECT SUM(CASE WHEN EntryType='CashIn' THEN Amount WHEN EntryType IN ('CashOut','Refund') THEN -Amount ELSE 0 END) FROM CashDrawerLedger WHERE ShiftId=s.ShiftId),0)
FROM Shifts s WHERE s.ShiftId=@ShiftId";
        cmd.Parameters.AddWithValue("@ShiftId", shiftId);
        return Convert.ToDecimal(cmd.ExecuteScalar());
    }

    public void CashInOut(string entryType, decimal amount, string remarks)
    {
        if (PosSession.CurrentShift == null || PosSession.CurrentUser == null) throw new InvalidOperationException("Open shift required.");
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO CashDrawerLedger(ShiftId, EntryType, Amount, Remarks, CreatedBy) VALUES(@ShiftId,@EntryType,@Amount,@Remarks,@CreatedBy)";
        cmd.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift.ShiftId);
        cmd.Parameters.AddWithValue("@EntryType", entryType);
        cmd.Parameters.AddWithValue("@Amount", amount);
        cmd.Parameters.AddWithValue("@Remarks", remarks);
        cmd.Parameters.AddWithValue("@CreatedBy", PosSession.CurrentUser.UserId);
        cmd.ExecuteNonQuery();
    }

    public ShiftInfo CloseShift(decimal closingCash, string remarks)
    {
        if (PosSession.CurrentShift == null) throw new InvalidOperationException("No open shift.");
        var expected = GetExpectedCash(PosSession.CurrentShift.ShiftId);
        var diff = closingCash - expected;
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE Shifts SET ClosingCash=@ClosingCash, ExpectedCash=@ExpectedCash, DifferenceAmount=@Difference, Status='Closed', ClosedAt=SYSUTCDATETIME(), ClosingRemarks=@Remarks
WHERE ShiftId=@ShiftId";
        cmd.Parameters.AddWithValue("@ClosingCash", closingCash);
        cmd.Parameters.AddWithValue("@ExpectedCash", expected);
        cmd.Parameters.AddWithValue("@Difference", diff);
        cmd.Parameters.AddWithValue("@Remarks", remarks);
        cmd.Parameters.AddWithValue("@ShiftId", PosSession.CurrentShift.ShiftId);
        cmd.ExecuteNonQuery();
        return GetById(PosSession.CurrentShift.ShiftId)!;
    }

    private static ShiftInfo Map(SqlDataReader r) => new()
    {
        ShiftId = SqlMap.Int(r, "ShiftId"),
        StoreId = SqlMap.Int(r, "StoreId"),
        TerminalId = SqlMap.Int(r, "TerminalId"),
        UserId = SqlMap.Int(r, "UserId"),
        OpeningCash = SqlMap.Decimal(r, "OpeningCash"),
        ExpectedCash = SqlMap.Decimal(r, "ExpectedCash"),
        ClosingCash = SqlMap.Decimal(r, "ClosingCash"),
        DifferenceAmount = SqlMap.Decimal(r, "DifferenceAmount"),
        Status = SqlMap.String(r, "Status"),
        OpenedAt = SqlMap.DateTime(r, "OpenedAt"),
        ClosedAt = SqlMap.NullableDateTime(r, "ClosedAt")
    };
}
