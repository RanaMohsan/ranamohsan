using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public static class PosSessionHelper
{
    /// <summary>
    /// Mobile sessions historically had StoreId=0 / BranchCode=MOBILE.
    /// Resolve a real tenant store + always remap UserId to tenant Users
    /// so POS inserts never use CompanyMobileAppUsers ids.
    /// </summary>
    public static async Task<UserSession> EnsureTenantPosSessionAsync(ConnectionFactory db, UserSession user)
    {
        if (string.IsNullOrWhiteSpace(user.DatabaseName))
            return user;

        var storeAlreadyResolved = user.StoreId > 0 &&
            !string.Equals(user.BranchCode, "MOBILE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(user.StoreName, "Mobile", StringComparison.OrdinalIgnoreCase);

        try
        {
            await using var con = await db.OpenTenantAsync(user.DatabaseName);
            var storeId = user.StoreId > 0 ? user.StoreId : 1;
            var branchCode = string.IsNullOrWhiteSpace(user.BranchCode) || user.BranchCode == "MOBILE" ? "MAIN" : user.BranchCode;
            var branchName = string.IsNullOrWhiteSpace(user.BranchName) || user.BranchName == "Mobile App" ? "Main Store" : user.BranchName;
            var storeName = string.IsNullOrWhiteSpace(user.StoreName) || user.StoreName == "Mobile" ? branchName : user.StoreName;
            var userId = user.UserId;

            if (!storeAlreadyResolved)
            {
                await using var storeCmd = con.CreateCommand();
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

            // Always remap UserId — mobile tokens may carry CompanyMobileAppUsers ids.
            userId = await ResolveTenantUserIdAsync(con, user.UserName, user.UserId);

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

    /// <summary>
    /// Map session identity to a row in tenant Users. Prefer UserName match; else admin fallback.
    /// Also verifies the current UserId exists before keeping it.
    /// </summary>
    public static async Task<int> ResolveTenantUserIdAsync(System.Data.Common.DbConnection con, string? userName, int currentUserId)
    {
        if (currentUserId > 0)
        {
            await using (var existsCmd = con.CreateCommand())
            {
                existsCmd.CommandText = "SELECT TOP 1 UserId FROM Users WHERE UserId=@UserId AND IsActive=1";
                var p = existsCmd.CreateParameter();
                p.ParameterName = "@UserId";
                p.Value = currentUserId;
                existsCmd.Parameters.Add(p);
                var existing = await existsCmd.ExecuteScalarAsync();
                if (existing != null && existing != DBNull.Value)
                    return Convert.ToInt32(existing);
            }
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            await using var userCmd = con.CreateCommand();
            userCmd.CommandText = @"
SELECT TOP 1 UserId
FROM Users
WHERE IsActive=1 AND LOWER(LTRIM(RTRIM(UserName))) = LOWER(LTRIM(RTRIM(@UserName)))
ORDER BY UserId";
            var p = userCmd.CreateParameter();
            p.ParameterName = "@UserName";
            p.Value = userName;
            userCmd.Parameters.Add(p);
            var mapped = await userCmd.ExecuteScalarAsync();
            if (mapped != null && mapped != DBNull.Value)
                return Convert.ToInt32(mapped);
        }

        await using var adminCmd = con.CreateCommand();
        adminCmd.CommandText = "SELECT TOP 1 UserId FROM Users WHERE IsActive=1 ORDER BY ISNULL(IsCompanySuperAdmin,0) DESC, UserId";
        var adminId = await adminCmd.ExecuteScalarAsync();
        if (adminId != null && adminId != DBNull.Value)
            return Convert.ToInt32(adminId);

        return currentUserId;
    }
}
