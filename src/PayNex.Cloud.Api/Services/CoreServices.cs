using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PayNex.Cloud.Api.Services;

public sealed class PasswordService
{
    private const int BcryptWorkFactor = 12;
    private const int LegacySaltSize = 16;
    private const int LegacyKeySize = 32;

    /// <summary>
    /// New passwords use BCrypt. Existing PBKDF2 and legacy SHA-256 hashes remain verifiable
    /// so accounts can be migrated without forcing an immediate password reset.
    /// </summary>
    public string Hash(string password)
    {
        password ??= string.Empty;
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: BcryptWorkFactor);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return false;
        password ??= string.Empty;

        if (hash.StartsWith("$2", StringComparison.Ordinal))
        {
            try { return BCrypt.Net.BCrypt.Verify(password, hash); }
            catch { return false; }
        }

        if (hash.StartsWith("PBKDF2$", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parts = hash.Split('$');
                if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations)) return false;
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                if (salt.Length != LegacySaltSize || expected.Length < LegacyKeySize / 2) return false;
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch { return false; }
        }

        // Compatibility with the original demo SHA-256 hashes. New or changed passwords never use this format.
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        return string.Equals(legacy, hash, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AuthTokenService
{
    private readonly PayNexOptions _options;
    public AuthTokenService(IOptions<PayNexOptions> options) => _options = options.Value;

    public string Create(UserSession session) => CreateJwt("tenant-user", session, DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(_options.AccessTokenExpiryMinutes, 5, 120)).ToUnixTimeSeconds());
    public string CreateSuperAdmin(SuperAdminSession session) => CreateJwt("super-admin", session, DateTimeOffset.UtcNow.AddHours(Math.Max(1, _options.TokenExpiryHours)).ToUnixTimeSeconds());

    public UserSession? Validate(string? token)
    {
        var payload = ValidateJwt<UserSession>(token, "tenant-user");
        return payload?.Session;
    }

    public SuperAdminSession? ValidateSuperAdmin(string? token)
    {
        var payload = ValidateJwt<SuperAdminSession>(token, "super-admin");
        return payload?.Session;
    }

    private string CreateJwt<T>(string typ, T session, long exp)
    {
        var header64 = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        })));
        var payload64 = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new TokenPayload<T>(typ, session, exp))));
        var sig = Sign(header64 + "." + payload64);
        return header64 + "." + payload64 + "." + sig;
    }

    private TokenPayload<T>? ValidateJwt<T>(string? token, string expectedType)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length == 2)
        {
            // Legacy pre-JWT token support for older client sessions.
            var legacyPayload64 = parts[0];
            if (!FixedEquals(Sign(legacyPayload64), parts[1])) return null;
            var legacyJson = Encoding.UTF8.GetString(Base64UrlDecode(legacyPayload64));
            var legacy = JsonSerializer.Deserialize<LegacyTenantPayload>(legacyJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (legacy == null || legacy.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() || typeof(T) != typeof(UserSession)) return null;
            return new TokenPayload<T>("tenant-user", (T)(object)legacy.Session, legacy.Exp);
        }

        if (parts.Length != 3) return null;
        var signed = parts[0] + "." + parts[1];
        if (!FixedEquals(Sign(signed), parts[2])) return null;
        var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        var payload = JsonSerializer.Deserialize<TokenPayload<T>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (payload == null || payload.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        if (!string.Equals(payload.Typ, expectedType, StringComparison.OrdinalIgnoreCase)) return null;
        return payload;
    }

    private bool FixedEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private string Sign(string content)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.TokenSecret));
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(content)));
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
    private sealed record TokenPayload<T>(string Typ, T Session, long Exp);
    private sealed record LegacyTenantPayload(UserSession Session, long Exp);
}

public static class ApiAuth
{
    public static UserSession? RequireUser(HttpContext http, AuthTokenService tokens)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) auth = auth[7..];
        if (string.IsNullOrWhiteSpace(auth)) auth = http.Request.Cookies["paynex_auth"] ?? string.Empty;
        return tokens.Validate(auth);
    }

    public static SuperAdminSession? RequireSuperAdmin(HttpContext http, AuthTokenService tokens)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) auth = auth[7..];
        return tokens.ValidateSuperAdmin(auth);
    }
}

public sealed class SqlScriptRunner
{
    public async Task ExecuteScriptFileAsync(SqlConnection con, string path)
    {
        var script = await File.ReadAllTextAsync(path);
        await ExecuteScriptAsync(con, script);
    }

    public async Task ExecuteScriptAsync(SqlConnection con, string script)
    {
        foreach (var batch in SplitBatches(script))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = con.CreateCommand();
            cmd.CommandTimeout = 180;
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitBatches(string script)
    {
        var sb = new StringBuilder();
        using var reader = new StringReader(script);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else sb.AppendLine(line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}

public sealed class TenantProvisioningService
{
    private readonly ConnectionFactory _db;
    private readonly SqlScriptRunner _runner;
    private readonly PasswordService _passwords;
    private readonly AuthenticationSecurityService _authSecurity;
    public PayNexOptions Options => _db.Options;
    public TenantProvisioningService(ConnectionFactory db, SqlScriptRunner runner, PasswordService passwords, AuthenticationSecurityService authSecurity)
    {
        _db = db;
        _runner = runner;
        _passwords = passwords;
        _authSecurity = authSecurity;
    }

    public async Task EnsureMasterDatabaseAsync()
    {
        await using (var master = await _db.OpenMasterServerAsync())
        {
            await using var cmd = master.CreateCommand();
            cmd.CommandText = $"IF DB_ID(@DbName) IS NULL EXEC('CREATE DATABASE [{Options.MasterDatabaseName.Replace("]", "]]")}]');";
            cmd.Parameters.AddWithValue("@DbName", Options.MasterDatabaseName);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var con = await _db.OpenMasterAsync())
        {
            await _runner.ExecuteScriptFileAsync(con, SchemaPath("MasterSchema.sql"));
        }
    }

