using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;

namespace PayNex.Cloud.Api.Services;

/// <summary>
/// Resolves User Card branch assignments for Cloud, Desktop, and Mobile sessions.
/// </summary>
public static class BranchAccessHelper
{
    public sealed record BranchInfo(int BranchId, string BranchCode, string BranchName, bool IsDefault);

    public static async Task EnsureSchemaAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF COL_LENGTH('Users','Email') IS NULL ALTER TABLE Users ADD Email NVARCHAR(180) NULL;
IF OBJECT_ID('UserBranchAssignments') IS NULL
BEGIN
CREATE TABLE UserBranchAssignments(
    UserId INT NOT NULL,
    StoreId INT NOT NULL,
    IsDefault BIT NOT NULL DEFAULT 0,
    CONSTRAINT PK_UserBranchAssignments PRIMARY KEY(UserId, StoreId)
);
END";
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<Dictionary<string, object?>>> ReadActiveBranchesAsync(SqlConnection con)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT StoreId BranchId,StoreCode BranchCode,StoreName BranchName,AddressLine,IsActive,
       ISNULL(IsMainBranch,0) IsMainBranch,StoreCode + ' - ' + StoreName DisplayName
FROM Stores WHERE IsActive=1
ORDER BY IsMainBranch DESC,StoreName";
        return await SqlList.ReadAsync(cmd);
    }

    public static async Task<int> GetUserHomeStoreIdAsync(SqlConnection con, int userId)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 ISNULL(StoreId,0) FROM Users WHERE UserId=@UserId";
        cmd.Parameters.AddWithValue("@UserId", userId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public static async Task<HashSet<int>> GetAssignedStoreIdsAsync(SqlConnection con, int userId)
    {
        await EnsureSchemaAsync(con);
        var allowed = new HashSet<int>();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT StoreId FROM UserBranchAssignments WHERE UserId=@UserId";
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) allowed.Add(Convert.ToInt32(r["StoreId"]));
        return allowed;
    }

    public static async Task<List<Dictionary<string, object?>>> FilterBranchesForUserAsync(
        SqlConnection con, int userId, List<Dictionary<string, object?>> branches, bool isSecurityAdmin)
    {
        // Always enforce User Card assignments — including Admin / Company Super Admin.
        // Users who need every branch must be assigned those branches on the User Card.
        _ = isSecurityAdmin;
        var allowed = await GetAssignedStoreIdsAsync(con, userId);
        if (allowed.Count == 0)
        {
            var homeId = await GetUserHomeStoreIdAsync(con, userId);
            var home = branches.Where(b => BranchIdOf(b) == homeId).ToList();
            if (home.Count > 0) return home;
            return branches.Take(1).ToList();
        }
        return branches.Where(b => allowed.Contains(BranchIdOf(b))).ToList();
    }

    public static async Task<bool> IsBranchAssignedToUserAsync(
        SqlConnection con, int userId, int branchId, bool isSecurityAdmin)
    {
        _ = isSecurityAdmin;
        await EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM UserBranchAssignments WHERE UserId=@UserId AND StoreId=@StoreId";
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@StoreId", branchId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0) return true;

        await using var empty = con.CreateCommand();
        empty.CommandText = "SELECT COUNT(1) FROM UserBranchAssignments WHERE UserId=@UserId";
        empty.Parameters.AddWithValue("@UserId", userId);
        if (Convert.ToInt32(await empty.ExecuteScalarAsync()) > 0) return false;

        var homeId = await GetUserHomeStoreIdAsync(con, userId);
        return homeId > 0 && homeId == branchId;
    }

    public static async Task<(BranchInfo Default, List<BranchInfo> Branches)> ResolveBranchesForUserAsync(
        SqlConnection con, int userId, bool isSecurityAdmin)
    {
        await EnsureSchemaAsync(con);
        var all = await ReadActiveBranchesAsync(con);
        var filtered = await FilterBranchesForUserAsync(con, userId, all, isSecurityAdmin);
        var homeId = await GetUserHomeStoreIdAsync(con, userId);
        int defaultId = 0;

        await using (var def = con.CreateCommand())
        {
            def.CommandText = @"
SELECT TOP 1 StoreId FROM UserBranchAssignments
WHERE UserId=@UserId AND IsDefault=1
ORDER BY StoreId";
            def.Parameters.AddWithValue("@UserId", userId);
            var d = await def.ExecuteScalarAsync();
            if (d != null && d != DBNull.Value) defaultId = Convert.ToInt32(d);
        }

        if (defaultId <= 0) defaultId = homeId;
        if (defaultId <= 0 && filtered.Count > 0) defaultId = BranchIdOf(filtered[0]);

        var list = new List<BranchInfo>();
        foreach (var row in filtered)
        {
            var id = BranchIdOf(row);
            var code = Convert.ToString(row.GetValueOrDefault("BranchCode") ?? "MAIN") ?? "MAIN";
            var name = Convert.ToString(row.GetValueOrDefault("BranchName") ?? "Main Branch") ?? "Main Branch";
            list.Add(new BranchInfo(id, code, name, id == defaultId));
        }

        if (list.Count == 0)
        {
            var main = all.FirstOrDefault() ?? new Dictionary<string, object?>
            {
                ["BranchId"] = 1,
                ["BranchCode"] = "MAIN",
                ["BranchName"] = "Main Branch"
            };
            var id = BranchIdOf(main);
            if (id <= 0) id = 1;
            var code = Convert.ToString(main.GetValueOrDefault("BranchCode") ?? "MAIN") ?? "MAIN";
            var name = Convert.ToString(main.GetValueOrDefault("BranchName") ?? "Main Branch") ?? "Main Branch";
            list.Add(new BranchInfo(id, code, name, true));
            return (list[0], list);
        }

        var preferred = list.FirstOrDefault(b => b.IsDefault) ?? list[0];
        if (!preferred.IsDefault)
        {
            list = list.Select(b => b with { IsDefault = b.BranchId == preferred.BranchId }).ToList();
            preferred = list.First(b => b.IsDefault);
        }
        return (preferred, list);
    }

    public static object[] ToPayload(IEnumerable<BranchInfo> branches) =>
        branches.Select(b => (object)new
        {
            branchId = b.BranchId,
            branchCode = b.BranchCode,
            branchName = b.BranchName,
            isDefault = b.IsDefault
        }).ToArray();

    private static int BranchIdOf(Dictionary<string, object?> b) =>
        Convert.ToInt32(b.GetValueOrDefault("BranchId") ?? b.GetValueOrDefault("StoreId") ?? 0);
}
