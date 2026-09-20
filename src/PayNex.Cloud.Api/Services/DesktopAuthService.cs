using Microsoft.Data.SqlClient;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using System.Data;
using System.Text.Json;

namespace PayNex.Cloud.Api.Services;

public sealed class DesktopAuthService
{
    private readonly ConnectionFactory _db;
    private readonly TenantProvisioningService _tenants;
    private readonly PasswordService _passwords;
    private readonly AuthTokenService _tokens;
    private readonly AuthenticationSecurityService _authSecurity;
    private readonly DesktopAccessService _desktopAccess;
    private readonly SubscriptionAccessService _subscriptionAccess;

    public DesktopAuthService(
        ConnectionFactory db,
        TenantProvisioningService tenants,
        PasswordService passwords,
        AuthTokenService tokens,
        AuthenticationSecurityService authSecurity,
        DesktopAccessService desktopAccess,
        SubscriptionAccessService subscriptionAccess)
    {
        _db = db;
        _tenants = tenants;
        _passwords = passwords;
        _tokens = tokens;
        _authSecurity = authSecurity;
        _desktopAccess = desktopAccess;
        _subscriptionAccess = subscriptionAccess;
    }

    public async Task<IResult> LoginAsync(HttpContext http, DesktopLoginRequest request)
    {
        var userName = (request.UserName ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            return Fail("MISSING_CREDENTIALS", "Email and password are required.", StatusCodes.Status400BadRequest);

        var ipAddress = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = http.Request.Headers["User-Agent"].ToString();
        var loginKey = userName;
        var lockedUntil = await _authSecurity.GetLockoutUntilAsync(loginKey, ipAddress);
        if (lockedUntil.HasValue)
            return Results.Json(new
            {
                ok = false,
                code = "LOGIN_LOCKED",
                message = $"Account sign-in is temporarily locked because of repeated failed attempts. Try again after {lockedUntil.Value:yyyy-MM-dd HH:mm:ss} UTC.",
                lockedUntil = lockedUntil.Value
            }, statusCode: StatusCodes.Status429TooManyRequests);

        await EnsureMasterDirectoryAsync();

        var email = NormalizeEmail(userName);
        var candidates = await ListDirectoryCandidatesAsync(userName, email);
        if (candidates.Count == 0)
        {
            await _authSecurity.RecordFailedLoginAsync(loginKey, ipAddress, userAgent, "USER_NOT_FOUND");
            return Fail("USER_NOT_FOUND", "This email is not registered for any InterNex company.", StatusCodes.Status401Unauthorized);
        }

        var matches = candidates.Where(item => _passwords.Verify(password, item.PasswordHash)).ToList();
        if (matches.Count == 0)
        {
            await _authSecurity.RecordFailedLoginAsync(loginKey, ipAddress, userAgent, "INVALID_PASSWORD");
            var again = await _authSecurity.GetLockoutUntilAsync(loginKey, ipAddress);
            if (again.HasValue)
                return Results.Json(new
                {
                    ok = false,
                    code = "LOGIN_LOCKED",
                    message = $"Account sign-in is temporarily locked because of repeated failed attempts. Try again after {again.Value:yyyy-MM-dd HH:mm:ss} UTC.",
                    lockedUntil = again.Value
                }, statusCode: StatusCodes.Status429TooManyRequests);
            return Fail("INVALID_PASSWORD", "Incorrect email or password.", StatusCodes.Status401Unauthorized);
        }

        if (matches.Count > 1)
            return Fail("CREDENTIAL_AMBIGUOUS", "This username and password match more than one company. Each username + password pair must be unique. Ask the InterNex owner to set a unique password on Desktop Detail.", StatusCodes.Status409Conflict);

        var directory = matches[0];

        TenantInfo tenant;
        try { tenant = await _tenants.GetTenantAsync(directory.CompanyCode); }
        catch
        {
            return Fail("COMPANY_NOT_FOUND", "Company record not found for this user.", StatusCodes.Status403Forbidden);
        }

        if (string.Equals(tenant.Status, "Suspended", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tenant.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
            return Fail("COMPANY_INACTIVE", $"Your company subscription is inactive. Company {tenant.CompanyCode} is {tenant.Status}.", StatusCodes.Status403Forbidden);

        var access = _subscriptionAccess.Evaluate(tenant);
        await _subscriptionAccess.SyncTenantLicenseStatusAsync(tenant.CompanyCode, access);
        if (!access.CanLogin)
            return Fail("LICENSE_EXPIRED", _subscriptionAccess.LoginBlockedMessage(access), StatusCodes.Status403Forbidden);

        var environmentName = string.Equals(request.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase) ? "Sandbox" : "Production";
        string databaseName;
        try { databaseName = ResolveTenantDatabase(tenant, environmentName); }
        catch (InvalidOperationException ex)
        {
            return Fail("ENVIRONMENT_UNAVAILABLE", ex.Message, StatusCodes.Status400BadRequest);
        }

        if (directory.UserId <= 0)
            directory = await LinkMobileUserToDesktopAsync(tenant, databaseName, directory);

        await using var con = await _db.OpenTenantAsync(databaseName);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,u.RoleId,r.RoleName,u.StoreId,s.StoreName,ISNULL(s.StoreCode,'MAIN') StoreCode,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,u.IsActive
FROM Users u
INNER JOIN Roles r ON r.RoleId=u.RoleId
INNER JOIN Stores s ON s.StoreId=u.StoreId
WHERE (u.UserId=@UserId OR LOWER(ISNULL(u.Email,''))=@Email OR LOWER(LTRIM(RTRIM(u.UserName)))=@UserName)
ORDER BY CASE WHEN u.UserId=@UserId THEN 0 ELSE 1 END, u.UserId";
        cmd.Parameters.AddWithValue("@UserId", directory.UserId);
        cmd.Parameters.AddWithValue("@Email", string.IsNullOrWhiteSpace(directory.Email) ? (object)DBNull.Value : directory.Email);
        cmd.Parameters.AddWithValue("@UserName", directory.UserName);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Fail("TENANT_USER_INACTIVE", "This email is not active in the assigned company database.", StatusCodes.Status403Forbidden);

        if (!SqlRead.Bool(reader, "IsActive"))
            return Fail("TENANT_USER_INACTIVE", "This user is inactive in the assigned company.", StatusCodes.Status403Forbidden);

        var tenantUserId = SqlRead.Int(reader, "UserId");
        var tenantUserName = SqlRead.String(reader, "UserName");
        var tenantDisplayName = SqlRead.String(reader, "DisplayName");
        var tenantRoleId = SqlRead.Int(reader, "RoleId");
        var tenantRoleName = SqlRead.String(reader, "RoleName");
        var tenantEmail = NormalizeEmail(SqlRead.String(reader, "Email"));
        if (string.IsNullOrWhiteSpace(tenantEmail)) tenantEmail = directory.Email;
        var storeId = SqlRead.Int(reader, "StoreId");
        var storeName = SqlRead.String(reader, "StoreName");
        var storeCode = SqlRead.String(reader, "StoreCode");
        var isCompanySuperAdmin = directory.IsCompanySuperAdmin || SqlRead.Bool(reader, "IsCompanySuperAdmin") ||
            tenantRoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
            tenantRoleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);
        await reader.CloseAsync();

        var companyDesktop = await _desktopAccess.GetCompanyBlockAsync(tenant.CompanyCode);
        if (companyDesktop.Blocked)
            return Fail("DESKTOP_APP_BLOCKED", string.IsNullOrWhiteSpace(companyDesktop.Reason)
                ? "Desktop application access is blocked for this company."
                : companyDesktop.Reason, StatusCodes.Status403Forbidden);

        var desktopUser = await _desktopAccess.GetUserAccessAsync(tenant.CompanyCode, tenantUserId);
        if (desktopUser.Blocked)
            return Fail("DESKTOP_USER_BLOCKED", string.IsNullOrWhiteSpace(desktopUser.Reason)
                ? "Desktop application access is blocked for this user."
                : desktopUser.Reason, StatusCodes.Status403Forbidden);
        if (!desktopUser.Allowed)
            return Fail("DESKTOP_ACCESS_DENIED", "This user is not allowed to sign in to InterNex Desktop. Ask the company administrator or InterNex owner to grant Desktop access.", StatusCodes.Status403Forbidden);

        var permissions = await ReadPermissionMapAsync(con, tenantUserId, isCompanySuperAdmin);
        var permissionsJson = EncodeDesktopPermissions(permissions);

        var (preferred, assignedBranches) = await BranchAccessHelper.ResolveBranchesForUserAsync(con, tenantUserId, isCompanySuperAdmin);
        storeId = preferred.BranchId;
        storeCode = preferred.BranchCode;
        storeName = preferred.BranchName;

        var pendingSession = new UserSession(
            tenant.CompanyCode, tenant.CompanyName, databaseName,
            tenantUserId, tenantUserName, tenantDisplayName,
            tenantRoleId, tenantRoleName, storeId, storeName, environmentName,
            storeId, storeCode, storeName, tenant.AllowMultipleBranches, tenantEmail, isCompanySuperAdmin, false, permissionsJson, string.Empty);

        await _authSecurity.ResetFailedLoginAsync(loginKey, ipAddress);

        if (!directory.EmailVerified)
        {
            try
            {
                var challenge = await _authSecurity.CreateLoginChallengeAsync(pendingSession, http);
                return Results.Ok(new
                {
                    ok = true,
                    code = "OTP_REQUIRED",
                    message = "Enter the one-time code sent to your email to finish the first login.",
                    requiresVerification = true,
                    challengeId = challenge.ChallengeId,
                    maskedEmail = challenge.MaskedEmail,
                    expiresInSeconds = challenge.ExpiresInSeconds,
                    fromEmail = challenge.Delivery.FromEmail,
                    deliveryStatus = challenge.Delivery.Message,
                    allowMultipleBranches = tenant.AllowMultipleBranches,
                    branches = BranchAccessHelper.ToPayload(assignedBranches),
                    company = new
                    {
                        companyCode = tenant.CompanyCode,
                        companyName = tenant.CompanyName,
                        status = tenant.Status,
                        licenseStatus = tenant.LicenseStatus,
                        subscriptionPlan = tenant.SubscriptionPlan
                    },
                    user = new
                    {
                        userId = tenantUserId,
                        userName = tenantUserName,
                        displayName = tenantDisplayName,
                        email = tenantEmail,
                        roleName = tenantRoleName,
                        storeId,
                        branchId = storeId,
                        branchCode = storeCode,
                        branchName = storeName
                    }
                });
            }
            catch (InvalidOperationException ex)
            {
                return Fail("OTP_SEND_FAILED", ex.Message, StatusCodes.Status400BadRequest);
            }
        }

        return await CompleteDesktopLoginAsync(http, pendingSession, permissions, request.DeviceName, request.AppVersion, assignedBranches);
    }

    public async Task<IResult> VerifyOtpAsync(HttpContext http, VerifyLoginOtpRequest request)
    {
        try
        {
            var verified = await _authSecurity.VerifyLoginChallengeAsync(request.ChallengeId, request.Code, http);
            var session = verified.PendingSession;
            if (!IsDesktopChannel(session))
                return Fail("WRONG_CHANNEL", "This verification belongs to a different sign-in flow. Sign in again from Desktop.", StatusCodes.Status400BadRequest);

            await MarkEmailVerifiedAsync(session);
            var permissions = ReadPermissions(session);
            return await CompleteDesktopLoginAsync(http, session, permissions, null, null, null);
        }
        catch (InvalidOperationException ex)
        {
            return Fail("OTP_INVALID", ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    public async Task<IResult> ResendOtpAsync(HttpContext http, ResendLoginOtpRequest request)
    {
        try
        {
            var challenge = await _authSecurity.ResendLoginChallengeAsync(request.ChallengeId, http);
            return Results.Ok(new
            {
                ok = true,
                requiresVerification = true,
                challengeId = challenge.ChallengeId,
                maskedEmail = challenge.MaskedEmail,
                expiresInSeconds = challenge.ExpiresInSeconds,
                deliveryStatus = challenge.Delivery.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return Fail("OTP_RESEND_FAILED", ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    private async Task<IResult> CompleteDesktopLoginAsync(
        HttpContext http,
        UserSession pendingSession,
        Dictionary<string, bool> permissions,
        string? deviceName,
        string? appVersion,
        IReadOnlyList<BranchAccessHelper.BranchInfo>? assignedBranches)
    {
        await MarkEmailVerifiedAsync(pendingSession);

        object[] branchesPayload;
        var session = pendingSession;
        if (assignedBranches == null || assignedBranches.Count == 0)
        {
            try
            {
                await using var con = await _db.OpenTenantAsync(pendingSession.DatabaseName);
                var isAdmin = pendingSession.IsCompanySuperAdmin ||
                    string.Equals(pendingSession.RoleName, "Admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pendingSession.RoleName, "Company Super Admin", StringComparison.OrdinalIgnoreCase);
                var (preferred, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(con, pendingSession.UserId, isAdmin);
                session = pendingSession with
                {
                    StoreId = preferred.BranchId,
                    StoreName = preferred.BranchName,
                    BranchId = preferred.BranchId,
                    BranchCode = preferred.BranchCode,
                    BranchName = preferred.BranchName
                };
                assignedBranches = assigned;
            }
            catch
            {
                assignedBranches = new[]
                {
                    new BranchAccessHelper.BranchInfo(
                        pendingSession.BranchId > 0 ? pendingSession.BranchId : pendingSession.StoreId,
                        string.IsNullOrWhiteSpace(pendingSession.BranchCode) ? "MAIN" : pendingSession.BranchCode,
                        string.IsNullOrWhiteSpace(pendingSession.BranchName) ? pendingSession.StoreName : pendingSession.BranchName,
                        true)
                };
            }
        }

        branchesPayload = BranchAccessHelper.ToPayload(assignedBranches);

        var sessionId = await CreateLoginSessionAsync(session, http);
        session = session with { SessionId = sessionId, PermissionsJson = EncodeDesktopPermissions(permissions) };
        var token = _tokens.Create(session);
        var refresh = await _authSecurity.CreateRefreshTokenAsync(session);

        return Results.Ok(new
        {
            ok = true,
            code = "LOGIN_OK",
            message = "Login successful. Company resolved automatically from the email credential.",
            requiresVerification = false,
            token,
            refreshToken = refresh.PlainToken,
            expiresInMinutes = _authSecurity.AccessTokenExpiryMinutes,
            allowMultipleBranches = session.AllowMultipleBranches,
            showSelector = session.AllowMultipleBranches && branchesPayload.Length > 1,
            branches = branchesPayload,
            company = new
            {
                companyCode = session.CompanyCode,
                companyName = session.CompanyName
            },
            user = session,
            permissions,
            device = new
            {
                deviceName,
                appVersion
            }
        });
    }

    private async Task MarkEmailVerifiedAsync(UserSession session)
    {
        await EnsureMasterDirectoryAsync();
        var email = NormalizeEmail(session.Email);
        await using var master = await _db.OpenMasterAsync();
        await using (var cmd = master.CreateCommand())
        {
            cmd.CommandText = @"
UPDATE CentralUserDirectory
SET EmailVerified=1, LastLoginAt=SYSUTCDATETIME(), UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode AND (UserId=@UserId OR LOWER(LTRIM(RTRIM(Email)))=@Email)";
            cmd.Parameters.AddWithValue("@CompanyCode", session.CompanyCode ?? string.Empty);
            cmd.Parameters.AddWithValue("@UserId", session.UserId);
            cmd.Parameters.AddWithValue("@Email", email);
            await cmd.ExecuteNonQueryAsync();
        }

        if (string.IsNullOrWhiteSpace(session.DatabaseName) || string.IsNullOrWhiteSpace(email)) return;
        try
        {
            await using var tenant = await _db.OpenTenantAsync(session.DatabaseName);
            await using var cmd = tenant.CreateCommand();
            cmd.CommandText = "UPDATE Users SET EmailVerified=1, UpdatedAt=SYSUTCDATETIME() WHERE UserId=@UserId OR LOWER(ISNULL(Email,''))=@Email";
            cmd.Parameters.AddWithValue("@UserId", session.UserId);
            cmd.Parameters.AddWithValue("@Email", email);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* ignore */ }
    }

    public async Task<IResult> RefreshAsync(HttpContext http, DesktopRefreshRequest? request)
    {
        var plain = request?.RefreshToken;
        if (string.IsNullOrWhiteSpace(plain))
            return Fail("MISSING_REFRESH_TOKEN", "Refresh token is required.", StatusCodes.Status401Unauthorized);

        var rotation = await _authSecurity.RotateRefreshTokenAsync(plain);
        if (rotation == null)
            return Fail("REFRESH_INVALID", "Refresh token is invalid or expired. Please login again.", StatusCodes.Status401Unauthorized);

        if (await IsSessionLoggedOutAsync(rotation.Session.SessionId))
        {
            await _authSecurity.RevokeRefreshTokenAsync(rotation.NewToken.PlainToken);
            return Fail("SESSION_LOGGED_OUT", "Session has expired or user logged out.", StatusCodes.Status401Unauthorized);
        }

        var accessToken = _tokens.Create(rotation.Session);
        var permissions = ReadPermissions(rotation.Session);
        object[] branchesPayload = Array.Empty<object>();
        try
        {
            if (!string.IsNullOrWhiteSpace(rotation.Session.DatabaseName))
            {
                await using var con = await _db.OpenTenantAsync(rotation.Session.DatabaseName);
                var isAdmin = rotation.Session.IsCompanySuperAdmin ||
                    string.Equals(rotation.Session.RoleName, "Admin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rotation.Session.RoleName, "Company Super Admin", StringComparison.OrdinalIgnoreCase);
                var (_, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(con, rotation.Session.UserId, isAdmin);
                branchesPayload = BranchAccessHelper.ToPayload(assigned);
            }
        }
        catch { /* omit on failure */ }

        if (branchesPayload.Length == 0)
        {
            branchesPayload = BranchAccessHelper.ToPayload(new[]
            {
                new BranchAccessHelper.BranchInfo(
                    rotation.Session.BranchId > 0 ? rotation.Session.BranchId : rotation.Session.StoreId,
                    string.IsNullOrWhiteSpace(rotation.Session.BranchCode) ? "MAIN" : rotation.Session.BranchCode,
                    string.IsNullOrWhiteSpace(rotation.Session.BranchName) ? rotation.Session.StoreName : rotation.Session.BranchName,
                    true)
            });
        }

        return Results.Ok(new
        {
            ok = true,
            code = "REFRESH_OK",
            token = accessToken,
            refreshToken = rotation.NewToken.PlainToken,
            expiresInMinutes = _authSecurity.AccessTokenExpiryMinutes,
            allowMultipleBranches = rotation.Session.AllowMultipleBranches,
            showSelector = rotation.Session.AllowMultipleBranches && branchesPayload.Length > 1,
            branches = branchesPayload,
            user = rotation.Session,
            permissions
        });
    }

    public IResult Me(UserSession session)
    {
        return Results.Ok(new
        {
            ok = true,
            user = session,
            permissions = ReadPermissions(session),
            allowMultipleBranches = session.AllowMultipleBranches
        });
    }

    public async Task<IResult> LogoutAsync(UserSession session, DesktopLogoutRequest? request)
    {
        await CloseSessionAsync(session.SessionId, session.CompanyCode, session.UserId);
        await _authSecurity.RevokeRefreshTokenAsync(request?.RefreshToken);
        return Results.Ok(new { ok = true, message = "Logged out. Access and refresh tokens have been invalidated." });
    }

    private async Task<List<DirectoryUser>> ListDirectoryCandidatesAsync(string userName, string email)
    {
        await using var master = await _db.OpenMasterAsync();
        await using var find = master.CreateCommand();
        if (!string.IsNullOrWhiteSpace(email))
        {
            find.CommandText = @"
SELECT DirectoryUserId,TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsActive
FROM CentralUserDirectory
WHERE Email=@Email AND IsActive=1
ORDER BY IsDefaultCompany DESC, DirectoryUserId";
            find.Parameters.AddWithValue("@Email", email);
        }
        else
        {
            find.CommandText = @"
SELECT DirectoryUserId,TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsActive
FROM CentralUserDirectory
WHERE LOWER(LTRIM(RTRIM(UserName))) = LOWER(LTRIM(RTRIM(@UserName))) AND IsActive=1
ORDER BY IsDefaultCompany DESC, DirectoryUserId";
            find.Parameters.AddWithValue("@UserName", userName);
        }

        var list = new List<DirectoryUser>();
        await using (var reader = await find.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                list.Add(new DirectoryUser(
                    SqlRead.String(reader, "CompanyCode"),
                    SqlRead.Int(reader, "UserId"),
                    NormalizeEmail(SqlRead.String(reader, "Email")),
                    SqlRead.String(reader, "UserName"),
                    SqlRead.String(reader, "DisplayName"),
                    SqlRead.String(reader, "PasswordHash"),
                    SqlRead.Bool(reader, "IsCompanySuperAdmin"),
                    SqlRead.Bool(reader, "EmailVerified")));
            }
        }

        if (!string.IsNullOrWhiteSpace(email))
            await AddMobileDirectoryCandidatesAsync(master, email, list);

        return list;
    }

    private static async Task AddMobileDirectoryCandidatesAsync(SqlConnection master, string email, List<DirectoryUser> list)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        await using var exists = master.CreateCommand();
        exists.CommandText = "SELECT CASE WHEN OBJECT_ID('CompanyMobileAppUsers') IS NULL THEN 0 ELSE 1 END";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync() ?? 0) == 0) return;

        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
SELECT u.CompanyCode, 0 UserId, LOWER(LTRIM(RTRIM(u.Email))) Email, u.UserName, u.DisplayName, u.PasswordHash
FROM CompanyMobileAppUsers u
LEFT JOIN CompanyMobileApps a ON a.CompanyCode=u.CompanyCode
WHERE LOWER(LTRIM(RTRIM(ISNULL(u.Email,''))))=@Email
  AND ISNULL(u.IsActive,1)=1
  AND ISNULL(u.IsBlocked,0)=0
  AND ISNULL(a.IsBlocked,0)=0";
        cmd.Parameters.AddWithValue("@Email", email);
        await using var reader = await cmd.ExecuteReaderAsync();
        var seen = new HashSet<string>(list.Select(item => item.CompanyCode), StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            var companyCode = SqlRead.String(reader, "CompanyCode");
            if (string.IsNullOrWhiteSpace(companyCode) || seen.Contains(companyCode)) continue;
            seen.Add(companyCode);
            list.Add(new DirectoryUser(
                companyCode,
                0,
                NormalizeEmail(SqlRead.String(reader, "Email")),
                SqlRead.String(reader, "UserName"),
                SqlRead.String(reader, "DisplayName"),
                SqlRead.String(reader, "PasswordHash"),
                false,
                false));
        }
    }

    private async Task<DirectoryUser> LinkMobileUserToDesktopAsync(TenantInfo tenant, string databaseName, DirectoryUser mobile)
    {
        await using var con = await _db.OpenTenantAsync(databaseName);
        await using (var schema = con.CreateCommand())
        {
            schema.CommandText = @"
IF COL_LENGTH('Users','Email') IS NULL ALTER TABLE Users ADD Email NVARCHAR(180) NULL;
IF COL_LENGTH('Users','EmailVerified') IS NULL ALTER TABLE Users ADD EmailVerified BIT NOT NULL CONSTRAINT DF_Users_EmailVerified_DesktopLink DEFAULT 0;
IF COL_LENGTH('Users','OwnerVisiblePassword') IS NULL ALTER TABLE Users ADD OwnerVisiblePassword NVARCHAR(128) NULL;
IF COL_LENGTH('Users','UpdatedAt') IS NULL ALTER TABLE Users ADD UpdatedAt DATETIME2 NULL;
IF COL_LENGTH('Users','IsCompanySuperAdmin') IS NULL ALTER TABLE Users ADD IsCompanySuperAdmin BIT NOT NULL CONSTRAINT DF_Users_IsCompanySuperAdmin_DesktopLink DEFAULT 0;
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Cashier') INSERT INTO Roles(RoleName) VALUES('Cashier');";
            await schema.ExecuteNonQueryAsync();
        }

        int userId;
        await using (var find = con.CreateCommand())
        {
            find.CommandText = @"
SELECT TOP 1 UserId FROM Users
WHERE LOWER(LTRIM(RTRIM(ISNULL(Email,''))))=@Email
   OR LOWER(LTRIM(RTRIM(UserName)))=@Email
ORDER BY CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(Email,''))))=@Email THEN 0 ELSE 1 END, UserId";
            find.Parameters.AddWithValue("@Email", mobile.Email);
            userId = Convert.ToInt32(await find.ExecuteScalarAsync() ?? 0);
        }

        if (userId <= 0)
        {
            await using var insert = con.CreateCommand();
            insert.CommandText = @"
INSERT INTO Users(UserName,DisplayName,Email,EmailVerified,PasswordHash,RoleId,StoreId,IsCompanySuperAdmin,IsActive)
OUTPUT INSERTED.UserId
VALUES(@UserName,@DisplayName,@Email,0,@PasswordHash,
       (SELECT TOP 1 RoleId FROM Roles WHERE RoleName='Cashier'),
       (SELECT TOP 1 StoreId FROM Stores ORDER BY ISNULL(IsMainBranch,0) DESC, StoreId),
       0,1);";
            insert.Parameters.AddWithValue("@UserName", mobile.Email.Length <= 80 ? mobile.Email : mobile.Email[..80]);
            insert.Parameters.AddWithValue("@DisplayName", string.IsNullOrWhiteSpace(mobile.DisplayName) ? mobile.Email : mobile.DisplayName);
            insert.Parameters.AddWithValue("@Email", mobile.Email);
            insert.Parameters.AddWithValue("@PasswordHash", mobile.PasswordHash);
            userId = Convert.ToInt32(await insert.ExecuteScalarAsync() ?? 0);
        }
        else
        {
            await using var update = con.CreateCommand();
            update.CommandText = @"
UPDATE Users
SET Email=@Email,
    UserName=CASE WHEN UserName LIKE 'REMOVED_PAYNEX_OWNER_%' THEN @UserName ELSE UserName END,
    DisplayName=CASE WHEN DisplayName='Removed InterNex Owner Access' THEN @DisplayName ELSE DisplayName END,
    PasswordHash=@PasswordHash,
    IsActive=1,
    UpdatedAt=SYSUTCDATETIME()
WHERE UserId=@UserId";
            update.Parameters.AddWithValue("@Email", mobile.Email);
            update.Parameters.AddWithValue("@UserName", mobile.Email.Length <= 80 ? mobile.Email : mobile.Email[..80]);
            update.Parameters.AddWithValue("@DisplayName", string.IsNullOrWhiteSpace(mobile.DisplayName) ? mobile.Email : mobile.DisplayName);
            update.Parameters.AddWithValue("@PasswordHash", mobile.PasswordHash);
            update.Parameters.AddWithValue("@UserId", userId);
            await update.ExecuteNonQueryAsync();
        }

        if (userId > 0)
        {
            foreach (var key in KnownPermissionKeys)
            {
                if (!(key.StartsWith("sales.", StringComparison.OrdinalIgnoreCase)
                      || key.StartsWith("purchase.", StringComparison.OrdinalIgnoreCase)
                      || key.StartsWith("inventory.", StringComparison.OrdinalIgnoreCase)
                      || key.StartsWith("pricing.", StringComparison.OrdinalIgnoreCase)
                      || key.StartsWith("reports.", StringComparison.OrdinalIgnoreCase)
                      || key.Equals("finance.createExpense", StringComparison.OrdinalIgnoreCase)))
                    continue;
                await using var perm = con.CreateCommand();
                perm.CommandText = @"
IF OBJECT_ID('UserPermissions') IS NOT NULL
AND NOT EXISTS(SELECT 1 FROM UserPermissions WHERE UserId=@UserId AND PermissionKey=@Key)
    INSERT INTO UserPermissions(UserId,PermissionKey,IsAllowed,UpdatedAt) VALUES(@UserId,@Key,1,SYSUTCDATETIME());";
                perm.Parameters.AddWithValue("@UserId", userId);
                perm.Parameters.AddWithValue("@Key", key);
                await perm.ExecuteNonQueryAsync();
            }
        }

        await using (var master = await _db.OpenMasterAsync())
        await using (var dir = master.CreateCommand())
        {
            dir.CommandText = @"
IF OBJECT_ID('CentralUserDirectory') IS NOT NULL
BEGIN
    IF EXISTS(SELECT 1 FROM CentralUserDirectory WHERE Email=@Email)
        UPDATE CentralUserDirectory
        SET TenantId=@TenantId, CompanyCode=@CompanyCode, UserId=@UserId, UserName=@UserName, DisplayName=@DisplayName,
            PasswordHash=@PasswordHash, IsActive=1, UpdatedAt=SYSUTCDATETIME()
        WHERE Email=@Email;
    ELSE
        INSERT INTO CentralUserDirectory(TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsDefaultCompany,IsActive)
        VALUES(@TenantId,@CompanyCode,@UserId,@Email,@UserName,@DisplayName,@PasswordHash,0,'Cashier',0,1,1);
END";
            dir.Parameters.AddWithValue("@TenantId", tenant.TenantId);
            dir.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            dir.Parameters.AddWithValue("@UserId", userId);
            dir.Parameters.AddWithValue("@Email", mobile.Email);
            dir.Parameters.AddWithValue("@UserName", mobile.Email.Length <= 80 ? mobile.Email : mobile.Email[..80]);
            dir.Parameters.AddWithValue("@DisplayName", string.IsNullOrWhiteSpace(mobile.DisplayName) ? mobile.Email : mobile.DisplayName);
            dir.Parameters.AddWithValue("@PasswordHash", mobile.PasswordHash);
            await dir.ExecuteNonQueryAsync();
        }

        try { await _desktopAccess.SetUserAccessAsync(tenant.CompanyCode, userId, mobile.Email, true); }
        catch { /* Desktop app registration is created on grant; login still allowed with default access. */ }

        return mobile with { UserId = userId, CompanyCode = tenant.CompanyCode };
    }

    private static string EncodeDesktopPermissions(Dictionary<string, bool> permissions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in permissions)
        {
            if (kv.Key.StartsWith("__", StringComparison.Ordinal)) continue;
            payload[kv.Key] = kv.Value;
        }
        payload["__authChannel"] = "desktop";
        return JsonSerializer.Serialize(payload);
    }

    private static bool IsDesktopChannel(UserSession session)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(session.PermissionsJson) ? "{}" : session.PermissionsJson);
            if (doc.RootElement.TryGetProperty("__authChannel", out var ch) && ch.ValueKind == JsonValueKind.String)
                return string.Equals(ch.GetString(), "desktop", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* ignore */ }
        return session.SessionId.StartsWith("DESK-", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, bool>> ReadPermissionMapAsync(SqlConnection con, int userId, bool isCompanySuperAdmin)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (isCompanySuperAdmin)
        {
            foreach (var key in KnownPermissionKeys) map[key] = true;
            return map;
        }

        foreach (var key in KnownPermissionKeys) map[key] = false;
        try
        {
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT PermissionKey,IsAllowed FROM UserPermissions WHERE UserId=@UserId";
            cmd.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                map[SqlRead.String(reader, "PermissionKey")] = SqlRead.Bool(reader, "IsAllowed");
        }
        catch
        {
            // Tenant permission table may be missing on an older database; keep defaults.
        }
        return map;
    }

    private static Dictionary<string, bool> ReadPermissions(UserSession session)
    {
        try
        {
            using var doc = JsonDocument.Parse(session.PermissionsJson ?? "{}");
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind == JsonValueKind.True) map[prop.Name] = true;
                else if (prop.Value.ValueKind == JsonValueKind.False) map[prop.Name] = false;
            }
            return map;
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<string> CreateLoginSessionAsync(UserSession session, HttpContext http)
    {
        await EnsureMasterDirectoryAsync();
        var sessionId = "DESK-" + Guid.NewGuid().ToString("N");
        var tenant = await _db.GetTenantByCodeAsync(session.CompanyCode);
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AuthSessionAudit(SessionId,TenantId,CompanyCode,UserId,UserName,Email,EventName,EnvironmentName,BranchCode,IpAddress,UserAgent)
VALUES(@SessionId,@TenantId,@CompanyCode,@UserId,@UserName,@Email,'LOGIN',@EnvironmentName,@BranchCode,@IpAddress,@UserAgent);
UPDATE Tenants SET LastLoginAt=SYSUTCDATETIME() WHERE CompanyCode=@CompanyCode;
UPDATE CentralUserDirectory SET LastLoginAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME() WHERE Email=@Email AND CompanyCode=@CompanyCode;";
        cmd.Parameters.AddWithValue("@SessionId", sessionId);
        var tenantId = cmd.Parameters.Add("@TenantId", SqlDbType.UniqueIdentifier);
        tenantId.Value = tenant == null ? DBNull.Value : tenant.TenantId;
        cmd.Parameters.AddWithValue("@CompanyCode", session.CompanyCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@UserId", session.UserId);
        cmd.Parameters.AddWithValue("@UserName", session.UserName ?? string.Empty);
        cmd.Parameters.AddWithValue("@Email", session.Email ?? string.Empty);
        cmd.Parameters.AddWithValue("@EnvironmentName", session.Environment ?? "Production");
        cmd.Parameters.AddWithValue("@BranchCode", session.BranchCode ?? string.Empty);
        cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
        cmd.Parameters.AddWithValue("@UserAgent", http.Request.Headers["User-Agent"].ToString());
        await cmd.ExecuteNonQueryAsync();
        return sessionId;
    }

    private async Task CloseSessionAsync(string? sessionId, string companyCode, int userId)
    {
        await EnsureMasterDirectoryAsync();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"INSERT INTO AuthSessionAudit(SessionId,CompanyCode,UserId,EventName)
VALUES(@SessionId,@CompanyCode,@UserId,'LOGOUT')";
        cmd.Parameters.AddWithValue("@SessionId", sessionId ?? string.Empty);
        cmd.Parameters.AddWithValue("@CompanyCode", companyCode);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> IsSessionLoggedOutAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        await EnsureMasterDirectoryAsync();
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM AuthSessionAudit WHERE SessionId=@SessionId AND EventName='LOGOUT'";
        cmd.Parameters.AddWithValue("@SessionId", sessionId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private async Task EnsureMasterDirectoryAsync()
    {
        await using var con = await _db.OpenMasterAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID('AuthSessionAudit') IS NULL
BEGIN
CREATE TABLE AuthSessionAudit(
    SessionAuditId BIGINT IDENTITY(1,1) PRIMARY KEY,
    SessionId NVARCHAR(80) NOT NULL,
    TenantId UNIQUEIDENTIFIER NULL,
    CompanyCode NVARCHAR(40) NULL,
    UserId INT NULL,
    UserName NVARCHAR(100) NULL,
    Email NVARCHAR(180) NULL,
    EventName NVARCHAR(40) NOT NULL,
    EnvironmentName NVARCHAR(40) NULL,
    BranchCode NVARCHAR(40) NULL,
    IpAddress NVARCHAR(80) NULL,
    UserAgent NVARCHAR(500) NULL,
    EventAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ResolveTenantDatabase(TenantInfo tenant, string environmentName)
    {
        if (string.Equals(environmentName, "Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            if (!tenant.AllowSandbox) throw new InvalidOperationException("Sandbox is not allowed for this company.");
            if (string.IsNullOrWhiteSpace(tenant.SandboxDatabaseName)) throw new InvalidOperationException("Sandbox database is not created yet.");
            return tenant.SandboxDatabaseName;
        }
        return string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
    }

    private static string NormalizeEmail(string? email)
    {
        var value = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length < 6 || value.Length > 180) return string.Empty;
        if (!value.Contains('@') || !value.Contains('.') || value.Contains(' ')) return string.Empty;
        return value;
    }

    private static IResult Fail(string code, string message, int status) =>
        Results.Json(new { ok = false, code, message }, statusCode: status);

    private sealed record DirectoryUser(string CompanyCode, int UserId, string Email, string UserName, string DisplayName, string PasswordHash, bool IsCompanySuperAdmin, bool EmailVerified);

    private static readonly string[] KnownPermissionKeys =
    [
        "sales.createInvoice","sales.editInvoice","sales.deleteInvoice","sales.postInvoice","sales.createReturn",
        "purchase.createInvoice","purchase.editInvoice","purchase.deleteInvoice","purchase.postInvoice","purchase.createReturn",
        "inventory.createItems","inventory.editItems","inventory.deleteItems","inventory.stockAdjustment","inventory.transfer","inventory.goodsReceipt","inventory.goodsIssue",
        "pricing.changeProductPrice","pricing.changeProductDiscount","pricing.overrideSellingPrice",
        "finance.createExpense","finance.approveExpense","finance.viewFinancialReports",
        "users.createUser","users.editUser","users.deleteUser","users.resetPassword","users.assignPermissions","users.promoteCompanySuperAdmin",
        "reports.viewReports","reports.exportReports","reports.printReports",
        "system.companySettings","system.branchManagement","system.backupRestore","system.generalConfiguration"
    ];
}