    public async Task<List<TenantInfo>> ListTenantsAsync()
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TenantId,CompanyCode,CompanyName,Slug,DatabaseName,Status,SubscriptionPlan,ExpiryDate,
       LicenseStatus,DatabaseCreationStatus,ProvisioningStatus,
       ISNULL(OwnerName,'') OwnerName,ISNULL(OwnerEmail,'') OwnerEmail,ISNULL(OwnerMobile,'') OwnerMobile,
       TrialStartDate,TrialEndDate,RenewalDate,CreatedAt,CompanyStartDate,LicenseExpiryDate,
       ISNULL(AllowSandbox,0) AllowSandbox,ISNULL(ProductionDatabaseName,DatabaseName) ProductionDatabaseName,
       ISNULL(SandboxDatabaseName,'') SandboxDatabaseName,SandboxCreatedAt,ISNULL(ActiveEnvironment,'Production') ActiveEnvironment,ISNULL(AllowMultipleBranches,0) AllowMultipleBranches,ISNULL(MaxBranches,1) MaxBranches
FROM Tenants ORDER BY CreatedAt DESC";
        var list = new List<TenantInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(MapTenant(r));
        return list;
    }

    public async Task<TenantCreatedResponse> CreateTenantAsync(CreateTenantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CompanyName)) throw new InvalidOperationException("Company name is required.");

        await EnsureMasterDatabaseAsync();

        // Company ID/code is now auto-generated by default. Manual code is still supported by API only.
        var companyCode = string.IsNullOrWhiteSpace(req.CompanyCode)
            ? await GenerateNextCompanyCodeAsync()
            : ConnectionFactory.NormalizeCode(req.CompanyCode);
        var slug = companyCode.ToLowerInvariant();
        var databaseName = $"PayNex_{companyCode}_DB";
        if (!ConnectionFactory.IsSafeDatabaseName(databaseName)) throw new InvalidOperationException("Invalid database name.");
        var adminUser = string.IsNullOrWhiteSpace(req.AdminUserName) ? "admin" : req.AdminUserName.Trim();
        var adminPassword = string.IsNullOrWhiteSpace(req.AdminPassword) ? "Admin@123" : req.AdminPassword;
        var adminEmail = NormalizeEmail(req.AdminEmail) ?? NormalizeEmail(req.OwnerEmail) ?? $"{adminUser.ToLowerInvariant()}@{companyCode.ToLowerInvariant()}.paynex.local";
        if (string.Equals(adminEmail, NormalizeEmail(Options.PlatformOwnerEmail), StringComparison.OrdinalIgnoreCase))
            adminEmail = $"{adminUser.ToLowerInvariant()}@{companyCode.ToLowerInvariant()}.paynex.local";
        var plan = string.IsNullOrWhiteSpace(req.SubscriptionPlan) ? "Standard" : req.SubscriptionPlan!.Trim();
        var startDate = req.CompanyStartDate?.Date ?? DateTime.Today;
        var expiry = req.LicenseExpiryDate?.Date ?? DateTime.Today.AddDays(Options.DefaultSubscriptionDays);
        var allowSandbox = req.AllowSandbox || req.CreateSandbox;
        var allowMultipleBranches = req.AllowMultipleBranches || req.MaxBranches > 1;
        var maxBranches = allowMultipleBranches ? Math.Max(2, req.MaxBranches) : 1;

        await using (var master = await _db.OpenMasterServerAsync())
        {
            await using var create = master.CreateCommand();
            create.CommandText = $"IF DB_ID(@DbName) IS NULL EXEC('CREATE DATABASE [{databaseName.Replace("]", "]]")}]');";
            create.Parameters.AddWithValue("@DbName", databaseName);
            await create.ExecuteNonQueryAsync();
        }

        await using (var tenantCon = await _db.OpenTenantAsync(databaseName))
        {
            await _runner.ExecuteScriptFileAsync(tenantCon, SchemaPath("TenantSchema.sql"));
            await SeedTenantAsync(tenantCon, req.CompanyName.Trim(), adminUser, adminPassword, adminEmail);
        }

        Guid tenantId;
        await using (var con = await _db.OpenMasterAsync())
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Tenants WHERE CompanyCode=@CompanyCode)
BEGIN
 UPDATE Tenants SET CompanyName=@CompanyName,DatabaseName=@DatabaseName,ProductionDatabaseName=@DatabaseName,OwnerName=@OwnerName,OwnerEmail=@OwnerEmail,OwnerMobile=@OwnerMobile,AdminContactName=@OwnerName,AdminContactEmail=@OwnerEmail,AdminContactMobile=@OwnerMobile,SubscriptionPlan=@Plan,LicenseStatus='Active',MaxBranches=@MaxBranches,AllowMultipleBranches=@AllowMultipleBranches,MaxUsers=@MaxUsers,MaxCounters=@MaxCounters,Status='Active',TrialStartDate=@TrialStartDate,TrialEndDate=@TrialEndDate,RenewalDate=@RenewalDate,ExpiryDate=@ExpiryDate,CompanyStartDate=@CompanyStartDate,LicenseExpiryDate=@LicenseExpiryDate,AllowSandbox=@AllowSandbox,DatabaseCreationStatus='Ready',ProvisioningStatus='Completed',LastProvisionedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME()
 WHERE CompanyCode=@CompanyCode;
 SELECT TenantId FROM Tenants WHERE CompanyCode=@CompanyCode;
