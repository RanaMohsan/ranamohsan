using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;

namespace PayNex.Cloud.Api.Security;

public sealed class SuperAdminService
{
    private readonly ConnectionFactory _db;
    private readonly PasswordService _passwords;
    private readonly PayNexOptions _options;

    public SuperAdminService(ConnectionFactory db, PasswordService passwords, IOptions<PayNexOptions> options)
    {
        _db = db;
        _passwords = passwords;
        _options = options.Value;
    }

    public async Task EnsureBootstrapSuperAdminAsync()
    {
        await UpsertSuperAdminAsync(
            _options.SuperAdminUserName.Trim(),
            _options.SuperAdminDisplayName.Trim(),
            _options.SuperAdminEmail.Trim(),
            _options.SuperAdminBootstrapPassword);

        var ownerEmail = NormalizeEmail(_options.PlatformOwnerEmail);
        if (!string.IsNullOrWhiteSpace(ownerEmail))
        {
            await UpsertSuperAdminAsync(
                string.IsNullOrWhiteSpace(_options.PlatformOwnerUserName) ? ownerEmail : _options.PlatformOwnerUserName.Trim(),
                string.IsNullOrWhiteSpace(_options.PlatformOwnerDisplayName) ? "PayNex Owner" : _options.PlatformOwnerDisplayName.Trim(),
                ownerEmail,
                string.IsNullOrWhiteSpace(_options.PlatformOwnerBootstrapPassword) ? _options.SuperAdminBootstrapPassword : _options.PlatformOwnerBootstrapPassword);
        }
    }

    private async Task UpsertSuperAdminAsync(string userName, string displayName, string email, string password)
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM SuperAdminUsers WHERE UserName=@UserName OR LOWER(ISNULL(Email,''))=@Email)
BEGIN
    UPDATE SuperAdminUsers
    SET UserName=@UserName,
        DisplayName=@DisplayName,
        Email=@Email,
        PasswordHash=@PasswordHash,
        RoleName='SuperAdmin',
        IsActive=1
    WHERE UserName=@UserName OR LOWER(ISNULL(Email,''))=@Email;
END
ELSE
BEGIN
    INSERT INTO SuperAdminUsers(UserName,DisplayName,Email,PasswordHash,RoleName,IsActive)
    VALUES(@UserName,@DisplayName,@Email,@PasswordHash,'SuperAdmin',1);
END";
        cmd.Parameters.AddWithValue("@UserName", userName);
        cmd.Parameters.AddWithValue("@DisplayName", displayName);
        cmd.Parameters.AddWithValue("@Email", NormalizeEmail(email));
        cmd.Parameters.AddWithValue("@PasswordHash", _passwords.Hash(password));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<SuperAdminSession?> LoginAsync(SuperAdminLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password)) return null;
        if (!string.IsNullOrWhiteSpace(request.CompanyCode) &&
            !string.Equals(request.CompanyCode.Trim(), _options.SuperAdminCompanyId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            await LogSecurityAsync(request.UserName, "SUPER_ADMIN_LOGIN", null, "Failed", "Invalid Super Admin company ID");
            return null;
        }
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 SuperAdminUserId,UserName,DisplayName,Email,PasswordHash,RoleName
FROM SuperAdminUsers
WHERE (UserName=@UserName OR LOWER(ISNULL(Email,''))=@Email) AND IsActive=1";
        cmd.Parameters.AddWithValue("@UserName", request.UserName.Trim());
        cmd.Parameters.AddWithValue("@Email", NormalizeEmail(request.UserName));
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            await LogSecurityAsync(request.UserName, "SUPER_ADMIN_LOGIN", null, "Failed", "User not found or inactive");
            return null;
        }

        var hash = SqlRead.String(r, "PasswordHash");
        if (!_passwords.Verify(request.Password, hash))
        {
            await LogSecurityAsync(request.UserName, "SUPER_ADMIN_LOGIN", null, "Failed", "Invalid password");
            return null;
        }

        var session = new SuperAdminSession(
            SqlRead.Int(r, "SuperAdminUserId"),
            SqlRead.String(r, "UserName"),
            SqlRead.String(r, "DisplayName"),
            SqlRead.String(r, "Email"),
            SqlRead.String(r, "RoleName"));

        await r.DisposeAsync();
        await using var update = con.CreateCommand();
        update.CommandText = "UPDATE SuperAdminUsers SET LastLoginAt=SYSUTCDATETIME() WHERE SuperAdminUserId=@Id";
        update.Parameters.AddWithValue("@Id", session.SuperAdminUserId);
        await update.ExecuteNonQueryAsync();
        await LogSecurityAsync(session.UserName, "SUPER_ADMIN_LOGIN", null, "Success", "Super admin login successful");
        return session;
    }


    private static string NormalizeEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        return value.Contains('@') && value.Contains('.') ? value : string.Empty;
    }

    public async Task LogSecurityAsync(string? userName, string actionName, string? path, string result, string? details)
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO SecurityAuditLog(UserName,ActionName,Path,IpAddress,Result,Details)
VALUES(@UserName,@ActionName,@Path,NULL,@Result,@Details)";
        cmd.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ActionName", actionName);
        cmd.Parameters.AddWithValue("@Path", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Result", result);
        cmd.Parameters.AddWithValue("@Details", (object?)details ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
