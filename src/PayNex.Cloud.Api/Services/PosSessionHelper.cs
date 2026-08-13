using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public static class PosSessionHelper
{
    /// <summary>
    /// Mobile sessions historically had StoreId=0 / BranchCode=MOBILE.
    /// Resolve a real tenant store + user so sales invoice posting works.
    /// </summary>
    public static async Task<UserSession> EnsureTenantPosSessionAsync(ConnectionFactory db, UserSession user)
    {
        if (user.StoreId > 0 &&
            !string.Equals(user.BranchCode, "MOBILE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(user.StoreName, "Mobile", StringComparison.OrdinalIgnoreCase))
            return user;

        if (string.IsNullOrWhiteSpace(user.DatabaseName))
            return user;

        try
        {
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            var storeId = user.StoreId > 0 ? user.StoreId : 1;
            var branchCode = string.IsNullOrWhiteSpace(user.BranchCode) || user.BranchCode == "MOBILE" ? "MAIN" : user.BranchCode;
            var branchName = string.IsNullOrWhiteSpace(user.BranchName) || user.BranchName == "Mobile App" ? "Main Store" : user.BranchName;
            var storeName = string.IsNullOrWhiteSpace(user.StoreName) || user.StoreName == "Mobile" ? branchName : user.StoreName;
            var userId = user.UserId;

            await using (var storeCmd = con.CreateCommand())
            {
                storeCmd.CommandText = @"
SELECT TOP 1 StoreId, ISNULL(StoreCode,'MAIN') StoreCode, ISNULL(StoreName,'Main Store') StoreName
FROM Stores
WHERE IsActive=1
ORDER BY ISNULL(IsMainBranch,0) DESC, StoreId";
                await using var reader = await storeCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    storeId = Convert.ToInt32(reader["StoreId"]);
                    branchCode = Convert.ToString(reader["StoreCode"]) ?? "MAIN";
                    branchName = Convert.ToString(reader["StoreName"]) ?? "Main Store";
                    storeName = branchName;
                }
            }

            await using (var userCmd = con.CreateCommand())
            {
                userCmd.CommandText = @"
SELECT TOP 1 UserId
FROM Users
WHERE IsActive=1 AND LOWER(LTRIM(RTRIM(UserName))) = LOWER(LTRIM(RTRIM(@UserName)))
ORDER BY UserId";
                userCmd.Parameters.AddWithValue("@UserName", user.UserName);
                var mapped = await userCmd.ExecuteScalarAsync();
                if (mapped != null && mapped != DBNull.Value)
                    userId = Convert.ToInt32(mapped);
                else
                {
                    await using var adminCmd = con.CreateCommand();
                    adminCmd.CommandText = "SELECT TOP 1 UserId FROM Users WHERE IsActive=1 ORDER BY ISNULL(IsCompanySuperAdmin,0) DESC, UserId";
                    var adminId = await adminCmd.ExecuteScalarAsync();
                    if (adminId != null && adminId != DBNull.Value)
                        userId = Convert.ToInt32(adminId);
                }
            }

            return user with
            {
                UserId = userId,
                StoreId = storeId,
                BranchId = storeId,
                BranchCode = branchCode,
                BranchName = branchName,
                StoreName = storeName
            };
        }
        catch
        {
            return user;
        }
    }
}