END
ELSE
BEGIN
 DECLARE @NewId UNIQUEIDENTIFIER=NEWID();
 INSERT INTO Tenants(TenantId,CompanyCode,CompanyName,Slug,DatabaseName,ProductionDatabaseName,OwnerName,OwnerEmail,OwnerMobile,AdminContactName,AdminContactEmail,AdminContactMobile,SubscriptionPlan,LicenseStatus,MaxBranches,AllowMultipleBranches,MaxUsers,MaxCounters,Status,TrialStartDate,TrialEndDate,RenewalDate,ExpiryDate,CompanyStartDate,LicenseExpiryDate,AllowSandbox,DatabaseCreationStatus,ProvisioningStatus,LastProvisionedAt)
 VALUES(@NewId,@CompanyCode,@CompanyName,@Slug,@DatabaseName,@DatabaseName,@OwnerName,@OwnerEmail,@OwnerMobile,@OwnerName,@OwnerEmail,@OwnerMobile,@Plan,'Active',@MaxBranches,@AllowMultipleBranches,@MaxUsers,@MaxCounters,'Active',@TrialStartDate,@TrialEndDate,@RenewalDate,@ExpiryDate,@CompanyStartDate,@LicenseExpiryDate,@AllowSandbox,'Ready','Completed',SYSUTCDATETIME());
 SELECT @NewId;
