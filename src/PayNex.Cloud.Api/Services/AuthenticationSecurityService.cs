using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using System.Data;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PayNex.Cloud.Api.Services;

public sealed class AuthenticationSecurityService
{
    private readonly ConnectionFactory _db;
    private readonly PasswordService _passwords;
    private readonly IConfiguration _configuration;
    private readonly byte[] _encryptionKey;

    public AuthenticationSecurityService(ConnectionFactory db, PasswordService passwords, IConfiguration configuration)
    {
        _db = db;
        _passwords = passwords;
        _configuration = configuration;
        var secret = _db.Options.TokenSecret ?? string.Empty;
        if (secret.Length < 32) secret = secret.PadRight(32, '#');
        _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public int AccessTokenExpiryMinutes => Math.Clamp(_db.Options.AccessTokenExpiryMinutes, 5, 120);
    public int RefreshTokenExpiryDays => Math.Clamp(_db.Options.RefreshTokenExpiryDays, 1, 30);
    public int DefaultTrustedDeviceDays => Math.Clamp(_db.Options.TrustedDeviceDays, 1, 90);
    public int DefaultOtpExpiryMinutes => Math.Clamp(_db.Options.LoginOtpExpiryMinutes, 5, 30);

    public async Task EnsureSchemaAsync()
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('PlatformEmailSecuritySettings') IS NULL
BEGIN
    CREATE TABLE PlatformEmailSecuritySettings(
        SettingId INT NOT NULL PRIMARY KEY,
        FromEmail NVARCHAR(180) NOT NULL,
        FromName NVARCHAR(150) NOT NULL,
        SmtpHost NVARCHAR(180) NOT NULL,
        SmtpPort INT NOT NULL,
        SmtpUser NVARCHAR(180) NULL,
        SmtpPasswordProtected NVARCHAR(MAX) NULL,
        EnableSsl BIT NOT NULL DEFAULT 1,
        ReturnDevOtp BIT NOT NULL DEFAULT 0,
        LoginOtpExpiryMinutes INT NOT NULL DEFAULT 10,
        TrustedDeviceDays INT NOT NULL DEFAULT 30,
        UpdatedBy NVARCHAR(180) NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID('LoginSecurityState') IS NULL
BEGIN
    CREATE TABLE LoginSecurityState(
        EmailKey NVARCHAR(180) NOT NULL,
        IpAddress NVARCHAR(80) NOT NULL,
        FailedCount INT NOT NULL DEFAULT 0,
        LockedUntil DATETIME2 NULL,
        LastFailedAt DATETIME2 NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LoginSecurityState PRIMARY KEY(EmailKey,IpAddress)
    );
END;
IF OBJECT_ID('LoginMfaChallenges') IS NULL
BEGIN
    CREATE TABLE LoginMfaChallenges(
        ChallengeId NVARCHAR(64) NOT NULL PRIMARY KEY,
        Email NVARCHAR(180) NOT NULL,
        CompanyCode NVARCHAR(40) NOT NULL,
        PendingSessionProtected NVARCHAR(MAX) NOT NULL,
        CodeHash NVARCHAR(500) NOT NULL,
        AttemptCount INT NOT NULL DEFAULT 0,
        MaxAttempts INT NOT NULL DEFAULT 5,
        ExpiresAt DATETIME2 NOT NULL,
        LastSentAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UsedAt DATETIME2 NULL,
        IpAddress NVARCHAR(80) NULL,
        UserAgent NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID('TrustedLoginDevices') IS NULL
BEGIN
    CREATE TABLE TrustedLoginDevices(
        TrustedDeviceId BIGINT IDENTITY(1,1) PRIMARY KEY,
        Email NVARCHAR(180) NOT NULL,
        CompanyCode NVARCHAR(40) NOT NULL,
        TokenHash CHAR(64) NOT NULL UNIQUE,
        UserAgentHash CHAR(64) NOT NULL,
        DeviceName NVARCHAR(180) NULL,
        ExpiresAt DATETIME2 NOT NULL,
        LastUsedAt DATETIME2 NULL,
        RevokedAt DATETIME2 NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID('AuthRefreshTokens') IS NULL
BEGIN
    CREATE TABLE AuthRefreshTokens(
        RefreshTokenId BIGINT IDENTITY(1,1) PRIMARY KEY,
        TokenHash CHAR(64) NOT NULL UNIQUE,
        SessionId NVARCHAR(80) NOT NULL,
        Email NVARCHAR(180) NULL,
        CompanyCode NVARCHAR(40) NULL,
        UserId INT NULL,
        ProtectedSessionJson NVARCHAR(MAX) NOT NULL,
        ExpiresAt DATETIME2 NOT NULL,
        LastUsedAt DATETIME2 NULL,
        RevokedAt DATETIME2 NULL,
        ReplacedByTokenHash CHAR(64) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID('PlatformDeleteClientChallenges') IS NULL
BEGIN
    CREATE TABLE PlatformDeleteClientChallenges(
        ChallengeId NVARCHAR(64) NOT NULL PRIMARY KEY,
        CompanyCode NVARCHAR(40) NOT NULL,
        OwnerEmail NVARCHAR(180) NOT NULL,
        CodeHash NVARCHAR(500) NOT NULL,
        DeleteTokenHash CHAR(64) NULL,
        AttemptCount INT NOT NULL DEFAULT 0,
        MaxAttempts INT NOT NULL DEFAULT 5,
        ExpiresAt DATETIME2 NOT NULL,
        VerifiedAt DATETIME2 NULL,
        UsedAt DATETIME2 NULL,
        IpAddress NVARCHAR(80) NULL,
        UserAgent NVARCHAR(500) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;";
        await cmd.ExecuteNonQueryAsync();
        await SeedDefaultSmtpIfMissingAsync();
    }

    public async Task<EffectiveEmailSecuritySettings> GetEffectiveEmailSettingsAsync()
    {
        await EnsureSchemaAsync();
        var config = ReadConfigEmailSettings();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT TOP 1 FromEmail,FromName,SmtpHost,SmtpPort,ISNULL(SmtpUser,'') SmtpUser,
ISNULL(SmtpPasswordProtected,'') SmtpPasswordProtected,EnableSsl,ReturnDevOtp,LoginOtpExpiryMinutes,TrustedDeviceDays
FROM PlatformEmailSecuritySettings WHERE SettingId=1";
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            var protectedPassword = SqlRead.String(r, "SmtpPasswordProtected");
            var fromEmail = SqlRead.String(r, "FromEmail");
            var fromName = SqlRead.String(r, "FromName");
            var host = SqlRead.String(r, "SmtpHost");
            var port = SqlRead.Int(r, "SmtpPort");
            var smtpUser = SqlRead.String(r, "SmtpUser");
            var password = string.IsNullOrWhiteSpace(protectedPassword) ? string.Empty : Unprotect(protectedPassword);
            var enableSsl = SqlRead.Bool(r, "EnableSsl");
            if (string.IsNullOrWhiteSpace(host))
            {
                fromEmail = string.IsNullOrWhiteSpace(fromEmail) || fromEmail.Equals("no-reply@paynex.local", StringComparison.OrdinalIgnoreCase)
                    ? config.FromEmail : fromEmail;
                fromName = string.IsNullOrWhiteSpace(fromName) ? config.FromName : fromName;
                host = config.SmtpHost;
                if (port < 1) port = config.SmtpPort;
                if (string.IsNullOrWhiteSpace(smtpUser)) smtpUser = config.SmtpUser;
                if (string.IsNullOrWhiteSpace(password)) password = config.SmtpPassword;
                enableSsl = config.EnableSsl;
            }
            return new EffectiveEmailSecuritySettings(
                fromEmail,
                fromName,
                host,
                port < 1 ? 587 : port,
                smtpUser,
                password,
                enableSsl,
                SqlRead.Bool(r, "ReturnDevOtp"),
                Math.Clamp(SqlRead.Int(r, "LoginOtpExpiryMinutes"), 5, 30),
                Math.Clamp(SqlRead.Int(r, "TrustedDeviceDays"), 1, 90),
                true);
        }

        return config;
    }

    private EffectiveEmailSecuritySettings ReadConfigEmailSettings() => new(
        _configuration["OtpEmail:FromEmail"] ?? "noreply.paynex@gmail.com",
        _configuration["OtpEmail:FromName"] ?? "InterNex Cloud ERP",
        _configuration["OtpEmail:SmtpHost"] ?? string.Empty,
        _configuration.GetValue<int?>("OtpEmail:SmtpPort") ?? 587,
        _configuration["OtpEmail:SmtpUser"] ?? string.Empty,
        _configuration["OtpEmail:SmtpPassword"] ?? string.Empty,
        _configuration.GetValue<bool?>("OtpEmail:EnableSsl") ?? true,
        _configuration.GetValue<bool?>("OtpEmail:ReturnDevOtp") ?? false,
        DefaultOtpExpiryMinutes,
        DefaultTrustedDeviceDays,
        false);

    private async Task SeedDefaultSmtpIfMissingAsync()
    {
        var config = ReadConfigEmailSettings();
        if (string.IsNullOrWhiteSpace(config.SmtpHost)) return;

        await using var con = await _db.OpenMasterAsync();
        string existingHost = string.Empty;
        await using (var read = con.CreateCommand())
        {
            read.CommandText = "SELECT TOP 1 ISNULL(SmtpHost,'') SmtpHost FROM PlatformEmailSecuritySettings WHERE SettingId=1";
            existingHost = (Convert.ToString(await read.ExecuteScalarAsync()) ?? string.Empty).Trim();
        }
        if (!string.IsNullOrWhiteSpace(existingHost)) return;

        var protectedPassword = string.IsNullOrWhiteSpace(config.SmtpPassword) ? string.Empty : Protect(config.SmtpPassword);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
MERGE PlatformEmailSecuritySettings AS target
USING (SELECT CAST(1 AS INT) SettingId) AS source ON target.SettingId=source.SettingId
WHEN MATCHED AND LTRIM(RTRIM(ISNULL(target.SmtpHost,''))) = '' THEN UPDATE SET
    FromEmail=@FromEmail,FromName=@FromName,SmtpHost=@SmtpHost,SmtpPort=@SmtpPort,SmtpUser=@SmtpUser,
    SmtpPasswordProtected=@SmtpPasswordProtected,EnableSsl=@EnableSsl,UpdatedBy=@UpdatedBy,UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(SettingId,FromEmail,FromName,SmtpHost,SmtpPort,SmtpUser,SmtpPasswordProtected,EnableSsl,ReturnDevOtp,LoginOtpExpiryMinutes,TrustedDeviceDays,UpdatedBy)
VALUES(1,@FromEmail,@FromName,@SmtpHost,@SmtpPort,@SmtpUser,@SmtpPasswordProtected,@EnableSsl,@ReturnDevOtp,@LoginOtpExpiryMinutes,@TrustedDeviceDays,@UpdatedBy);";
        cmd.Parameters.AddWithValue("@FromEmail", config.FromEmail);
        cmd.Parameters.AddWithValue("@FromName", config.FromName);
        cmd.Parameters.AddWithValue("@SmtpHost", config.SmtpHost.Trim());
        cmd.Parameters.AddWithValue("@SmtpPort", config.SmtpPort < 1 ? 587 : config.SmtpPort);
        cmd.Parameters.AddWithValue("@SmtpUser", config.SmtpUser);
        cmd.Parameters.AddWithValue("@SmtpPasswordProtected", protectedPassword);
        cmd.Parameters.AddWithValue("@EnableSsl", config.EnableSsl);
        cmd.Parameters.AddWithValue("@ReturnDevOtp", config.ReturnDevOtp);
        cmd.Parameters.AddWithValue("@LoginOtpExpiryMinutes", DefaultOtpExpiryMinutes);
        cmd.Parameters.AddWithValue("@TrustedDeviceDays", DefaultTrustedDeviceDays);
        cmd.Parameters.AddWithValue("@UpdatedBy", "system-smtp-seed");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveEmailSettingsAsync(OwnerEmailSecuritySettingsRequest request, string updatedBy)
    {
        await EnsureSchemaAsync();
        var fromEmail = NormalizeEmail(request.FromEmail);
        if (string.IsNullOrWhiteSpace(fromEmail)) throw new InvalidOperationException("A valid sender email address is required.");
        if (request.SmtpPort < 1 || request.SmtpPort > 65535) throw new InvalidOperationException("SMTP port must be between 1 and 65535.");

        var existing = await GetEffectiveEmailSettingsAsync();
        var password = request.ClearStoredPassword
            ? string.Empty
            : string.IsNullOrWhiteSpace(request.SmtpPassword) ? existing.SmtpPassword : request.SmtpPassword!;
        var protectedPassword = string.IsNullOrEmpty(password) ? string.Empty : Protect(password);

        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
MERGE PlatformEmailSecuritySettings AS target
USING (SELECT CAST(1 AS INT) SettingId) AS source ON target.SettingId=source.SettingId
WHEN MATCHED THEN UPDATE SET FromEmail=@FromEmail,FromName=@FromName,SmtpHost=@SmtpHost,SmtpPort=@SmtpPort,
SmtpUser=@SmtpUser,SmtpPasswordProtected=@SmtpPasswordProtected,EnableSsl=@EnableSsl,ReturnDevOtp=@ReturnDevOtp,
LoginOtpExpiryMinutes=@LoginOtpExpiryMinutes,TrustedDeviceDays=@TrustedDeviceDays,UpdatedBy=@UpdatedBy,UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(SettingId,FromEmail,FromName,SmtpHost,SmtpPort,SmtpUser,SmtpPasswordProtected,EnableSsl,ReturnDevOtp,LoginOtpExpiryMinutes,TrustedDeviceDays,UpdatedBy)
VALUES(1,@FromEmail,@FromName,@SmtpHost,@SmtpPort,@SmtpUser,@SmtpPasswordProtected,@EnableSsl,@ReturnDevOtp,@LoginOtpExpiryMinutes,@TrustedDeviceDays,@UpdatedBy);";
        cmd.Parameters.AddWithValue("@FromEmail", fromEmail);
        cmd.Parameters.AddWithValue("@FromName", string.IsNullOrWhiteSpace(request.FromName) ? "InterNex Cloud ERP" : request.FromName.Trim());
        cmd.Parameters.AddWithValue("@SmtpHost", (request.SmtpHost ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("@SmtpPort", request.SmtpPort);
        cmd.Parameters.AddWithValue("@SmtpUser", (request.SmtpUser ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("@SmtpPasswordProtected", protectedPassword);
        cmd.Parameters.AddWithValue("@EnableSsl", request.EnableSsl);
        cmd.Parameters.AddWithValue("@ReturnDevOtp", request.ReturnDevOtp);
        cmd.Parameters.AddWithValue("@LoginOtpExpiryMinutes", Math.Clamp(request.LoginOtpExpiryMinutes, 5, 30));
        cmd.Parameters.AddWithValue("@TrustedDeviceDays", Math.Clamp(request.TrustedDeviceDays, 1, 90));
        cmd.Parameters.AddWithValue("@UpdatedBy", updatedBy ?? string.Empty);
        await cmd.ExecuteNonQueryAsync();
        await WriteSecurityAuditAsync(updatedBy, "OWNER_EMAIL_SECURITY_SETTINGS_UPDATED", "Success", $"Sender={fromEmail}; Host={request.SmtpHost}; Port={request.SmtpPort}", null, null);
    }

    public async Task<EmailDeliveryResult> SendOtpEmailAsync(string toEmail, string code, string companyName, string requestedBy, string purpose)
    {
        var settings = await GetEffectiveEmailSettingsAsync();
        var subject = purpose.Equals("LOGIN_MFA", StringComparison.OrdinalIgnoreCase)
            ? "InterNex Cloud ERP sign-in verification code"
            : purpose.Equals("DELETE_CLIENT", StringComparison.OrdinalIgnoreCase)
                ? "InterNex Cloud ERP client deletion code"
                : "InterNex Cloud ERP email verification code";
        var expiry = purpose.Equals("LOGIN_MFA", StringComparison.OrdinalIgnoreCase) || purpose.Equals("DELETE_CLIENT", StringComparison.OrdinalIgnoreCase)
            ? settings.LoginOtpExpiryMinutes
            : 15;
        var safeCompany = System.Net.WebUtility.HtmlEncode(companyName ?? "InterNex");
        var safeCode = System.Net.WebUtility.HtmlEncode(code ?? string.Empty);
        var html = $@"<!DOCTYPE html><html><body style=""margin:0;padding:24px;background:#f4f6f8;font-family:Segoe UI,Arial,sans-serif;color:#1b2430"">
<div style=""max-width:480px;margin:0 auto;background:#fff;border-radius:12px;padding:28px 24px;border:1px solid #e5eaf0"">
  <div style=""text-align:center;font-size:14px;color:#5b6b7c;margin-bottom:8px"">InterNex Cloud ERP</div>
  <div style=""text-align:center;font-size:18px;font-weight:700;margin-bottom:18px"">Verification code</div>
  <div style=""text-align:center;font-size:42px;font-weight:800;letter-spacing:10px;line-height:1.2;color:#0b5cab;padding:18px 8px;background:#f3f8fd;border-radius:10px;margin:0 0 16px"">{safeCode}</div>
  <p style=""text-align:center;font-size:14px;margin:0 0 8px""><b>Don't share this code</b> with anyone.</p>
  <p style=""text-align:center;font-size:13px;color:#5b6b7c;margin:0"">Company: {safeCompany}<br>Expires in {expiry} minutes.</p>
</div></body></html>";
        return await SendEmailAsync(settings, toEmail, subject, html, isHtml: true);
    }

    public async Task<EmailDeliveryResult> SendNewUserCredentialsEmailAsync(
        string toEmail,
        string displayName,
        string companyName,
        string userName,
        string initialPassword)
    {
        var normalizedEmail = NormalizeEmail(toEmail);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
            return new EmailDeliveryResult(false, false, false, string.Empty, "A valid user email address is required to send login details.");
        if (string.IsNullOrWhiteSpace(initialPassword))
            return new EmailDeliveryResult(false, false, false, string.Empty, "An initial password is required to send login details.");

        try
        {
            var settings = await GetEffectiveEmailSettingsAsync();
            var publicBaseUrl = (_configuration["PayNex:PublicBaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
            var loginLine = string.IsNullOrWhiteSpace(publicBaseUrl)
                ? "Login page: Open the InterNex Cloud ERP address provided by your administrator."
                : $"Login page: {publicBaseUrl}/login.html";
            var trustedDays = Math.Clamp(settings.TrustedDeviceDays, 1, 90);
            var body = $@"Hello {displayName},

Your InterNex Cloud ERP user account has been created.

Company: {companyName}
Login email: {normalizedEmail}
User name: {userName}
Initial password: {initialPassword}
{loginLine}

On your first sign-in, a 6-digit OTP will be sent to this email address. After successful OTP verification, this browser will remain trusted for {trustedDays} days. OTP verification will be required again after that trust period expires or when you use a new browser/device.

Keep this email and password private. If you did not expect this account, contact your company administrator.";

            var result = await SendEmailAsync(settings, normalizedEmail, "Your InterNex Cloud ERP login details", body);
            await WriteSecurityAuditAsync(
                normalizedEmail,
                "NEW_USER_CREDENTIALS_EMAIL",
                result.Sent ? "Success" : "Failed",
                $"Company={companyName}; Sender={result.FromEmail}; Status={result.Message}",
                null,
                null);
            return result;
        }
        catch (Exception ex)
        {
            return new EmailDeliveryResult(false, false, false, string.Empty, $"Login details email could not be sent: {Limit(ex.Message, 400)}");
        }
    }

    public async Task<EmailDeliveryResult> SendTestEmailAsync(string toEmail)
    {
        var normalized = NormalizeEmail(toEmail);
        if (string.IsNullOrWhiteSpace(normalized)) throw new InvalidOperationException("A valid test recipient email is required.");
        var settings = await GetEffectiveEmailSettingsAsync();
        return await SendEmailAsync(settings, normalized, "InterNex Cloud ERP email setup test", "This is a test email from the InterNex owner-managed Email & OTP Security Setup. This mailbox sends new-user login details and sign-in OTP codes.");
    }

    public async Task<DateTimeOffset?> GetLockoutUntilAsync(string email, string ipAddress)
    {
        await EnsureSchemaAsync();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT MAX(LockedUntil) FROM LoginSecurityState
WHERE EmailKey=@Email AND IpAddress IN (@Ip,'*') AND LockedUntil>SYSUTCDATETIME()";
        cmd.Parameters.AddWithValue("@Email", LoginKey(email));
        cmd.Parameters.AddWithValue("@Ip", NormalizeIp(ipAddress));
        var value = await cmd.ExecuteScalarAsync();
        if (value == null || value == DBNull.Value) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(Convert.ToDateTime(value), DateTimeKind.Utc));
    }

    public async Task RecordFailedLoginAsync(string email, string ipAddress, string userAgent, string reason)
    {
        await EnsureSchemaAsync();
        var maxAttempts = Math.Clamp(_db.Options.MaxFailedLoginAttempts, 3, 20);
        var lockoutMinutes = Math.Clamp(_db.Options.LoginLockoutMinutes, 5, 120);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
DECLARE @Keys TABLE(EmailKey NVARCHAR(180),IpAddress NVARCHAR(80));
INSERT INTO @Keys VALUES(@Email,@Ip),(@Email,'*');
MERGE LoginSecurityState AS target
USING @Keys AS source
ON target.EmailKey=source.EmailKey AND target.IpAddress=source.IpAddress
WHEN MATCHED THEN UPDATE SET
 FailedCount=CASE WHEN target.LockedUntil IS NOT NULL AND target.LockedUntil<=SYSUTCDATETIME() THEN 1 ELSE target.FailedCount+1 END,
 LockedUntil=CASE WHEN (CASE WHEN target.LockedUntil IS NOT NULL AND target.LockedUntil<=SYSUTCDATETIME() THEN 1 ELSE target.FailedCount+1 END)>=@MaxAttempts THEN DATEADD(MINUTE,@LockoutMinutes,SYSUTCDATETIME()) ELSE target.LockedUntil END,
 LastFailedAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(EmailKey,IpAddress,FailedCount,LockedUntil,LastFailedAt)
VALUES(source.EmailKey,source.IpAddress,1,CASE WHEN @MaxAttempts<=1 THEN DATEADD(MINUTE,@LockoutMinutes,SYSUTCDATETIME()) ELSE NULL END,SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@Email", LoginKey(email));
        cmd.Parameters.AddWithValue("@Ip", NormalizeIp(ipAddress));
        cmd.Parameters.AddWithValue("@MaxAttempts", maxAttempts);
        cmd.Parameters.AddWithValue("@LockoutMinutes", lockoutMinutes);
        await cmd.ExecuteNonQueryAsync();
        await WriteSecurityAuditAsync(email, "LOGIN_FAILED", "Denied", reason, ipAddress, userAgent);
    }

    public async Task ResetFailedLoginAsync(string email, string ipAddress)
    {
        await EnsureSchemaAsync();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM LoginSecurityState WHERE EmailKey=@Email AND IpAddress IN (@Ip,'*')";
        cmd.Parameters.AddWithValue("@Email", LoginKey(email));
        cmd.Parameters.AddWithValue("@Ip", NormalizeIp(ipAddress));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> IsTrustedDeviceAsync(string email, string companyCode, string? plainToken, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(plainToken)) return false;
        await EnsureSchemaAsync();
        var tokenHash = HashToken(plainToken);
        var userAgentHash = HashToken(userAgent ?? string.Empty);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE TrustedLoginDevices SET LastUsedAt=SYSUTCDATETIME()
OUTPUT inserted.TrustedDeviceId
WHERE TokenHash=@TokenHash AND Email=@Email AND CompanyCode=@CompanyCode AND UserAgentHash=@UserAgentHash
AND RevokedAt IS NULL AND ExpiresAt>SYSUTCDATETIME();";
        cmd.Parameters.AddWithValue("@TokenHash", tokenHash);
        cmd.Parameters.AddWithValue("@Email", NormalizeEmail(email));
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@UserAgentHash", userAgentHash);
        return await cmd.ExecuteScalarAsync() != null;
    }

    public async Task<(string Token, DateTimeOffset ExpiresAt)> TrustDeviceAsync(string email, string companyCode, string userAgent, string? deviceName)
    {
        await EnsureSchemaAsync();
        var settings = await GetEffectiveEmailSettingsAsync();
        var days = Math.Clamp(settings.TrustedDeviceDays, 1, 90);
        var token = GenerateToken(48);
        var expires = DateTimeOffset.UtcNow.AddDays(days);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO TrustedLoginDevices(Email,CompanyCode,TokenHash,UserAgentHash,DeviceName,ExpiresAt)
VALUES(@Email,@CompanyCode,@TokenHash,@UserAgentHash,@DeviceName,@ExpiresAt);";
        cmd.Parameters.AddWithValue("@Email", NormalizeEmail(email));
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@TokenHash", HashToken(token));
        cmd.Parameters.AddWithValue("@UserAgentHash", HashToken(userAgent ?? string.Empty));
        cmd.Parameters.AddWithValue("@DeviceName", string.IsNullOrWhiteSpace(deviceName) ? DescribeDevice(userAgent) : deviceName.Trim());
        cmd.Parameters.AddWithValue("@ExpiresAt", expires.UtcDateTime);
        await cmd.ExecuteNonQueryAsync();
        return (token, expires);
    }

    public async Task<LoginChallengeCreated> CreateLoginChallengeAsync(UserSession pendingSession, HttpContext http)
    {
        await EnsureSchemaAsync();
        var email = NormalizeEmail(pendingSession.Email);
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("A registered email address is required for secure login verification.");
        var settings = await GetEffectiveEmailSettingsAsync();
        var expiryMinutes = Math.Clamp(settings.LoginOtpExpiryMinutes, 5, 30);
        var challengeId = Guid.NewGuid().ToString("N");
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var protectedSession = Protect(JsonSerializer.Serialize(pendingSession));

        await using (var con = await _db.OpenMasterAsync())
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"
UPDATE LoginMfaChallenges SET UsedAt=SYSUTCDATETIME() WHERE Email=@Email AND CompanyCode=@CompanyCode AND UsedAt IS NULL;
INSERT INTO LoginMfaChallenges(ChallengeId,Email,CompanyCode,PendingSessionProtected,CodeHash,MaxAttempts,ExpiresAt,IpAddress,UserAgent)
VALUES(@ChallengeId,@Email,@CompanyCode,@PendingSessionProtected,@CodeHash,5,DATEADD(MINUTE,@ExpiryMinutes,SYSUTCDATETIME()),@IpAddress,@UserAgent);";
            cmd.Parameters.AddWithValue("@ChallengeId", challengeId);
            cmd.Parameters.AddWithValue("@Email", email);
            cmd.Parameters.AddWithValue("@CompanyCode", pendingSession.CompanyCode);
            cmd.Parameters.AddWithValue("@PendingSessionProtected", protectedSession);
            cmd.Parameters.AddWithValue("@CodeHash", _passwords.Hash(code));
            cmd.Parameters.AddWithValue("@ExpiryMinutes", expiryMinutes);
            cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
            cmd.Parameters.AddWithValue("@UserAgent", Limit(http.Request.Headers["User-Agent"].ToString(), 500));
            await cmd.ExecuteNonQueryAsync();
        }

        var delivery = await SendOtpEmailAsync(email, code, pendingSession.CompanyName, pendingSession.DisplayName, "LOGIN_MFA");
        if (!delivery.Sent && delivery.SmtpConfigured && !delivery.ReturnDevOtp)
        {
            await InvalidateChallengeAsync(challengeId);
            throw new InvalidOperationException("The verification email could not be sent. The InterNex owner must review the Email & OTP Setup.");
        }
        if (!delivery.Sent && !delivery.SmtpConfigured && !delivery.ReturnDevOtp)
        {
            await InvalidateChallengeAsync(challengeId);
            throw new InvalidOperationException("Login verification email is not configured. Contact the InterNex owner.");
        }

        await WriteSecurityAuditAsync(email, "LOGIN_OTP_SENT", delivery.Sent ? "Success" : "Development", $"Company={pendingSession.CompanyCode}; From={delivery.FromEmail}", http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers["User-Agent"].ToString());
        return new LoginChallengeCreated(challengeId, MaskEmail(email), expiryMinutes * 60, delivery.ReturnDevOtp ? code : null, delivery);
    }

    public async Task<LoginChallengeCreated> ResendLoginChallengeAsync(string challengeId, HttpContext http)
    {
        await EnsureSchemaAsync();
        UserSession session;
        DateTime lastSent;
        await using (var con = await _db.OpenMasterAsync())
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"SELECT TOP 1 PendingSessionProtected,LastSentAt FROM LoginMfaChallenges
WHERE ChallengeId=@ChallengeId AND UsedAt IS NULL AND ExpiresAt>SYSUTCDATETIME()";
            cmd.Parameters.AddWithValue("@ChallengeId", challengeId ?? string.Empty);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException("The login verification request has expired. Sign in again.");
            session = JsonSerializer.Deserialize<UserSession>(Unprotect(SqlRead.String(r, "PendingSessionProtected")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The login verification request is invalid.");
            lastSent = Convert.ToDateTime(r["LastSentAt"]);
        }
        if (DateTime.UtcNow - DateTime.SpecifyKind(lastSent, DateTimeKind.Utc) < TimeSpan.FromSeconds(60))
            throw new InvalidOperationException("Please wait 60 seconds before requesting another verification code.");

        var settings = await GetEffectiveEmailSettingsAsync();
        var expiryMinutes = Math.Clamp(settings.LoginOtpExpiryMinutes, 5, 30);
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        await using (var con = await _db.OpenMasterAsync())
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"UPDATE LoginMfaChallenges SET CodeHash=@CodeHash,AttemptCount=0,ExpiresAt=DATEADD(MINUTE,@ExpiryMinutes,SYSUTCDATETIME()),LastSentAt=SYSUTCDATETIME(),IpAddress=@IpAddress,UserAgent=@UserAgent WHERE ChallengeId=@ChallengeId AND UsedAt IS NULL";
            cmd.Parameters.AddWithValue("@CodeHash", _passwords.Hash(code));
            cmd.Parameters.AddWithValue("@ExpiryMinutes", expiryMinutes);
            cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
            cmd.Parameters.AddWithValue("@UserAgent", Limit(http.Request.Headers["User-Agent"].ToString(), 500));
            cmd.Parameters.AddWithValue("@ChallengeId", challengeId);
            await cmd.ExecuteNonQueryAsync();
        }

        var delivery = await SendOtpEmailAsync(session.Email, code, session.CompanyName, session.DisplayName, "LOGIN_MFA");
        if (!delivery.Sent && !delivery.ReturnDevOtp) throw new InvalidOperationException("The verification email could not be sent. Contact the InterNex owner.");
        return new LoginChallengeCreated(challengeId, MaskEmail(session.Email), expiryMinutes * 60, delivery.ReturnDevOtp ? code : null, delivery);
    }

    public async Task<VerifiedLoginChallenge> VerifyLoginChallengeAsync(string challengeId, string code, HttpContext http)
    {
        await EnsureSchemaAsync();
        string codeHash;
        string protectedSession;
        string email;
        string companyCode;
        int attemptCount;
        int maxAttempts;
        DateTime expiresAt;
        string challengeUserAgent;

        await using var con = await _db.OpenMasterAsync();
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"SELECT TOP 1 Email,CompanyCode,PendingSessionProtected,CodeHash,AttemptCount,MaxAttempts,ExpiresAt,UsedAt,ISNULL(UserAgent,'') UserAgent
FROM LoginMfaChallenges WHERE ChallengeId=@ChallengeId";
            cmd.Parameters.AddWithValue("@ChallengeId", challengeId ?? string.Empty);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException("Invalid login verification request.");
            if (r["UsedAt"] != DBNull.Value) throw new InvalidOperationException("This verification code has already been used. Sign in again.");
            email = SqlRead.String(r, "Email");
            companyCode = SqlRead.String(r, "CompanyCode");
            protectedSession = SqlRead.String(r, "PendingSessionProtected");
            codeHash = SqlRead.String(r, "CodeHash");
            attemptCount = SqlRead.Int(r, "AttemptCount");
            maxAttempts = SqlRead.Int(r, "MaxAttempts");
            expiresAt = Convert.ToDateTime(r["ExpiresAt"]);
            challengeUserAgent = SqlRead.String(r, "UserAgent");
        }

        var currentUserAgent = Limit(http.Request.Headers["User-Agent"].ToString(), 500);
        // Enforce UA binding for browser sessions; allow empty/missing UA for native mobile/desktop clients.
        if (!string.IsNullOrWhiteSpace(challengeUserAgent) &&
            !string.IsNullOrWhiteSpace(currentUserAgent) &&
            !string.Equals(challengeUserAgent, currentUserAgent, StringComparison.Ordinal))
        {
            await InvalidateChallengeAsync(challengeId);
            await WriteSecurityAuditAsync(email, "LOGIN_OTP_DEVICE_MISMATCH", "Denied", $"Company={companyCode}", http.Connection.RemoteIpAddress?.ToString(), currentUserAgent);
            throw new InvalidOperationException("This verification request belongs to a different browser. Sign in again.");
        }

        if (DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) <= DateTime.UtcNow)
        {
            await InvalidateChallengeAsync(challengeId);
            throw new InvalidOperationException("The verification code has expired. Sign in again.");
        }
        if (attemptCount >= maxAttempts)
        {
            await InvalidateChallengeAsync(challengeId);
            throw new InvalidOperationException("Too many incorrect verification attempts. Sign in again.");
        }

        if (!_passwords.Verify((code ?? string.Empty).Trim(), codeHash))
        {
            await using var failed = con.CreateCommand();
            failed.CommandText = @"UPDATE LoginMfaChallenges SET AttemptCount=AttemptCount+1,UsedAt=CASE WHEN AttemptCount+1>=MaxAttempts THEN SYSUTCDATETIME() ELSE UsedAt END WHERE ChallengeId=@ChallengeId";
            failed.Parameters.AddWithValue("@ChallengeId", challengeId);
            await failed.ExecuteNonQueryAsync();
            await WriteSecurityAuditAsync(email, "LOGIN_OTP_FAILED", "Denied", $"Company={companyCode}", http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers["User-Agent"].ToString());
            throw new InvalidOperationException("Invalid verification code.");
        }

        await using (var used = con.CreateCommand())
        {
            used.CommandText = "UPDATE LoginMfaChallenges SET UsedAt=SYSUTCDATETIME() WHERE ChallengeId=@ChallengeId AND UsedAt IS NULL";
            used.Parameters.AddWithValue("@ChallengeId", challengeId);
            if (await used.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("This verification request is no longer valid.");
        }

        var session = JsonSerializer.Deserialize<UserSession>(Unprotect(protectedSession), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The login verification session is invalid.");
        await WriteSecurityAuditAsync(email, "LOGIN_OTP_VERIFIED", "Success", $"Company={companyCode}", http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers["User-Agent"].ToString());
        return new VerifiedLoginChallenge(session, email, companyCode);
    }

    public async Task<RefreshTokenIssue> CreateRefreshTokenAsync(UserSession session)
    {
        await EnsureSchemaAsync();
        await using var con = await _db.OpenMasterAsync();
        return await InsertRefreshTokenAsync(con, null, session);
    }

    public async Task<RefreshTokenRotation?> RotateRefreshTokenAsync(string? plainToken)
    {
        if (string.IsNullOrWhiteSpace(plainToken)) return null;
        await EnsureSchemaAsync();
        var oldHash = HashToken(plainToken);
        await using var con = await _db.OpenMasterAsync();
        await using var tx = (SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable);

        string protectedSession;
        string sessionId;
        long refreshTokenId;
        DateTime expiresAt;
        DateTime? revokedAt;
        await using (var select = con.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = @"SELECT TOP 1 RefreshTokenId,SessionId,ProtectedSessionJson,ExpiresAt,RevokedAt
FROM AuthRefreshTokens WITH (UPDLOCK,ROWLOCK) WHERE TokenHash=@TokenHash";
            select.Parameters.AddWithValue("@TokenHash", oldHash);
            await using var r = await select.ExecuteReaderAsync();
            if (!await r.ReadAsync())
            {
                await tx.RollbackAsync();
                return null;
            }
            refreshTokenId = Convert.ToInt64(r["RefreshTokenId"]);
            sessionId = SqlRead.String(r, "SessionId");
            protectedSession = SqlRead.String(r, "ProtectedSessionJson");
            expiresAt = Convert.ToDateTime(r["ExpiresAt"]);
            revokedAt = r["RevokedAt"] == DBNull.Value ? null : Convert.ToDateTime(r["RevokedAt"]);
        }

        if (revokedAt.HasValue)
        {
            // A rotated token was replayed. Revoke the full token family for this login session.
            await using var revokeFamily = con.CreateCommand();
            revokeFamily.Transaction = tx;
            revokeFamily.CommandText = "UPDATE AuthRefreshTokens SET RevokedAt=COALESCE(RevokedAt,SYSUTCDATETIME()) WHERE SessionId=@SessionId";
            revokeFamily.Parameters.AddWithValue("@SessionId", sessionId);
            await revokeFamily.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return null;
        }

        if (DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) <= DateTime.UtcNow)
        {
            await using var expire = con.CreateCommand();
            expire.Transaction = tx;
            expire.CommandText = "UPDATE AuthRefreshTokens SET RevokedAt=COALESCE(RevokedAt,SYSUTCDATETIME()) WHERE RefreshTokenId=@Id";
            expire.Parameters.AddWithValue("@Id", refreshTokenId);
            await expire.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return null;
        }

        var session = JsonSerializer.Deserialize<UserSession>(Unprotect(protectedSession), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (session == null)
        {
            await tx.RollbackAsync();
            return null;
        }
        var newToken = await InsertRefreshTokenAsync(con, tx, session);
        await using (var revoke = con.CreateCommand())
        {
            revoke.Transaction = tx;
            revoke.CommandText = "UPDATE AuthRefreshTokens SET RevokedAt=SYSUTCDATETIME(),LastUsedAt=SYSUTCDATETIME(),ReplacedByTokenHash=@NewHash WHERE RefreshTokenId=@Id AND RevokedAt IS NULL";
            revoke.Parameters.AddWithValue("@NewHash", HashToken(newToken.PlainToken));
            revoke.Parameters.AddWithValue("@Id", refreshTokenId);
            if (await revoke.ExecuteNonQueryAsync() != 1)
            {
                await tx.RollbackAsync();
                return null;
            }
        }
        await tx.CommitAsync();
        return new RefreshTokenRotation(session, newToken);
    }

    public async Task<RefreshTokenIssue> ReplaceRefreshTokenAsync(string? currentPlainToken, UserSession session)
    {
        if (!string.IsNullOrWhiteSpace(currentPlainToken))
            await RevokeRefreshTokenAsync(currentPlainToken);
        return await CreateRefreshTokenAsync(session);
    }

    public async Task RevokeRefreshTokenAsync(string? plainToken)
    {
        if (string.IsNullOrWhiteSpace(plainToken)) return;
        await EnsureSchemaAsync();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE AuthRefreshTokens SET RevokedAt=COALESCE(RevokedAt,SYSUTCDATETIME()) WHERE TokenHash=@TokenHash";
        cmd.Parameters.AddWithValue("@TokenHash", HashToken(plainToken));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RevokeUserAuthenticationAsync(string email, string companyCode, int userId)
    {
        await EnsureSchemaAsync();
        var normalizedEmail = NormalizeEmail(email);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE AuthRefreshTokens SET RevokedAt=COALESCE(RevokedAt,SYSUTCDATETIME())
WHERE Email=@Email AND CompanyCode=@CompanyCode AND UserId=@UserId;
UPDATE TrustedLoginDevices SET RevokedAt=COALESCE(RevokedAt,SYSUTCDATETIME())
WHERE Email=@Email AND CompanyCode=@CompanyCode;
IF OBJECT_ID('AuthSessionAudit') IS NOT NULL
BEGIN
    INSERT INTO AuthSessionAudit(SessionId,CompanyCode,UserId,UserName,Email,EventName)
    SELECT DISTINCT a.SessionId,@CompanyCode,@UserId,MAX(ISNULL(a.UserName,'')),@Email,'LOGOUT'
    FROM AuthSessionAudit a
    WHERE a.EventName='LOGIN' AND a.CompanyCode=@CompanyCode AND a.UserId=@UserId
      AND NOT EXISTS(SELECT 1 FROM AuthSessionAudit x WHERE x.SessionId=a.SessionId AND x.EventName='LOGOUT')
    GROUP BY a.SessionId;
END;";
        cmd.Parameters.AddWithValue("@Email", normalizedEmail);
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await cmd.ExecuteNonQueryAsync();
        await WriteSecurityAuditAsync(normalizedEmail, "USER_AUTHENTICATION_REVOKED", "Success", $"Company={companyCode}; UserId={userId}", null, null);
    }

    public async Task RevokeTrustedDeviceAsync(string? plainToken)
    {
        if (string.IsNullOrWhiteSpace(plainToken)) return;
        await EnsureSchemaAsync();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE TrustedLoginDevices SET RevokedAt=COALESCE(RevokedAt,SYSUTCDATETIME()) WHERE TokenHash=@TokenHash";
        cmd.Parameters.AddWithValue("@TokenHash", HashToken(plainToken));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DeleteClientChallengeCreated> CreateDeleteClientChallengeAsync(string companyCode, string companyName, string ownerEmail, HttpContext http)
    {
        await EnsureSchemaAsync();
        var email = NormalizeEmail(ownerEmail);
        if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("A platform owner email is required to delete a client.");
        var code = companyCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Company code is required.");

        var settings = await GetEffectiveEmailSettingsAsync();
        var expiryMinutes = Math.Clamp(settings.LoginOtpExpiryMinutes, 5, 30);
        var challengeId = Guid.NewGuid().ToString("N");
        var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        await using (var con = await _db.OpenMasterAsync())
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"
UPDATE PlatformDeleteClientChallenges SET UsedAt=COALESCE(UsedAt,SYSUTCDATETIME())
WHERE CompanyCode=@CompanyCode AND UsedAt IS NULL;
INSERT INTO PlatformDeleteClientChallenges(ChallengeId,CompanyCode,OwnerEmail,CodeHash,MaxAttempts,ExpiresAt,IpAddress,UserAgent)
VALUES(@ChallengeId,@CompanyCode,@OwnerEmail,@CodeHash,5,DATEADD(MINUTE,@ExpiryMinutes,SYSUTCDATETIME()),@IpAddress,@UserAgent);";
            cmd.Parameters.AddWithValue("@ChallengeId", challengeId);
            cmd.Parameters.AddWithValue("@CompanyCode", code);
            cmd.Parameters.AddWithValue("@OwnerEmail", email);
            cmd.Parameters.AddWithValue("@CodeHash", _passwords.Hash(otp));
            cmd.Parameters.AddWithValue("@ExpiryMinutes", expiryMinutes);
            cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
            cmd.Parameters.AddWithValue("@UserAgent", Limit(http.Request.Headers["User-Agent"].ToString(), 500));
            await cmd.ExecuteNonQueryAsync();
        }

        var delivery = await SendOtpEmailAsync(email, otp, companyName, email, "DELETE_CLIENT");
        if (!delivery.Sent && !delivery.ReturnDevOtp)
        {
            await InvalidateDeleteClientChallengeAsync(challengeId);
            throw new InvalidOperationException(delivery.SmtpConfigured
                ? "The deletion verification email could not be sent. Review Email & OTP Setup."
                : "Login verification email is not configured. Contact the InterNex owner.");
        }

        await WriteSecurityAuditAsync(email, "DELETE_CLIENT_OTP_SENT", delivery.Sent ? "Success" : "Development", $"Company={code}; From={delivery.FromEmail}", http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers["User-Agent"].ToString());
        return new DeleteClientChallengeCreated(challengeId, MaskEmail(email), expiryMinutes * 60, delivery.ReturnDevOtp ? otp : null, delivery);
    }

    public async Task<DeleteClientChallengeVerified> VerifyDeleteClientChallengeAsync(string companyCode, string challengeId, string? code)
    {
        await EnsureSchemaAsync();
        var expectedCompany = (companyCode ?? string.Empty).Trim();
        string codeHash;
        string ownerEmail;
        string storedCompany;
        int attemptCount;
        int maxAttempts;
        DateTime expiresAt;

        await using var con = await _db.OpenMasterAsync();
        await using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"SELECT TOP 1 CompanyCode,OwnerEmail,CodeHash,AttemptCount,MaxAttempts,ExpiresAt,UsedAt,VerifiedAt
FROM PlatformDeleteClientChallenges WHERE ChallengeId=@ChallengeId";
            cmd.Parameters.AddWithValue("@ChallengeId", challengeId ?? string.Empty);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException("Invalid deletion verification request.");
            if (r["UsedAt"] != DBNull.Value) throw new InvalidOperationException("This deletion request has already been used.");
            storedCompany = SqlRead.String(r, "CompanyCode");
            ownerEmail = SqlRead.String(r, "OwnerEmail");
            codeHash = SqlRead.String(r, "CodeHash");
            attemptCount = SqlRead.Int(r, "AttemptCount");
            maxAttempts = SqlRead.Int(r, "MaxAttempts");
            expiresAt = Convert.ToDateTime(r["ExpiresAt"]);
        }

        if (!string.Equals(storedCompany, expectedCompany, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This verification request does not match the selected company.");
        if (DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc) <= DateTime.UtcNow)
        {
            await InvalidateDeleteClientChallengeAsync(challengeId);
            throw new InvalidOperationException("The verification code has expired. Start the delete again.");
        }
        if (attemptCount >= maxAttempts)
        {
            await InvalidateDeleteClientChallengeAsync(challengeId);
            throw new InvalidOperationException("Too many incorrect verification attempts. Start the delete again.");
        }
        if (!_passwords.Verify((code ?? string.Empty).Trim(), codeHash))
        {
            await using var failed = con.CreateCommand();
            failed.CommandText = @"UPDATE PlatformDeleteClientChallenges SET AttemptCount=AttemptCount+1,UsedAt=CASE WHEN AttemptCount+1>=MaxAttempts THEN SYSUTCDATETIME() ELSE UsedAt END WHERE ChallengeId=@ChallengeId";
            failed.Parameters.AddWithValue("@ChallengeId", challengeId);
            await failed.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Invalid verification code.");
        }

        var deleteToken = GenerateToken(32);
        await using (var verified = con.CreateCommand())
        {
            verified.CommandText = @"UPDATE PlatformDeleteClientChallenges
SET VerifiedAt=SYSUTCDATETIME(), DeleteTokenHash=@DeleteTokenHash, ExpiresAt=DATEADD(MINUTE,10,SYSUTCDATETIME())
WHERE ChallengeId=@ChallengeId AND UsedAt IS NULL";
            verified.Parameters.AddWithValue("@DeleteTokenHash", HashToken(deleteToken));
            verified.Parameters.AddWithValue("@ChallengeId", challengeId);
            if (await verified.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("This verification request is no longer valid.");
        }

        await WriteSecurityAuditAsync(ownerEmail, "DELETE_CLIENT_OTP_VERIFIED", "Success", $"Company={storedCompany}", null, null);
        return new DeleteClientChallengeVerified(deleteToken, storedCompany, MaskEmail(ownerEmail));
    }

    public async Task ConsumeDeleteClientTokenAsync(string companyCode, string? deleteToken)
    {
        await EnsureSchemaAsync();
        var tokenHash = HashToken(deleteToken);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"UPDATE PlatformDeleteClientChallenges
SET UsedAt=SYSUTCDATETIME()
WHERE DeleteTokenHash=@DeleteTokenHash AND CompanyCode=@CompanyCode AND VerifiedAt IS NOT NULL AND UsedAt IS NULL AND ExpiresAt>SYSUTCDATETIME();
SELECT @@ROWCOUNT;";
        cmd.Parameters.AddWithValue("@DeleteTokenHash", tokenHash);
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode ?? string.Empty);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) != 1)
            throw new InvalidOperationException("The deletion confirmation is invalid or has expired. Start the delete again.");
    }

    private async Task InvalidateDeleteClientChallengeAsync(string challengeId)
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE PlatformDeleteClientChallenges SET UsedAt=COALESCE(UsedAt,SYSUTCDATETIME()) WHERE ChallengeId=@ChallengeId";
        cmd.Parameters.AddWithValue("@ChallengeId", challengeId ?? string.Empty);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<RefreshTokenIssue> InsertRefreshTokenAsync(SqlConnection con, SqlTransaction? tx, UserSession session)
    {
        var plain = GenerateToken(64);
        var expires = DateTimeOffset.UtcNow.AddDays(RefreshTokenExpiryDays);
        await using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO AuthRefreshTokens(TokenHash,SessionId,Email,CompanyCode,UserId,ProtectedSessionJson,ExpiresAt)
VALUES(@TokenHash,@SessionId,@Email,@CompanyCode,@UserId,@ProtectedSessionJson,@ExpiresAt)";
        cmd.Parameters.AddWithValue("@TokenHash", HashToken(plain));
        cmd.Parameters.AddWithValue("@SessionId", session.SessionId ?? string.Empty);
        cmd.Parameters.AddWithValue("@Email", session.Email ?? string.Empty);
        cmd.Parameters.AddWithValue("@CompanyCode", session.CompanyCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@UserId", session.UserId);
        cmd.Parameters.AddWithValue("@ProtectedSessionJson", Protect(JsonSerializer.Serialize(session)));
        cmd.Parameters.AddWithValue("@ExpiresAt", expires.UtcDateTime);
        await cmd.ExecuteNonQueryAsync();
        return new RefreshTokenIssue(plain, expires);
    }

    private async Task<EmailDeliveryResult> SendEmailAsync(EffectiveEmailSecuritySettings settings, string toEmail, string subject, string body, bool isHtml = false)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost))
            return new EmailDeliveryResult(false, false, settings.ReturnDevOtp, settings.FromEmail, "SMTP host is not configured.");
        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromEmail, settings.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = isHtml
            };
            message.To.Add(toEmail);
            using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Timeout = 30000
            };
            if (!string.IsNullOrWhiteSpace(settings.SmtpUser))
                smtp.Credentials = new NetworkCredential(settings.SmtpUser, settings.SmtpPassword);
            await smtp.SendMailAsync(message);
            return new EmailDeliveryResult(true, true, settings.ReturnDevOtp, settings.FromEmail, $"Email sent from {settings.FromEmail} to {toEmail}.");
        }
        catch (Exception ex)
        {
            return new EmailDeliveryResult(false, true, settings.ReturnDevOtp, settings.FromEmail, Limit(ex.Message, 500));
        }
    }

    private async Task InvalidateChallengeAsync(string challengeId)
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE LoginMfaChallenges SET UsedAt=COALESCE(UsedAt,SYSUTCDATETIME()) WHERE ChallengeId=@ChallengeId";
        cmd.Parameters.AddWithValue("@ChallengeId", challengeId ?? string.Empty);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WriteSecurityAuditAsync(string? userName, string action, string result, string details, string? ipAddress, string? userAgent)
    {
        try
        {
            await using var con = await _db.OpenMasterAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"IF OBJECT_ID('SecurityAuditLog') IS NOT NULL
INSERT INTO SecurityAuditLog(UserName,ActionName,Path,IpAddress,Result,Details)
VALUES(@UserName,@ActionName,@Path,@IpAddress,@Result,@Details)";
            cmd.Parameters.AddWithValue("@UserName", userName ?? string.Empty);
            cmd.Parameters.AddWithValue("@ActionName", action);
            cmd.Parameters.AddWithValue("@Path", Limit(userAgent ?? string.Empty, 250));
            cmd.Parameters.AddWithValue("@IpAddress", NormalizeIp(ipAddress));
            cmd.Parameters.AddWithValue("@Result", result);
            cmd.Parameters.AddWithValue("@Details", Limit(details, 500));
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Authentication must not fail because an audit insert is temporarily unavailable.
        }
    }

    private string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(cipher)}";
    }

    private string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return string.Empty;
        var parts = protectedValue.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") throw new InvalidOperationException("Protected authentication data is invalid.");
        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var cipher = Convert.FromBase64String(parts[3]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static string GenerateToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashToken(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    private static string LoginKey(string email) => string.IsNullOrWhiteSpace(NormalizeEmail(email)) ? "unknown" : NormalizeEmail(email);
    private static string NormalizeIp(string? ip) => Limit(string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim(), 80);
    private static string NormalizeEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length < 6 || value.Length > 180 || !value.Contains('@') || !value.Contains('.') || value.Contains(' ')) return string.Empty;
        return value;
    }
    private static string MaskEmail(string email)
    {
        var parts = (email ?? string.Empty).Split('@');
        if (parts.Length != 2) return "your registered email";
        var local = parts[0];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return visible + new string('*', Math.Max(2, local.Length - visible.Length)) + "@" + parts[1];
    }
    private static string DescribeDevice(string userAgent)
    {
        var ua = userAgent ?? string.Empty;
        var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" :
            ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" :
            ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Mozilla Firefox" :
            ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari" : "Web Browser";
        var os = ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows" :
            ua.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) ? "macOS" :
            ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android" :
            ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone" : "Device";
        return $"{browser} on {os}";
    }
    private static string Limit(string? value, int length)
    {
        var text = value ?? string.Empty;
        return text.Length <= length ? text : text[..length];
    }
}
