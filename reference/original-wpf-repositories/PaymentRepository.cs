using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;

namespace PayNex_POS_B1.Repositories;

public class PaymentRepository
{
    public List<PaymentLine> GetPaymentMethods()
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT PaymentMethodId, PaymentMethodName FROM PaymentMethods WHERE IsActive=1 ORDER BY PaymentMethodId";
        var list = new List<PaymentLine>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PaymentLine
            {
                PaymentMethodId = SqlMap.Int(r, "PaymentMethodId"),
                PaymentMethodName = SqlMap.String(r, "PaymentMethodName"),
                Amount = 0
            });
        }
        return list;
    }
}