END";
            cmd.Parameters.AddWithValue("@CompanyCode", companyCode);
            cmd.Parameters.AddWithValue("@CompanyName", req.CompanyName.Trim());
            cmd.Parameters.AddWithValue("@Slug", slug);
            cmd.Parameters.AddWithValue("@DatabaseName", databaseName);
            cmd.Parameters.AddWithValue("@OwnerName", req.OwnerName ?? "");
            cmd.Parameters.AddWithValue("@OwnerEmail", req.OwnerEmail ?? "");
            cmd.Parameters.AddWithValue("@OwnerMobile", req.OwnerMobile ?? "");
            cmd.Parameters.AddWithValue("@Plan", plan);
            cmd.Parameters.AddWithValue("@MaxBranches", maxBranches);
            cmd.Parameters.AddWithValue("@AllowMultipleBranches", allowMultipleBranches);
            cmd.Parameters.AddWithValue("@MaxUsers", req.MaxUsers <= 0 ? 5 : req.MaxUsers);
            cmd.Parameters.AddWithValue("@MaxCounters", req.MaxCounters <= 0 ? 2 : req.MaxCounters);
            cmd.Parameters.AddWithValue("@ExpiryDate", expiry);
            cmd.Parameters.AddWithValue("@CompanyStartDate", startDate);
            cmd.Parameters.AddWithValue("@LicenseExpiryDate", expiry);
            cmd.Parameters.AddWithValue("@AllowSandbox", allowSandbox);
            cmd.Parameters.AddWithValue("@TrialStartDate", startDate);
            cmd.Parameters.AddWithValue("@TrialEndDate", expiry);
            cmd.Parameters.AddWithValue("@RenewalDate", expiry);
            tenantId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        await using (var auditCon = await _db.OpenMasterAsync())
        await using (var auditCmd = auditCon.CreateCommand())
        {
            auditCmd.CommandText = "INSERT INTO TenantAuditLog(TenantId,CompanyCode,ActionName,Description) VALUES(@TenantId,@CompanyCode,'TENANT_PROVISIONED','Company registered, database created, schema applied, default setup seeded, tenant activated.');";
            auditCmd.Parameters.AddWithValue("@TenantId", tenantId);
            auditCmd.Parameters.AddWithValue("@CompanyCode", companyCode);
            await auditCmd.ExecuteNonQueryAsync();
        }

        await SyncSeededAdminCentralDirectoryAsync(tenantId, companyCode, databaseName, adminUser, adminEmail, adminPassword, req.CompanyName.Trim());
        if (req.CreateSandbox)
        {
            await CreateSandboxAsync(companyCode, new SandboxCreateRequest());
        }

        var credentialsDelivery = await _authSecurity.SendNewUserCredentialsEmailAsync(
            adminEmail,
            string.IsNullOrWhiteSpace(req.OwnerName) ? "Company Administrator" : req.OwnerName.Trim(),
            req.CompanyName.Trim(),
            adminUser,
            adminPassword);
        var credentialsMessage = credentialsDelivery.Sent
            ? $"First administrator login details were emailed from {credentialsDelivery.FromEmail}."
            : $"The company was created, but the administrator login email was not sent: {credentialsDelivery.Message}";

        return new TenantCreatedResponse(
            tenantId,
            companyCode,
            req.CompanyName.Trim(),
            databaseName,
            "/login.html",
            adminUser,
            $"Company registered, tenant database created, schema applied, default data seeded, and client activated. {credentialsMessage}",
            credentialsDelivery.Sent,
            credentialsDelivery.FromEmail,
            credentialsDelivery.Message);
    }

    public async Task RemovePlatformOwnerFromTenantAccessAsync()
    {
        await EnsureMasterDatabaseAsync();
        var ownerEmail = NormalizeEmail(Options.PlatformOwnerEmail);
        if (string.IsNullOrWhiteSpace(ownerEmail)) return;
        var ownerUserName = string.IsNullOrWhiteSpace(Options.PlatformOwnerUserName) ? ownerEmail : Options.PlatformOwnerUserName.Trim().ToLowerInvariant();

        await using (var master = await _db.OpenMasterAsync())
        await using (var cleanDir = master.CreateCommand())
        {
            cleanDir.CommandText = @"
IF OBJECT_ID('CentralUserDirectory') IS NOT NULL
BEGIN
    DELETE FROM CentralUserDirectory WHERE LOWER(ISNULL(Email,''))=@Email OR LOWER(ISNULL(UserName,''))=@Email OR LOWER(ISNULL(UserName,''))=@UserName;
END";
            cleanDir.Parameters.AddWithValue("@Email", ownerEmail);
            cleanDir.Parameters.AddWithValue("@UserName", ownerUserName);
            await cleanDir.ExecuteNonQueryAsync();
        }

        var tenants = await ListTenantsAsync();
        foreach (var tenant in tenants)
        {
            var names = new List<string>();
            var productionDb = string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
            if (!string.IsNullOrWhiteSpace(productionDb)) names.Add(productionDb);
            if (!string.IsNullOrWhiteSpace(tenant.SandboxDatabaseName)) names.Add(tenant.SandboxDatabaseName);

            foreach (var dbName in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!ConnectionFactory.IsSafeDatabaseName(dbName)) continue;
                try
                {
                    await using var con = await _db.OpenTenantAsync(dbName);
                    await using var cmd = con.CreateCommand();
                    cmd.CommandText = @"
IF OBJECT_ID('Users') IS NOT NULL
BEGIN
    IF COL_LENGTH('Users','Email') IS NULL ALTER TABLE Users ADD Email NVARCHAR(180) NULL;
    IF COL_LENGTH('Users','UpdatedAt') IS NULL ALTER TABLE Users ADD UpdatedAt DATETIME2 NULL;
    UPDATE Users
    SET IsActive=0,
        Email='',
        UserName=CONCAT('REMOVED_PAYNEX_OWNER_', UserId),
        DisplayName='Removed PayNex Owner Access',
        UpdatedAt=SYSUTCDATETIME()
    WHERE LOWER(ISNULL(Email,''))=@Email OR LOWER(ISNULL(UserName,''))=@Email OR LOWER(ISNULL(UserName,''))=@UserName;
END";
                    cmd.Parameters.AddWithValue("@Email", ownerEmail);
                    cmd.Parameters.AddWithValue("@UserName", ownerUserName);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Tenant DB may be offline or schema may be old. Client login is still blocked centrally.
                }
            }
        }
    }

    public Task EnsurePlatformOwnerAccessAllTenantsAsync()
    {
        // Disabled by design: PayNex owner email is Super Admin Portal only, not tenant/company access.
        return Task.CompletedTask;
    }

    public Task EnsurePlatformOwnerAccessForTenantAsync(Guid tenantId, string companyCode, string databaseName, string companyName)
    {
        // Disabled by design: never create PayNex owner inside client company databases.
        return Task.CompletedTask;
    }

    private Task<int> EnsurePlatformOwnerUserInTenantDbAsync(string databaseName)
    {
        // Disabled by design: never create PayNex owner inside client company databases.
        return Task.FromResult(0);
    }

    public async Task<TenantInfo> GetTenantAsync(string companyCode)
    {
        await EnsureMasterDatabaseAsync();
        var tenant = await _db.GetTenantByCodeAsync(companyCode);
        return tenant ?? throw new InvalidOperationException("Company code not found.");
    }

    private static TenantInfo MapTenant(SqlDataReader r) => new(
        r.GetGuid(r.GetOrdinal("TenantId")),
        SqlRead.String(r,"CompanyCode"),
        SqlRead.String(r,"CompanyName"),
        SqlRead.String(r,"Slug"),
        SqlRead.String(r,"DatabaseName"),
        SqlRead.String(r,"Status"),
        SqlRead.String(r,"SubscriptionPlan"),
        SqlRead.NullableDateTime(r,"ExpiryDate"),
        SqlRead.String(r,"LicenseStatus"),
        SqlRead.String(r,"DatabaseCreationStatus"),
        SqlRead.String(r,"ProvisioningStatus"),
        SqlRead.String(r,"OwnerName"),
        SqlRead.String(r,"OwnerEmail"),
        SqlRead.String(r,"OwnerMobile"),
        SqlRead.NullableDateTime(r,"TrialStartDate"),
        SqlRead.NullableDateTime(r,"TrialEndDate"),
        SqlRead.NullableDateTime(r,"RenewalDate"),
        SqlRead.NullableDateTime(r,"CreatedAt"),
        SqlRead.NullableDateTime(r,"CompanyStartDate"),
        SqlRead.NullableDateTime(r,"LicenseExpiryDate"),
        SqlRead.Bool(r,"AllowSandbox"),
        SqlRead.String(r,"ProductionDatabaseName"),
        SqlRead.String(r,"SandboxDatabaseName"),
        SqlRead.NullableDateTime(r,"SandboxCreatedAt"),
        SqlRead.String(r,"ActiveEnvironment"), SqlRead.Bool(r,"AllowMultipleBranches"), SqlRead.Int(r,"MaxBranches"));

    public async Task UpdateTenantCardAsync(string companyCode, TenantCardUpdateRequest request)
    {
        await EnsureMasterDatabaseAsync();
        var code = ConnectionFactory.NormalizeCode(companyCode);
        if (string.IsNullOrWhiteSpace(request.CompanyName)) throw new InvalidOperationException("Company name is required.");
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE Tenants
SET CompanyName=@CompanyName,
    OwnerName=@OwnerName,
    OwnerEmail=@OwnerEmail,
    OwnerMobile=@OwnerMobile,
    AdminContactName=@OwnerName,
    AdminContactEmail=@OwnerEmail,
    AdminContactMobile=@OwnerMobile,
    SubscriptionPlan=@Plan,
    Status=@Status,
    LicenseStatus=@LicenseStatus,
    CompanyStartDate=@CompanyStartDate,
    LicenseExpiryDate=@LicenseExpiryDate,
    ExpiryDate=@LicenseExpiryDate,
    RenewalDate=@RenewalDate,
    AllowSandbox=@AllowSandbox,
    AllowMultipleBranches=@AllowMultipleBranches,
    MaxBranches=@MaxBranches,
    Notes=@Notes,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
INSERT INTO TenantAuditLog(CompanyCode,ActionName,Description)
VALUES(@CompanyCode,'UPDATE_TENANT_CARD','Company card updated from Super Admin list page.');";
        cmd.Parameters.AddWithValue("@CompanyCode", code);
        cmd.Parameters.AddWithValue("@CompanyName", request.CompanyName.Trim());
        cmd.Parameters.AddWithValue("@OwnerName", request.OwnerName ?? "");
        cmd.Parameters.AddWithValue("@OwnerEmail", request.OwnerEmail ?? "");
        cmd.Parameters.AddWithValue("@OwnerMobile", request.OwnerMobile ?? "");
        cmd.Parameters.AddWithValue("@Plan", string.IsNullOrWhiteSpace(request.SubscriptionPlan) ? "Standard" : request.SubscriptionPlan.Trim());
        cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim());
        cmd.Parameters.AddWithValue("@LicenseStatus", string.IsNullOrWhiteSpace(request.LicenseStatus) ? "Active" : request.LicenseStatus.Trim());
        cmd.Parameters.AddWithValue("@CompanyStartDate", request.CompanyStartDate.HasValue ? (object)request.CompanyStartDate.Value.Date : DBNull.Value);
        cmd.Parameters.AddWithValue("@LicenseExpiryDate", request.LicenseExpiryDate.HasValue ? (object)request.LicenseExpiryDate.Value.Date : DBNull.Value);
        cmd.Parameters.AddWithValue("@RenewalDate", request.RenewalDate.HasValue ? (object)request.RenewalDate.Value.Date : DBNull.Value);
        cmd.Parameters.AddWithValue("@AllowSandbox", request.AllowSandbox);
        cmd.Parameters.AddWithValue("@AllowMultipleBranches", request.AllowMultipleBranches);
        cmd.Parameters.AddWithValue("@MaxBranches", request.AllowMultipleBranches ? Math.Max(2, request.MaxBranches) : 1);
        cmd.Parameters.AddWithValue("@Notes", request.Notes ?? "");
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new InvalidOperationException("Company code not found.");
    }

    public async Task<TenantInfo> CreateSandboxAsync(string companyCode, SandboxCreateRequest? request = null)
    {
        await EnsureMasterDatabaseAsync();
        var tenant = await _db.GetTenantByCodeAsync(companyCode) ?? throw new InvalidOperationException("Company code not found.");
        var sandboxDb = string.IsNullOrWhiteSpace(tenant.SandboxDatabaseName)
            ? $"PayNex_{tenant.CompanyCode}_SBX_DB"
            : tenant.SandboxDatabaseName;
        if (!ConnectionFactory.IsSafeDatabaseName(sandboxDb)) throw new InvalidOperationException("Invalid sandbox database name.");

        await using (var master = await _db.OpenMasterServerAsync())
        {
            await using var create = master.CreateCommand();
            create.CommandText = $"IF DB_ID(@DbName) IS NULL EXEC('CREATE DATABASE [{sandboxDb.Replace("]", "]]")}]');";
            create.Parameters.AddWithValue("@DbName", sandboxDb);
            await create.ExecuteNonQueryAsync();
        }

        await using (var sandboxCon = await _db.OpenTenantAsync(sandboxDb))
        {
            await _runner.ExecuteScriptFileAsync(sandboxCon, SchemaPath("TenantSchema.sql"));
            await SeedTenantAsync(sandboxCon, tenant.CompanyName + " Sandbox", "admin", "Admin@123", tenant.OwnerEmail);
        }

        await using (var con = await _db.OpenMasterAsync())
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"
UPDATE Tenants
SET AllowSandbox=1,
    SandboxDatabaseName=@SandboxDatabaseName,
    SandboxCreatedAt=ISNULL(SandboxCreatedAt,SYSUTCDATETIME()),
    ActiveEnvironment=CASE WHEN @SetAsDefault=1 THEN 'Sandbox' ELSE ActiveEnvironment END,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
