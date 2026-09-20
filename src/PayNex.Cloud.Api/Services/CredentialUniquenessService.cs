using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;

namespace PayNex.Cloud.Api.Services;

public sealed class CredentialUniquenessService
{
    private readonly ConnectionFactory _db;
    private readonly TenantProvisioningService _tenants;
    private readonly PasswordService _passwords;
    private readonly ILogger<CredentialUniquenessService> _log;

    public CredentialUniquenessService(
        ConnectionFactory db,
        TenantProvisioningService tenants,
        PasswordService passwords,
        ILogger<CredentialUniquenessService> log)
    {
        _db = db;
        _tenants = tenants;
        _passwords = passwords;
        _log = log;
    }

    public static string MakeCompanyPassword(string userName, string companyCode)
    {
        var raw = string.IsNullOrWhiteSpace(userName) ? "User" : userName.Trim();
        var titled = char.ToUpperInvariant(raw[0]) + (raw.Length > 1 ? raw[1..].ToLowerInvariant() : string.Empty);
        if (!titled.Any(char.IsLower)) titled += "x";
        var code = string.IsNullOrWhiteSpace(companyCode) ? "PNX" : companyCode.Trim().ToUpperInvariant();
        return $"{titled}@{code}";
    }

    public async Task<string?> FindConflictMessageAsync(string password, string? excludeCompanyCode, int excludeUserId)
    {
        if (string.IsNullOrWhiteSpace(password)) return null;
        await using var master = await _db.OpenMasterAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
SELECT CompanyCode, UserId, UserName, PasswordHash
FROM CentralUserDirectory
WHERE IsActive=1 AND ISNULL(PasswordHash,'')<>''";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var company = Convert.ToString(reader["CompanyCode"]) ?? "";
            var userId = Convert.ToInt32(reader["UserId"]);
            if (!string.IsNullOrWhiteSpace(excludeCompanyCode) &&
                company.Equals(excludeCompanyCode, StringComparison.OrdinalIgnoreCase) &&
                userId == excludeUserId)
                continue;
            var hash = Convert.ToString(reader["PasswordHash"]) ?? "";
            if (_passwords.Verify(password, hash))
            {
                var otherUser = Convert.ToString(reader["UserName"]) ?? "";
                return $"This password is already used by {otherUser} in company {company}. Username + password must be unique so InterNex can identify the company.";
            }
        }
        return null;
    }

    public async Task RepairSharedPasswordsAsync()
    {
        List<TenantInfo> tenants;
        try { tenants = await _tenants.ListTenantsAsync(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Credential uniqueness repair skipped because tenants could not be loaded.");
            return;
        }

        var entries = new List<UserEntry>();
        foreach (var tenant in tenants)
        {
            var databaseName = string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
            if (string.IsNullOrWhiteSpace(databaseName) || !ConnectionFactory.IsSafeDatabaseName(databaseName))
                continue;
            try
            {
                await using var con = await _db.OpenTenantAsync(databaseName);
                await using (var alter = con.CreateCommand())
                {
                    alter.CommandText = "IF COL_LENGTH('Users','OwnerVisiblePassword') IS NULL ALTER TABLE Users ADD OwnerVisiblePassword NVARCHAR(128) NULL;";
                    await alter.ExecuteNonQueryAsync();
                }
                await using var cmd = con.CreateCommand();
                cmd.CommandText = @"
SELECT u.UserId,u.UserName,ISNULL(u.DisplayName,'') DisplayName,ISNULL(u.Email,'') Email,
       ISNULL(r.RoleName,'') RoleName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,u.IsActive,
       ISNULL(u.PasswordHash,'') PasswordHash,ISNULL(u.OwnerVisiblePassword,'') OwnerVisiblePassword
FROM Users u
LEFT JOIN Roles r ON r.RoleId=u.RoleId";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var hash = Convert.ToString(reader["PasswordHash"]) ?? "";
                    var plain = (Convert.ToString(reader["OwnerVisiblePassword"]) ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(plain) && _passwords.Verify("Admin@123", hash))
                        plain = "Admin@123";
                    entries.Add(new UserEntry
                    {
                        TenantId = tenant.TenantId,
                        CompanyCode = tenant.CompanyCode,
                        DatabaseName = databaseName,
                        UserId = Convert.ToInt32(reader["UserId"]),
                        UserName = Convert.ToString(reader["UserName"]) ?? "",
                        DisplayName = Convert.ToString(reader["DisplayName"]) ?? "",
                        Email = Convert.ToString(reader["Email"]) ?? "",
                        RoleName = Convert.ToString(reader["RoleName"]) ?? "",
                        IsCompanySuperAdmin = Convert.ToBoolean(reader["IsCompanySuperAdmin"]),
                        IsActive = Convert.ToBoolean(reader["IsActive"]),
                        PasswordHash = hash,
                        Plain = plain
                    });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Credential uniqueness repair skipped tenant {CompanyCode}.", tenant.CompanyCode);
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var toChange = new List<UserEntry>();
        foreach (var group in entries.GroupBy(e => string.IsNullOrWhiteSpace(e.Plain) ? $"hash:{e.UserId}:{e.CompanyCode}" : e.Plain, StringComparer.Ordinal))
        {
            var rows = group.ToList();
            var sharedDefault = rows.Count > 1 || string.Equals(rows[0].Plain, "Admin@123", StringComparison.Ordinal);
            if (!sharedDefault)
            {
                if (!string.IsNullOrWhiteSpace(rows[0].Plain)) used.Add(rows[0].Plain);
                continue;
            }

            if (rows.Count == 1 && string.Equals(rows[0].Plain, "Admin@123", StringComparison.Ordinal))
            {
                toChange.Add(rows[0]);
                continue;
            }

            for (var i = 0; i < rows.Count; i++)
            {
                if (i == 0 && !string.Equals(rows[i].Plain, "Admin@123", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(rows[i].Plain))
                {
                    used.Add(rows[i].Plain);
                    continue;
                }
                toChange.Add(rows[i]);
            }
        }

        foreach (var entry in toChange)
        {
            var next = NextUniquePassword(entry.UserName, entry.CompanyCode, used);
            used.Add(next);
            var hash = _passwords.Hash(next);
            await using (var con = await _db.OpenTenantAsync(entry.DatabaseName))
            await using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "UPDATE Users SET PasswordHash=@Hash, OwnerVisiblePassword=@Plain, UpdatedAt=SYSUTCDATETIME() WHERE UserId=@UserId";
                cmd.Parameters.AddWithValue("@Hash", hash);
                cmd.Parameters.AddWithValue("@Plain", next);
                cmd.Parameters.AddWithValue("@UserId", entry.UserId);
                await cmd.ExecuteNonQueryAsync();
            }

            var email = (entry.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.EndsWith(".paynex.local", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("Assigned unique desktop password for {UserName} in {CompanyCode}.", entry.UserName, entry.CompanyCode);
                continue;
            }
            await using var master = await _db.OpenMasterAsync();
            await using var dir = master.CreateCommand();
            dir.CommandText = @"
IF EXISTS(SELECT 1 FROM CentralUserDirectory WHERE CompanyCode=@CompanyCode AND UserId=@UserId)
BEGIN
    UPDATE CentralUserDirectory
    SET UserName=@UserName, DisplayName=@DisplayName, Email=@Email, PasswordHash=@Hash,
        RoleName=@RoleName, IsCompanySuperAdmin=@IsCompanySuperAdmin, IsActive=@IsActive, UpdatedAt=SYSUTCDATETIME()
    WHERE CompanyCode=@CompanyCode AND UserId=@UserId;
END
ELSE
BEGIN
    INSERT INTO CentralUserDirectory(TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsDefaultCompany,IsActive)
    VALUES(@TenantId,@CompanyCode,@UserId,@Email,@UserName,@DisplayName,@Hash,0,@RoleName,@IsCompanySuperAdmin,1,@IsActive);
END";
            dir.Parameters.AddWithValue("@TenantId", entry.TenantId);
            dir.Parameters.AddWithValue("@CompanyCode", entry.CompanyCode);
            dir.Parameters.AddWithValue("@UserId", entry.UserId);
            dir.Parameters.AddWithValue("@Email", email);
            dir.Parameters.AddWithValue("@UserName", entry.UserName);
            dir.Parameters.AddWithValue("@DisplayName", string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.UserName : entry.DisplayName);
            dir.Parameters.AddWithValue("@Hash", hash);
            dir.Parameters.AddWithValue("@RoleName", string.IsNullOrWhiteSpace(entry.RoleName) ? "User" : entry.RoleName);
            dir.Parameters.AddWithValue("@IsCompanySuperAdmin", entry.IsCompanySuperAdmin);
            dir.Parameters.AddWithValue("@IsActive", entry.IsActive);
            await dir.ExecuteNonQueryAsync();
            _log.LogInformation("Assigned unique desktop password for {UserName} in {CompanyCode}.", entry.UserName, entry.CompanyCode);
        }
    }

    private static string NextUniquePassword(string userName, string companyCode, HashSet<string> used)
    {
        var seed = MakeCompanyPassword(userName, companyCode);
        if (!used.Contains(seed)) return seed;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = seed + i;
            if (!used.Contains(candidate)) return candidate;
        }
        return seed + Guid.NewGuid().ToString("N")[..6];
    }

    private sealed class UserEntry
    {
        public Guid TenantId { get; set; }
        public string CompanyCode { get; set; } = "";
        public string DatabaseName { get; set; } = "";
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string RoleName { get; set; } = "";
        public bool IsCompanySuperAdmin { get; set; }
        public bool IsActive { get; set; }
        public string PasswordHash { get; set; } = "";
        public string Plain { get; set; } = "";
    }
}
