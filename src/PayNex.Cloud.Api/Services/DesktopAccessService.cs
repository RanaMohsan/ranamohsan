using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using System.Data;

namespace PayNex.Cloud.Api.Services;

public sealed class DesktopAccessService
{
    private readonly ConnectionFactory _db;
    private readonly TenantProvisioningService _tenants;
    private readonly PasswordService _passwords;

    public DesktopAccessService(ConnectionFactory db, TenantProvisioningService tenants, PasswordService passwords)
    {
        _db = db;
        _tenants = tenants;
        _passwords = passwords;
    }

    public async Task EnsureSchemaAsync()
    {
        await using var master = await _db.OpenMasterAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('dbo.CompanyDesktopApps','U') IS NULL
BEGIN
    CREATE TABLE dbo.CompanyDesktopApps(
        AppId BIGINT IDENTITY(1,1) PRIMARY KEY,
        TenantId UNIQUEIDENTIFIER NOT NULL,
        CompanyCode NVARCHAR(40) NOT NULL,
        AppName NVARCHAR(150) NOT NULL,
        AppVersion NVARCHAR(40) NULL,
        Status NVARCHAR(30) NOT NULL CONSTRAINT DF_CompanyDesktopApps_Status DEFAULT('Active'),
        IsBlocked BIT NOT NULL CONSTRAINT DF_CompanyDesktopApps_IsBlocked DEFAULT(0),
        BlockReason NVARCHAR(500) NULL,
        Notes NVARCHAR(500) NULL,
        RegisteredAt DATETIME2 NOT NULL CONSTRAINT DF_CompanyDesktopApps_RegisteredAt DEFAULT(SYSUTCDATETIME()),
        UpdatedAt DATETIME2 NULL,
        BlockedAt DATETIME2 NULL
    );
    CREATE UNIQUE INDEX UX_CompanyDesktopApps_CompanyCode ON dbo.CompanyDesktopApps(CompanyCode);
END
IF OBJECT_ID('dbo.CompanyDesktopUserAccess','U') IS NULL
BEGIN
    CREATE TABLE dbo.CompanyDesktopUserAccess(
        DesktopAccessId BIGINT IDENTITY(1,1) PRIMARY KEY,
        AppId BIGINT NOT NULL,
        TenantId UNIQUEIDENTIFIER NOT NULL,
        CompanyCode NVARCHAR(40) NOT NULL,
        TenantUserId INT NOT NULL,
        UserName NVARCHAR(80) NOT NULL,
        IsAllowed BIT NOT NULL CONSTRAINT DF_CompanyDesktopUserAccess_IsAllowed DEFAULT(1),
        IsBlocked BIT NOT NULL CONSTRAINT DF_CompanyDesktopUserAccess_IsBlocked DEFAULT(0),
        BlockReason NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_CompanyDesktopUserAccess_CreatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedAt DATETIME2 NULL,
        BlockedAt DATETIME2 NULL
    );
    CREATE UNIQUE INDEX UX_CompanyDesktopUserAccess_CompanyUser ON dbo.CompanyDesktopUserAccess(CompanyCode, TenantUserId);
END";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(bool Blocked, string? Reason)> GetCompanyBlockAsync(string companyCode)
    {
        await EnsureSchemaAsync();
        await using var master = await _db.OpenMasterAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 IsBlocked, BlockReason FROM CompanyDesktopApps WHERE CompanyCode=@CompanyCode";
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (false, null);
        return (Convert.ToBoolean(reader["IsBlocked"]), reader["BlockReason"] as string);
    }

    public async Task<(bool Allowed, bool Blocked, string? Reason)> GetUserAccessAsync(string companyCode, int tenantUserId)
    {
        await EnsureSchemaAsync();
        await using var master = await _db.OpenMasterAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 IsAllowed, IsBlocked, BlockReason
FROM CompanyDesktopUserAccess
WHERE CompanyCode=@CompanyCode AND TenantUserId=@TenantUserId";
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode);
        cmd.Parameters.AddWithValue("@TenantUserId", tenantUserId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (true, false, null);
        return (Convert.ToBoolean(reader["IsAllowed"]), Convert.ToBoolean(reader["IsBlocked"]), reader["BlockReason"] as string);
    }

    public async Task<object> GetDetailAsync(string companyCode, bool includePasswords)
    {
        var tenant = await _tenants.GetTenantAsync(companyCode);
        await EnsureSchemaAsync();

        Dictionary<string, object?>? app = null;
        Dictionary<string, Dictionary<string, object?>> accessByUser = new();
        await using (var master = await _db.OpenMasterAsync())
        {
            await using (var cmd = master.CreateCommand())
            {
                cmd.CommandText = @"
SELECT TOP 1 AppId,TenantId,CompanyCode,AppName,AppVersion,Status,IsBlocked,BlockReason,Notes,RegisteredAt,UpdatedAt,BlockedAt
FROM CompanyDesktopApps WHERE CompanyCode=@CompanyCode";
                cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
                app = (await SqlList.ReadAsync(cmd)).FirstOrDefault();
            }

            await using (var cmd = master.CreateCommand())
            {
                cmd.CommandText = @"
SELECT DesktopAccessId,AppId,TenantUserId,UserName,IsAllowed,IsBlocked,BlockReason,UpdatedAt,BlockedAt
FROM CompanyDesktopUserAccess WHERE CompanyCode=@CompanyCode";
                cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
                foreach (var row in await SqlList.ReadAsync(cmd))
                {
                    var id = Convert.ToInt32(row.TryGetValue("TenantUserId", out var v) ? v ?? 0 : 0);
                    if (id > 0) accessByUser[id.ToString()] = row;
                }
            }
        }

        var users = new List<Dictionary<string, object?>>();
        var databaseName = string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
        if (!string.IsNullOrWhiteSpace(databaseName) && ConnectionFactory.IsSafeDatabaseName(databaseName))
        {
            await using var con = await _db.OpenTenantAsync(databaseName);
            users = await SqlList.ReadAsync(con, @"
SELECT u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,ISNULL(u.PhoneNumber,'') PhoneNumber,
       ISNULL(r.RoleName,'') RoleName,ISNULL(s.StoreCode,'') BranchCode,ISNULL(s.StoreName,'') BranchName,
       u.IsActive,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,
       CONVERT(bit,CASE WHEN ISNULL(u.PasswordHash,'')='' THEN 0 ELSE 1 END) HasPassword,
       ISNULL(u.OwnerVisiblePassword,'') OwnerVisiblePassword,
       ISNULL(u.PasswordHash,'') PasswordHash
FROM Users u
LEFT JOIN Roles r ON r.RoleId=u.RoleId
LEFT JOIN Stores s ON s.StoreId=u.StoreId
ORDER BY u.UserId");

            foreach (var row in users)
            {
                var userId = Convert.ToInt32(row.TryGetValue("UserId", out var idObj) ? idObj : 0);
                var plain = Convert.ToString(row.TryGetValue("OwnerVisiblePassword", out var ov) ? ov : "")?.Trim() ?? "";
                var hash = Convert.ToString(row.TryGetValue("PasswordHash", out var ph) ? ph : "") ?? "";
                if (string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(hash) && _passwords.Verify("Admin@123", hash))
                    plain = "Admin@123";
                if (!string.IsNullOrWhiteSpace(plain) && userId > 0)
                {
                    await using var fix = con.CreateCommand();
                    fix.CommandText = "UPDATE Users SET OwnerVisiblePassword=@Pwd WHERE UserId=@UserId AND ISNULL(OwnerVisiblePassword,'')=''";
                    fix.Parameters.AddWithValue("@Pwd", plain);
                    fix.Parameters.AddWithValue("@UserId", userId);
                    await fix.ExecuteNonQueryAsync();
                }

                accessByUser.TryGetValue(userId.ToString(), out var access);
                var allowed = access == null || Convert.ToBoolean(access.TryGetValue("IsAllowed", out var a) ? a : true);
                var blocked = access != null && Convert.ToBoolean(access.TryGetValue("IsBlocked", out var b) ? b : false);
                row["DesktopAccessAllowed"] = allowed && !blocked;
                row["DesktopUserBlocked"] = blocked;
                row["DesktopBlockReason"] = access != null && access.TryGetValue("BlockReason", out var br) ? br : "";
                row["PasswordPlain"] = includePasswords ? plain : "";
                row["PasswordInfo"] = includePasswords
                    ? (string.IsNullOrWhiteSpace(plain) ? (string.IsNullOrWhiteSpace(hash) ? "No password set" : "Unknown - reset to reveal") : plain)
                    : (string.IsNullOrWhiteSpace(hash) ? "No password set" : "Hidden");
                row.Remove("PasswordHash");
                row.Remove("OwnerVisiblePassword");
            }
        }

        return new
        {
            company = new
            {
                tenant.TenantId,
                tenant.CompanyCode,
                tenant.CompanyName,
                tenant.OwnerName,
                tenant.OwnerEmail,
                tenant.Status,
                tenant.LicenseStatus
            },
            registered = app != null,
            app,
            users,
            passwordVisibleToOwnerOnly = includePasswords
        };
    }

    public async Task<(long AppId, string Message)> RegisterAsync(string companyCode, string? appName, string? appVersion, string? notes)
    {
        var tenant = await _tenants.GetTenantAsync(companyCode);
        await EnsureSchemaAsync();
        await using var master = await _db.OpenMasterAsync();
        await using (var exists = master.CreateCommand())
        {
            exists.CommandText = "SELECT TOP 1 AppId FROM CompanyDesktopApps WHERE CompanyCode=@CompanyCode";
            exists.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            var existing = await exists.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value)
                return (Convert.ToInt64(existing), "Desktop app is already registered for this company.");
        }

        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
INSERT INTO CompanyDesktopApps(TenantId,CompanyCode,AppName,AppVersion,Status,IsBlocked,Notes,RegisteredAt)
OUTPUT INSERTED.AppId
VALUES(@TenantId,@CompanyCode,@AppName,@AppVersion,'Active',0,@Notes,SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@AppName", string.IsNullOrWhiteSpace(appName) ? "InterNex Desktop" : appName.Trim());
        cmd.Parameters.AddWithValue("@AppVersion", (object?)appVersion?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)notes?.Trim() ?? DBNull.Value);
        var appId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        return (appId, "Desktop app registered for this company.");
    }

    public async Task EnsureRegisteredAsync(string companyCode)
    {
        await RegisterAsync(companyCode, "InterNex Desktop", "1.0.0", null);
    }

    public async Task<bool> SaveAsync(string companyCode, string? appName, string? appVersion, string? notes)
    {
        var tenant = await _tenants.GetTenantAsync(companyCode);
        await EnsureSchemaAsync();
        await using var master = await _db.OpenMasterAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
UPDATE CompanyDesktopApps
SET AppName=@AppName, AppVersion=@AppVersion, Notes=@Notes, UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
SELECT @@ROWCOUNT;";
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@AppName", string.IsNullOrWhiteSpace(appName) ? "InterNex Desktop" : appName.Trim());
        cmd.Parameters.AddWithValue("@AppVersion", (object?)appVersion?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", (object?)notes?.Trim() ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    public async Task<bool> SetCompanyBlockedAsync(string companyCode, bool isBlocked, string? reason)
    {
        var tenant = await _tenants.GetTenantAsync(companyCode);
        await EnsureRegisteredAsync(tenant.CompanyCode);
        await using var master = await _db.OpenMasterAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
UPDATE CompanyDesktopApps
SET IsBlocked=@IsBlocked,
    Status=CASE WHEN @IsBlocked=1 THEN 'Blocked' ELSE 'Active' END,
    BlockReason=CASE WHEN @IsBlocked=1 THEN @BlockReason ELSE NULL END,
    BlockedAt=CASE WHEN @IsBlocked=1 THEN SYSUTCDATETIME() ELSE NULL END,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
SELECT @@ROWCOUNT;";
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@IsBlocked", isBlocked);
        cmd.Parameters.AddWithValue("@BlockReason", (object?)reason?.Trim() ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    public async Task SetUserAccessAsync(string companyCode, int tenantUserId, string userName, bool isAllowed)
    {
        var tenant = await _tenants.GetTenantAsync(companyCode);
        await EnsureRegisteredAsync(tenant.CompanyCode);
        long appId;
        await using (var master = await _db.OpenMasterAsync())
        await using (var find = master.CreateCommand())
        {
            find.CommandText = "SELECT TOP 1 AppId FROM CompanyDesktopApps WHERE CompanyCode=@CompanyCode";
            find.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            appId = Convert.ToInt64(await find.ExecuteScalarAsync() ?? 0L);
        }

        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
MERGE CompanyDesktopUserAccess AS t
USING (SELECT @CompanyCode CompanyCode, @TenantUserId TenantUserId) AS s
ON t.CompanyCode=s.CompanyCode AND t.TenantUserId=s.TenantUserId
WHEN MATCHED THEN UPDATE SET IsAllowed=@IsAllowed, UserName=@UserName, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(AppId,TenantId,CompanyCode,TenantUserId,UserName,IsAllowed,IsBlocked,CreatedAt)
VALUES(@AppId,@TenantId,@CompanyCode,@TenantUserId,@UserName,@IsAllowed,0,SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@AppId", appId);
        cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@TenantUserId", tenantUserId);
        cmd.Parameters.AddWithValue("@UserName", userName);
        cmd.Parameters.AddWithValue("@IsAllowed", isAllowed);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetUserBlockedAsync(string companyCode, int tenantUserId, string userName, bool isBlocked, string? reason)
    {
        var tenant = await _tenants.GetTenantAsync(companyCode);
        await EnsureRegisteredAsync(tenant.CompanyCode);
        long appId;
        await using (var master = await _db.OpenMasterAsync())
        await using (var find = master.CreateCommand())
        {
            find.CommandText = "SELECT TOP 1 AppId FROM CompanyDesktopApps WHERE CompanyCode=@CompanyCode";
            find.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            appId = Convert.ToInt64(await find.ExecuteScalarAsync() ?? 0L);
        }

        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
MERGE CompanyDesktopUserAccess AS t
USING (SELECT @CompanyCode CompanyCode, @TenantUserId TenantUserId) AS s
ON t.CompanyCode=s.CompanyCode AND t.TenantUserId=s.TenantUserId
WHEN MATCHED THEN UPDATE SET
    IsBlocked=@IsBlocked,
    IsAllowed=CASE WHEN @IsBlocked=1 THEN 0 ELSE t.IsAllowed END,
    BlockReason=CASE WHEN @IsBlocked=1 THEN @BlockReason ELSE NULL END,
    BlockedAt=CASE WHEN @IsBlocked=1 THEN SYSUTCDATETIME() ELSE NULL END,
    UserName=@UserName,
    UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(AppId,TenantId,CompanyCode,TenantUserId,UserName,IsAllowed,IsBlocked,BlockReason,CreatedAt,BlockedAt)
VALUES(@AppId,@TenantId,@CompanyCode,@TenantUserId,@UserName,CASE WHEN @IsBlocked=1 THEN 0 ELSE 1 END,@IsBlocked,@BlockReason,SYSUTCDATETIME(),CASE WHEN @IsBlocked=1 THEN SYSUTCDATETIME() ELSE NULL END);";
        cmd.Parameters.AddWithValue("@AppId", appId);
        cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@TenantUserId", tenantUserId);
        cmd.Parameters.AddWithValue("@UserName", userName);
        cmd.Parameters.AddWithValue("@IsBlocked", isBlocked);
        cmd.Parameters.AddWithValue("@BlockReason", (object?)reason?.Trim() ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