INSERT INTO TenantAuditLog(TenantId,CompanyCode,ActionName,Description)
VALUES(@TenantId,@CompanyCode,'SANDBOX_CREATED',CONCAT('Sandbox database ready: ',@SandboxDatabaseName));";
            cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
            cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            cmd.Parameters.AddWithValue("@SandboxDatabaseName", sandboxDb);
            cmd.Parameters.AddWithValue("@SetAsDefault", request?.SetAsDefaultEnvironment == true);
            await cmd.ExecuteNonQueryAsync();
        }

        return await GetTenantAsync(tenant.CompanyCode);
    }

    public async Task UpdateTenantStatusAsync(string companyCode, TenantStatusUpdateRequest request)
    {
        await EnsureMasterDatabaseAsync();
        var code = ConnectionFactory.NormalizeCode(companyCode);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE Tenants
SET Status=@Status, LicenseStatus=@LicenseStatus, RenewalDate=@RenewalDate, Notes=@Notes, UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
INSERT INTO TenantAuditLog(CompanyCode,ActionName,Description)
VALUES(@CompanyCode,'UPDATE_TENANT_STATUS',CONCAT('Status: ',@Status, ', License: ', @LicenseStatus));";
        cmd.Parameters.AddWithValue("@CompanyCode", code);
        cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim());
        cmd.Parameters.AddWithValue("@LicenseStatus", string.IsNullOrWhiteSpace(request.LicenseStatus) ? "Active" : request.LicenseStatus.Trim());
        cmd.Parameters.AddWithValue("@RenewalDate", request.RenewalDate.HasValue ? (object)request.RenewalDate.Value.Date : DBNull.Value);
        cmd.Parameters.AddWithValue("@Notes", request.Notes ?? "");
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new InvalidOperationException("Company code not found.");
    }

    private async Task<string> GenerateNextCompanyCodeAsync()
    {
        await using var con = await _db.OpenMasterAsync();

        await using var nextCmd = con.CreateCommand();
        nextCmd.CommandText = @"
SELECT ISNULL(MAX(TRY_CONVERT(INT, SUBSTRING(CompanyCode, 4, 20))), 0) + 1
FROM Tenants
WHERE CompanyCode LIKE 'PNX%';";
        var start = Convert.ToInt32(await nextCmd.ExecuteScalarAsync());

        for (var i = 0; i < 10000; i++)
        {
            var candidate = $"PNX{start + i:000000}";
            await using var exists = con.CreateCommand();
            exists.CommandText = "SELECT COUNT(1) FROM Tenants WHERE CompanyCode=@CompanyCode";
            exists.Parameters.AddWithValue("@CompanyCode", candidate);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                return candidate;
        }

        throw new InvalidOperationException("Unable to generate company ID. Please try again.");
    }

    private async Task SeedTenantAsync(SqlConnection con, string companyName, string adminUser, string adminPassword, string? adminEmail)
    {
        var safeAdminEmail = NormalizeEmail(adminEmail);
        if (string.Equals(safeAdminEmail, NormalizeEmail(Options.PlatformOwnerEmail), StringComparison.OrdinalIgnoreCase))
            safeAdminEmail = null;
        var hash = _passwords.Hash(adminPassword);
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Admin') INSERT INTO Roles(RoleName) VALUES('Admin'); IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Company Super Admin') INSERT INTO Roles(RoleName) VALUES('Company Super Admin'); IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Manager') INSERT INTO Roles(RoleName) VALUES('Manager'); IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Cashier') INSERT INTO Roles(RoleName) VALUES('Cashier');");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Stores) INSERT INTO Stores(StoreCode,StoreName,BranchCode,BranchName,AddressLine,IsMainBranch,IsActive) VALUES('MAIN','Main Store','MAIN','Main Store','Main Branch',1,1);");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Terminals) INSERT INTO Terminals(StoreId,TerminalCode,TerminalName,IsActive) VALUES(1,'COUNTER-01','Counter 01',1);");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Customers) INSERT INTO Customers(CustomerCode,CustomerName,Mobile,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive) VALUES('WALKIN','Walk-in Customer','',0,0,0,0,1);");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Categories) INSERT INTO Categories(CategoryName) VALUES('Dairy'),('Bakery'),('Beverage'),('Grocery'),('Snacks');");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Brands) INSERT INTO Brands(BrandName) VALUES('Local'),('Nestle'),('Pepsi'),('Dawn'),('National');");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM TaxGroups) INSERT INTO TaxGroups(TaxGroupName,TaxPercent,IsInclusive) VALUES('GST 18%',18,0),('Zero Rated',0,0);");
        await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM PaymentMethods) INSERT INTO PaymentMethods(PaymentMethodName,RequiresReference,IsActive) VALUES('Cash',0,1),('Card',1,1),('Wallet',1,1),('Bank Transfer',1,1),('Credit',0,1);");
        await ExecAsync(con, @"
MERGE ChartOfAccounts AS target USING (VALUES
('1000','Cash','Asset','Debit'),('1010','Bank','Asset','Debit'),('1100','Accounts Receivable','Asset','Debit'),('1200','Inventory','Asset','Debit'),('1300','Input GST','Asset','Debit'),
('2000','Accounts Payable','Liability','Credit'),('2100','Output GST Payable','Liability','Credit'),('3000','Opening Balance Equity','Equity','Credit'),('4000','Sales Revenue','Income','Credit'),('4010','Sales Returns','Income','Debit'),('5000','Cost of Goods Sold','Expense','Debit'),('5100','Stock Adjustment','Expense','Debit')
) AS source(AccountNo,AccountName,AccountType,NormalBalance) ON target.AccountNo=source.AccountNo
WHEN NOT MATCHED THEN INSERT(AccountNo,AccountName,AccountType,NormalBalance,IsSystem,IsActive) VALUES(source.AccountNo,source.AccountName,source.AccountType,source.NormalBalance,1,1);");
        await ExecAsync(con, @"
IF NOT EXISTS(SELECT 1 FROM PostingSetup WHERE SetupId=1)
INSERT INTO PostingSetup(SetupId,CashAccount,BankAccount,ReceivableAccount,InventoryAccount,InputTaxAccount,PayableAccount,OutputTaxAccount,OpeningBalanceAccount,SalesAccount,SalesReturnAccount,CogsAccount,StockAdjustmentAccount,CashierDiscountLimit,BlockNegativeStock,CostingMethod)
VALUES(1,'1000','1010','1100','1200','1300','2000','2100','3000','4000','4010','5000','5100',5,1,'Average');");
        await ExecAsync(con, @"
MERGE NumberSeries AS t USING (VALUES('POS','POS'),('PURCHASE_INVOICE','PI'),('SALES_INVOICE','SI'),('RETURN','RET'),('CUSTOMER_PAYMENT','CPAY'),('VENDOR_PAYMENT','VPAY'),('TRANSFER','TRF'),('ADJUSTMENT','ADJ'),('HOLD','HOLD'),('SALES_QUOTE','SQ'),('SALES_ORDER','SO'),('BACKUP','BKP')) s(SeriesCode,Prefix)
ON t.SeriesCode=s.SeriesCode WHEN NOT MATCHED THEN INSERT(SeriesCode,Prefix,LastNumber,NumberLength,IncludeDate) VALUES(s.SeriesCode,s.Prefix,0,6,1);");
        await using (var comp = con.CreateCommand())
        {
            comp.CommandText = @"IF NOT EXISTS(SELECT 1 FROM CompanyInformation) INSERT INTO CompanyInformation(CompanyName,AddressLine,PhoneNo,Email,Website,TaxRegistrationNo,LogoPath) VALUES(@Company,'Main Branch','','','','','') ELSE UPDATE CompanyInformation SET CompanyName=@Company,UpdatedAt=SYSUTCDATETIME();";
            comp.Parameters.AddWithValue("@Company", companyName);
            await comp.ExecuteNonQueryAsync();
        }
        await using (var userCmd = con.CreateCommand())
        {
            userCmd.CommandText = @"
IF COL_LENGTH('Users','OwnerVisiblePassword') IS NULL ALTER TABLE Users ADD OwnerVisiblePassword NVARCHAR(128) NULL;
IF NOT EXISTS(SELECT 1 FROM Users WHERE UserName=@UserName)
    INSERT INTO Users(UserName,DisplayName,Email,EmailVerified,PasswordHash,OwnerVisiblePassword,RoleId,StoreId,IsCompanySuperAdmin,IsActive)
    VALUES(@UserName,'System Admin',@Email,0,@Hash,@Pwd,(SELECT TOP 1 RoleId FROM Roles WHERE RoleName='Admin'),1,1,1)
ELSE
    UPDATE Users SET Email=CASE WHEN ISNULL(Email,'')='' THEN @Email ELSE Email END, IsCompanySuperAdmin=1, OwnerVisiblePassword=CASE WHEN ISNULL(OwnerVisiblePassword,'')='' THEN @Pwd ELSE OwnerVisiblePassword END, UpdatedAt=SYSUTCDATETIME() WHERE UserName=@UserName;";
            userCmd.Parameters.AddWithValue("@UserName", adminUser);
            userCmd.Parameters.AddWithValue("@Email", safeAdminEmail ?? $"{adminUser.ToLowerInvariant()}@paynex.local");
            userCmd.Parameters.AddWithValue("@Hash", hash);
            userCmd.Parameters.AddWithValue("@Pwd", adminPassword);
            await userCmd.ExecuteNonQueryAsync();
        }
        
        await ExecAsync(con, @"
IF NOT EXISTS(SELECT 1 FROM Stores WHERE StoreCode='WH') INSERT INTO Stores(StoreCode,StoreName,BranchCode,BranchName,AddressLine,IsMainBranch,IsActive) VALUES('WH','Warehouse','WH','Warehouse','Warehouse Store',0,1);
IF NOT EXISTS(SELECT 1 FROM Terminals WHERE TerminalCode='COUNTER-02') INSERT INTO Terminals(StoreId,TerminalCode,TerminalName,IsActive) VALUES(1,'COUNTER-02','Counter 02',1);
IF NOT EXISTS(SELECT 1 FROM Users WHERE UserName='manager') INSERT INTO Users(UserName,DisplayName,PasswordHash,OwnerVisiblePassword,RoleId,StoreId,IsActive) VALUES('manager','Store Manager',@HASH,N'Admin@123',(SELECT TOP 1 RoleId FROM Roles WHERE RoleName='Manager'),1,1);
IF NOT EXISTS(SELECT 1 FROM Users WHERE UserName='cashier') INSERT INTO Users(UserName,DisplayName,PasswordHash,OwnerVisiblePassword,RoleId,StoreId,IsActive) VALUES('cashier','Counter Cashier',@HASH,N'Admin@123',(SELECT TOP 1 RoleId FROM Roles WHERE RoleName='Cashier'),1,1);
UPDATE Users SET OwnerVisiblePassword=N'Admin@123' WHERE UserName IN ('manager','cashier') AND ISNULL(OwnerVisiblePassword,'')='';
IF NOT EXISTS(SELECT 1 FROM Customers WHERE CustomerCode='C001') INSERT INTO Customers(CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive) VALUES('C001','Ahmed Traders','03001234567','ahmed@example.com','Lahore',50000,0,0,0,1);
IF NOT EXISTS(SELECT 1 FROM Customers WHERE CustomerCode='C002') INSERT INTO Customers(CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive) VALUES('C002','Ali General Store','03007654321','ali@example.com','Karachi',75000,0,0,0,1);
IF NOT EXISTS(SELECT 1 FROM Vendors WHERE VendorCode='V001') INSERT INTO Vendors(VendorCode,VendorName,ContactPerson,Mobile,Email,AddressLine,PaymentTerms,OpeningBalance,CurrentBalance,IsActive) VALUES('V001','Metro Supplier','Mr. Metro','03001112222','metro@example.com','Lahore','15 Days',0,0,1);
IF NOT EXISTS(SELECT 1 FROM Vendors WHERE VendorCode='V002') INSERT INTO Vendors(VendorCode,VendorName,ContactPerson,Mobile,Email,AddressLine,PaymentTerms,OpeningBalance,CurrentBalance,IsActive) VALUES('V002','Grocery Wholesale','Mr. Grocery','03005556666','grocery@example.com','Karachi','30 Days',0,0,1);
".Replace("@HASH", "N'" + hash.Replace("'", "''") + "'"));

await ExecAsync(con, @"
IF NOT EXISTS(SELECT 1 FROM Products)
BEGIN
INSERT INTO Products(ProductCode,Barcode,ProductName,CategoryId,BrandId,UnitOfMeasure,PurchasePrice,SalePrice,RetailPrice,TaxGroupId,DiscountAllowed,MinStockLevel,ReorderLevel,StockOnHand,IsActive)
VALUES ('P-1001','111001','Milk 1 Liter',1,2,'PCS',210,250,250,1,1,10,20,100,1),('P-1002','111002','Bread Large',2,4,'PCS',120,160,160,2,1,10,20,80,1),('P-1003','111003','Soft Drink 1.5L',3,3,'PCS',150,200,200,1,1,10,20,120,1),('P-1004','111004','Rice 5KG',4,5,'BAG',1450,1650,1650,2,0,5,10,50,1);
INSERT INTO StockByStore(StoreId,ProductId,Quantity,AverageCost) SELECT 1,ProductId,StockOnHand,PurchasePrice FROM Products;
END");
    }


    private async Task SyncSeededAdminCentralDirectoryAsync(Guid tenantId, string companyCode, string databaseName, string adminUser, string adminEmail, string adminPassword, string companyName)
    {
        var email = NormalizeEmail(adminEmail) ?? $"{adminUser.ToLowerInvariant()}@{companyCode.ToLowerInvariant()}.paynex.local";
        if (string.Equals(email, NormalizeEmail(Options.PlatformOwnerEmail), StringComparison.OrdinalIgnoreCase))
            email = $"{adminUser.ToLowerInvariant()}@{companyCode.ToLowerInvariant()}.paynex.local";
        var hash = _passwords.Hash(adminPassword);
        int userId = 0;
        await using (var tenantCon = await _db.OpenTenantAsync(databaseName))
        await using (var getUser = tenantCon.CreateCommand())
        {
            getUser.CommandText = "SELECT TOP 1 UserId FROM Users WHERE UserName=@UserName ORDER BY UserId";
            getUser.Parameters.AddWithValue("@UserName", adminUser);
            userId = Convert.ToInt32(await getUser.ExecuteScalarAsync() ?? 0);
        }
        if (userId <= 0) return;

        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM CentralUserDirectory WHERE Email=@Email AND CompanyCode=@CompanyCode)
BEGIN
    UPDATE CentralUserDirectory
    SET TenantId=@TenantId,UserId=@UserId,UserName=@UserName,DisplayName=@DisplayName,PasswordHash=@PasswordHash,RoleName='Admin',IsCompanySuperAdmin=1,IsActive=1,UpdatedAt=SYSUTCDATETIME()
    WHERE Email=@Email AND CompanyCode=@CompanyCode;
END
ELSE
BEGIN
    INSERT INTO CentralUserDirectory(TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsDefaultCompany,IsActive)
    VALUES(@TenantId,@CompanyCode,@UserId,@Email,@UserName,@DisplayName,@PasswordHash,0,'Admin',1,1,1);
END";
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode);
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@UserName", adminUser);
        cmd.Parameters.AddWithValue("@DisplayName", "System Admin");
        cmd.Parameters.AddWithValue("@PasswordHash", hash);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string? NormalizeEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        return value.Contains('@') && value.Contains('.') ? value : null;
    }

    private static async Task ExecAsync(SqlConnection con, string sql)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string SchemaPath(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "database", fileName);
        if (File.Exists(path)) return path;
        path = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "database", fileName));
        return path;
    }
}
