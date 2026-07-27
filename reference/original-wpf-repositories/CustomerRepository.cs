using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;

namespace PayNex_POS_B1.Repositories;

public class CustomerRepository
{
    public List<Customer> Search(string term = "")
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 100 CustomerId, CustomerCode, CustomerName, ISNULL(Mobile,'') Mobile, ISNULL(Email,'') Email,
       ISNULL(AddressLine,'') AddressLine, CreditLimit, LoyaltyPoints,
       ISNULL(OpeningBalance,0) OpeningBalance, ISNULL(CurrentBalance,0) CurrentBalance, IsActive
FROM Customers
WHERE IsActive = 1 AND (@Term='' OR CustomerName LIKE @Like OR Mobile LIKE @Like OR CustomerCode LIKE @Like OR Email LIKE @Like OR AddressLine LIKE @Like)
ORDER BY CASE WHEN CustomerCode='WALKIN' THEN 0 ELSE 1 END, CustomerName";
        cmd.Parameters.AddWithValue("@Term", term.Trim());
        cmd.Parameters.AddWithValue("@Like", $"%{term.Trim()}%");
        return ReadCustomers(cmd);
    }

    public Customer GetWalkIn() => Search("WALKIN").First();

    public void Upsert(Customer c)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Customers WHERE CustomerId=@CustomerId)
BEGIN
    UPDATE Customers SET CustomerCode=@CustomerCode, CustomerName=@CustomerName, Mobile=@Mobile, Email=@Email,
      AddressLine=@AddressLine, CreditLimit=@CreditLimit, LoyaltyPoints=@LoyaltyPoints,
      OpeningBalance=@OpeningBalance, IsActive=@IsActive
    WHERE CustomerId=@CustomerId;
END
ELSE
BEGIN
    INSERT INTO Customers(CustomerCode, CustomerName, Mobile, Email, AddressLine, CreditLimit, LoyaltyPoints, OpeningBalance, CurrentBalance, IsActive)
    VALUES(@CustomerCode, @CustomerName, @Mobile, @Email, @AddressLine, @CreditLimit, @LoyaltyPoints, @OpeningBalance, @OpeningBalance, @IsActive);
END";
        cmd.Parameters.AddWithValue("@CustomerId", c.CustomerId);
        cmd.Parameters.AddWithValue("@CustomerCode", c.CustomerCode.Trim());
        cmd.Parameters.AddWithValue("@CustomerName", c.CustomerName.Trim());
        cmd.Parameters.AddWithValue("@Mobile", c.Mobile.Trim());
        cmd.Parameters.AddWithValue("@Email", c.Email.Trim());
        cmd.Parameters.AddWithValue("@AddressLine", c.AddressLine.Trim());
        cmd.Parameters.AddWithValue("@CreditLimit", c.CreditLimit);
        cmd.Parameters.AddWithValue("@LoyaltyPoints", c.LoyaltyPoints);
        cmd.Parameters.AddWithValue("@OpeningBalance", c.OpeningBalance);
        cmd.Parameters.AddWithValue("@IsActive", c.IsActive);
        cmd.ExecuteNonQuery();
    }

    private static List<Customer> ReadCustomers(SqlCommand cmd)
    {
        var list = new List<Customer>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Customer
            {
                CustomerId = SqlMap.Int(r, "CustomerId"),
                CustomerCode = SqlMap.String(r, "CustomerCode"),
                CustomerName = SqlMap.String(r, "CustomerName"),
                Mobile = SqlMap.String(r, "Mobile"),
                Email = SqlMap.String(r, "Email"),
                AddressLine = SqlMap.String(r, "AddressLine"),
                CreditLimit = SqlMap.Decimal(r, "CreditLimit"),
                LoyaltyPoints = SqlMap.Decimal(r, "LoyaltyPoints"),
                OpeningBalance = SqlMap.Decimal(r, "OpeningBalance"),
                CurrentBalance = SqlMap.Decimal(r, "CurrentBalance"),
                IsActive = SqlMap.Bool(r, "IsActive")
            });
        }
        return list;
    }
}
