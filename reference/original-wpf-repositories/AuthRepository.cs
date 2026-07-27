using Microsoft.Data.SqlClient;
using PayNex_POS_B1.Data;
using PayNex_POS_B1.Models;
using PayNex_POS_B1.Services;

namespace PayNex_POS_B1.Repositories;

public class AuthRepository
{
    public UserAccount? Login(string username, string password)
    {
        using var con = Db.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT u.UserId, u.UserName, u.DisplayName, u.PasswordHash, u.RoleId, r.RoleName, u.StoreId, s.StoreName
FROM Users u
INNER JOIN Roles r ON r.RoleId = u.RoleId
INNER JOIN Stores s ON s.StoreId = u.StoreId
WHERE u.UserName = @UserName AND u.IsActive = 1";
        cmd.Parameters.AddWithValue("@UserName", username.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var hash = SqlMap.String(r, "PasswordHash");
        if (!SecurityService.VerifyPassword(password, hash)) return null;

        return new UserAccount
        {
            UserId = SqlMap.Int(r, "UserId"),
            UserName = SqlMap.String(r, "UserName"),
            DisplayName = SqlMap.String(r, "DisplayName"),
            RoleId = SqlMap.Int(r, "RoleId"),
            RoleName = SqlMap.String(r, "RoleName"),
            StoreId = SqlMap.Int(r, "StoreId"),
            StoreName = SqlMap.String(r, "StoreName")
        };
    }
}
