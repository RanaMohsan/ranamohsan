using PayNex.Cloud.Api.Services.Images;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.RateLimiting;
using PayNex.Cloud.Api.Data;
using PayNex.Cloud.Api.Models;
using PayNex.Cloud.Api.Services;
using PayNex.Cloud.Api.Security;
using PayNex.Cloud.Api.Middleware;
using PayNex.Cloud.Api.Endpoints;
using PayNex.Cloud.Api.Validation;
using System.Data;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PayNexOptions>(builder.Configuration.GetSection("PayNex"));
builder.Services.Configure<ItemImageOptions>(builder.Configuration.GetSection(ItemImageOptions.SectionName));
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 40L * 1024 * 1024;
});
var allowedCorsOrigins = builder.Configuration.GetSection("PayNex:AllowedCorsOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<SqlScriptRunner>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<AuthTokenService>();
builder.Services.AddSingleton<AuthenticationSecurityService>();
builder.Services.AddSingleton<TenantProvisioningService>();
builder.Services.AddSingleton<CloudReportHtmlService>();
builder.Services.AddSingleton<SuperAdminService>();
builder.Services.AddSingleton<RolePermissionService>();
builder.Services.AddSingleton<AuditTrailService>();
builder.Services.AddSingleton<BackupRestoreService>();
builder.Services.AddSingleton<RequestValidationService>();
builder.Services.AddSingleton<ConfigurationPackageService>();
builder.Services.AddSingleton<ExpenseManagementService>();
builder.Services.AddSingleton<BankAccountService>();
builder.Services.AddSingleton<PlatformSubscriptionService>();
builder.Services.AddSingleton<SubscriptionAccessService>();
builder.Services.AddSingleton<DesktopAuthService>();
builder.Services.AddSingleton<DesktopAccessService>();
builder.Services.AddSingleton<CredentialUniquenessService>();
builder.Services.AddSingleton<IImageOptimizationService, ImageOptimizationService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (allowedCorsOrigins.Length > 0) p.WithOrigins(allowedCorsOrigins).AllowAnyHeader().AllowAnyMethod();
    else p.SetIsOriginAllowed(origin => string.IsNullOrWhiteSpace(origin) || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase)).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Baseline browser hardening. Inline scripts/styles remain allowed because the existing ERP UI uses them.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});

app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
app.UseMiddleware<AdminAuthorizationMiddleware>();

// Server-side authentication/session validation for every tenant API request.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/mobile/login", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/mobile/verify-otp", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/mobile/resend-otp", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/mobile/health", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/mobile/refresh", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/desktop/health", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/desktop/login", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/desktop/verify-otp", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/desktop/resend-otp", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/api/desktop/refresh", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase))
    {
        var tokenService = context.RequestServices.GetRequiredService<AuthTokenService>();
        var user = ApiAuth.RequireUser(context, tokenService);
        if (user == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Authentication is required." });
            return;
        }
        var db = context.RequestServices.GetRequiredService<ConnectionFactory>();
        if (await IsTenantSessionLoggedOutAsync(db, user.SessionId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Session has expired or user logged out." });
            return;
        }
    }
    await next();
});

// Double-submit CSRF protection for browser requests authenticated by secure cookies.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var unsafeMethod = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
    var publicAuthAction = path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/verify-otp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/resend-otp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/mobile/login", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/mobile/verify-otp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/mobile/resend-otp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/mobile/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/mobile/refresh", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/desktop/login", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/desktop/verify-otp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/desktop/resend-otp", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/desktop/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/desktop/refresh", StringComparison.OrdinalIgnoreCase);
    var hasCookieAuthentication = context.Request.Cookies.ContainsKey("paynex_auth") || context.Request.Cookies.ContainsKey("paynex_refresh");
    var hasBearerAuthentication = context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    if (unsafeMethod && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !publicAuthAction && hasCookieAuthentication && !hasBearerAuthentication)
    {
        var csrfCookie = context.Request.Cookies["paynex_csrf"] ?? string.Empty;
        var csrfHeader = context.Request.Headers["X-PayNex-CSRF"].ToString();
        if (string.IsNullOrWhiteSpace(csrfCookie) || !FixedTimeEqualsString(csrfCookie, csrfHeader))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "Security validation failed. Refresh the page and try again." });
            return;
        }
    }
    await next();
});

app.UseMiddleware<RolePermissionMiddleware>();
app.UseMiddleware<AuditTrailMiddleware>();

// Browser route protection for static ERP pages. API routes still use Authorization headers.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var isHtmlPage = context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(path) || path.Equals("/", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
    var isPublic = path.Equals("/login.html", StringComparison.OrdinalIgnoreCase) ||
        // admin.html is the dedicated Super Admin sign-in surface; its APIs remain protected by AdminAuthorizationMiddleware.
        path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase);

    if (isHtmlPage && !isPublic)
    {
        var tokenService = context.RequestServices.GetRequiredService<AuthTokenService>();
        var cookieToken = context.Request.Cookies["paynex_auth"];
        var session = tokenService.Validate(cookieToken);
        var db = context.RequestServices.GetRequiredService<ConnectionFactory>();
        if (session != null && await IsTenantSessionLoggedOutAsync(db, session.SessionId)) session = null;
        if (session == null)
        {
            var returnUrl = Uri.EscapeDataString((path == "/" ? "/workspace.html" : path) + context.Request.QueryString);
            context.Response.Redirect("/login.html?returnUrl=" + returnUrl, permanent: false);
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        ctx.Context.Response.Headers.Pragma = "no-cache";
        ctx.Context.Response.Headers.Expires = "0";
    }
});

// Swagger / All APIs UI is PayNex Owner only — never public to tenant users.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        var tokens = context.RequestServices.GetRequiredService<AuthTokenService>();
        var user = ApiAuth.RequireUser(context, tokens);
        if (user == null)
        {
            context.Response.Redirect("/login.html?returnUrl=" + Uri.EscapeDataString("/swagger/index.html"), permanent: false);
            return;
        }
        if (!user.IsPlatformOwner)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("InterNex Owner access is required to view All APIs.");
            return;
        }
    }
    await next();
});

app.UseSwagger();
app.UseSwaggerUI();

using (var scope = app.Services.CreateScope())
{
    var tenantProvisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
    await tenantProvisioning.EnsureMasterDatabaseAsync();
    await scope.ServiceProvider.GetRequiredService<SuperAdminService>().EnsureBootstrapSuperAdminAsync();
    await tenantProvisioning.RemovePlatformOwnerFromTenantAccessAsync();
    await scope.ServiceProvider.GetRequiredService<AuthenticationSecurityService>().EnsureSchemaAsync();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "OK", app = "InterNex Cloud SaaS", utc = DateTime.UtcNow }));

app.MapSuperAdminEndpoints();
app.MapInvoiceDraftEndpoints();
app.MapSalesReturnOrderEndpoints();
app.MapBankAccountEndpoints();
app.MapPlatformSubscriptionEndpoints();
app.MapDesktopEndpoints();
app.MapDesktopPlatformEndpoints();

app.MapGet("/api/platform/companies", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    var list = await tenants.ListTenantsAsync();
    return Results.Ok(list.Select(t => new
    {
        t.TenantId,
        t.CompanyCode,
        t.CompanyName,
        t.OwnerName,
        t.OwnerEmail,
        t.OwnerMobile,
        t.Status,
        t.SubscriptionPlan,
        t.LicenseStatus,
        t.DatabaseCreationStatus,
        t.ProvisioningStatus,
        t.CompanyStartDate,
        t.LicenseExpiryDate,
        t.ExpiryDate,
        t.RenewalDate,
        t.CreatedAt,
        t.AllowSandbox,
        t.ProductionDatabaseName,
        t.SandboxDatabaseName,
        t.SandboxCreatedAt,
        t.ActiveEnvironment,
        t.AllowMultipleBranches,
        t.MaxBranches
    }));
});

app.MapGet("/api/platform/companies/{companyCode}/details", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, PasswordService passwords, string companyCode) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    var databaseName = string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
    var users = new List<Dictionary<string, object?>>();
    var branches = new List<Dictionary<string, object?>>();
    var directory = new List<Dictionary<string, object?>>();

    if (!string.IsNullOrWhiteSpace(databaseName) && ConnectionFactory.IsSafeDatabaseName(databaseName))
    {
        try
        {
            await using var con = await db.OpenTenantAsync(databaseName);
            await EnsureTenantUserSecuritySchemaAsync(con);
            await EnsureTenantBranchSchemaAsync(con);
            users = await SqlList.ReadAsync(con, @"
SELECT u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,ISNULL(u.PhoneNumber,'') PhoneNumber,
       ISNULL(r.RoleName,'') RoleName,ISNULL(s.StoreCode,'') BranchCode,ISNULL(s.StoreName,'') BranchName,
       u.IsActive,ISNULL(u.EmailVerified,0) EmailVerified,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,
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
                if (string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(hash) && passwords.Verify("Admin@123", hash))
                    plain = "Admin@123";

                if (!string.IsNullOrWhiteSpace(plain) && userId > 0)
                {
                    await using var fix = con.CreateCommand();
                    fix.CommandText = "UPDATE Users SET OwnerVisiblePassword=@Pwd WHERE UserId=@UserId AND ISNULL(OwnerVisiblePassword,'')=''";
                    fix.Parameters.AddWithValue("@Pwd", plain);
                    fix.Parameters.AddWithValue("@UserId", userId);
                    await fix.ExecuteNonQueryAsync();
                }

                row["PasswordPlain"] = plain;
                row["PasswordInfo"] = string.IsNullOrWhiteSpace(plain)
                    ? (string.IsNullOrWhiteSpace(hash) ? "No password set" : "Unknown - reset to reveal")
                    : plain;
                row.Remove("PasswordHash");
                row.Remove("OwnerVisiblePassword");
            }

            branches = await SqlList.ReadAsync(con, @"
SELECT StoreId BranchId,StoreCode BranchCode,StoreName BranchName,AddressLine,IsActive,ISNULL(IsMainBranch,0) IsMainBranch
FROM Stores
ORDER BY ISNULL(IsMainBranch,0) DESC, StoreName");
        }
        catch (Exception ex)
        {
            users.Add(new Dictionary<string, object?> { ["Error"] = "Unable to read tenant database: " + ex.Message });
        }
    }

    await using (var master = await db.OpenMasterAsync())
    await using (var cmd = master.CreateCommand())
    {
        cmd.CommandText = @"
SELECT DirectoryUserId,CompanyCode,UserId,Email,UserName,DisplayName,RoleName,EmailVerified,IsCompanySuperAdmin,IsActive,LastLoginAt,CreatedAt,UpdatedAt
FROM CentralUserDirectory
WHERE CompanyCode=@CompanyCode
ORDER BY IsCompanySuperAdmin DESC, DisplayName";
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        directory = await SqlList.ReadAsync(cmd);
    }

    return Results.Ok(new { company = tenant, users, branches, centralDirectory = directory, passwordPolicy = "Passwords are shown clearly for InterNex Owner support. Use Copy, or Set / Reset to change a user password." });
});

app.MapPost("/api/platform/companies/{companyCode}/users/{id:int}/reset-password", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, PasswordService passwords, AuthenticationSecurityService authSecurity, CredentialUniquenessService uniqueness, string companyCode, int id, ResetUserPasswordRequest request) =>
{
    var actor = ApiAuth.RequireUser(http, tokens);
    if (actor == null) return Results.Unauthorized();
    if (!actor.IsPlatformOwner) return Results.Forbid();
    if (!IsAcceptablePassword(request.NewPassword))
        return Results.BadRequest(new { message = "Password must be 8 to 128 characters and include upper-case, lower-case, and a number." });
    var passwordConflict = await uniqueness.FindConflictMessageAsync(request.NewPassword, companyCode, id);
    if (!string.IsNullOrWhiteSpace(passwordConflict))
        return Results.BadRequest(new { message = passwordConflict });

    var tenant = await tenants.GetTenantAsync(companyCode);
    var databaseName = string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
    if (!ConnectionFactory.IsSafeDatabaseName(databaseName))
        return Results.BadRequest(new { message = "The company Production database is not available." });

    var passwordHash = passwords.Hash(request.NewPassword);
    string email;
    string userName;
    string displayName;
    string roleName;
    bool emailVerified;
    bool isCompanySuperAdmin;
    bool isActive;

    await using (var tenantConnection = await db.OpenTenantAsync(databaseName))
    {
        await EnsureTenantUserSecuritySchemaAsync(tenantConnection);
        await using var command = tenantConnection.CreateCommand();
        command.CommandText = @"
UPDATE Users
SET PasswordHash=@PasswordHash,OwnerVisiblePassword=@OwnerVisiblePassword,UpdatedAt=SYSUTCDATETIME()
WHERE UserId=@UserId;
SELECT TOP 1 ISNULL(u.Email,'') Email,u.UserName,u.DisplayName,ISNULL(r.RoleName,'') RoleName,
       ISNULL(u.EmailVerified,0) EmailVerified,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,u.IsActive
FROM Users u
LEFT JOIN Roles r ON r.RoleId=u.RoleId
WHERE u.UserId=@UserId;";
        command.Parameters.AddWithValue("@UserId", id);
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        command.Parameters.AddWithValue("@OwnerVisiblePassword", request.NewPassword.Trim());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.NotFound(new { message = "Company user not found." });
        email = NormalizeCloudEmail(SqlRead.String(reader, "Email"));
        userName = SqlRead.String(reader, "UserName");
        displayName = SqlRead.String(reader, "DisplayName");
        roleName = SqlRead.String(reader, "RoleName");
        emailVerified = SqlRead.Bool(reader, "EmailVerified");
        isCompanySuperAdmin = SqlRead.Bool(reader, "IsCompanySuperAdmin");
        isActive = SqlRead.Bool(reader, "IsActive");
    }

    if (!string.IsNullOrWhiteSpace(email))
    {
        await EnsureMasterUserDirectorySchemaAsync(db);
        await using var master = await db.OpenMasterAsync();
        await using var directoryCommand = master.CreateCommand();
        directoryCommand.CommandText = @"
IF EXISTS(SELECT 1 FROM CentralUserDirectory WHERE CompanyCode=@CompanyCode AND (UserId=@UserId OR Email=@Email))
BEGIN
    UPDATE CentralUserDirectory
    SET PasswordHash=@PasswordHash,Email=@Email,UserName=@UserName,DisplayName=@DisplayName,
        RoleName=@RoleName,IsCompanySuperAdmin=@IsCompanySuperAdmin,IsActive=@IsActive,
        EmailVerified=@EmailVerified,UpdatedAt=SYSUTCDATETIME()
    WHERE CompanyCode=@CompanyCode AND (UserId=@UserId OR Email=@Email);
END
ELSE
BEGIN
    INSERT INTO CentralUserDirectory(TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsDefaultCompany,IsActive)
    VALUES(@TenantId,@CompanyCode,@UserId,@Email,@UserName,@DisplayName,@PasswordHash,@EmailVerified,@RoleName,@IsCompanySuperAdmin,1,@IsActive);
END";
        directoryCommand.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        directoryCommand.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        directoryCommand.Parameters.AddWithValue("@UserId", id);
        directoryCommand.Parameters.AddWithValue("@Email", email);
        directoryCommand.Parameters.AddWithValue("@UserName", userName);
        directoryCommand.Parameters.AddWithValue("@DisplayName", displayName);
        directoryCommand.Parameters.AddWithValue("@PasswordHash", passwordHash);
        directoryCommand.Parameters.AddWithValue("@RoleName", roleName);
        directoryCommand.Parameters.AddWithValue("@EmailVerified", emailVerified);
        directoryCommand.Parameters.AddWithValue("@IsCompanySuperAdmin", isCompanySuperAdmin);
        directoryCommand.Parameters.AddWithValue("@IsActive", isActive);
        await directoryCommand.ExecuteNonQueryAsync();
    }
    await authSecurity.RevokeUserAuthenticationAsync(email, tenant.CompanyCode, id);

    return Results.Ok(new
    {
        userId = id,
        passwordChanged = true,
        passwordPlain = request.NewPassword.Trim(),
        message = "Password saved. It is now shown clearly in the Password column. Existing sessions were revoked."
    });
});


app.MapPost("/api/platform/companies", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, RequestValidationService validator, CreateTenantRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    var validation = validator.ValidateCreateTenant(request);
    if (!validation.Ok) return Results.BadRequest(new { message = validation.Message });
    var result = await tenants.CreateTenantAsync(request);
    return Results.Ok(result);
});

app.MapPut("/api/platform/companies/{companyCode}", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, RequestValidationService validator, string companyCode, TenantCardUpdateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    var validation = validator.ValidateTenantCardUpdate(request);
    if (!validation.Ok) return Results.BadRequest(new { message = validation.Message });
    await tenants.UpdateTenantCardAsync(companyCode, request);
    return Results.Ok(await tenants.GetTenantAsync(companyCode));
});

app.MapPost("/api/platform/companies/{companyCode}/sandbox", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, string companyCode, SandboxCreateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    var tenant = await tenants.CreateSandboxAsync(companyCode, request);
    return Results.Ok(new { message = "Sandbox database is ready.", tenant });
});

app.MapPost("/api/platform/companies/{companyCode}/delete-challenge", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, AuthenticationSecurityService authSecurity, string companyCode) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    try
    {
        var tenant = await tenants.GetTenantAsync(companyCode);
        var ownerEmail = NormalizeCloudEmail(user.Email);
        if (string.IsNullOrWhiteSpace(ownerEmail)) ownerEmail = NormalizeCloudEmail(tenants.Options.PlatformOwnerEmail);
        if (string.IsNullOrWhiteSpace(ownerEmail)) return Results.BadRequest(new { message = "A platform owner email is required to delete a client." });
        var challenge = await authSecurity.CreateDeleteClientChallengeAsync(tenant.CompanyCode, tenant.CompanyName, ownerEmail, http);
        return Results.Ok(new
        {
            challengeId = challenge.ChallengeId,
            maskedEmail = challenge.MaskedEmail,
            expiresInSeconds = challenge.ExpiresInSeconds,
            message = $"A verification code was sent to {challenge.MaskedEmail}."
        });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});

app.MapPost("/api/platform/companies/{companyCode}/delete-challenge/verify", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, AuthenticationSecurityService authSecurity, string companyCode, VerifyLoginOtpRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    try
    {
        var tenant = await tenants.GetTenantAsync(companyCode);
        var verified = await authSecurity.VerifyDeleteClientChallengeAsync(tenant.CompanyCode, request.ChallengeId, request.Code);
        return Results.Ok(new
        {
            deleteToken = verified.DeleteToken,
            companyCode = verified.CompanyCode,
            maskedEmail = verified.MaskedEmail,
            message = "Verification succeeded. Confirm to permanently delete this client."
        });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});

app.MapPost("/api/platform/companies/{companyCode}/delete", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, AuthenticationSecurityService authSecurity, string companyCode, DeleteClientRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    try
    {
        var tenant = await tenants.GetTenantAsync(companyCode);
        var typedCode = (request.ConfirmCompanyCode ?? string.Empty).Trim();
        if (!string.Equals(typedCode, tenant.CompanyCode, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { message = "Type the company code exactly to confirm deletion." });
        await authSecurity.ConsumeDeleteClientTokenAsync(tenant.CompanyCode, request.DeleteToken);
        var result = await tenants.DeleteClientAsync(tenant.CompanyCode, user.Email);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
});

app.MapPost("/api/platform/companies/{companyCode}/open", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, TenantProvisioningService tenants, string companyCode, PlatformCompanySwitchRequest request) =>
{
    var owner = ApiAuth.RequireUser(http, tokens);
    if (owner == null) return Results.Unauthorized();
    if (!owner.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    var environmentName = NormalizeTenantEnvironment(request.Environment);
    string databaseName;
    try { databaseName = ResolveTenantDatabase(tenant, environmentName); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }

    await using var con = await db.OpenTenantAsync(databaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    await EnsureTenantBranchSchemaAsync(con);
    var branch = await GetMainBranchAsync(con);

    int tenantUserId = 0;
    int roleId = 0;
    int storeId = branch.BranchId;
    string roleName = "Platform Super Admin";
    string displayName = owner.DisplayName;
    string userName = "paynex-owner";
    string storeName = branch.BranchName;
    var ownerEmail = NormalizeCloudEmail(owner.Email);

    await using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = @"
SELECT TOP 1 u.UserId,u.UserName,u.DisplayName,u.RoleId,ISNULL(r.RoleName,'Admin') RoleName,u.StoreId,ISNULL(s.StoreName,'Main Store') StoreName
FROM Users u
LEFT JOIN Roles r ON r.RoleId=u.RoleId
LEFT JOIN Stores s ON s.StoreId=u.StoreId
WHERE u.IsActive=1
ORDER BY CASE WHEN @OwnerEmail<>'' AND LOWER(LTRIM(RTRIM(ISNULL(u.Email,''))))=@OwnerEmail THEN 0 ELSE 1 END,
         ISNULL(u.IsCompanySuperAdmin,0) DESC,
         CASE WHEN ISNULL(r.RoleName,'') IN ('Admin','Company Super Admin') THEN 0 ELSE 1 END,
         u.UserId";
        cmd.Parameters.AddWithValue("@OwnerEmail", ownerEmail);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            tenantUserId = SqlRead.Int(r, "UserId");
            roleId = SqlRead.Int(r, "RoleId");
            storeId = SqlRead.Int(r, "StoreId");
            storeName = SqlRead.String(r, "StoreName");
            var tenantDisplay = SqlRead.String(r, "DisplayName");
            if (!string.IsNullOrWhiteSpace(tenantDisplay)) displayName = tenantDisplay;
            var tenantUserName = SqlRead.String(r, "UserName");
            if (!string.IsNullOrWhiteSpace(tenantUserName)) userName = tenantUserName;
        }
    }

    if (tenantUserId <= 0) return Results.BadRequest(new { message = "No active company admin user found in selected company database. Create/repair the client first." });

    var permissionsJson = JsonSerializer.Serialize(PermissionMap(true));
    var sessionId = await CreateTenantLoginSessionAsync(db, tenant, tenantUserId, userName, owner.Email, http, environmentName, branch.BranchCode);
    var session = new UserSession(
        tenant.CompanyCode, tenant.CompanyName, databaseName,
        tenantUserId, userName, displayName,
        roleId, roleName, storeId, storeName, environmentName,
        branch.BranchId, branch.BranchCode, branch.BranchName, tenant.AllowMultipleBranches,
        owner.Email, true, true, permissionsJson, sessionId);
    var token = tokens.Create(session);
    var refresh = await authSecurity.ReplaceRefreshTokenAsync(http.Request.Cookies["paynex_refresh"], session);
    SetTenantAuthCookie(http, token, authSecurity.AccessTokenExpiryMinutes);
    SetRefreshTokenCookie(http, refresh.PlainToken, refresh.ExpiresAt);
    SetCsrfCookie(http);
    return Results.Ok(new { token, user = session, redirectUrl = "/workspace.html" });
});

app.MapGet("/api/platform/companies/{companyCode}/mobile-app", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, string companyCode) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);

    Dictionary<string, object?>? app = null;
    List<Dictionary<string, object?>> users;
    await using (var master = await db.OpenMasterAsync())
    {
        await using (var cmd = master.CreateCommand())
        {
            cmd.CommandText = @"
SELECT TOP 1 AppId,TenantId,CompanyCode,AppName,Platform,PackageName,BundleId,AppVersion,ApiKey,Status,IsBlocked,BlockReason,Notes,RegisteredAt,UpdatedAt,BlockedAt
FROM CompanyMobileApps
WHERE CompanyCode=@CompanyCode";
            cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            var rows = await SqlList.ReadAsync(cmd);
            app = rows.FirstOrDefault();
        }

        await using (var cmd = master.CreateCommand())
        {
            cmd.CommandText = @"
SELECT MobileAppUserId,AppId,CompanyCode,UserName,DisplayName,Email,Mobile,RoleName,IsBlocked,IsActive,BlockReason,CreatedAt,UpdatedAt,BlockedAt,
       CONVERT(bit,CASE WHEN ISNULL(PasswordHash,'')='' THEN 0 ELSE 1 END) HasPassword,
       ISNULL(OwnerVisiblePassword,'') OwnerVisiblePassword,
       ISNULL(EmailVerified,0) EmailVerified
FROM CompanyMobileAppUsers
WHERE CompanyCode=@CompanyCode
ORDER BY DisplayName, UserName";
            cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
            users = await SqlList.ReadAsync(cmd);
            foreach (var row in users)
            {
                var plain = Convert.ToString(row.TryGetValue("OwnerVisiblePassword", out var ov) ? ov : "")?.Trim() ?? "";
                row["PasswordPlain"] = plain;
                row["passwordPlain"] = plain;
                row.Remove("OwnerVisiblePassword");
                row.Remove("PasswordHash");
            }
        }
    }

    return Results.Ok(new
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
        users
    });
});

app.MapPost("/api/platform/companies/{companyCode}/mobile-app", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, string companyCode, MobileAppRegisterRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);

    var appName = string.IsNullOrWhiteSpace(request.AppName) ? "InterNex Mobile" : request.AppName.Trim();
    var platform = NormalizeMobileAppPlatform(request.Platform);
    var apiKey = "PNXMOB-" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();

    await using var master = await db.OpenMasterAsync();
    await using (var exists = master.CreateCommand())
    {
        exists.CommandText = "SELECT COUNT(1) FROM CompanyMobileApps WHERE CompanyCode=@CompanyCode";
        exists.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        var count = Convert.ToInt32(await exists.ExecuteScalarAsync() ?? 0);
        if (count > 0) return Results.BadRequest(new { message = "Mobile app is already registered for this company. Open Mobile App Detail to manage it." });
    }

    await using (var cmd = master.CreateCommand())
    {
        cmd.CommandText = @"
INSERT INTO CompanyMobileApps(TenantId,CompanyCode,AppName,Platform,PackageName,BundleId,AppVersion,ApiKey,Status,IsBlocked,Notes,RegisteredAt)
OUTPUT INSERTED.AppId
VALUES(@TenantId,@CompanyCode,@AppName,@Platform,@PackageName,@BundleId,@AppVersion,@ApiKey,'Active',0,@Notes,SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@AppName", appName);
        cmd.Parameters.AddWithValue("@Platform", platform);
        cmd.Parameters.AddWithValue("@PackageName", (object?)request.PackageName?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@BundleId", (object?)request.BundleId?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AppVersion", (object?)request.AppVersion?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ApiKey", apiKey);
        cmd.Parameters.AddWithValue("@Notes", (object?)request.Notes?.Trim() ?? DBNull.Value);
        var appId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        return Results.Ok(new { message = "Mobile app registered for this company.", appId, apiKey, companyCode = tenant.CompanyCode });
    }
});

app.MapPut("/api/platform/companies/{companyCode}/mobile-app", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, string companyCode, MobileAppUpdateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);

    var appName = string.IsNullOrWhiteSpace(request.AppName) ? "InterNex Mobile" : request.AppName.Trim();
    var platform = NormalizeMobileAppPlatform(request.Platform);

    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
UPDATE CompanyMobileApps
SET AppName=@AppName,Platform=@Platform,PackageName=@PackageName,BundleId=@BundleId,AppVersion=@AppVersion,Notes=@Notes,UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
SELECT @@ROWCOUNT;";
    cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
    cmd.Parameters.AddWithValue("@AppName", appName);
    cmd.Parameters.AddWithValue("@Platform", platform);
    cmd.Parameters.AddWithValue("@PackageName", (object?)request.PackageName?.Trim() ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@BundleId", (object?)request.BundleId?.Trim() ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@AppVersion", (object?)request.AppVersion?.Trim() ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@Notes", (object?)request.Notes?.Trim() ?? DBNull.Value);
    var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    if (rows <= 0) return Results.NotFound(new { message = "Mobile app is not registered for this company." });
    return Results.Ok(new { message = "Mobile app details saved.", companyCode = tenant.CompanyCode });
});

app.MapPost("/api/platform/companies/{companyCode}/mobile-app/block", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, string companyCode, MobileAppBlockRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);

    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
UPDATE CompanyMobileApps
SET IsBlocked=@IsBlocked,
    Status=CASE WHEN @IsBlocked=1 THEN 'Blocked' ELSE 'Active' END,
    BlockReason=CASE WHEN @IsBlocked=1 THEN @BlockReason ELSE NULL END,
    BlockedAt=CASE WHEN @IsBlocked=1 THEN SYSUTCDATETIME() ELSE NULL END,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode;
SELECT @@ROWCOUNT;";
    cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
    cmd.Parameters.AddWithValue("@IsBlocked", request.IsBlocked);
    cmd.Parameters.AddWithValue("@BlockReason", (object?)request.Reason?.Trim() ?? DBNull.Value);
    var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    if (rows <= 0) return Results.NotFound(new { message = "Mobile app is not registered for this company." });
    return Results.Ok(new
    {
        message = request.IsBlocked ? "Company mobile app access is blocked." : "Company mobile app access is unblocked.",
        isBlocked = request.IsBlocked,
        companyCode = tenant.CompanyCode
    });
});

app.MapPost("/api/platform/companies/{companyCode}/mobile-app/users", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, PasswordService passwords, string companyCode, MobileAppUserCreateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);

    var email = NormalizeCloudEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest(new { message = "A valid email address is required. Email is the mobile login id." });
    // Login id is always the email (plan: no free-form username).
    var userName = email;
    var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim();
    var mobile = (request.Mobile ?? string.Empty).Trim();
    var roleName = string.IsNullOrWhiteSpace(request.RoleName) ? "Mobile User" : request.RoleName.Trim();

    if (!IsAcceptablePassword(request.Password))
        return Results.BadRequest(new { message = "Password must be 8 to 128 characters and include upper-case, lower-case, and a number." });

    var emailConflict = await FindGlobalEmailConflictAsync(db, email, forCompanyCode: tenant.CompanyCode, allowSameCompanyDirectory: true);
    if (!string.IsNullOrWhiteSpace(emailConflict))
        return Results.BadRequest(new { message = emailConflict });

    await using var master = await db.OpenMasterAsync();
    long appId;
    bool companyBlocked;
    await using (var find = master.CreateCommand())
    {
        find.CommandText = "SELECT TOP 1 AppId,IsBlocked FROM CompanyMobileApps WHERE CompanyCode=@CompanyCode";
        find.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        await using var reader = await find.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.BadRequest(new { message = "Register the mobile app for this company before adding users." });
        appId = Convert.ToInt64(reader["AppId"]);
        companyBlocked = Convert.ToBoolean(reader["IsBlocked"]);
    }

    await using (var dup = master.CreateCommand())
    {
        dup.CommandText = "SELECT COUNT(1) FROM CompanyMobileAppUsers WHERE CompanyCode=@CompanyCode AND (UserName=@UserName OR LOWER(ISNULL(Email,''))=@Email)";
        dup.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        dup.Parameters.AddWithValue("@UserName", userName);
        dup.Parameters.AddWithValue("@Email", email);
        if (Convert.ToInt32(await dup.ExecuteScalarAsync() ?? 0) > 0)
            return Results.BadRequest(new { message = "A mobile app user with this email already exists for the company." });
    }

    var passwordHash = passwords.Hash(request.Password);
    await using (var cmd = master.CreateCommand())
    {
        cmd.CommandText = @"
INSERT INTO CompanyMobileAppUsers(AppId,TenantId,CompanyCode,UserName,DisplayName,Email,Mobile,PasswordHash,OwnerVisiblePassword,RoleName,IsBlocked,IsActive,EmailVerified,CreatedAt)
OUTPUT INSERTED.MobileAppUserId
VALUES(@AppId,@TenantId,@CompanyCode,@UserName,@DisplayName,@Email,@Mobile,@PasswordHash,@OwnerVisiblePassword,@RoleName,0,1,0,SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@AppId", appId);
        cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
        cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
        cmd.Parameters.AddWithValue("@UserName", userName);
        cmd.Parameters.AddWithValue("@DisplayName", displayName);
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@Mobile", string.IsNullOrWhiteSpace(mobile) ? DBNull.Value : mobile);
        cmd.Parameters.AddWithValue("@PasswordHash", passwordHash);
        cmd.Parameters.AddWithValue("@OwnerVisiblePassword", request.Password.Trim());
        cmd.Parameters.AddWithValue("@RoleName", roleName);
        var mobileAppUserId = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        return Results.Ok(new
        {
            message = companyBlocked
                ? "Mobile app user added. First login will require OTP on this email. Note: company mobile access is currently blocked."
                : "Mobile app user added. Login id is the email. First login will require OTP verification.",
            mobileAppUserId,
            userName,
            email,
            passwordPlain = request.Password.Trim(),
            companyCode = tenant.CompanyCode
        });
    }
});

app.MapPost("/api/platform/companies/{companyCode}/mobile-app/users/{id:long}/reset-password", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, PasswordService passwords, string companyCode, long id, ResetUserPasswordRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    if (!IsAcceptablePassword(request.NewPassword))
        return Results.BadRequest(new { message = "Password must be 8 to 128 characters and include upper-case, lower-case, and a number." });

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);
    var passwordHash = passwords.Hash(request.NewPassword);
    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
UPDATE CompanyMobileAppUsers
SET PasswordHash=@PasswordHash, OwnerVisiblePassword=@OwnerVisiblePassword, UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode AND MobileAppUserId=@Id;
SELECT TOP 1 UserName FROM CompanyMobileAppUsers WHERE CompanyCode=@CompanyCode AND MobileAppUserId=@Id;";
    cmd.Parameters.AddWithValue("@PasswordHash", passwordHash);
    cmd.Parameters.AddWithValue("@OwnerVisiblePassword", request.NewPassword.Trim());
    cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
    cmd.Parameters.AddWithValue("@Id", id);
    var userName = Convert.ToString(await cmd.ExecuteScalarAsync() ?? "");
    if (string.IsNullOrWhiteSpace(userName))
        return Results.BadRequest(new { message = "Mobile app user not found." });
    return Results.Ok(new
    {
        message = "Password saved. It is now shown clearly in the Password column.",
        userName,
        passwordPlain = request.NewPassword.Trim()
    });
});

app.MapPost("/api/platform/companies/{companyCode}/mobile-app/users/{id:long}/block", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants, string companyCode, long id, MobileAppUserBlockRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();

    var tenant = await tenants.GetTenantAsync(companyCode);
    await EnsureCompanyMobileAppSchemaAsync(db);

    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
UPDATE CompanyMobileAppUsers
SET IsBlocked=@IsBlocked,
    IsActive=CASE WHEN @IsBlocked=1 THEN 0 ELSE 1 END,
    BlockReason=CASE WHEN @IsBlocked=1 THEN @BlockReason ELSE NULL END,
    BlockedAt=CASE WHEN @IsBlocked=1 THEN SYSUTCDATETIME() ELSE NULL END,
    UpdatedAt=SYSUTCDATETIME()
WHERE CompanyCode=@CompanyCode AND MobileAppUserId=@MobileAppUserId;
SELECT @@ROWCOUNT;";
    cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
    cmd.Parameters.AddWithValue("@MobileAppUserId", id);
    cmd.Parameters.AddWithValue("@IsBlocked", request.IsBlocked);
    cmd.Parameters.AddWithValue("@BlockReason", (object?)request.Reason?.Trim() ?? DBNull.Value);
    var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
    if (rows <= 0) return Results.NotFound(new { message = "Mobile app user not found for this company." });
    return Results.Ok(new
    {
        message = request.IsBlocked ? "Mobile app user is blocked." : "Mobile app user is unblocked.",
        isBlocked = request.IsBlocked,
        mobileAppUserId = id
    });
});


// ===== PayNex Mobile App public APIs (one app build for all clients; username finds company) =====
app.MapGet("/api/mobile/health", () => Results.Ok(new
{
    status = "OK",
    service = "InterNex Mobile API",
    utc = DateTime.UtcNow,
    message = "Mobile app can connect. Login with username + password. Company is resolved automatically."
}));

app.MapGet("/api/mobile/live-sync", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    try
    {
        await using var con = await db.OpenTenantAsync(user.DatabaseName);
        await BankAccountService.EnsureSchemaAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandTimeout = 3;
        cmd.CommandText = @"
SELECT
  (SELECT COUNT_BIG(1) FROM Customers) AS CustomerCount,
  (SELECT ISNULL(MAX(CustomerId),0) FROM Customers) AS CustomerMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      CustomerId,
      ISNULL(CustomerCode,''),
      ISNULL(CustomerName,''),
      ISNULL(Mobile,''),
      CONVERT(bigint, ROUND(ISNULL(CurrentBalance,0)*100,0)),
      ISNULL(CONVERT(int,IsActive),0)
  )),0) FROM Customers) AS CustomerStamp,
  (SELECT COUNT_BIG(1) FROM Vendors) AS VendorCount,
  (SELECT ISNULL(MAX(VendorId),0) FROM Vendors) AS VendorMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      VendorId,
      ISNULL(VendorCode,''),
      ISNULL(VendorName,''),
      ISNULL(Mobile,''),
      CONVERT(bigint, ROUND(ISNULL(CurrentBalance,0)*100,0)),
      ISNULL(CONVERT(int,IsActive),0)
  )),0) FROM Vendors) AS VendorStamp,
  (SELECT COUNT_BIG(1) FROM SalesInvoiceHeader WHERE StoreId=@StoreId) AS SalesCount,
  (SELECT ISNULL(MAX(SalesInvoiceId),0) FROM SalesInvoiceHeader WHERE StoreId=@StoreId) AS SalesMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      SalesInvoiceId,
      ISNULL(Status,''),
      CONVERT(bigint, ROUND(ISNULL(GrandTotal,0)*100,0)),
      ISNULL(CustomerId,0)
  )),0) FROM SalesInvoiceHeader WHERE StoreId=@StoreId) AS SalesStamp,
  (SELECT COUNT_BIG(1) FROM PurchaseInvoiceHeader WHERE StoreId=@StoreId) AS PurchaseCount,
  (SELECT ISNULL(MAX(PurchaseInvoiceId),0) FROM PurchaseInvoiceHeader WHERE StoreId=@StoreId) AS PurchaseMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      PurchaseInvoiceId,
      ISNULL(Status,''),
      CONVERT(bigint, ROUND(ISNULL(GrandTotal,0)*100,0)),
      ISNULL(VendorId,0)
  )),0) FROM PurchaseInvoiceHeader WHERE StoreId=@StoreId) AS PurchaseStamp,
  (SELECT COUNT_BIG(1) FROM BankAccounts) AS BankCount,
  (SELECT ISNULL(MAX(BankAccountId),0) FROM BankAccounts) AS BankMaxId,
  CONVERT(bigint, (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      BankAccountId,
      ISNULL(BankCode,''),
      ISNULL(BankName,''),
      ISNULL(AccountNumber,''),
      ISNULL(CONVERT(int,IsActive),0)
  )),0) FROM BankAccounts))
  + CONVERT(bigint, (SELECT ISNULL(MAX(BankLedgerEntryId),0) FROM BankLedgerEntries)) AS BankStamp,
  (SELECT COUNT_BIG(1) FROM CustomerPayments) AS CustomerPaymentCount,
  (SELECT ISNULL(MAX(PaymentId),0) FROM CustomerPayments) AS CustomerPaymentMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      PaymentId,
      ISNULL(CustomerId,0),
      CONVERT(bigint, ROUND(ISNULL(Amount,0)*100,0)),
      ISNULL(PaymentMethod,''),
      ISNULL(BankAccountId,0),
      ISNULL(SalesInvoiceId,0)
  )),0) FROM CustomerPayments) AS CustomerPaymentStamp,
  (SELECT COUNT_BIG(1) FROM VendorPayments) AS VendorPaymentCount,
  (SELECT ISNULL(MAX(PaymentId),0) FROM VendorPayments) AS VendorPaymentMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      PaymentId,
      ISNULL(VendorId,0),
      CONVERT(bigint, ROUND(ISNULL(Amount,0)*100,0)),
      ISNULL(PaymentMethod,''),
      ISNULL(BankAccountId,0),
      ISNULL(PurchaseInvoiceId,0)
  )),0) FROM VendorPayments) AS VendorPaymentStamp,
  (SELECT COUNT_BIG(1) FROM Products) AS ProductCount,
  (SELECT ISNULL(MAX(ProductId),0) FROM Products) AS ProductMaxId,
  (SELECT ISNULL(CHECKSUM_AGG(CHECKSUM(
      ProductId,
      ISNULL(ProductCode,''),
      ISNULL(ProductName,''),
      CONVERT(bigint, ROUND(ISNULL(SalePrice,0)*100,0)),
      CONVERT(bigint, ROUND(ISNULL(StockOnHand,0)*100,0)),
      ISNULL(CONVERT(int,IsActive),0)
  )),0) FROM Products) AS ProductStamp";
        cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
        var row = await SqlList.ReadSingleAsync(cmd);
        static long Num(Dictionary<string, object?> data, string key)
        {
            var value = data.GetValueOrDefault(key);
            return value == null ? 0 : Convert.ToInt64(value);
        }
        return Results.Ok(new
        {
            customerCount = Num(row, "CustomerCount"),
            customerMaxId = Num(row, "CustomerMaxId"),
            customerStamp = Num(row, "CustomerStamp"),
            vendorCount = Num(row, "VendorCount"),
            vendorMaxId = Num(row, "VendorMaxId"),
            vendorStamp = Num(row, "VendorStamp"),
            salesCount = Num(row, "SalesCount"),
            salesMaxId = Num(row, "SalesMaxId"),
            salesStamp = Num(row, "SalesStamp"),
            purchaseCount = Num(row, "PurchaseCount"),
            purchaseMaxId = Num(row, "PurchaseMaxId"),
            purchaseStamp = Num(row, "PurchaseStamp"),
            bankCount = Num(row, "BankCount"),
            bankMaxId = Num(row, "BankMaxId"),
            bankStamp = Num(row, "BankStamp"),
            customerPaymentCount = Num(row, "CustomerPaymentCount"),
            customerPaymentMaxId = Num(row, "CustomerPaymentMaxId"),
            customerPaymentStamp = Num(row, "CustomerPaymentStamp"),
            vendorPaymentCount = Num(row, "VendorPaymentCount"),
            vendorPaymentMaxId = Num(row, "VendorPaymentMaxId"),
            vendorPaymentStamp = Num(row, "VendorPaymentStamp"),
            productCount = Num(row, "ProductCount"),
            productMaxId = Num(row, "ProductMaxId"),
            productStamp = Num(row, "ProductStamp"),
            utc = DateTime.UtcNow
        });
    }
    catch
    {
        return Results.Ok(new
        {
            customerCount = 0L,
            customerMaxId = 0L,
            customerStamp = 0L,
            vendorCount = 0L,
            vendorMaxId = 0L,
            vendorStamp = 0L,
            salesCount = 0L,
            salesMaxId = 0L,
            salesStamp = 0L,
            purchaseCount = 0L,
            purchaseMaxId = 0L,
            purchaseStamp = 0L,
            bankCount = 0L,
            bankMaxId = 0L,
            bankStamp = 0L,
            customerPaymentCount = 0L,
            customerPaymentMaxId = 0L,
            customerPaymentStamp = 0L,
            vendorPaymentCount = 0L,
            vendorPaymentMaxId = 0L,
            vendorPaymentStamp = 0L,
            productCount = 0L,
            productMaxId = 0L,
            productStamp = 0L,
            utc = DateTime.UtcNow
        });
    }
});

app.MapPost("/api/mobile/login", async (HttpContext http, ConnectionFactory db, TenantProvisioningService tenants, PasswordService passwords, AuthTokenService tokens, AuthenticationSecurityService authSecurity, SubscriptionAccessService subscriptionAccess, MobileLoginRequest request) =>
{
    await EnsureCompanyMobileAppSchemaAsync(db);
    var loginId = (request.UserName ?? string.Empty).Trim();
    var password = request.Password ?? string.Empty;
    if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(password))
        return Results.BadRequest(new { ok = false, code = "MISSING_CREDENTIALS", message = "Email and password are required." });

    var loginEmail = NormalizeCloudEmail(loginId);
    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1
    u.MobileAppUserId,u.AppId,u.TenantId,u.CompanyCode,u.UserName,u.DisplayName,u.Email,u.Mobile,u.PasswordHash,u.RoleName,
    ISNULL(u.EmailVerified,0) EmailVerified,
    u.IsBlocked UserBlocked,u.IsActive UserActive,ISNULL(u.BlockReason,'') UserBlockReason,
    a.AppName,a.Platform,a.ApiKey,a.Status AppStatus,a.IsBlocked AppBlocked,ISNULL(a.BlockReason,'') AppBlockReason,
    a.PackageName,a.BundleId,a.AppVersion
FROM CompanyMobileAppUsers u
INNER JOIN CompanyMobileApps a ON a.AppId=u.AppId AND a.CompanyCode=u.CompanyCode
WHERE LOWER(LTRIM(RTRIM(u.UserName))) = LOWER(LTRIM(RTRIM(@LoginId)))
   OR (@LoginEmail <> '' AND LOWER(LTRIM(RTRIM(ISNULL(u.Email,'')))) = @LoginEmail)
ORDER BY CASE WHEN LOWER(LTRIM(RTRIM(ISNULL(u.Email,'')))) = @LoginEmail THEN 0 ELSE 1 END, u.MobileAppUserId";
    cmd.Parameters.AddWithValue("@LoginId", loginId);
    cmd.Parameters.AddWithValue("@LoginEmail", string.IsNullOrWhiteSpace(loginEmail) ? string.Empty : loginEmail);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.Json(new { ok = false, code = "USER_NOT_FOUND", message = "This email is not registered for any company mobile app." }, statusCode: StatusCodes.Status401Unauthorized);

    var mobileAppUserId = Convert.ToInt64(reader["MobileAppUserId"]);
    var companyCode = SqlRead.String(reader, "CompanyCode");
    var userName = SqlRead.String(reader, "UserName");
    var displayName = SqlRead.String(reader, "DisplayName");
    var email = NormalizeCloudEmail(SqlRead.String(reader, "Email"));
    if (string.IsNullOrWhiteSpace(email)) email = NormalizeCloudEmail(userName);
    var mobile = SqlRead.String(reader, "Mobile");
    var roleName = SqlRead.String(reader, "RoleName");
    var passwordHash = SqlRead.String(reader, "PasswordHash");
    var emailVerified = Convert.ToBoolean(reader["EmailVerified"]);
    var userBlocked = Convert.ToBoolean(reader["UserBlocked"]);
    var userActive = Convert.ToBoolean(reader["UserActive"]);
    var userBlockReason = SqlRead.String(reader, "UserBlockReason");
    var appName = SqlRead.String(reader, "AppName");
    var platform = SqlRead.String(reader, "Platform");
    var apiKey = SqlRead.String(reader, "ApiKey");
    var appStatus = SqlRead.String(reader, "AppStatus");
    var appBlocked = Convert.ToBoolean(reader["AppBlocked"]);
    var appBlockReason = SqlRead.String(reader, "AppBlockReason");
    await reader.CloseAsync();

    if (!passwords.Verify(password, passwordHash))
        return Results.Json(new { ok = false, code = "INVALID_PASSWORD", message = "Invalid email or password." }, statusCode: StatusCodes.Status401Unauthorized);

    if (userBlocked || !userActive)
        return Results.Json(new
        {
            ok = false,
            code = "USER_BLOCKED",
            message = string.IsNullOrWhiteSpace(userBlockReason)
                ? "This mobile user is blocked. Contact InterNex support."
                : $"This mobile user is blocked. {userBlockReason}"
        }, statusCode: StatusCodes.Status403Forbidden);

    if (appBlocked || string.Equals(appStatus, "Blocked", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new
        {
            ok = false,
            code = "COMPANY_APP_BLOCKED",
            companyCode,
            message = string.IsNullOrWhiteSpace(appBlockReason)
                ? "This company mobile app access is blocked in InterNex."
                : $"This company mobile app access is blocked. {appBlockReason}"
        }, statusCode: StatusCodes.Status403Forbidden);

    if (string.IsNullOrWhiteSpace(email))
        return Results.Json(new { ok = false, code = "EMAIL_REQUIRED", message = "This mobile user has no email. Ask the InterNex owner to set a login email on Mobile App Detail." }, statusCode: StatusCodes.Status403Forbidden);

    TenantInfo tenant;
    try { tenant = await tenants.GetTenantAsync(companyCode); }
    catch
    {
        return Results.Json(new { ok = false, code = "COMPANY_NOT_FOUND", message = "Company linked to this mobile user was not found in InterNex." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.Equals(tenant.Status, "Suspended", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tenant.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { ok = false, code = "COMPANY_INACTIVE", companyCode, message = $"Company {companyCode} is {tenant.Status} in InterNex." }, statusCode: StatusCodes.Status403Forbidden);

    var mobileAccess = subscriptionAccess.Evaluate(tenant);
    await subscriptionAccess.SyncTenantLicenseStatusAsync(tenant.CompanyCode, mobileAccess);
    if (!mobileAccess.CanLogin)
        return Results.Json(new
        {
            ok = false,
            code = "LICENSE_BLOCKED",
            companyCode,
            message = subscriptionAccess.LoginBlockedMessage(mobileAccess)
        }, statusCode: StatusCodes.Status403Forbidden);

    var databaseName = string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
    var permissionsJson = BuildMobilePermissionsJson(mobileAppUserId);

    var storeId = 1;
    var branchId = 1;
    var branchCode = "MAIN";
    var branchName = "Main Store";
    var storeName = "Main Store";
    var sessionUserId = mobileAppUserId > int.MaxValue ? int.MaxValue : (int)mobileAppUserId;
    var mappedErpUser = false;
    object[] assignedBranchesPayload = Array.Empty<object>();
    try
    {
        await using var tenantCon = await db.OpenTenantAsync(databaseName);
        await BranchAccessHelper.EnsureSchemaAsync(tenantCon);

        // Prefer ERP User Card match by email/username so UserBranchAssignments apply.
        // If no ERP user exists, stay Main-only (do not inherit admin branch rights).
        await using (var userCmd = tenantCon.CreateCommand())
        {
            userCmd.CommandText = @"
SELECT TOP 1 UserId, ISNULL(IsCompanySuperAdmin,0) IsCompanySuperAdmin, ISNULL(RoleId,0) RoleId
FROM Users
WHERE IsActive=1 AND (
    (ISNULL(@Email,'') <> '' AND LOWER(LTRIM(RTRIM(ISNULL(Email,'')))) = LOWER(LTRIM(RTRIM(@Email))))
    OR LOWER(LTRIM(RTRIM(UserName))) = LOWER(LTRIM(RTRIM(@UserName)))
)
ORDER BY CASE
    WHEN ISNULL(@Email,'') <> '' AND LOWER(LTRIM(RTRIM(ISNULL(Email,'')))) = LOWER(LTRIM(RTRIM(@Email))) THEN 0
    ELSE 1 END, UserId";
            userCmd.Parameters.AddWithValue("@UserName", userName);
            userCmd.Parameters.AddWithValue("@Email", string.IsNullOrWhiteSpace(email) ? (object)DBNull.Value : email);
            await using var userReader = await userCmd.ExecuteReaderAsync();
            if (await userReader.ReadAsync())
            {
                sessionUserId = Convert.ToInt32(userReader["UserId"]);
                mappedErpUser = true;
            }
        }

        if (mappedErpUser)
        {
            var isAdmin = string.Equals(roleName, "Admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(roleName, "Company Super Admin", StringComparison.OrdinalIgnoreCase);
            var (preferred, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(tenantCon, sessionUserId, isAdmin);
            storeId = preferred.BranchId;
            branchId = preferred.BranchId;
            branchCode = preferred.BranchCode;
            branchName = preferred.BranchName;
            storeName = preferred.BranchName;
            assignedBranchesPayload = BranchAccessHelper.ToPayload(assigned);
        }
        else
        {
            await using var storeCmd = tenantCon.CreateCommand();
            storeCmd.CommandText = @"
SELECT TOP 1 StoreId, ISNULL(StoreCode,'MAIN') StoreCode, ISNULL(StoreName,'Main Store') StoreName
FROM Stores
WHERE IsActive=1
ORDER BY ISNULL(IsMainBranch,0) DESC, StoreId";
            await using var storeReader = await storeCmd.ExecuteReaderAsync();
            if (await storeReader.ReadAsync())
            {
                storeId = Convert.ToInt32(storeReader["StoreId"]);
                branchId = storeId;
                branchCode = Convert.ToString(storeReader["StoreCode"]) ?? "MAIN";
                branchName = Convert.ToString(storeReader["StoreName"]) ?? "Main Store";
                storeName = branchName;
            }
            assignedBranchesPayload = BranchAccessHelper.ToPayload(new[]
            {
                new BranchAccessHelper.BranchInfo(branchId, branchCode, branchName, true)
            });
        }
    }
    catch
    {
        // Keep safe defaults when tenant DB is temporarily unavailable.
        assignedBranchesPayload = BranchAccessHelper.ToPayload(new[]
        {
            new BranchAccessHelper.BranchInfo(branchId, branchCode, branchName, true)
        });
    }

    var pendingSession = new UserSession(
        tenant.CompanyCode, tenant.CompanyName, databaseName,
        sessionUserId, userName, string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
        0, string.IsNullOrWhiteSpace(roleName) ? "Mobile User" : roleName,
        storeId, storeName, "Production",
        branchId, branchCode, branchName, tenant.AllowMultipleBranches,
        email, false, false, permissionsJson, string.Empty);

    if (!emailVerified)
    {
        try
        {
            var challenge = await authSecurity.CreateLoginChallengeAsync(pendingSession, http);
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
                company = new
                {
                    tenant.CompanyCode,
                    tenant.CompanyName,
                    tenant.Status,
                    tenant.LicenseStatus,
                    tenant.SubscriptionPlan
                },
                user = new
                {
                    mobileAppUserId,
                    userName,
                    displayName,
                    email,
                    mobile,
                    roleName
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { ok = false, code = "OTP_SEND_FAILED", message = ex.Message });
        }
    }

    return await CompleteMobileLoginAsync(http, db, tokens, authSecurity, pendingSession, mobileAppUserId, mobile, appName, platform, apiKey, appStatus, request.DeviceName, request.AppVersion, assignedBranchesPayload);
}).RequireRateLimiting("auth");

app.MapPost("/api/mobile/verify-otp", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, VerifyLoginOtpRequest request) =>
{
    try
    {
        var verified = await authSecurity.VerifyLoginChallengeAsync(request.ChallengeId, request.Code, http);
        var session = verified.PendingSession;
        if (!string.Equals(ReadAuthChannel(session), "mobile", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok = false, code = "WRONG_CHANNEL", message = "This verification belongs to a different sign-in flow. Sign in again from the mobile app." });

        var mobileAppUserId = ReadMobileAppUserId(session);
        await MarkMobileEmailVerifiedAsync(db, mobileAppUserId, session.Email, session.CompanyCode);
        return await CompleteMobileLoginAsync(http, db, tokens, authSecurity, session, mobileAppUserId, null, null, null, null, null, null, null, null);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { ok = false, code = "OTP_INVALID", message = ex.Message });
    }
}).RequireRateLimiting("auth");

app.MapPost("/api/mobile/resend-otp", async (HttpContext http, AuthenticationSecurityService authSecurity, ResendLoginOtpRequest request) =>
{
    try
    {
        var challenge = await authSecurity.ResendLoginChallengeAsync(request.ChallengeId, http);
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
        return Results.BadRequest(new { ok = false, code = "OTP_RESEND_FAILED", message = ex.Message });
    }
}).RequireRateLimiting("auth");

app.MapGet("/api/mobile/me", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TenantProvisioningService tenants) =>
{
    var session = ApiAuth.RequireUser(http, tokens);
    if (session == null) return Results.Unauthorized();
    await EnsureCompanyMobileAppSchemaAsync(db);

    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1 u.MobileAppUserId,u.CompanyCode,u.UserName,u.DisplayName,u.Email,u.Mobile,u.RoleName,u.IsBlocked,u.IsActive,
       a.IsBlocked AppBlocked,a.Status AppStatus,ISNULL(a.ApiKey,'') ApiKey,a.AppName,a.Platform
FROM CompanyMobileAppUsers u
INNER JOIN CompanyMobileApps a ON a.AppId=u.AppId AND a.CompanyCode=u.CompanyCode
WHERE u.CompanyCode=@CompanyCode AND LOWER(LTRIM(RTRIM(u.UserName))) = LOWER(LTRIM(RTRIM(@UserName)))";
    cmd.Parameters.AddWithValue("@CompanyCode", session.CompanyCode);
    cmd.Parameters.AddWithValue("@UserName", session.UserName);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.Json(new { ok = false, code = "SESSION_INVALID", message = "Mobile session user was not found." }, statusCode: StatusCodes.Status401Unauthorized);

    if (Convert.ToBoolean(reader["IsBlocked"]) || !Convert.ToBoolean(reader["IsActive"]) || Convert.ToBoolean(reader["AppBlocked"]))
        return Results.Json(new { ok = false, code = "ACCESS_BLOCKED", message = "Mobile access is blocked. Please login again." }, statusCode: StatusCodes.Status403Forbidden);

    TenantInfo? tenantInfo = null;
    try { tenantInfo = await tenants.GetTenantAsync(session.CompanyCode); } catch { /* keep session values */ }

    object[] branchesPayload = Array.Empty<object>();
    try
    {
        if (!string.IsNullOrWhiteSpace(session.DatabaseName) && session.UserId > 0)
        {
            await using var tenantCon = await db.OpenTenantAsync(session.DatabaseName);
            var (_, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(tenantCon, session.UserId, IsCompanySecurityAdmin(session));
            branchesPayload = BranchAccessHelper.ToPayload(assigned);
        }
    }
    catch { /* omit branches on failure */ }

    if (branchesPayload.Length == 0)
    {
        branchesPayload = BranchAccessHelper.ToPayload(new[]
        {
            new BranchAccessHelper.BranchInfo(
                session.BranchId > 0 ? session.BranchId : session.StoreId,
                string.IsNullOrWhiteSpace(session.BranchCode) ? "MAIN" : session.BranchCode,
                string.IsNullOrWhiteSpace(session.BranchName) ? "Main Store" : session.BranchName,
                true)
        });
    }

    return Results.Ok(new
    {
        ok = true,
        allowMultipleBranches = session.AllowMultipleBranches,
        showSelector = session.AllowMultipleBranches && branchesPayload.Length > 1,
        storeId = session.StoreId,
        branchId = session.BranchId,
        branchCode = session.BranchCode,
        branchName = session.BranchName,
        branches = branchesPayload,
        company = new
        {
            companyCode = SqlRead.String(reader, "CompanyCode"),
            companyName = session.CompanyName,
            status = tenantInfo?.Status,
            licenseStatus = tenantInfo?.LicenseStatus,
            subscriptionPlan = tenantInfo?.SubscriptionPlan
        },
        user = new
        {
            mobileAppUserId = Convert.ToInt64(reader["MobileAppUserId"]),
            userId = session.UserId,
            userName = SqlRead.String(reader, "UserName"),
            displayName = SqlRead.String(reader, "DisplayName"),
            email = SqlRead.String(reader, "Email"),
            mobile = SqlRead.String(reader, "Mobile"),
            roleName = SqlRead.String(reader, "RoleName"),
            storeId = session.StoreId,
            branchId = session.BranchId,
            branchCode = session.BranchCode,
            branchName = session.BranchName
        },
        app = new
        {
            appName = SqlRead.String(reader, "AppName"),
            platform = SqlRead.String(reader, "Platform"),
            status = SqlRead.String(reader, "AppStatus"),
            apiKey = SqlRead.String(reader, "ApiKey")
        }
    });
});

app.MapGet("/api/mobile/company", async (HttpContext http, AuthTokenService tokens, TenantProvisioningService tenants, ConnectionFactory db) =>
{
    var session = ApiAuth.RequireUser(http, tokens);
    if (session == null) return Results.Unauthorized();
    await EnsureCompanyMobileAppSchemaAsync(db);

    var tenant = await tenants.GetTenantAsync(session.CompanyCode);
    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1 AppName,Platform,Status,IsBlocked,ISNULL(BlockReason,'') BlockReason,ApiKey,AppVersion,PackageName,BundleId
FROM CompanyMobileApps WHERE CompanyCode=@CompanyCode";
    cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
    var rows = await SqlList.ReadAsync(cmd);
    var appRow = rows.FirstOrDefault();
    if (appRow == null)
        return Results.Json(new { ok = false, code = "COMPANY_APP_NOT_REGISTERED", message = "Mobile app is not registered for this company in InterNex." }, statusCode: StatusCodes.Status403Forbidden);

    object? Get(string key) =>
        appRow.TryGetValue(key, out var v) ? v :
        appRow.TryGetValue(char.ToLowerInvariant(key[0]) + key[1..], out var v2) ? v2 : null;

    var appBlocked = Convert.ToBoolean(Get("IsBlocked") ?? false);
    if (appBlocked)
        return Results.Json(new { ok = false, code = "COMPANY_APP_BLOCKED", message = "Company mobile app is blocked." }, statusCode: StatusCodes.Status403Forbidden);

    return Results.Ok(new
    {
        ok = true,
        company = new
        {
            companyCode = tenant.CompanyCode,
            companyName = tenant.CompanyName,
            status = tenant.Status,
            licenseStatus = tenant.LicenseStatus,
            subscriptionPlan = tenant.SubscriptionPlan
        },
        app = new
        {
            appName = Get("AppName"),
            platform = Get("Platform"),
            status = Get("Status"),
            apiKey = Get("ApiKey")
        }
    });
});

app.MapPost("/api/mobile/logout", (HttpContext http, AuthTokenService tokens) =>
{
    var session = ApiAuth.RequireUser(http, tokens);
    if (session == null) return Results.Unauthorized();
    return Results.Ok(new { ok = true, message = "Logged out. Clear token on the mobile device." });
});

app.MapPost("/api/mobile/refresh", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, MobileRefreshRequest? request) =>
{
    var plain = request?.RefreshToken;
    if (string.IsNullOrWhiteSpace(plain))
        plain = http.Request.Cookies["paynex_refresh"];
    if (string.IsNullOrWhiteSpace(plain))
        return Results.Json(new { ok = false, code = "MISSING_REFRESH_TOKEN", message = "Refresh token is required." }, statusCode: StatusCodes.Status401Unauthorized);

    var rotation = await authSecurity.RotateRefreshTokenAsync(plain);
    if (rotation == null)
        return Results.Json(new { ok = false, code = "REFRESH_INVALID", message = "Refresh token is invalid or expired. Please login again." }, statusCode: StatusCodes.Status401Unauthorized);

    if (await IsTenantSessionLoggedOutAsync(db, rotation.Session.SessionId))
    {
        await authSecurity.RevokeRefreshTokenAsync(rotation.NewToken.PlainToken);
        return Results.Json(new { ok = false, code = "SESSION_LOGGED_OUT", message = "Session has expired or user logged out." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var refreshedSession = rotation.Session;
    object[] branchesPayload = Array.Empty<object>();
    try
    {
        var map = RolePermissionService.ParseBoolPermissionMap(refreshedSession.PermissionsJson);
        if (map.TryGetValue("mobile.access", out var mobileOk) && mobileOk)
        {
            map["sales.createInvoice"] = true;
            map["sales.editInvoice"] = true;
            map["sales.postInvoice"] = true;
            map["sales.createReturn"] = true;
            map["finance.createExpense"] = true;
            var storeId = refreshedSession.StoreId > 0 ? refreshedSession.StoreId : refreshedSession.BranchId;
            var branchId = refreshedSession.BranchId > 0 ? refreshedSession.BranchId : storeId;
            var branchCode = string.IsNullOrWhiteSpace(refreshedSession.BranchCode) || refreshedSession.BranchCode == "MOBILE" ? "MAIN" : refreshedSession.BranchCode;
            var branchName = string.IsNullOrWhiteSpace(refreshedSession.BranchName) || refreshedSession.BranchName == "Mobile App" ? "Main Store" : refreshedSession.BranchName;
            var storeName = string.IsNullOrWhiteSpace(refreshedSession.StoreName) || refreshedSession.StoreName == "Mobile" ? branchName : refreshedSession.StoreName;
            var userId = refreshedSession.UserId;
            if (!string.IsNullOrWhiteSpace(refreshedSession.DatabaseName))
            {
                await using var tenantCon = await db.OpenTenantAsync(refreshedSession.DatabaseName);
                await BranchAccessHelper.EnsureSchemaAsync(tenantCon);

                // Remap to ERP user by email/username when possible (do not fall back to admin).
                await using (var userCmd = tenantCon.CreateCommand())
                {
                    userCmd.CommandText = @"
SELECT TOP 1 UserId FROM Users
WHERE IsActive=1 AND (
    (ISNULL(@Email,'') <> '' AND LOWER(LTRIM(RTRIM(ISNULL(Email,'')))) = LOWER(LTRIM(RTRIM(@Email))))
    OR LOWER(LTRIM(RTRIM(UserName))) = LOWER(LTRIM(RTRIM(@UserName)))
)
ORDER BY CASE
    WHEN ISNULL(@Email,'') <> '' AND LOWER(LTRIM(RTRIM(ISNULL(Email,'')))) = LOWER(LTRIM(RTRIM(@Email))) THEN 0
    ELSE 1 END, UserId";
                    userCmd.Parameters.AddWithValue("@UserName", refreshedSession.UserName);
                    userCmd.Parameters.AddWithValue("@Email", string.IsNullOrWhiteSpace(refreshedSession.Email) ? (object)DBNull.Value : NormalizeCloudEmail(refreshedSession.Email));
                    var mapped = await userCmd.ExecuteScalarAsync();
                    if (mapped != null && mapped != DBNull.Value) userId = Convert.ToInt32(mapped);
                }

                var isAdmin = IsCompanySecurityAdmin(refreshedSession with { UserId = userId });
                var (preferred, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(tenantCon, userId, isAdmin);
                // Keep current branch if still assigned; otherwise move to resolved default.
                var keepCurrent = assigned.Any(b => b.BranchId == branchId && branchId > 0);
                if (!keepCurrent)
                {
                    storeId = preferred.BranchId;
                    branchId = preferred.BranchId;
                    branchCode = preferred.BranchCode;
                    branchName = preferred.BranchName;
                    storeName = preferred.BranchName;
                }
                else
                {
                    var current = assigned.First(b => b.BranchId == branchId);
                    branchCode = current.BranchCode;
                    branchName = current.BranchName;
                    storeName = current.BranchName;
                    storeId = branchId;
                }
                branchesPayload = BranchAccessHelper.ToPayload(assigned);
            }
            refreshedSession = refreshedSession with
            {
                UserId = userId,
                StoreId = storeId,
                BranchId = branchId,
                BranchCode = branchCode,
                BranchName = branchName,
                StoreName = storeName,
                PermissionsJson = BuildMobilePermissionsJson(ReadMobileAppUserId(refreshedSession))
            };
        }
    }
    catch { /* keep original refresh session */ }

    if (branchesPayload.Length == 0)
    {
        branchesPayload = BranchAccessHelper.ToPayload(new[]
        {
            new BranchAccessHelper.BranchInfo(
                refreshedSession.BranchId > 0 ? refreshedSession.BranchId : refreshedSession.StoreId,
                string.IsNullOrWhiteSpace(refreshedSession.BranchCode) ? "MAIN" : refreshedSession.BranchCode,
                string.IsNullOrWhiteSpace(refreshedSession.BranchName) ? "Main Store" : refreshedSession.BranchName,
                true)
        });
    }

    var accessToken = tokens.Create(refreshedSession);
    var refreshOut = rotation.NewToken.PlainToken;
    if (!ReferenceEquals(refreshedSession, rotation.Session))
    {
        var reissued = await authSecurity.ReplaceRefreshTokenAsync(rotation.NewToken.PlainToken, refreshedSession);
        refreshOut = reissued.PlainToken;
    }
    return Results.Ok(new
    {
        ok = true,
        code = "REFRESH_OK",
        token = accessToken,
        refreshToken = refreshOut,
        authenticated = true,
        requiresVerification = false,
        allowMultipleBranches = refreshedSession.AllowMultipleBranches,
        showSelector = refreshedSession.AllowMultipleBranches && branchesPayload.Length > 1,
        branches = branchesPayload,
        storeId = refreshedSession.StoreId,
        branchId = refreshedSession.BranchId,
        branchCode = refreshedSession.BranchCode,
        branchName = refreshedSession.BranchName,
        user = refreshedSession,
        expiresInMinutes = authSecurity.AccessTokenExpiryMinutes
    });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (HttpContext http, ConnectionFactory db, TenantProvisioningService tenants, SuperAdminService admins, PasswordService passwords, AuthTokenService tokens, AuthenticationSecurityService authSecurity, SubscriptionAccessService subscriptionAccess, LoginRequest request) =>
{
    await EnsureMasterUserDirectorySchemaAsync(db);
    var loginId = (request.UserName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(loginId))
        loginId = (request.Email ?? string.Empty).Trim();
    var email = NormalizeCloudEmail(request.Email);
    if (string.IsNullOrWhiteSpace(email) && loginId.Contains('@'))
        email = NormalizeCloudEmail(loginId);
    var userName = loginId;

    var ipAddress = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var userAgent = http.Request.Headers["User-Agent"].ToString();
    var loginKey = string.IsNullOrWhiteSpace(email) ? (string.IsNullOrWhiteSpace(userName) ? "unknown" : userName) : email;
    var lockedUntil = await authSecurity.GetLockoutUntilAsync(loginKey, ipAddress);
    if (lockedUntil.HasValue)
        return Results.Json(new
        {
            message = $"Account sign-in is temporarily locked because of repeated failed attempts. Try again after {lockedUntil.Value:yyyy-MM-dd HH:mm:ss} UTC.",
            lockedUntil = lockedUntil.Value
        }, statusCode: StatusCodes.Status429TooManyRequests);

    var ownerLoginId = string.IsNullOrWhiteSpace(loginId) ? email : loginId;
    var isOwnerLogin = IsPlatformOwnerEmail(email, tenants.Options)
        || IsPlatformOwnerEmail(ownerLoginId, tenants.Options)
        || string.Equals(ownerLoginId, tenants.Options.PlatformOwnerUserName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ownerLoginId, tenants.Options.SuperAdminUserName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ownerLoginId, tenants.Options.SuperAdminEmail, StringComparison.OrdinalIgnoreCase);
    if (isOwnerLogin)
    {
        var ownerAdmin = await admins.LoginAsync(new SuperAdminLoginRequest(ownerLoginId, request.Password, null));
        if (ownerAdmin == null)
            return await LoginFailureAsync(authSecurity, loginKey, ipAddress, userAgent, "Invalid username or password.");
        await authSecurity.ResetFailedLoginAsync(loginKey, ipAddress);
        var ownerPermissions = JsonSerializer.Serialize(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform.owner"] = true,
            ["platform.viewCompanies"] = true,
            ["platform.viewCompanyUsers"] = true,
            ["platform.viewCompanyBranches"] = true,
            ["platform.manageEmailSecurity"] = true,
            ["reports.viewReports"] = true,
            ["reports.printReports"] = true,
            ["system.generalConfiguration"] = true
        });
        var ownerSession = new UserSession(
            "PAYNEX", "InterNex Cloud ERP", string.Empty,
            ownerAdmin.SuperAdminUserId, ownerAdmin.UserName, ownerAdmin.DisplayName,
            0, "Platform Super Admin", 0, "InterNex Platform", NormalizeTenantEnvironment(request.Environment),
            0, "PLATFORM", "InterNex Platform", false, ownerAdmin.Email, true, true, ownerPermissions, string.Empty);
        // Platform owner always requires OTP on every login (no trusted-device skip).
        return await StartOrCompleteLoginAsync(http, db, tokens, authSecurity, ownerSession, forceOtp: true);
    }

    // Login by user name or email. Company and database are resolved from CentralUserDirectory.
    if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(userName))
    {
        await using var master = await db.OpenMasterAsync();
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

        var candidates = new List<(string CompanyCode, int UserId, string Email, string UserName, string DisplayName, string PasswordHash, bool EmailVerified, bool IsCompanySuperAdmin)>();
        await using (var d = await find.ExecuteReaderAsync())
        {
            while (await d.ReadAsync())
            {
                candidates.Add((
                    SqlRead.String(d, "CompanyCode"),
                    SqlRead.Int(d, "UserId"),
                    NormalizeCloudEmail(SqlRead.String(d, "Email")),
                    SqlRead.String(d, "UserName"),
                    SqlRead.String(d, "DisplayName"),
                    SqlRead.String(d, "PasswordHash"),
                    SqlRead.Bool(d, "EmailVerified"),
                    SqlRead.Bool(d, "IsCompanySuperAdmin")));
            }
        }

        if (candidates.Count == 0)
            return await LoginFailureAsync(authSecurity, loginKey, ipAddress, userAgent, "Invalid username or password.");
        var matches = candidates.Where(item => passwords.Verify(request.Password, item.PasswordHash)).ToList();
        if (matches.Count == 0)
            return await LoginFailureAsync(authSecurity, loginKey, ipAddress, userAgent, "Invalid username or password.");
        if (matches.Count > 1)
            return Results.BadRequest(new { message = "This username and password match more than one company. Ask the InterNex owner to set a unique password." });

        var directory = matches[0];
        var directoryEmailVerified = directory.EmailVerified;
        var companyCode = directory.CompanyCode;
        var directoryUserId = directory.UserId;
        var directoryIsCompanySuperAdmin = directory.IsCompanySuperAdmin;
        if (string.IsNullOrWhiteSpace(email)) email = directory.Email;

        var tenant = await db.GetTenantByCodeAsync(companyCode);
        if (tenant == null) return Results.BadRequest(new { message = "Company record not found for this user." });
        if (!tenant.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "Company is inactive. Please contact InterNex Super Admin." });
        var access = subscriptionAccess.Evaluate(tenant);
        await subscriptionAccess.SyncTenantLicenseStatusAsync(tenant.CompanyCode, access);
        if (!access.CanLogin)
            return Results.BadRequest(new { message = subscriptionAccess.LoginBlockedMessage(access) });

        var environmentName = NormalizeTenantEnvironment(request.Environment);
        string databaseName;
        try { databaseName = ResolveTenantDatabase(tenant, environmentName); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }

        await using var con = await db.OpenTenantAsync(databaseName);
        await EnsureTenantUserSecuritySchemaAsync(con);
        await EnsureTenantBranchSchemaAsync(con);
        var branch = await GetMainBranchAsync(con);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,u.PasswordHash,u.RoleId,r.RoleName,u.StoreId,s.StoreName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin
FROM Users u
INNER JOIN Roles r ON r.RoleId=u.RoleId
INNER JOIN Stores s ON s.StoreId=u.StoreId
WHERE (u.UserId=@UserId OR LOWER(ISNULL(u.Email,''))=@Email OR LOWER(LTRIM(RTRIM(u.UserName)))=@UserName) AND u.IsActive=1";
        cmd.Parameters.AddWithValue("@UserId", directoryUserId);
        cmd.Parameters.AddWithValue("@Email", string.IsNullOrWhiteSpace(email) ? (object)DBNull.Value : email);
        cmd.Parameters.AddWithValue("@UserName", directory.UserName);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return Results.BadRequest(new { message = "This user is not active in the assigned company database." });

        var tenantUserId = SqlRead.Int(r, "UserId");
        var tenantUserName = SqlRead.String(r, "UserName");
        var tenantDisplayName = SqlRead.String(r, "DisplayName");
        var tenantRoleId = SqlRead.Int(r, "RoleId");
        var tenantRoleName = SqlRead.String(r, "RoleName");
        var tenantEmail = NormalizeCloudEmail(SqlRead.String(r, "Email"));
        var isCompanySuperAdmin = directoryIsCompanySuperAdmin || SqlRead.Bool(r, "IsCompanySuperAdmin") || tenantRoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) || tenantRoleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);
        await r.CloseAsync();
        if (string.IsNullOrWhiteSpace(tenantEmail)) tenantEmail = email;
        var permissionsJson = await ReadUserPermissionsJsonAsync(con, tenantUserId, isCompanySuperAdmin);
        var pendingSession = new UserSession(
            tenant.CompanyCode, tenant.CompanyName, databaseName,
            tenantUserId, tenantUserName, tenantDisplayName,
            tenantRoleId, tenantRoleName, branch.BranchId, branch.BranchName, environmentName,
            branch.BranchId, branch.BranchCode, branch.BranchName, tenant.AllowMultipleBranches, tenantEmail, isCompanySuperAdmin, false, permissionsJson, string.Empty);
        await authSecurity.ResetFailedLoginAsync(loginKey, ipAddress);
        return await StartOrCompleteLoginAsync(http, db, tokens, authSecurity, pendingSession, forceOtp: !directoryEmailVerified);
    }

    // Legacy compatibility remains available only for tenant users that have a registered email for OTP.
    var tenantLegacy = await db.GetTenantByCodeAsync(request.CompanyCode ?? string.Empty);
    if (tenantLegacy == null)
        return await LoginFailureAsync(authSecurity, loginKey, ipAddress, userAgent, "Invalid email or password.");
    if (!tenantLegacy.Status.Equals("Active", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "Company is inactive. Please contact InterNex Super Admin." });
    var legacyAccess = subscriptionAccess.Evaluate(tenantLegacy);
    await subscriptionAccess.SyncTenantLicenseStatusAsync(tenantLegacy.CompanyCode, legacyAccess);
    if (!legacyAccess.CanLogin)
        return Results.BadRequest(new { message = subscriptionAccess.LoginBlockedMessage(legacyAccess) });

    var legacyEnvironment = NormalizeTenantEnvironment(request.Environment);
    string legacyDatabaseName;
    try { legacyDatabaseName = ResolveTenantDatabase(tenantLegacy, legacyEnvironment); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }
    await using var legacyCon = await db.OpenTenantAsync(legacyDatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(legacyCon);
    await EnsureTenantBranchSchemaAsync(legacyCon);
    var legacyBranch = await GetMainBranchAsync(legacyCon);
    await using var legacyCmd = legacyCon.CreateCommand();
    legacyCmd.CommandText = @"
SELECT TOP 1 u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,ISNULL(u.EmailVerified,0) EmailVerified,u.PasswordHash,u.RoleId,r.RoleName,u.StoreId,s.StoreName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin
FROM Users u
INNER JOIN Roles r ON r.RoleId=u.RoleId
INNER JOIN Stores s ON s.StoreId=u.StoreId
WHERE u.UserName=@UserName AND u.IsActive=1";
    legacyCmd.Parameters.AddWithValue("@UserName", (request.UserName ?? string.Empty).Trim());
    await using var lr = await legacyCmd.ExecuteReaderAsync();
    if (!await lr.ReadAsync()) return await LoginFailureAsync(authSecurity, loginKey, ipAddress, userAgent, "Invalid username or password.");
    var legacyHash = SqlRead.String(lr, "PasswordHash");
    if (!passwords.Verify(request.Password, legacyHash)) return await LoginFailureAsync(authSecurity, loginKey, ipAddress, userAgent, "Invalid username or password.");

    var legacyUserId = SqlRead.Int(lr, "UserId");
    var legacyUserName = SqlRead.String(lr, "UserName");
    var legacyDisplayName = SqlRead.String(lr, "DisplayName");
    var legacyRoleId = SqlRead.Int(lr, "RoleId");
    var legacyRoleName = SqlRead.String(lr, "RoleName");
    var legacyEmail = NormalizeCloudEmail(SqlRead.String(lr, "Email"));
    var legacyEmailVerified = SqlRead.Bool(lr, "EmailVerified");
    var legacyIsSuper = SqlRead.Bool(lr, "IsCompanySuperAdmin") || legacyRoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) || legacyRoleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);
    await lr.CloseAsync();
    if (string.IsNullOrWhiteSpace(legacyEmail)) return Results.BadRequest(new { message = "This legacy user does not have a valid registered email. Add an email before login." });
    var legacyPermissionsJson = await ReadUserPermissionsJsonAsync(legacyCon, legacyUserId, legacyIsSuper);
    var legacySession = new UserSession(
        tenantLegacy.CompanyCode, tenantLegacy.CompanyName, legacyDatabaseName,
        legacyUserId, legacyUserName, legacyDisplayName,
        legacyRoleId, legacyRoleName, legacyBranch.BranchId, legacyBranch.BranchName, legacyEnvironment,
        legacyBranch.BranchId, legacyBranch.BranchCode, legacyBranch.BranchName, tenantLegacy.AllowMultipleBranches, legacyEmail, legacyIsSuper, false, legacyPermissionsJson, string.Empty);
    await authSecurity.ResetFailedLoginAsync(loginKey, ipAddress);
    return await StartOrCompleteLoginAsync(http, db, tokens, authSecurity, legacySession, forceOtp: !legacyEmailVerified);
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/verify-otp", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, VerifyLoginOtpRequest request) =>
{
    try
    {
        var verified = await authSecurity.VerifyLoginChallengeAsync(request.ChallengeId, request.Code, http);
        var response = await CompleteAuthenticationAsync(http, db, tokens, authSecurity, verified.PendingSession);
        var trusted = await authSecurity.TrustDeviceAsync(verified.Email, verified.CompanyCode, http.Request.Headers["User-Agent"].ToString(), null);
        SetTrustedDeviceCookie(http, trusted.Token, trusted.ExpiresAt);
        return Results.Ok(response);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/resend-otp", async (HttpContext http, AuthenticationSecurityService authSecurity, ResendLoginOtpRequest request) =>
{
    try
    {
        var challenge = await authSecurity.ResendLoginChallengeAsync(request.ChallengeId, http);
        return Results.Ok(new
        {
            requiresVerification = true,
            challengeId = challenge.ChallengeId,
            maskedEmail = challenge.MaskedEmail,
            expiresInSeconds = challenge.ExpiresInSeconds,
            deliveryStatus = challenge.Delivery.Message
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/refresh", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity) =>
{
    var rotation = await authSecurity.RotateRefreshTokenAsync(http.Request.Cookies["paynex_refresh"]);
    if (rotation == null)
    {
        ClearAuthenticationCookies(http);
        return Results.Unauthorized();
    }
    if (await IsTenantSessionLoggedOutAsync(db, rotation.Session.SessionId))
    {
        await authSecurity.RevokeRefreshTokenAsync(rotation.NewToken.PlainToken);
        ClearAuthenticationCookies(http);
        return Results.Unauthorized();
    }
    var accessToken = tokens.Create(rotation.Session);
    SetTenantAuthCookie(http, accessToken, authSecurity.AccessTokenExpiryMinutes);
    SetRefreshTokenCookie(http, rotation.NewToken.PlainToken, rotation.NewToken.ExpiresAt);
    SetCsrfCookie(http);
    return Results.Ok(new AuthenticatedLoginResponse(accessToken, rotation.Session));
}).RequireRateLimiting("auth");

app.MapGet("/api/me", async (HttpContext http, AuthTokenService tokens, ConnectionFactory db) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    var sandboxReady = false;
    if (!user.IsPlatformOwner || !string.IsNullOrWhiteSpace(user.DatabaseName))
    {
        var code = user.CompanyCode;
        if (!string.IsNullOrWhiteSpace(code) && !string.Equals(code, "PAYNEX", StringComparison.OrdinalIgnoreCase))
        {
            var tenant = await db.GetTenantByCodeAsync(code);
            sandboxReady = tenant != null && tenant.AllowSandbox && !string.IsNullOrWhiteSpace(tenant.SandboxDatabaseName);
        }
    }
    return Results.Ok(user with { SandboxReady = sandboxReady });
});

app.MapGet("/api/me/subscription", async (HttpContext http, AuthTokenService tokens, ConnectionFactory db, SubscriptionAccessService subscriptionAccess) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (user.IsPlatformOwner)
    {
        return Results.Ok(new
        {
            expired = false,
            showRenewBanner = false,
            canPost = true,
            canLogin = true,
            daysPast = 0,
            phase = SubscriptionAccessPhase.Active.ToString(),
            expiryDate = (string?)null,
            renewMessage = (string?)null,
            licenseStatus = "Active",
        });
    }

    var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
    var access = subscriptionAccess.Evaluate(tenant);
    if (access.IsExpired)
        await subscriptionAccess.SyncTenantLicenseStatusAsync(user.CompanyCode, access);

    return Results.Ok(new
    {
        expired = access.IsExpired,
        showRenewBanner = access.ShouldShowRenewBanner,
        canPost = access.CanPost,
        canLogin = access.CanLogin,
        daysPast = access.DaysPast,
        phase = access.Phase.ToString(),
        expiryDate = access.ExpiryDate?.ToString("yyyy-MM-dd"),
        renewMessage = access.ShouldShowRenewBanner ? subscriptionAccess.RenewBannerMessage(access) : null,
        licenseStatus = access.LicenseStatus,
    });
});

app.MapGet("/api/currencies", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureCurrenciesSchemaAsync(con);
    return Results.Ok(await SqlList.ReadAsync(con, @"SELECT CurrencyId,CurrencyCode,CurrencyName,Symbol,DecimalPlaces,ExchangeRate,IsBase,IsActive,CreatedAt,UpdatedAt
FROM Currencies ORDER BY IsBase DESC,IsActive DESC,CurrencyCode"));
});

app.MapGet("/api/currencies/base", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureCurrenciesSchemaAsync(con);
    return Results.Ok(await ReadBaseCurrencyAsync(con));
});

app.MapPost("/api/currencies", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CurrencyUpsertRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "system.generalConfiguration")) return Results.Forbid();
    var code = (request.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant();
    var name = (request.CurrencyName ?? string.Empty).Trim();
    var symbol = (request.Symbol ?? string.Empty).Trim();
    if (code.Length < 3 || code.Length > 10 || code.Any(ch => !char.IsLetterOrDigit(ch)))
        return Results.BadRequest(new { message = "Currency code must contain 3 to 10 letters or numbers." });
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
        return Results.BadRequest(new { message = "Currency name is required and cannot exceed 80 characters." });
    if (symbol.Length > 12) return Results.BadRequest(new { message = "Currency symbol cannot exceed 12 characters." });
    if (request.DecimalPlaces < 0 || request.DecimalPlaces > 4)
        return Results.BadRequest(new { message = "Decimal places must be between 0 and 4." });
    if (request.ExchangeRate <= 0)
        return Results.BadRequest(new { message = "Exchange rate must be greater than zero." });

    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureCurrenciesSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        if (request.IsBase)
        {
            await using var clearBase = new SqlCommand("UPDATE Currencies SET IsBase=0,UpdatedAt=SYSUTCDATETIME() WHERE IsBase=1", con, tran);
            await clearBase.ExecuteNonQueryAsync();
        }

        await using var cmd = new SqlCommand(@"
DECLARE @SavedId INT;
IF @CurrencyId>0
BEGIN
    IF NOT EXISTS(SELECT 1 FROM Currencies WHERE CurrencyId=@CurrencyId) THROW 50001,'Currency was not found.',1;
    IF EXISTS(SELECT 1 FROM Currencies WHERE CurrencyCode=@Code AND CurrencyId<>@CurrencyId) THROW 50001,'Currency code already exists.',1;
    UPDATE Currencies SET CurrencyCode=@Code,CurrencyName=@Name,Symbol=@Symbol,DecimalPlaces=@Decimals,
        ExchangeRate=@Rate,IsBase=@IsBase,IsActive=@IsActive,UpdatedAt=SYSUTCDATETIME()
    WHERE CurrencyId=@CurrencyId;
    SET @SavedId=@CurrencyId;
END
ELSE IF EXISTS(SELECT 1 FROM Currencies WHERE CurrencyCode=@Code)
BEGIN
    UPDATE Currencies SET CurrencyName=@Name,Symbol=@Symbol,DecimalPlaces=@Decimals,
        ExchangeRate=@Rate,IsBase=@IsBase,IsActive=@IsActive,UpdatedAt=SYSUTCDATETIME()
    WHERE CurrencyCode=@Code;
    SELECT @SavedId=CurrencyId FROM Currencies WHERE CurrencyCode=@Code;
END
ELSE
BEGIN
    INSERT INTO Currencies(CurrencyCode,CurrencyName,Symbol,DecimalPlaces,ExchangeRate,IsBase,IsActive)
    VALUES(@Code,@Name,@Symbol,@Decimals,@Rate,@IsBase,@IsActive);
    SET @SavedId=CONVERT(INT,SCOPE_IDENTITY());
END;
IF NOT EXISTS(SELECT 1 FROM Currencies WHERE IsBase=1 AND IsActive=1)
    UPDATE Currencies SET IsBase=1,IsActive=1,ExchangeRate=1,UpdatedAt=SYSUTCDATETIME() WHERE CurrencyId=@SavedId;
SELECT @SavedId;", con, tran);
        cmd.Parameters.AddWithValue("@CurrencyId", request.CurrencyId);
        cmd.Parameters.AddWithValue("@Code", code);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Symbol", string.IsNullOrWhiteSpace(symbol) ? code : symbol);
        cmd.Parameters.AddWithValue("@Decimals", request.DecimalPlaces);
        cmd.Parameters.AddWithValue("@Rate", request.IsBase ? 1m : request.ExchangeRate);
        cmd.Parameters.AddWithValue("@IsBase", request.IsBase);
        cmd.Parameters.AddWithValue("@IsActive", request.IsBase || request.IsActive);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        await tran.CommitAsync();
        return Results.Ok(new { currencyId = id, message = "Currency setup saved. The base currency is now used on the dashboard." });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/platform/security/email-settings", async (HttpContext http, AuthTokenService tokens, AuthenticationSecurityService authSecurity) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    var settings = await authSecurity.GetEffectiveEmailSettingsAsync();
    return Results.Ok(new
    {
        settings.FromEmail,
        settings.FromName,
        settings.SmtpHost,
        settings.SmtpPort,
        settings.SmtpUser,
        passwordConfigured = !string.IsNullOrWhiteSpace(settings.SmtpPassword),
        settings.EnableSsl,
        settings.ReturnDevOtp,
        settings.LoginOtpExpiryMinutes,
        settings.TrustedDeviceDays,
        settings.IsDatabaseConfigured
    });
});

app.MapPut("/api/platform/security/email-settings", async (HttpContext http, AuthTokenService tokens, AuthenticationSecurityService authSecurity, OwnerEmailSecuritySettingsRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    try
    {
        await authSecurity.SaveEmailSettingsAsync(request, user.Email);
        return Results.Ok(new { message = "Owner email and OTP security settings saved securely." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/platform/security/email-settings/test", async (HttpContext http, AuthTokenService tokens, AuthenticationSecurityService authSecurity, OwnerEmailTestRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    if (!user.IsPlatformOwner) return Results.Forbid();
    try
    {
        var result = await authSecurity.SendTestEmailAsync(request.ToEmail);
        if (!result.Sent) return Results.BadRequest(new { message = result.Message, result.FromEmail, result.SmtpConfigured });
        return Results.Ok(new { message = result.Message, result.FromEmail });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/auth/logout", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user != null) await CloseTenantLoginSessionAsync(db, user.SessionId, user.CompanyCode, user.UserId);
    await authSecurity.RevokeRefreshTokenAsync(http.Request.Cookies["paynex_refresh"]);
    ClearAuthenticationCookies(http);
    return Results.Ok(new { message = "Logged out. The access and refresh tokens have been invalidated." });
});

app.MapPost("/api/auth/forget-device", async (HttpContext http, AuthTokenService tokens, AuthenticationSecurityService authSecurity) =>
{
    if (ApiAuth.RequireUser(http, tokens) == null) return Results.Unauthorized();
    await authSecurity.RevokeTrustedDeviceAsync(http.Request.Cookies["paynex_trusted_device"]);
    ClearTrustedDeviceCookie(http);
    return Results.Ok(new { message = "This browser is no longer trusted. An email OTP will be required on the next login." });
});

app.MapPost("/api/auth/environment", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, EnvironmentSwitchRequest request) =>
{
    var current = ApiAuth.RequireUser(http, tokens);
    if (current == null) return Results.Unauthorized();
    if (current.IsPlatformOwner)
    {
        var ownerEnvironment = NormalizeTenantEnvironment(request.Environment);
        var ownerSession = current with { Environment = ownerEnvironment };
        var ownerToken = tokens.Create(ownerSession);
        var ownerRefresh = await authSecurity.ReplaceRefreshTokenAsync(http.Request.Cookies["paynex_refresh"], ownerSession);
        SetTenantAuthCookie(http, ownerToken, authSecurity.AccessTokenExpiryMinutes);
        SetRefreshTokenCookie(http, ownerRefresh.PlainToken, ownerRefresh.ExpiresAt);
        SetCsrfCookie(http);
        return Results.Ok(new { token = ownerToken, user = ownerSession });
    }
    var tenant = await db.GetTenantByCodeAsync(current.CompanyCode);
    if (tenant == null) return Results.BadRequest(new { message = "Company code not found." });
    var environmentName = NormalizeTenantEnvironment(request.Environment);
    string databaseName;
    try { databaseName = ResolveTenantDatabase(tenant, environmentName); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { message = ex.Message }); }

    await using var con = await db.OpenTenantAsync(databaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    await EnsureTenantBranchSchemaAsync(con);
    var branch = await GetBranchOrMainAsync(con, current.BranchId);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1 u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,u.RoleId,r.RoleName,u.StoreId,s.StoreName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin
FROM Users u
INNER JOIN Roles r ON r.RoleId=u.RoleId
INNER JOIN Stores s ON s.StoreId=u.StoreId
WHERE (u.UserName=@UserName OR LOWER(ISNULL(u.Email,''))=@Email) AND u.IsActive=1";
    cmd.Parameters.AddWithValue("@UserName", current.UserName);
    cmd.Parameters.AddWithValue("@Email", NormalizeCloudEmail(current.Email));
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Results.BadRequest(new { message = $"User '{current.UserName}' does not exist in {environmentName}. Login with the sandbox admin user or create the user in that database." });

    var switchUserId = SqlRead.Int(r, "UserId");
    var switchUserName = SqlRead.String(r, "UserName");
    var switchDisplayName = SqlRead.String(r, "DisplayName");
    var switchRoleId = SqlRead.Int(r, "RoleId");
    var roleName = SqlRead.String(r, "RoleName");
    var switchEmail = SqlRead.String(r, "Email");
    var switchIsSuper = SqlRead.Bool(r, "IsCompanySuperAdmin") || roleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) || roleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);
    await r.CloseAsync();
    var permissionsJson = await ReadUserPermissionsJsonAsync(con, switchUserId, switchIsSuper);
    var session = new UserSession(
        tenant.CompanyCode, tenant.CompanyName, databaseName,
        switchUserId, switchUserName, switchDisplayName,
        switchRoleId, roleName, branch.BranchId, branch.BranchName, environmentName,
        branch.BranchId, branch.BranchCode, branch.BranchName, tenant.AllowMultipleBranches, switchEmail, switchIsSuper, false, permissionsJson, current.SessionId);
    var token = tokens.Create(session);
    var refresh = await authSecurity.ReplaceRefreshTokenAsync(http.Request.Cookies["paynex_refresh"], session);
    SetTenantAuthCookie(http, token, authSecurity.AccessTokenExpiryMinutes);
    SetRefreshTokenCookie(http, refresh.PlainToken, refresh.ExpiresAt);
    SetCsrfCookie(http);
    return Results.Ok(new { token, user = session });
});



app.MapGet("/api/branches/context", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (user.IsPlatformOwner) return Results.Ok(new { allowMultipleBranches = false, showSelector = false, currentBranchId = 0, currentBranchCode = "PLATFORM", currentBranchName = "InterNex Platform", branches = Array.Empty<object>() });
    var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantBranchSchemaAsync(con);
    var isAdmin = IsCompanySecurityAdmin(user);
    var (_, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(con, user.UserId, isAdmin);
    var allow = tenant?.AllowMultipleBranches == true;
    var current = assigned.FirstOrDefault(x => x.BranchId == user.BranchId) ?? assigned.FirstOrDefault();
    return Results.Ok(new
    {
        allowMultipleBranches = allow,
        showSelector = allow && assigned.Count > 1,
        currentBranchId = current?.BranchId ?? user.BranchId,
        currentBranchCode = current?.BranchCode ?? user.BranchCode,
        currentBranchName = current?.BranchName ?? user.BranchName,
        branches = BranchAccessHelper.ToPayload(assigned)
    });
});

app.MapGet("/api/branches", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (user.IsPlatformOwner) return Results.Ok(new { allowMultipleBranches = false, branches = Array.Empty<object>() });
    var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantBranchSchemaAsync(con);
    // Full store rows for branch admin UI; still filtered to User Card assignments for non-admins.
    var branches = await BranchAccessHelper.FilterBranchesForUserAsync(con, user.UserId, await ReadBranchesAsync(con), IsCompanySecurityAdmin(user));
    return Results.Ok(new { allowMultipleBranches = tenant?.AllowMultipleBranches == true, branches });
});

app.MapPost("/api/branches/switch", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, BranchSwitchRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
    if (tenant == null) return Results.BadRequest(new { message = "Company code not found." });
    if (!tenant.AllowMultipleBranches) return Results.BadRequest(new { message = "Multiple branches are not enabled for this company." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantBranchSchemaAsync(con);
    await EnsureTenantUserSecuritySchemaAsync(con);
    if (!await BranchAccessHelper.IsBranchAssignedToUserAsync(con, user.UserId, request.BranchId, IsCompanySecurityAdmin(user)))
        return Results.BadRequest(new { message = "This branch is not assigned to the logged-in user." });
    var branch = await GetBranchByIdAsync(con, request.BranchId);
    if (branch.BranchId <= 0) return Results.BadRequest(new { message = "Selected branch is not active or does not exist." });
    var session = user with { StoreId = branch.BranchId, StoreName = branch.BranchName, BranchId = branch.BranchId, BranchCode = branch.BranchCode, BranchName = branch.BranchName, AllowMultipleBranches = tenant.AllowMultipleBranches };
    var token = tokens.Create(session);
    var currentRefresh = http.Request.Cookies["paynex_refresh"];
    if (string.IsNullOrWhiteSpace(currentRefresh) && !string.IsNullOrWhiteSpace(request.RefreshToken))
        currentRefresh = request.RefreshToken;
    var refresh = await authSecurity.ReplaceRefreshTokenAsync(currentRefresh, session);
    SetTenantAuthCookie(http, token, authSecurity.AccessTokenExpiryMinutes);
    SetRefreshTokenCookie(http, refresh.PlainToken, refresh.ExpiresAt);
    SetCsrfCookie(http);
    var (_, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(con, session.UserId, IsCompanySecurityAdmin(session));
    return Results.Ok(new
    {
        token,
        refreshToken = refresh.PlainToken,
        user = session,
        allowMultipleBranches = tenant.AllowMultipleBranches,
        branches = BranchAccessHelper.ToPayload(assigned)
    });
});

app.MapPost("/api/branches", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, BranchUpsertRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanUserManagePermission(user, "system.branchManagement")) return Results.BadRequest(new { message = "Branch Management permission is required." });
    var tenant = await db.GetTenantByCodeAsync(user.CompanyCode);
    if (tenant == null) return Results.BadRequest(new { message = "Company code not found." });
    if (!tenant.AllowMultipleBranches) return Results.BadRequest(new { message = "Enable Allow Multiple Branches from Super Admin first." });
    var branchCode = ConnectionFactory.NormalizeCode(request.BranchCode);
    if (string.IsNullOrWhiteSpace(request.BranchName)) return Results.BadRequest(new { message = "Branch name is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantBranchSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var activeCount = 0;
        await using (var count = new SqlCommand("SELECT COUNT(1) FROM Stores WHERE IsActive=1", con, tran)) activeCount = Convert.ToInt32(await count.ExecuteScalarAsync());
        if (request.BranchId <= 0 && tenant.MaxBranches > 0 && activeCount >= tenant.MaxBranches) throw new InvalidOperationException($"Maximum branch limit reached. Allowed branches: {tenant.MaxBranches}.");
        int branchId;
        if (request.BranchId > 0)
        {
            await using var cmd = new SqlCommand(@"UPDATE Stores SET StoreCode=@Code,StoreName=@Name,BranchCode=@Code,BranchName=@Name,AddressLine=@Address,IsActive=@Active WHERE StoreId=@Id", con, tran);
            cmd.Parameters.AddWithValue("@Id", request.BranchId); cmd.Parameters.AddWithValue("@Code", branchCode); cmd.Parameters.AddWithValue("@Name", request.BranchName.Trim()); cmd.Parameters.AddWithValue("@Address", request.AddressLine ?? ""); cmd.Parameters.AddWithValue("@Active", request.IsActive);
            if (await cmd.ExecuteNonQueryAsync() == 0) throw new InvalidOperationException("Branch not found.");
            branchId = request.BranchId;
        }
        else
        {
            await using var cmd = new SqlCommand(@"INSERT INTO Stores(StoreCode,StoreName,BranchCode,BranchName,AddressLine,IsActive,IsMainBranch) OUTPUT INSERTED.StoreId VALUES(@Code,@Name,@Code,@Name,@Address,@Active,0)", con, tran);
            cmd.Parameters.AddWithValue("@Code", branchCode); cmd.Parameters.AddWithValue("@Name", request.BranchName.Trim()); cmd.Parameters.AddWithValue("@Address", request.AddressLine ?? ""); cmd.Parameters.AddWithValue("@Active", request.IsActive);
            branchId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        await using (var terminal = new SqlCommand(@"IF NOT EXISTS(SELECT 1 FROM Terminals WHERE StoreId=@StoreId) INSERT INTO Terminals(StoreId,TerminalCode,TerminalName,IsActive) VALUES(@StoreId,CONCAT('COUNTER-',@StoreId),'Counter 01',1);", con, tran))
        { terminal.Parameters.AddWithValue("@StoreId", branchId); await terminal.ExecuteNonQueryAsync(); }
        await using (var stock = new SqlCommand(@"INSERT INTO StockByStore(StoreId,ProductId,Quantity,AverageCost) SELECT @StoreId,p.ProductId,0,p.PurchasePrice FROM Products p WHERE NOT EXISTS(SELECT 1 FROM StockByStore s WHERE s.StoreId=@StoreId AND s.ProductId=p.ProductId);", con, tran))
        { stock.Parameters.AddWithValue("@StoreId", branchId); await stock.ExecuteNonQueryAsync(); }
        await tran.CommitAsync();
        await using var readCon = await db.OpenTenantAsync(user.DatabaseName);
        await EnsureTenantBranchSchemaAsync(readCon);
        return Results.Ok(new { message = "Branch saved.", branches = await ReadBranchesAsync(readCon) });
    }
    catch (Exception ex) { await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});

app.MapGet("/api/lookups", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var paymentMethods = await SqlList.ReadAsync(con, "SELECT PaymentMethodId,PaymentMethodName,RequiresReference FROM PaymentMethods WHERE IsActive=1 ORDER BY PaymentMethodId");
    var customers = await SqlList.ReadAsync(con, "SELECT TOP 500 CustomerId,CustomerCode,CustomerName,Mobile,Email,AddressLine,CurrentBalance FROM Customers WHERE IsActive=1 ORDER BY CustomerName");
    var stores = await SqlList.ReadAsync(con, "SELECT StoreId,StoreCode,StoreName FROM Stores WHERE IsActive=1 ORDER BY StoreName");
    return Results.Ok(new { paymentMethods, customers, stores });
});


app.MapGet("/api/users/lookups", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanOpenUserManagement(user)) return Results.BadRequest(new { message = "User management permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    var roles = await SqlList.ReadAsync(con, "SELECT RoleId,RoleName FROM Roles ORDER BY CASE WHEN RoleName='Admin' THEN 0 WHEN RoleName='Company Super Admin' THEN 1 WHEN RoleName='Manager' THEN 2 ELSE 3 END, RoleName");
    var stores = await SqlList.ReadAsync(con, "SELECT StoreId,StoreCode,StoreName FROM Stores WHERE IsActive=1 ORDER BY StoreName");
    var permissionGroups = PermissionCatalog().GroupBy(x => x.Category).Select(g => new { category = g.Key, permissions = g.Select(p => new { key = p.Key, label = p.Label }) });
    return Results.Ok(new { roles, stores, permissionGroups, currentUserIsCompanySuperAdmin = user.IsCompanySuperAdmin, allowMultipleBranches = user.AllowMultipleBranches });
});

app.MapGet("/api/security/permissions", (HttpContext http, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    var permissionGroups = PermissionCatalog().GroupBy(x => x.Category).Select(g => new { category = g.Key, permissions = g.Select(p => new { key = p.Key, label = p.Label }) });
    return Results.Ok(new { groups = permissionGroups, current = ReadSessionPermissionMap(user) });
});

app.MapGet("/api/users", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanOpenUserManagement(user)) return Results.BadRequest(new { message = "User management permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 500 u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,ISNULL(u.PhoneNumber,'') PhoneNumber,ISNULL(u.EmailVerified,0) EmailVerified,u.RoleId,r.RoleName,u.StoreId,s.StoreCode,s.StoreName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,u.IsActive,CASE WHEN u.ProfileImage IS NULL THEN 0 ELSE 1 END HasProfileImage,u.CreatedAt
FROM Users u
INNER JOIN Roles r ON r.RoleId=u.RoleId
INNER JOIN Stores s ON s.StoreId=u.StoreId
WHERE (@Term='' OR u.UserName LIKE @Like OR u.DisplayName LIKE @Like OR ISNULL(u.Email,'') LIKE @Like OR ISNULL(u.PhoneNumber,'') LIKE @Like OR r.RoleName LIKE @Like)
ORDER BY u.UserName";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/users/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanOpenUserManagement(user)) return Results.BadRequest(new { message = "User management permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 u.UserId,u.UserName,u.DisplayName,ISNULL(u.Email,'') Email,ISNULL(u.PhoneNumber,'') PhoneNumber,ISNULL(u.EmailVerified,0) EmailVerified,u.RoleId,r.RoleName,u.StoreId,s.StoreCode,s.StoreName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,u.IsActive,CASE WHEN u.ProfileImage IS NULL THEN 0 ELSE 1 END HasProfileImage,u.CreatedAt,u.UpdatedAt
FROM Users u INNER JOIN Roles r ON r.RoleId=u.RoleId INNER JOIN Stores s ON s.StoreId=u.StoreId WHERE u.UserId=@UserId";
    cmd.Parameters.AddWithValue("@UserId", id);
    var detail = await SqlList.ReadSingleAsync(cmd);
    if (detail.Count == 0) return Results.NotFound(new { message = "User not found." });
    var permissions = await ReadUserPermissionMapAsync(con, id, Convert.ToBoolean(detail.GetValueOrDefault("IsCompanySuperAdmin") ?? false));
    var branches = await SqlList.ReadAsync(con, $"SELECT uba.StoreId,s.StoreCode,s.StoreName,uba.IsDefault FROM UserBranchAssignments uba INNER JOIN Stores s ON s.StoreId=uba.StoreId WHERE uba.UserId={id} ORDER BY uba.IsDefault DESC,s.StoreName");
    return Results.Ok(new { user = detail, permissions, branches, permissionGroups = PermissionCatalog().GroupBy(x => x.Category).Select(g => new { category = g.Key, permissions = g.Select(p => new { key = p.Key, label = p.Label }) }) });
});

app.MapGet("/api/me/photo", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    // Platform-owner company sessions keep IsPlatformOwner=true. Photo is on the tenant Users row
    // (User Card). Prefer session UserId, then fall back to matching email (owner DP case).
    if (string.IsNullOrWhiteSpace(user.DatabaseName)) return Results.NotFound();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);

    async Task<IResult?> TryReadPhotoAsync(string sql, Action<SqlCommand> bind)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;
        var contentType = Convert.ToString(reader[1]);
        if (string.IsNullOrWhiteSpace(contentType)) contentType = "image/png";
        return Results.File((byte[])reader[0], contentType);
    }

    if (user.UserId > 0)
    {
        var byId = await TryReadPhotoAsync(
            "SELECT TOP 1 ProfileImage,ISNULL(ProfileImageContentType,'image/png') ProfileImageContentType FROM Users WHERE UserId=@UserId AND ProfileImage IS NOT NULL",
            cmd => cmd.Parameters.AddWithValue("@UserId", user.UserId));
        if (byId != null) return byId;
    }

    var email = NormalizeCloudEmail(user.Email);
    if (!string.IsNullOrWhiteSpace(email))
    {
        var byEmail = await TryReadPhotoAsync(
            "SELECT TOP 1 ProfileImage,ISNULL(ProfileImageContentType,'image/png') ProfileImageContentType FROM Users WHERE LOWER(LTRIM(RTRIM(ISNULL(Email,''))))=@Email AND ProfileImage IS NOT NULL ORDER BY UserId",
            cmd => cmd.Parameters.AddWithValue("@Email", email));
        if (byEmail != null) return byEmail;
    }

    return Results.NotFound();
});

app.MapGet("/api/company/logo", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(user.DatabaseName)) return Results.NotFound();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT TOP 1 LogoImage,ISNULL(LogoPath,'') LogoPath FROM CompanyInformation ORDER BY CompanyInformationId";
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.IsDBNull(0)) return Results.NotFound();
    var bytes = (byte[])reader[0];
    if (bytes.Length == 0) return Results.NotFound();
    var path = Convert.ToString(reader[1]) ?? "";
    var contentType = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        ? "image/jpeg"
        : path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            ? "image/webp"
            : path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                ? "image/gif"
                : "image/png";
    return Results.File(bytes, contentType);
});

app.MapGet("/api/users/{id:int}/photo", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (id != user.UserId && !CanOpenUserManagement(user)) return Results.Forbid();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT TOP 1 ProfileImage,ISNULL(ProfileImageContentType,'image/png') ProfileImageContentType FROM Users WHERE UserId=@UserId";
    cmd.Parameters.AddWithValue("@UserId", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.IsDBNull(0)) return Results.NotFound();
    return Results.File((byte[])reader[0], Convert.ToString(reader[1]) ?? "image/png");
});

app.MapPost("/api/users/{id:int}/reset-password", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, PasswordService passwords, AuthenticationSecurityService authSecurity, CredentialUniquenessService uniqueness, int id, ResetUserPasswordRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanUserManagePermission(user, "users.resetPassword")) return Results.BadRequest(new { message = "Reset Password permission is required." });
    if (!IsAcceptablePassword(request.NewPassword)) return Results.BadRequest(new { message = "Password must be at least 8 characters and include upper-case, lower-case, and a number." });
    var passwordConflict = await uniqueness.FindConflictMessageAsync(request.NewPassword, user.CompanyCode, id);
    if (!string.IsNullOrWhiteSpace(passwordConflict)) return Results.BadRequest(new { message = passwordConflict });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);
    var hash = passwords.Hash(request.NewPassword);
    string email = string.Empty;
    string userName = string.Empty;
    string displayName = string.Empty;
    string roleName = string.Empty;
    bool emailVerified = false;
    bool isSuper = false;
    bool isActive = true;
    await using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = @"UPDATE Users SET PasswordHash=@PasswordHash,UpdatedAt=SYSUTCDATETIME() WHERE UserId=@UserId;
SELECT TOP 1 ISNULL(u.Email,'') Email,ISNULL(u.EmailVerified,0) EmailVerified,u.UserName,u.DisplayName,r.RoleName,ISNULL(u.IsCompanySuperAdmin,0) IsCompanySuperAdmin,u.IsActive FROM Users u INNER JOIN Roles r ON r.RoleId=u.RoleId WHERE u.UserId=@UserId";
        cmd.Parameters.AddWithValue("@UserId", id);
        cmd.Parameters.AddWithValue("@PasswordHash", hash);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return Results.NotFound(new { message = "User not found." });
        email = NormalizeCloudEmail(SqlRead.String(r, "Email"));
        userName = SqlRead.String(r, "UserName");
        displayName = SqlRead.String(r, "DisplayName");
        roleName = SqlRead.String(r, "RoleName");
        emailVerified = SqlRead.Bool(r, "EmailVerified");
        isSuper = SqlRead.Bool(r, "IsCompanySuperAdmin");
        isActive = SqlRead.Bool(r, "IsActive");
    }
    if (!string.IsNullOrWhiteSpace(email))
    {
        await UpsertCentralUserDirectoryAsync(db, user, id, email, userName, displayName, hash, roleName, emailVerified, isSuper || roleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) || roleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase), isActive);
        await authSecurity.RevokeUserAuthenticationAsync(email, user.CompanyCode, id);
    }
    return Results.Ok(new { passwordChanged = true, message = "Password reset successfully. Existing sessions and trusted devices were revoked. Copy it from the one-time password receipt before leaving this page." });
});

app.MapPost("/api/users", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, PasswordService passwords, AuthenticationSecurityService authSecurity, CredentialUniquenessService uniqueness, UserUpsertRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    var isNewUser = request.UserId <= 0;
    request.Permissions ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    request.BranchIds ??= new List<int>();
    if (isNewUser && !CanUserManagePermission(user, "users.createUser")) return Results.BadRequest(new { message = "Create User permission is required." });
    if (!isNewUser && !CanUserManagePermission(user, "users.editUser")) return Results.BadRequest(new { message = "Edit User permission is required." });
    if (request.Permissions.Count > 0 && !CanUserManagePermission(user, "users.assignPermissions")) return Results.BadRequest(new { message = "Assign Permissions permission is required." });
    if (request.IsCompanySuperAdmin && !CanUserManagePermission(user, "users.promoteCompanySuperAdmin")) return Results.BadRequest(new { message = "Promote to Company Super Admin permission is required." });
    var email = NormalizeCloudEmail(request.Email);
    var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? (request.FullName ?? string.Empty).Trim() : request.DisplayName.Trim();
    if (string.IsNullOrWhiteSpace(email) || !RequestValidationService.IsGmailAddress(email))
        return Results.BadRequest(new { message = "Company users must use a real Gmail address (@gmail.com). Login OTP is sent to that inbox." });
    if (string.IsNullOrWhiteSpace(displayName)) return Results.BadRequest(new { message = "Full name is required." });
    if (request.UserId <= 0 && string.IsNullOrWhiteSpace(request.Password)) return Results.BadRequest(new { message = "Password is required for new user." });
    if (!string.IsNullOrWhiteSpace(request.Password) && !IsAcceptablePassword(request.Password)) return Results.BadRequest(new { message = "Password must be at least 8 characters and include upper-case, lower-case, and a number." });
    if (!string.IsNullOrWhiteSpace(request.Password))
    {
        var passwordConflict = await uniqueness.FindConflictMessageAsync(request.Password, user.CompanyCode, request.UserId);
        if (!string.IsNullOrWhiteSpace(passwordConflict)) return Results.BadRequest(new { message = passwordConflict });
    }

    var emailConflict = await FindGlobalEmailConflictAsync(db, email, forCompanyCode: user.CompanyCode, excludeTenantUserId: request.UserId > 0 ? request.UserId : null);
    if (!string.IsNullOrWhiteSpace(emailConflict)) return Results.BadRequest(new { message = emailConflict });

    await EnsureMasterUserDirectorySchemaAsync(db);
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantUserSecuritySchemaAsync(con);

    string existingEmail = string.Empty;
    bool existingVerified = false;
    if (request.UserId > 0)
    {
        await using var existing = con.CreateCommand();
        existing.CommandText = "SELECT TOP 1 ISNULL(Email,'') Email,ISNULL(EmailVerified,0) EmailVerified FROM Users WHERE UserId=@UserId";
        existing.Parameters.AddWithValue("@UserId", request.UserId);
        await using var er = await existing.ExecuteReaderAsync();
        if (await er.ReadAsync()) { existingEmail = NormalizeCloudEmail(SqlRead.String(er, "Email")); existingVerified = SqlRead.Bool(er, "EmailVerified"); }
    }
    var emailChanged = !string.Equals(existingEmail, email, StringComparison.OrdinalIgnoreCase);
    var emailVerified = !isNewUser && !emailChanged && existingVerified;

    // Login id is the email (UserName kept in sync for compatibility).
    var userName = email;
    var passwordHash = string.IsNullOrWhiteSpace(request.Password) ? string.Empty : passwords.Hash(request.Password);
    var profileImage = request.RemoveProfileImage ? null : ParseUserProfileImage(request.ProfileImageBase64);
    int id;
    await using (var cmd = con.CreateCommand())
    {
        cmd.CommandText = @"IF EXISTS(SELECT 1 FROM Users WHERE UserId=@UserId)
BEGIN
    UPDATE Users SET UserName=@UserName,DisplayName=@DisplayName,Email=@Email,PhoneNumber=@PhoneNumber,EmailVerified=@EmailVerified,RoleId=@RoleId,StoreId=@StoreId,IsCompanySuperAdmin=@IsCompanySuperAdmin,IsActive=@IsActive,UpdatedAt=SYSUTCDATETIME(),
        PasswordHash=CASE WHEN @PasswordHash='' THEN PasswordHash ELSE @PasswordHash END,
        ProfileImage=CASE WHEN @RemoveProfileImage=1 THEN NULL WHEN @HasNewProfileImage=1 THEN @ProfileImage ELSE ProfileImage END,
        ProfileImageContentType=CASE WHEN @RemoveProfileImage=1 THEN NULL WHEN @HasNewProfileImage=1 THEN @ProfileImageContentType ELSE ProfileImageContentType END
    WHERE UserId=@UserId;
    SELECT @UserId;
END
ELSE
BEGIN
    INSERT INTO Users(UserName,DisplayName,Email,PhoneNumber,EmailVerified,PasswordHash,RoleId,StoreId,IsCompanySuperAdmin,IsActive,ProfileImage,ProfileImageContentType)
    OUTPUT INSERTED.UserId VALUES(@UserName,@DisplayName,@Email,@PhoneNumber,@EmailVerified,@PasswordHash,@RoleId,@StoreId,@IsCompanySuperAdmin,@IsActive,@ProfileImage,@ProfileImageContentType);
END";
        cmd.Parameters.AddWithValue("@UserId", request.UserId);
        cmd.Parameters.AddWithValue("@UserName", userName);
        cmd.Parameters.AddWithValue("@DisplayName", displayName);
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@PhoneNumber", (request.PhoneNumber ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("@EmailVerified", emailVerified);
        cmd.Parameters.AddWithValue("@PasswordHash", passwordHash);
        cmd.Parameters.AddWithValue("@RoleId", request.RoleId);
        cmd.Parameters.AddWithValue("@StoreId", request.StoreId);
        cmd.Parameters.AddWithValue("@IsCompanySuperAdmin", request.IsCompanySuperAdmin);
        cmd.Parameters.AddWithValue("@IsActive", request.IsActive);
        cmd.Parameters.AddWithValue("@RemoveProfileImage", request.RemoveProfileImage);
        cmd.Parameters.AddWithValue("@HasNewProfileImage", profileImage.HasValue);
        cmd.Parameters.Add("@ProfileImage", SqlDbType.VarBinary, -1).Value = profileImage.HasValue ? profileImage.Value.Bytes : (object)DBNull.Value;
        cmd.Parameters.Add("@ProfileImageContentType", SqlDbType.NVarChar, 80).Value = profileImage.HasValue ? profileImage.Value.ContentType : (object)DBNull.Value;
        id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    if (!string.IsNullOrWhiteSpace(request.Password))
    {
        await using var visible = con.CreateCommand();
        visible.CommandText = "UPDATE Users SET OwnerVisiblePassword=@Pwd WHERE UserId=@UserId";
        visible.Parameters.AddWithValue("@Pwd", request.Password.Trim());
        visible.Parameters.AddWithValue("@UserId", id);
        await visible.ExecuteNonQueryAsync();
    }

    string roleName = "";
    await using (var role = con.CreateCommand())
    {
        role.CommandText = "SELECT TOP 1 RoleName FROM Roles WHERE RoleId=@RoleId";
        role.Parameters.AddWithValue("@RoleId", request.RoleId);
        roleName = Convert.ToString(await role.ExecuteScalarAsync()) ?? "Standard User";
    }

    await SaveUserPermissionsAsync(con, id, request.Permissions);
    await SaveUserBranchAssignmentsAsync(con, id, request.StoreId, request.BranchIds);
    await UpsertCentralUserDirectoryAsync(db, user, id, email, userName, displayName, passwordHash, roleName, emailVerified, request.IsCompanySuperAdmin || roleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) || roleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase), request.IsActive);
    if (!isNewUser && (!string.IsNullOrWhiteSpace(request.Password) || emailChanged || !request.IsActive))
        await authSecurity.RevokeUserAuthenticationAsync(string.IsNullOrWhiteSpace(existingEmail) ? email : existingEmail, user.CompanyCode, id);

    EmailDeliveryResult? credentialsDelivery = null;
    if (isNewUser)
    {
        credentialsDelivery = await authSecurity.SendNewUserCredentialsEmailAsync(
            email,
            displayName,
            user.CompanyName,
            userName,
            request.Password!);
    }

    var message = isNewUser
        ? credentialsDelivery!.Sent
            ? $"User created successfully. Login email and initial password were sent from {credentialsDelivery.FromEmail}. OTP will be requested on first login."
            : $"User created successfully, but the login details email was not sent: {credentialsDelivery.Message}"
        : "User card saved, permissions updated, and central directory synchronized.";
    return Results.Ok(new
    {
        userId = id,
        passwordChanged = !string.IsNullOrWhiteSpace(request.Password),
        credentialsEmailSent = credentialsDelivery?.Sent ?? false,
        credentialsEmailFrom = credentialsDelivery?.FromEmail ?? string.Empty,
        credentialsEmailStatus = credentialsDelivery?.Message ?? string.Empty,
        message
    });
});

app.MapGet("/api/products", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term, bool? includeInactive, int? skip, int? take) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureProductDefaultsColumnsAsync(con);
    await using var cmd = con.CreateCommand();
    var pageSize = Math.Clamp(take ?? 500, 1, 500);
    var offset = Math.Max(skip ?? 0, 0);
    cmd.CommandText = @"
SELECT p.ProductId,p.ProductCode,p.Barcode,p.ProductName,
       ISNULL(c.CategoryName,'') CategoryName,ISNULL(b.BrandName,'') BrandName,
       p.UnitOfMeasure,p.PurchasePrice,p.SalePrice,p.RetailPrice,
       ISNULL(t.TaxPercent,0) TaxPercent,ISNULL(t.IsInclusive,0) TaxInclusive,
       p.DiscountAllowed,ISNULL(p.ProductDiscountPercent,0) ProductDiscountPercent,p.MinStockLevel,p.ReorderLevel,ISNULL(sb.Quantity,p.StockOnHand) StockOnHand,p.ImagePath,CASE WHEN p.ProductImage IS NULL THEN 0 ELSE 1 END HasImage,p.IsActive
FROM Products p
LEFT JOIN Categories c ON c.CategoryId=p.CategoryId
LEFT JOIN Brands b ON b.BrandId=p.BrandId
LEFT JOIN TaxGroups t ON t.TaxGroupId=p.TaxGroupId
LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId
WHERE (@IncludeInactive=1 OR p.IsActive=1) AND (@Term='' OR p.ProductName LIKE @Like OR p.Barcode LIKE @Like OR p.ProductCode LIKE @Like)
ORDER BY p.ProductName, p.ProductId
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@IncludeInactive", includeInactive == true ? 1 : 0);
    cmd.Parameters.AddWithValue("@Skip", offset);
    cmd.Parameters.AddWithValue("@Take", pageSize);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/products/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureProductDefaultsColumnsAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 p.ProductId,p.ProductCode,p.Barcode,p.ProductName,
       ISNULL(c.CategoryName,'') CategoryName,ISNULL(b.BrandName,'') BrandName,
       p.UnitOfMeasure,p.PurchasePrice,p.SalePrice,p.RetailPrice,
       ISNULL(t.TaxPercent,0) TaxPercent,ISNULL(t.IsInclusive,0) TaxInclusive,
       p.DiscountAllowed,ISNULL(p.ProductDiscountPercent,0) ProductDiscountPercent,
       p.MinStockLevel,p.ReorderLevel,ISNULL(sb.Quantity,p.StockOnHand) StockOnHand,
       p.ImagePath,CASE WHEN p.ProductImage IS NULL THEN 0 ELSE 1 END HasImage,
       p.IsActive,p.CreatedAt
FROM Products p
LEFT JOIN Categories c ON c.CategoryId=p.CategoryId
LEFT JOIN Brands b ON b.BrandId=p.BrandId
LEFT JOIN TaxGroups t ON t.TaxGroupId=p.TaxGroupId
LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId
WHERE p.ProductId=@ProductId";
    cmd.Parameters.AddWithValue("@ProductId", id);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var detail = await SqlList.ReadSingleAsync(cmd);
    return detail.Count == 0 ? Results.NotFound(new { message = "Item not found." }) : Results.Ok(detail);
});


app.MapGet("/api/products/next-code", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT ISNULL(MAX(TRY_CONVERT(INT, REPLACE(REPLACE(ProductCode,'ITM-',''),'ITEM-',''))),0)+1
FROM Products
WHERE ProductCode LIKE 'ITM-%' OR ProductCode LIKE 'ITEM-%';";
    var next = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    if (next <= 1)
    {
        await using var c2 = con.CreateCommand();
        c2.CommandText = "SELECT ISNULL(COUNT(1),0)+1 FROM Products";
        next = Convert.ToInt32(await c2.ExecuteScalarAsync());
    }
    return Results.Ok(new { productCode = $"ITM-{next:000000}" });
});

app.MapGet("/api/customers/next-code", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var code = await AllocateNextCustomerCodeAsync(con);
    return Results.Ok(new { customerCode = code });
});

app.MapGet("/api/vendors/next-code", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var code = await AllocateNextVendorCodeAsync(con);
    return Results.Ok(new { vendorCode = code });
});

app.MapGet("/api/products/{id:int}/image", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens);
    if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT ProductImage FROM Products WHERE ProductId=@Id AND ProductImage IS NOT NULL";
    cmd.Parameters.AddWithValue("@Id", id);
    var data = await cmd.ExecuteScalarAsync();
    if (data == null || data == DBNull.Value) return Results.NotFound();
    var bytes = (byte[])data;
    return Results.File(bytes, ImageOptimizationService.DetectRasterContentType(bytes));
});

app.MapPost("/api/products/optimize-image", async (HttpContext http, AuthTokenService tokens, IImageOptimizationService optimizer, OptimizeItemImageRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "inventory.createItems") && !HasSessionPermission(user, "inventory.editItems"))
        return Results.BadRequest(new { message = "Create or Edit Items permission is required." });
    try
    {
        var source = ImageOptimizationService.DecodeBase64Payload(request.ImageBase64);
        var result = await optimizer.OptimizeItemImageAsync(source, http.RequestAborted);
        return Results.Ok(new
        {
            imageBase64 = Convert.ToBase64String(result.Data),
            contentType = result.ContentType,
            originalSizeBytes = result.OriginalSizeBytes,
            optimizedSizeBytes = result.OptimizedSizeBytes,
            width = result.Width,
            height = result.Height,
            quality = result.Quality,
            wasResized = result.WasResized,
            message = result.SummaryMessage,
            details = result.Details
        });
    }
    catch (ImageOptimizationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/products", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, IImageOptimizationService optimizer, ProductUpsertRequest p) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (p.ProductId <= 0 && !HasSessionPermission(user, "inventory.createItems")) return Results.BadRequest(new { message = "Create Items permission is required." });
    if (p.ProductId > 0 && !HasSessionPermission(user, "inventory.editItems")) return Results.BadRequest(new { message = "Edit Items permission is required." });
    if ((p.SalePrice > 0 || p.RetailPrice > 0) && !HasSessionPermission(user, "pricing.changeProductPrice") && !HasSessionPermission(user, "inventory.createItems")) return Results.BadRequest(new { message = "Change Product Price permission is required." });
    if (string.IsNullOrWhiteSpace(p.ProductCode) || string.IsNullOrWhiteSpace(p.ProductName)) return Results.BadRequest(new { message = "Item code and item name are required." });
    byte[]? imageBytes = null;
    if (!string.IsNullOrWhiteSpace(p.ImageBase64))
    {
        try
        {
            var source = ImageOptimizationService.DecodeBase64Payload(p.ImageBase64);
            var optimized = await optimizer.OptimizeItemImageAsync(source, http.RequestAborted);
            imageBytes = optimized.Data;
        }
        catch (ImageOptimizationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Products WHERE ProductId=@ProductId)
BEGIN
 UPDATE Products SET ProductCode=@ProductCode,Barcode=@Barcode,ProductName=@ProductName,UnitOfMeasure=@UnitOfMeasure,
 PurchasePrice=@PurchasePrice,SalePrice=@SalePrice,RetailPrice=@RetailPrice,StockOnHand=@StockOnHand,
 DiscountAllowed=@DiscountAllowed,MinStockLevel=@MinStockLevel,ReorderLevel=@ReorderLevel,IsActive=@IsActive,
 ImagePath=CASE WHEN @ImagePath='' THEN ImagePath ELSE @ImagePath END,
 ProductImage=CASE WHEN @HasImage=1 THEN @ProductImage ELSE ProductImage END
 WHERE ProductId=@ProductId;
 SELECT @ProductId;
END
ELSE
BEGIN
 INSERT INTO Products(ProductCode,Barcode,ProductName,UnitOfMeasure,PurchasePrice,SalePrice,RetailPrice,TaxGroupId,DiscountAllowed,MinStockLevel,ReorderLevel,StockOnHand,ImagePath,ProductImage,IsActive)
 OUTPUT INSERTED.ProductId
 VALUES(@ProductCode,@Barcode,@ProductName,@UnitOfMeasure,@PurchasePrice,@SalePrice,@RetailPrice,1,@DiscountAllowed,@MinStockLevel,@ReorderLevel,@StockOnHand,@ImagePath,@ProductImage,@IsActive);
END";
    cmd.Parameters.AddWithValue("@ProductId", p.ProductId);
    cmd.Parameters.AddWithValue("@ProductCode", p.ProductCode.Trim());
    cmd.Parameters.AddWithValue("@Barcode", string.IsNullOrWhiteSpace(p.Barcode) ? p.ProductCode.Trim() : p.Barcode.Trim());
    cmd.Parameters.AddWithValue("@ProductName", p.ProductName.Trim());
    cmd.Parameters.AddWithValue("@UnitOfMeasure", string.IsNullOrWhiteSpace(p.UnitOfMeasure) ? "PCS" : p.UnitOfMeasure.Trim());
    cmd.Parameters.AddWithValue("@PurchasePrice", p.PurchasePrice);
    cmd.Parameters.AddWithValue("@SalePrice", p.SalePrice);
    cmd.Parameters.AddWithValue("@RetailPrice", p.RetailPrice <= 0 ? p.SalePrice : p.RetailPrice);
    cmd.Parameters.AddWithValue("@StockOnHand", p.StockOnHand);
    cmd.Parameters.AddWithValue("@DiscountAllowed", p.DiscountAllowed);
    cmd.Parameters.AddWithValue("@MinStockLevel", p.MinStockLevel);
    cmd.Parameters.AddWithValue("@ReorderLevel", p.ReorderLevel);
    cmd.Parameters.AddWithValue("@IsActive", p.IsActive);
    cmd.Parameters.AddWithValue("@ImagePath", p.ImagePath ?? "");
    cmd.Parameters.AddWithValue("@HasImage", imageBytes == null ? 0 : 1);
    cmd.Parameters.Add("@ProductImage", SqlDbType.VarBinary, -1).Value = imageBytes is null ? (object)DBNull.Value : imageBytes;
    var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    await using (var stock = con.CreateCommand())
    {
        stock.CommandText = @"MERGE StockByStore AS t USING(SELECT @StoreId StoreId,@ProductId ProductId) s
ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=@Qty,AverageCost=@Cost
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@StoreId,@ProductId,@Qty,@Cost);";
        stock.Parameters.AddWithValue("@StoreId", user.StoreId);
        stock.Parameters.AddWithValue("@ProductId", id);
        stock.Parameters.AddWithValue("@Qty", p.StockOnHand);
        stock.Parameters.AddWithValue("@Cost", p.PurchasePrice);
        await stock.ExecuteNonQueryAsync();
    }
    return Results.Ok(new { productId = id });
});

app.MapGet("/api/customers", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term, bool? includeInactive, int? skip, int? take) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    var pageSize = Math.Clamp(take ?? 500, 1, 500);
    var offset = Math.Max(skip ?? 0, 0);
    cmd.CommandText = @"SELECT CustomerId,CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive
FROM Customers WHERE (@IncludeInactive=1 OR IsActive=1) AND (@Term='' OR CustomerName LIKE @Like OR CustomerCode LIKE @Like OR Mobile LIKE @Like)
ORDER BY CustomerName, CustomerId
OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    cmd.Parameters.AddWithValue("@IncludeInactive", includeInactive == true ? 1 : 0);
    cmd.Parameters.AddWithValue("@Skip", offset);
    cmd.Parameters.AddWithValue("@Take", pageSize);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/customers/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 CustomerId,CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive,CreatedAt FROM Customers WHERE CustomerId=@CustomerId";
    cmd.Parameters.AddWithValue("@CustomerId", id);
    var detail = await SqlList.ReadSingleAsync(cmd);
    return detail.Count == 0 ? Results.NotFound(new { message = "Customer not found." }) : Results.Ok(detail);
});

app.MapPost("/api/customers", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CustomerUpsertRequest c) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var customerCode = (c.CustomerCode ?? string.Empty).Trim();
    var customerName = (c.CustomerName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(customerName))
        return Results.BadRequest(new { message = "Customer name is required." });

    if (c.CustomerId <= 0)
    {
        if (string.IsNullOrWhiteSpace(customerCode))
            customerCode = await AllocateNextCustomerCodeAsync(con);
        else
        {
            await using var dup = con.CreateCommand();
            dup.CommandText = "SELECT TOP 1 CustomerCode FROM Customers WHERE UPPER(LTRIM(RTRIM(CustomerCode)))=UPPER(@Code)";
            dup.Parameters.AddWithValue("@Code", customerCode);
            var existing = await dup.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value)
            {
                var suggested = await AllocateNextCustomerCodeAsync(con);
                return Results.Json(new
                {
                    code = "DUPLICATE_CODE",
                    message = $"Customer code '{customerCode}' already exists in InterNex.",
                    suggestedCode = suggested
                }, statusCode: StatusCodes.Status409Conflict);
            }
        }
    }
    else if (string.IsNullOrWhiteSpace(customerCode))
    {
        return Results.BadRequest(new { message = "Customer code is required." });
    }
    else
    {
        await using var dupEdit = con.CreateCommand();
        dupEdit.CommandText = "SELECT TOP 1 CustomerId FROM Customers WHERE UPPER(LTRIM(RTRIM(CustomerCode)))=UPPER(@Code) AND CustomerId<>@Id";
        dupEdit.Parameters.AddWithValue("@Code", customerCode);
        dupEdit.Parameters.AddWithValue("@Id", c.CustomerId);
        var otherId = await dupEdit.ExecuteScalarAsync();
        if (otherId != null && otherId != DBNull.Value)
        {
            var suggested = await AllocateNextCustomerCodeAsync(con);
            return Results.Json(new
            {
                code = "DUPLICATE_CODE",
                message = $"Customer code '{customerCode}' already exists in InterNex.",
                suggestedCode = suggested
            }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    try
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Customers WHERE CustomerId=@CustomerId)
BEGIN
 IF EXISTS(SELECT 1 FROM Customers WHERE CustomerId=@CustomerId AND CustomerCode='WALKIN')
  THROW 50031, 'Walk-in Customer is a protected system customer and cannot be changed or deactivated.', 1;
 UPDATE Customers SET CustomerCode=@CustomerCode,CustomerName=@CustomerName,Mobile=@Mobile,Email=@Email,AddressLine=@AddressLine,CreditLimit=@CreditLimit,IsActive=@IsActive WHERE CustomerId=@CustomerId;
 SELECT @CustomerId;
END
ELSE
BEGIN
 IF @CustomerCode='WALKIN'
  THROW 50032, 'Walk-in Customer is created and maintained by the system only.', 1;
 INSERT INTO Customers(CustomerCode,CustomerName,Mobile,Email,AddressLine,CreditLimit,LoyaltyPoints,OpeningBalance,CurrentBalance,IsActive)
 OUTPUT INSERTED.CustomerId VALUES(@CustomerCode,@CustomerName,@Mobile,@Email,@AddressLine,@CreditLimit,0,@OpeningBalance,@OpeningBalance,@IsActive);
END";
        cmd.Parameters.AddWithValue("@CustomerId", c.CustomerId);
        cmd.Parameters.AddWithValue("@CustomerCode", customerCode);
        cmd.Parameters.AddWithValue("@CustomerName", customerName);
        cmd.Parameters.AddWithValue("@Mobile", c.Mobile ?? "");
        cmd.Parameters.AddWithValue("@Email", c.Email ?? "");
        cmd.Parameters.AddWithValue("@AddressLine", c.AddressLine ?? "");
        cmd.Parameters.AddWithValue("@CreditLimit", c.CreditLimit);
        cmd.Parameters.AddWithValue("@OpeningBalance", c.OpeningBalance);
        cmd.Parameters.AddWithValue("@IsActive", c.IsActive);
        return Results.Ok(new { customerId = Convert.ToInt32(await cmd.ExecuteScalarAsync()), customerCode });
    }
    catch (SqlException ex) when (ex.Number is 2627 or 2601)
    {
        var suggested = await AllocateNextCustomerCodeAsync(con);
        return Results.Json(new
        {
            code = "DUPLICATE_CODE",
            message = $"Customer code '{customerCode}' already exists in InterNex.",
            suggestedCode = suggested
        }, statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapGet("/api/vendors", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term, bool? includeInactive) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 500 VendorId,VendorCode,VendorName,ContactPerson,Mobile,Email,AddressLine,PaymentTerms,OpeningBalance,CurrentBalance,IsActive
FROM Vendors WHERE (@IncludeInactive=1 OR IsActive=1) AND (@Term='' OR VendorName LIKE @Like OR VendorCode LIKE @Like OR Mobile LIKE @Like) ORDER BY VendorName";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    cmd.Parameters.AddWithValue("@IncludeInactive", includeInactive == true ? 1 : 0);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/vendors/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 VendorId,VendorCode,VendorName,ContactPerson,Mobile,Email,AddressLine,PaymentTerms,OpeningBalance,CurrentBalance,IsActive,CreatedAt FROM Vendors WHERE VendorId=@VendorId";
    cmd.Parameters.AddWithValue("@VendorId", id);
    var detail = await SqlList.ReadSingleAsync(cmd);
    return detail.Count == 0 ? Results.NotFound(new { message = "Vendor not found." }) : Results.Ok(detail);
});

app.MapPost("/api/vendors", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, VendorUpsertRequest v) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var vendorCode = (v.VendorCode ?? string.Empty).Trim();
    var vendorName = (v.VendorName ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(vendorName))
        return Results.BadRequest(new { message = "Vendor name is required." });

    if (v.VendorId <= 0)
    {
        if (string.IsNullOrWhiteSpace(vendorCode))
            vendorCode = await AllocateNextVendorCodeAsync(con);
        else
        {
            await using var dup = con.CreateCommand();
            dup.CommandText = "SELECT TOP 1 VendorCode FROM Vendors WHERE UPPER(LTRIM(RTRIM(VendorCode)))=UPPER(@Code)";
            dup.Parameters.AddWithValue("@Code", vendorCode);
            var existing = await dup.ExecuteScalarAsync();
            if (existing != null && existing != DBNull.Value)
            {
                var suggested = await AllocateNextVendorCodeAsync(con);
                return Results.Json(new
                {
                    code = "DUPLICATE_CODE",
                    message = $"Vendor code '{vendorCode}' already exists in InterNex.",
                    suggestedCode = suggested
                }, statusCode: StatusCodes.Status409Conflict);
            }
        }
    }
    else if (string.IsNullOrWhiteSpace(vendorCode))
    {
        return Results.BadRequest(new { message = "Vendor code is required." });
    }
    else
    {
        await using var dupEdit = con.CreateCommand();
        dupEdit.CommandText = "SELECT TOP 1 VendorId FROM Vendors WHERE UPPER(LTRIM(RTRIM(VendorCode)))=UPPER(@Code) AND VendorId<>@Id";
        dupEdit.Parameters.AddWithValue("@Code", vendorCode);
        dupEdit.Parameters.AddWithValue("@Id", v.VendorId);
        var otherId = await dupEdit.ExecuteScalarAsync();
        if (otherId != null && otherId != DBNull.Value)
        {
            var suggested = await AllocateNextVendorCodeAsync(con);
            return Results.Json(new
            {
                code = "DUPLICATE_CODE",
                message = $"Vendor code '{vendorCode}' already exists in InterNex.",
                suggestedCode = suggested
            }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    try
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Vendors WHERE VendorId=@VendorId)
BEGIN
 UPDATE Vendors SET VendorCode=@VendorCode,VendorName=@VendorName,ContactPerson=@ContactPerson,Mobile=@Mobile,Email=@Email,AddressLine=@AddressLine,PaymentTerms=@PaymentTerms,IsActive=@IsActive WHERE VendorId=@VendorId;
 SELECT @VendorId;
END
ELSE
BEGIN
 INSERT INTO Vendors(VendorCode,VendorName,ContactPerson,Mobile,Email,AddressLine,PaymentTerms,OpeningBalance,CurrentBalance,IsActive)
 OUTPUT INSERTED.VendorId VALUES(@VendorCode,@VendorName,@ContactPerson,@Mobile,@Email,@AddressLine,@PaymentTerms,@OpeningBalance,@OpeningBalance,@IsActive);
END";
        cmd.Parameters.AddWithValue("@VendorId", v.VendorId);
        cmd.Parameters.AddWithValue("@VendorCode", vendorCode);
        cmd.Parameters.AddWithValue("@VendorName", vendorName);
        cmd.Parameters.AddWithValue("@ContactPerson", v.ContactPerson ?? "");
        cmd.Parameters.AddWithValue("@Mobile", v.Mobile ?? "");
        cmd.Parameters.AddWithValue("@Email", v.Email ?? "");
        cmd.Parameters.AddWithValue("@AddressLine", v.AddressLine ?? "");
        cmd.Parameters.AddWithValue("@PaymentTerms", v.PaymentTerms ?? "");
        cmd.Parameters.AddWithValue("@OpeningBalance", v.OpeningBalance);
        cmd.Parameters.AddWithValue("@IsActive", v.IsActive);
        return Results.Ok(new { vendorId = Convert.ToInt32(await cmd.ExecuteScalarAsync()), vendorCode });
    }
    catch (SqlException ex) when (ex.Number is 2627 or 2601)
    {
        var suggested = await AllocateNextVendorCodeAsync(con);
        return Results.Json(new
        {
            code = "DUPLICATE_CODE",
            message = $"Vendor code '{vendorCode}' already exists in InterNex.",
            suggestedCode = suggested
        }, statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapPost("/api/pos/sales", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, SalePostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "sales.createInvoice") && !HasSessionPermission(user, "sales.postInvoice"))
        return Results.Json(new { message = "Create Invoice or Post Invoice permission is required." }, statusCode: StatusCodes.Status403Forbidden);
    if (request.Lines.Count == 0) return Results.BadRequest(new { message = "Cart is empty." });
    user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    await DesktopIdempotency.EnsureSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
        if (!string.IsNullOrWhiteSpace(clientDocumentId))
        {
            var existingSale = await DesktopIdempotency.FindPosSaleAsync(con, tran, clientDocumentId);
            if (existingSale != null)
            {
                await tran.CommitAsync();
                return Results.Ok(new
                {
                    saleId = existingSale.SaleId,
                    invoiceNo = existingSale.InvoiceNo,
                    grandTotal = existingSale.GrandTotal,
                    paid = existingSale.Paid,
                    change = existingSale.Change,
                    lines = existingSale.Lines,
                    duplicate = true,
                    message = "Desktop sale was already posted. Existing InterNex document returned."
                });
            }
        }
        var shiftId = await PosSql.EnsureOpenShiftAsync(con, tran, user, request.ShiftId > 0 ? request.ShiftId : null);
        var terminalId = await PosSql.EnsureTerminalIdAsync(con, tran, user.StoreId);
        var applicationSource = ResolveApplicationSource(http, user);
        if (applicationSource == "Cloud" && !string.IsNullOrWhiteSpace(clientDocumentId))
            applicationSource = "Desktop";
        var invoiceNo = await PosSql.NextNumberAsync(con, tran, "POS");
        var customerId = await ResolvePosCustomerIdAsync(con, tran, request.CustomerId);
        var saleLines = new List<CalculatedSaleLine>();
        foreach (var line in request.Lines)
        {
            var p = await PosSql.GetProductForSaleAsync(con, tran, line.ProductId, user.StoreId);
            if (p == null) throw new InvalidOperationException($"Product {line.ProductId} not found.");
            if (p.StockOnHand < line.Quantity) throw new InvalidOperationException($"Stock not available for {p.ProductName}. Available: {p.StockOnHand:N2}");
            var effective = line.UnitPrice.HasValue && line.UnitPrice.Value > 0 ? new ProductForSale(p.ProductId,p.ProductName,p.ProductCode,p.Barcode,line.UnitPrice.Value,p.PurchasePrice,p.StockOnHand,p.TaxPercent,p.TaxInclusive,p.DiscountAllowed) : p;
            saleLines.Add(PosSql.CalculateLine(effective, line.Quantity, line.DiscountPercent));
        }
        var subTotal = saleLines.Sum(x => x.Gross);
        var discount = saleLines.Sum(x => x.DiscountAmount);
        var tax = saleLines.Sum(x => x.TaxAmount);
        var grandTotal = saleLines.Sum(x => x.LineTotal);
        var payments = request.Payments.Count == 0
            ? new List<SalePaymentRequest>{ new(0, "Cash", grandTotal, "") }
            : request.Payments;
        var paid = payments.Sum(x => x.Amount);
        if (paid < grandTotal) throw new InvalidOperationException("Paid amount is less than grand total.");
        var change = paid - grandTotal;

        int saleId;
        await using (var cmd = new SqlCommand(@"
INSERT INTO SalesHeader(InvoiceNo,StoreId,BranchCode,TerminalId,ShiftId,UserId,CustomerId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,PaidAmount,ChangeAmount,Status,Remarks,ExternalClientDocumentId,ApplicationSource)
OUTPUT INSERTED.SaleId VALUES(@InvoiceNo,@StoreId,@BranchCode,@TerminalId,@ShiftId,@UserId,@CustomerId,@SubTotal,@DiscountAmount,@TaxAmount,@GrandTotal,@PaidAmount,@ChangeAmount,'Posted',@Remarks,@ClientDocumentId,@ApplicationSource)", con, tran))
        {
            cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
            cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
            cmd.Parameters.AddWithValue("@TerminalId", terminalId);
            cmd.Parameters.AddWithValue("@ShiftId", shiftId);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            cmd.Parameters.AddWithValue("@CustomerId", customerId);
            cmd.Parameters.AddWithValue("@SubTotal", subTotal);
            cmd.Parameters.AddWithValue("@DiscountAmount", discount);
            cmd.Parameters.AddWithValue("@TaxAmount", tax);
            cmd.Parameters.AddWithValue("@GrandTotal", grandTotal);
            cmd.Parameters.AddWithValue("@PaidAmount", paid);
            cmd.Parameters.AddWithValue("@ChangeAmount", change);
            cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
            cmd.Parameters.AddWithValue("@ClientDocumentId", string.IsNullOrWhiteSpace(clientDocumentId) ? DBNull.Value : clientDocumentId);
            cmd.Parameters.AddWithValue("@ApplicationSource", applicationSource);
            saleId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        foreach (var line in saleLines)
        {
            await PosSql.InsertSaleLineAndStockAsync(con, tran, saleId, invoiceNo, user, line);
        }
        foreach (var pay in payments.Where(x => x.Amount > 0))
        {
            var paymentMethodId = await PosSql.ResolvePaymentMethodIdAsync(
                con, tran, pay.PaymentMethodId, pay.PaymentMethodName);
            await using var cmd = new SqlCommand("INSERT INTO PaymentLines(SaleId,PaymentMethodId,Amount,ReferenceNo) VALUES(@SaleId,@PaymentMethodId,@Amount,@ReferenceNo)", con, tran);
            cmd.Parameters.AddWithValue("@SaleId", saleId);
            cmd.Parameters.AddWithValue("@PaymentMethodId", paymentMethodId);
            cmd.Parameters.AddWithValue("@Amount", pay.Amount);
            cmd.Parameters.AddWithValue("@ReferenceNo", pay.ReferenceNo ?? "");
            await cmd.ExecuteNonQueryAsync();
        }
        await PosSql.PostBasicSalesAccountingAsync(con, tran, invoiceNo, saleId, grandTotal, tax, saleLines.Sum(x => x.Quantity * x.UnitCost), payments, user.BranchCode);
        var creditAmount = payments.Where(x => x.PaymentMethodName.Equals("Credit", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Amount);
        if (creditAmount > 0 && customerId > 0)
        {
            await using var ledger = new SqlCommand(@"
UPDATE Customers SET CurrentBalance=CurrentBalance+@CreditAmount WHERE CustomerId=@CustomerId;
DECLARE @Bal DECIMAL(18,2)=(SELECT CurrentBalance FROM Customers WHERE CustomerId=@CustomerId);
INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@CustomerId,CAST(GETDATE() AS DATE),'POS Sale',@InvoiceNo,@CreditAmount,0,@Bal,'Credit sale',@SaleId);", con, tran);
            ledger.Parameters.AddWithValue("@CustomerId", customerId);
            ledger.Parameters.AddWithValue("@CreditAmount", creditAmount);
            ledger.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
            ledger.Parameters.AddWithValue("@SaleId", saleId);
            await ledger.ExecuteNonQueryAsync();
        }
        await tran.CommitAsync();
        return Results.Ok(new { saleId, invoiceNo, grandTotal, paid, change, lines = saleLines.Count });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        var sqlEx = ex as Microsoft.Data.SqlClient.SqlException
            ?? ex.InnerException as Microsoft.Data.SqlClient.SqlException;
        var message = sqlEx is { Number: 547 }
            ? PosSql.FormatPosForeignKeyError(sqlEx)
            : ex.Message;
        return Results.BadRequest(new { message });
    }
});

app.MapPost("/api/purchases", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, PurchasePostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (request.VendorId <= 0) return Results.BadRequest(new { message = "Vendor is required." });
    if (request.Lines.Count == 0) return Results.BadRequest(new { message = "Purchase lines are required." });
    user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await DesktopIdempotency.EnsureSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
        if (!string.IsNullOrWhiteSpace(clientDocumentId))
        {
            var existingPurchase = await DesktopIdempotency.FindPurchaseInvoiceAsync(con, tran, clientDocumentId);
            if (existingPurchase != null)
            {
                await tran.CommitAsync();
                return Results.Ok(new
                {
                    purchaseInvoiceId = existingPurchase.PurchaseInvoiceId,
                    invoiceNo = existingPurchase.InvoiceNo,
                    grandTotal = existingPurchase.GrandTotal,
                    paid = existingPurchase.PaidAmount,
                    balance = existingPurchase.BalanceAmount,
                    duplicate = true,
                    message = "Purchase was already posted. Existing InterNex document returned."
                });
            }
        }
        var subTotal = request.Lines.Sum(x => x.Quantity * x.UnitCost);
        var taxAmount = request.Lines.Sum(x => Math.Round((x.Quantity * x.UnitCost) * x.TaxPercent / 100m, 2));
        var grandTotal = subTotal + taxAmount;
        var paid = request.PaidAmount;
        var balance = Math.Max(0, grandTotal - paid);
        var id = request.PurchaseInvoiceId;
        string invoiceNo;
        if (id > 0)
        {
            await using (var find = new SqlCommand("SELECT InvoiceNo FROM PurchaseInvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE PurchaseInvoiceId=@Id AND StoreId=@StoreId AND Status='Open'", con, tran))
            {
                find.Parameters.AddWithValue("@Id", id);
                find.Parameters.AddWithValue("@StoreId", user.StoreId);
                var existingNo = await find.ExecuteScalarAsync();
                if (existingNo == null || existingNo == DBNull.Value) throw new InvalidOperationException("Open purchase invoice draft was not found or has already been posted.");
                invoiceNo = Convert.ToString(existingNo) ?? string.Empty;
            }
            await using var update = new SqlCommand(@"
UPDATE PurchaseInvoiceHeader SET VendorId=@VendorId,InvoiceDate=@InvoiceDate,VendorInvoiceNo=@VendorInvoiceNo,UserId=@UserId,SubTotal=@SubTotal,DiscountAmount=0,TaxAmount=@TaxAmount,GrandTotal=@GrandTotal,PaidAmount=@PaidAmount,BalanceAmount=@Balance,Status='Posted',Remarks=@Remarks,PostedAt=SYSUTCDATETIME() WHERE PurchaseInvoiceId=@Id AND StoreId=@StoreId AND Status='Open';
DELETE FROM PurchaseInvoiceLines WHERE PurchaseInvoiceId=@Id;", con, tran);
            update.Parameters.AddWithValue("@Id", id);
            update.Parameters.AddWithValue("@VendorId", request.VendorId);
            update.Parameters.AddWithValue("@InvoiceDate", request.InvoiceDate.Date);
            update.Parameters.AddWithValue("@VendorInvoiceNo", request.VendorInvoiceNo ?? "");
            update.Parameters.AddWithValue("@StoreId", user.StoreId);
            update.Parameters.AddWithValue("@UserId", user.UserId);
            update.Parameters.AddWithValue("@SubTotal", subTotal);
            update.Parameters.AddWithValue("@TaxAmount", taxAmount);
            update.Parameters.AddWithValue("@GrandTotal", grandTotal);
            update.Parameters.AddWithValue("@PaidAmount", paid);
            update.Parameters.AddWithValue("@Balance", balance);
            update.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
            await update.ExecuteNonQueryAsync();
        }
        else
        {
            invoiceNo = await PosSql.NextNumberAsync(con, tran, "PURCHASE_INVOICE");
            await using var cmd = new SqlCommand(@"
INSERT INTO PurchaseInvoiceHeader(InvoiceNo,VendorId,InvoiceDate,VendorInvoiceNo,StoreId,BranchCode,UserId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,PaidAmount,BalanceAmount,Status,Remarks,ExternalClientDocumentId)
OUTPUT INSERTED.PurchaseInvoiceId VALUES(@InvoiceNo,@VendorId,@InvoiceDate,@VendorInvoiceNo,@StoreId,@BranchCode,@UserId,@SubTotal,0,@TaxAmount,@GrandTotal,@PaidAmount,@Balance,'Posted',@Remarks,@ClientDocumentId)", con, tran);
            cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
            cmd.Parameters.AddWithValue("@VendorId", request.VendorId);
            cmd.Parameters.AddWithValue("@InvoiceDate", request.InvoiceDate.Date);
            cmd.Parameters.AddWithValue("@VendorInvoiceNo", request.VendorInvoiceNo ?? "");
            cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            cmd.Parameters.AddWithValue("@SubTotal", subTotal);
            cmd.Parameters.AddWithValue("@TaxAmount", taxAmount);
            cmd.Parameters.AddWithValue("@GrandTotal", grandTotal);
            cmd.Parameters.AddWithValue("@PaidAmount", paid);
            cmd.Parameters.AddWithValue("@Balance", balance);
            cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
            cmd.Parameters.AddWithValue("@ClientDocumentId", string.IsNullOrWhiteSpace(clientDocumentId) ? DBNull.Value : clientDocumentId);
            id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        foreach (var l in request.Lines)
        {
            var lineTotal = l.Quantity * l.UnitCost + Math.Round((l.Quantity * l.UnitCost) * l.TaxPercent / 100m, 2);
            await using var cmd = new SqlCommand(@"
INSERT INTO PurchaseInvoiceLines(PurchaseInvoiceId,ProductId,ProductName,Quantity,UnitCost,TaxPercent,TaxAmount,LineTotal,TaxInclusive)
SELECT @Id,ProductId,ProductName,@Qty,@Cost,@TaxPercent,@TaxAmount,@LineTotal,0 FROM Products WHERE ProductId=@ProductId;
UPDATE Products SET StockOnHand=StockOnHand+@Qty,PurchasePrice=@Cost WHERE ProductId=@ProductId;
MERGE StockByStore AS t USING(SELECT @StoreId StoreId,@ProductId ProductId) s ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=Quantity+@Qty,AverageCost=@Cost
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@StoreId,@ProductId,@Qty,@Cost);
INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy)
VALUES(@StoreId,@BranchCode,@ProductId,'Purchase',@DocNo,@Qty,0,@Cost,'Purchase invoice',@UserId);", con, tran);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@ProductId", l.ProductId);
            cmd.Parameters.AddWithValue("@Qty", l.Quantity);
            cmd.Parameters.AddWithValue("@Cost", l.UnitCost);
            cmd.Parameters.AddWithValue("@TaxPercent", l.TaxPercent);
            cmd.Parameters.AddWithValue("@TaxAmount", Math.Round((l.Quantity * l.UnitCost) * l.TaxPercent / 100m, 2));
            cmd.Parameters.AddWithValue("@LineTotal", lineTotal);
            cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
            cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
            cmd.Parameters.AddWithValue("@DocNo", invoiceNo);
            cmd.Parameters.AddWithValue("@UserId", user.UserId);
            await cmd.ExecuteNonQueryAsync();
        }
        if (balance > 0)
        {
            await using var vendorLedger = new SqlCommand(@"
UPDATE Vendors SET CurrentBalance=CurrentBalance+@Balance WHERE VendorId=@VendorId;
DECLARE @Bal DECIMAL(18,2)=(SELECT CurrentBalance FROM Vendors WHERE VendorId=@VendorId);
INSERT INTO VendorLedgerEntries(VendorId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@VendorId,@PostingDate,'Purchase Invoice',@InvoiceNo,0,@Balance,@Bal,'Purchase on credit',@PurchaseInvoiceId);", con, tran);
            vendorLedger.Parameters.AddWithValue("@VendorId", request.VendorId);
            vendorLedger.Parameters.AddWithValue("@Balance", balance);
            vendorLedger.Parameters.AddWithValue("@PostingDate", request.InvoiceDate.Date);
            vendorLedger.Parameters.AddWithValue("@InvoiceNo", invoiceNo);
            vendorLedger.Parameters.AddWithValue("@PurchaseInvoiceId", id);
            await vendorLedger.ExecuteNonQueryAsync();
        }
        var setup = await GetPostingSetupAsync(con, tran);
        if (subTotal > 0) await InsertGlAsync(con, tran, setup["InventoryAccount"], request.InvoiceDate.Date, "Purchase Invoice", invoiceNo, subTotal, 0, "Inventory purchase", id);
        if (taxAmount > 0) await InsertGlAsync(con, tran, setup["InputTaxAccount"], request.InvoiceDate.Date, "Purchase Invoice", invoiceNo, taxAmount, 0, "Input tax", id);
        if (paid > 0) await InsertGlAsync(con, tran, setup["CashAccount"], request.InvoiceDate.Date, "Purchase Invoice", invoiceNo, 0, paid, "Purchase paid", id);
        if (balance > 0) await InsertGlAsync(con, tran, setup["PayableAccount"], request.InvoiceDate.Date, "Purchase Invoice", invoiceNo, 0, balance, "Vendor payable", id);
        await tran.CommitAsync();
        return Results.Ok(new { purchaseInvoiceId = id, invoiceNo, grandTotal, paid, balance });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/reports/dashboard", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var metricsCmd = con.CreateCommand();
    metricsCmd.CommandText = @"
WITH CurrentMonth AS (SELECT DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) MonthStart, DATEADD(MONTH,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) NextMonthStart)
SELECT
ISNULL((SELECT SUM(GrandTotal) FROM SalesHeader sh CROSS JOIN CurrentMonth m WHERE sh.StoreId=@StoreId AND sh.Status='Posted' AND sh.SaleDate>=m.MonthStart AND sh.SaleDate<m.NextMonthStart),0) TotalSales,
ISNULL((SELECT COUNT(1) FROM SalesHeader sh CROSS JOIN CurrentMonth m WHERE sh.StoreId=@StoreId AND sh.Status='Posted' AND sh.SaleDate>=m.MonthStart AND sh.SaleDate<m.NextMonthStart),0) SalesInvoices,
ISNULL((SELECT SUM(GrandTotal) FROM PurchaseInvoiceHeader ph CROSS JOIN CurrentMonth m WHERE ph.StoreId=@StoreId AND ph.Status='Posted' AND ph.InvoiceDate>=m.MonthStart AND ph.InvoiceDate<m.NextMonthStart),0) TotalPurchases,
ISNULL((SELECT COUNT(1) FROM Products WHERE IsActive=1),0) ProductCount,
ISNULL((SELECT COUNT(1) FROM Customers WHERE IsActive=1),0) CustomerCount,
ISNULL((SELECT SUM(ISNULL(sb.Quantity,p.StockOnHand)*p.PurchasePrice) FROM Products p LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId WHERE p.IsActive=1),0) InventoryValue";
    metricsCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var metrics = await SqlList.ReadSingleAsync(metricsCmd);

    await using var topCmd = con.CreateCommand();
    topCmd.CommandText = @"SELECT TOP 10 sl.ProductName,SUM(sl.Quantity) Quantity,SUM(sl.LineTotal) Amount FROM SalesLines sl INNER JOIN SalesHeader sh ON sh.SaleId=sl.SaleId WHERE sh.StoreId=@StoreId AND sh.Status='Posted' GROUP BY sl.ProductName ORDER BY SUM(sl.LineTotal) DESC";
    topCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var topProducts = await SqlList.ReadAsync(topCmd);
    return Results.Ok(new { metrics, topProducts });
});

app.MapGet("/api/reports/sales", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 SaleId,InvoiceNo,SaleDate,SubTotal,DiscountAmount,TaxAmount,GrandTotal,PaidAmount,ChangeAmount,Status,Remarks FROM SalesHeader WHERE StoreId=@StoreId AND SaleDate>=@From AND SaleDate<DATEADD(DAY,1,@To) ORDER BY SaleId DESC";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});


app.MapGet("/api/company", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var company = await SqlList.ReadSingleAsync(con, "SELECT TOP 1 CompanyInformationId,CompanyName,AddressLine,PhoneNo,Email,Website,TaxRegistrationNo,LogoPath,LogoImage,UpdatedAt FROM CompanyInformation ORDER BY CompanyInformationId");
    if (company.TryGetValue("LogoImage", out var logoObj) && logoObj is byte[] bytes && bytes.Length > 0)
    {
        company["LogoBase64"] = Convert.ToBase64String(bytes);
        company.Remove("LogoImage");
    }
    return Results.Ok(company);
});

app.MapGet("/api/accounting/chart-of-accounts", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT AccountId,AccountNo,AccountName,AccountType,NormalBalance,IsSystem,IsActive
FROM ChartOfAccounts
WHERE (@Term='' OR AccountNo LIKE @Like OR AccountName LIKE @Like OR AccountType LIKE @Like)
ORDER BY AccountNo";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/accounting/chart-of-accounts/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 AccountId,AccountNo,AccountName,AccountType,NormalBalance,IsSystem,IsActive,CreatedAt FROM ChartOfAccounts WHERE AccountId=@AccountId";
    cmd.Parameters.AddWithValue("@AccountId", id);
    var detail = await SqlList.ReadSingleAsync(cmd);
    return detail.Count == 0 ? Results.NotFound(new { message = "G/L account not found." }) : Results.Ok(detail);
});

app.MapGet("/api/accounting/gl-entries", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 g.GLEntryId,a.AccountNo,a.AccountName,g.PostingDate,g.DocumentType,g.DocumentNo,g.DebitAmount,g.CreditAmount,g.Description,g.CreatedAt
FROM GLEntries g INNER JOIN ChartOfAccounts a ON a.AccountId=g.AccountId
WHERE g.PostingDate>=@From AND g.PostingDate<=@To
AND (@Term='' OR a.AccountNo LIKE @Like OR a.AccountName LIKE @Like OR g.DocumentNo LIKE @Like OR g.DocumentType LIKE @Like)
ORDER BY g.GLEntryId DESC";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    return Results.Ok(await SqlList.ReadAsync(cmd));
});



app.MapPut("/api/company", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CompanyInformationUpdateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.CompanyName)) return Results.BadRequest(new { message = "Company name is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    byte[]? logoBytes = null;
    if (!string.IsNullOrWhiteSpace(request.LogoBase64))
    {
        var raw = request.LogoBase64.Contains(',') ? request.LogoBase64[(request.LogoBase64.IndexOf(',') + 1)..] : request.LogoBase64;
        logoBytes = Convert.FromBase64String(raw);
    }
    cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM CompanyInformation)
BEGIN
 UPDATE CompanyInformation SET CompanyName=@CompanyName,AddressLine=@AddressLine,PhoneNo=@PhoneNo,Email=@Email,Website=@Website,TaxRegistrationNo=@TaxRegistrationNo,LogoPath=@LogoPath,
 LogoImage=CASE WHEN @RemoveLogo=1 THEN NULL WHEN @HasLogo=1 THEN @LogoImage ELSE LogoImage END,UpdatedAt=SYSUTCDATETIME();
END
ELSE
BEGIN
 INSERT INTO CompanyInformation(CompanyName,AddressLine,PhoneNo,Email,Website,TaxRegistrationNo,LogoPath,LogoImage) VALUES(@CompanyName,@AddressLine,@PhoneNo,@Email,@Website,@TaxRegistrationNo,@LogoPath,CASE WHEN @HasLogo=1 THEN @LogoImage ELSE NULL END);
END";
    cmd.Parameters.AddWithValue("@CompanyName", request.CompanyName.Trim());
    cmd.Parameters.AddWithValue("@AddressLine", request.AddressLine ?? "");
    cmd.Parameters.AddWithValue("@PhoneNo", request.PhoneNo ?? "");
    cmd.Parameters.AddWithValue("@Email", request.Email ?? "");
    cmd.Parameters.AddWithValue("@Website", request.Website ?? "");
    cmd.Parameters.AddWithValue("@TaxRegistrationNo", request.TaxRegistrationNo ?? "");
    cmd.Parameters.AddWithValue("@LogoPath", request.LogoPath ?? "");
    cmd.Parameters.AddWithValue("@RemoveLogo", request.RemoveLogo);
    cmd.Parameters.AddWithValue("@HasLogo", logoBytes != null);
    var logoParam = new SqlParameter("@LogoImage", SqlDbType.VarBinary, -1) { Value = logoBytes == null ? DBNull.Value : logoBytes };
    cmd.Parameters.Add(logoParam);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { message = "Company information updated." });
});

app.MapGet("/api/shifts/current", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1 s.ShiftId,s.StoreId,s.TerminalId,s.UserId,s.OpeningCash,
ISNULL(s.ExpectedCash, s.OpeningCash
 + ISNULL((SELECT SUM(pl.Amount) FROM PaymentLines pl INNER JOIN SalesHeader sh ON sh.SaleId=pl.SaleId INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId WHERE sh.ShiftId=s.ShiftId AND sh.Status='Posted' AND pm.PaymentMethodName='Cash'),0)
 - ISNULL((SELECT SUM(RefundAmount) FROM ReturnHeader WHERE ShiftId=s.ShiftId AND Status='Posted'),0)
 + ISNULL((SELECT SUM(CASE WHEN EntryType='Cash In' THEN Amount WHEN EntryType='Cash Out' THEN -Amount ELSE 0 END) FROM CashDrawerLedger WHERE ShiftId=s.ShiftId),0)) ExpectedCash,
ISNULL(s.ClosingCash,0) ClosingCash,ISNULL(s.DifferenceAmount,0) DifferenceAmount,s.Status,s.OpenedAt,s.ClosedAt
FROM Shifts s WHERE s.StoreId=@StoreId AND s.Status='Open' AND (@UserWiseShift=0 OR s.UserId=@UserId) ORDER BY s.ShiftId DESC";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    var list = await SqlList.ReadAsync(cmd);
    return list.Count == 0 ? Results.NotFound(new { message = "No open shift." }) : Results.Ok(list[0]);
});

app.MapPost("/api/shifts/open", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, OpenShiftRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM Shifts WHERE StoreId=@StoreId AND Status='Open' AND (@UserWiseShift=0 OR UserId=@UserId))
 SELECT TOP 1 ShiftId FROM Shifts WHERE StoreId=@StoreId AND Status='Open' AND (@UserWiseShift=0 OR UserId=@UserId) ORDER BY ShiftId DESC;
ELSE
BEGIN
 DECLARE @ResolvedTerminalId INT=(SELECT TOP 1 TerminalId FROM Terminals WHERE StoreId=@StoreId AND IsActive=1 ORDER BY TerminalId);
 IF @ResolvedTerminalId IS NULL
 BEGIN
  INSERT INTO Terminals(StoreId,TerminalCode,TerminalName,IsActive) VALUES(@StoreId,CONCAT('COUNTER-',@StoreId),'Counter 01',1);
  SET @ResolvedTerminalId=SCOPE_IDENTITY();
 END;
 INSERT INTO Shifts(StoreId,BranchCode,TerminalId,UserId,OpeningCash,Status) OUTPUT INSERTED.ShiftId VALUES(@StoreId,@BranchCode,@ResolvedTerminalId,@UserId,@OpeningCash,'Open');
END;";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    cmd.Parameters.AddWithValue("@TerminalId", request.TerminalId <= 0 ? 1 : request.TerminalId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@OpeningCash", request.OpeningCash < 0 ? 0 : request.OpeningCash);
    var shiftId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    return Results.Ok(new { shiftId, message = "Shift is open." });
});

app.MapPost("/api/shifts/close", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloseShiftRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
DECLARE @ShiftId INT = (SELECT TOP 1 ShiftId FROM Shifts WHERE StoreId=@StoreId AND Status='Open' AND (@UserWiseShift=0 OR UserId=@UserId) ORDER BY ShiftId DESC);
IF @ShiftId IS NULL THROW 50001, 'No open shift found.', 1;
DECLARE @Expected DECIMAL(18,2) = (
 SELECT OpeningCash +
 ISNULL((SELECT SUM(pl.Amount) FROM PaymentLines pl INNER JOIN SalesHeader sh ON sh.SaleId=pl.SaleId INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId WHERE sh.ShiftId=@ShiftId AND sh.Status='Posted' AND pm.PaymentMethodName='Cash'),0) -
 ISNULL((SELECT SUM(RefundAmount) FROM ReturnHeader WHERE ShiftId=@ShiftId AND Status='Posted'),0) +
 ISNULL((SELECT SUM(CASE WHEN EntryType='Cash In' THEN Amount WHEN EntryType='Cash Out' THEN -Amount ELSE 0 END) FROM CashDrawerLedger WHERE ShiftId=@ShiftId),0)
 FROM Shifts WHERE ShiftId=@ShiftId);
UPDATE Shifts SET ExpectedCash=@Expected,ClosingCash=@ClosingCash,DifferenceAmount=@ClosingCash-@Expected,Status='Closed',ClosedAt=SYSUTCDATETIME(),ClosingRemarks=@Remarks WHERE ShiftId=@ShiftId;
SELECT @ShiftId ShiftId,@Expected ExpectedCash,@ClosingCash ClosingCash,@ClosingCash-@Expected DifferenceAmount;";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    cmd.Parameters.AddWithValue("@ClosingCash", request.ClosingCash < 0 ? 0 : request.ClosingCash);
    cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
    return Results.Ok(await SqlList.ReadSingleAsync(cmd));
});

app.MapGet("/api/shifts/settings", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);

    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1
    ISNULL(EnableShiftManagement,0) EnableShiftManagement,
    ISNULL(EnableShiftManagement,0) EnableShift,
    ISNULL(UserWiseShift,1) UserWiseShift
FROM ShiftManagementSettings WHERE SettingId=1";
    var settings = await SqlList.ReadSingleAsync(cmd);

    bool userWiseShift = true;
    if (settings.TryGetValue("UserWiseShift", out var uw) && uw != null && uw != DBNull.Value)
        userWiseShift = Convert.ToBoolean(uw);

    await using var currentCmd = con.CreateCommand();
    currentCmd.CommandText = @"
SELECT TOP 1 s.ShiftId,s.StoreId,s.TerminalId,s.UserId,s.OpeningCash,s.Status,s.OpenedAt,s.ClosedAt
FROM Shifts s WHERE s.StoreId=@StoreId AND s.Status='Open' AND (@UserWiseShift=0 OR s.UserId=@UserId) ORDER BY s.ShiftId DESC";
    currentCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    currentCmd.Parameters.AddWithValue("@UserId", user.UserId);
    currentCmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    var currentList = await SqlList.ReadAsync(currentCmd);
    var enable = ReadFlag(settings, "EnableShiftManagement") || ReadFlag(settings, "enableShiftManagement");
    object? current = currentList.Count == 0 ? null : ShiftRowSnapshot(currentList[0]);
    return Results.Ok(new
    {
        enableShiftManagement = enable,
        enableShift = enable,
        userWiseShift,
        currentShift = current
    });
});

app.MapPut("/api/shifts/settings", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, ShiftManagementSettingsUpdateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "system.generalConfiguration")) return Results.Forbid();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);

    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
UPDATE ShiftManagementSettings
SET EnableShiftManagement=@EnableShiftManagement,
    EnableShift=@EnableShift,
    UserWiseShift=@UserWiseShift,
    UpdatedAt=SYSUTCDATETIME()
WHERE SettingId=1;

IF @@ROWCOUNT=0
    INSERT INTO ShiftManagementSettings(SettingId,EnableShiftManagement,EnableShift,UserWiseShift)
    VALUES(1,@EnableShiftManagement,@EnableShift,@UserWiseShift);";
    cmd.Parameters.AddWithValue("@EnableShiftManagement", request.EnableShiftManagement);
    cmd.Parameters.AddWithValue("@EnableShift", request.EnableShiftManagement);
    cmd.Parameters.AddWithValue("@UserWiseShift", request.UserWiseShift);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new
    {
        message = "Shift settings saved.",
        enableShiftManagement = request.EnableShiftManagement,
        enableShift = request.EnableShiftManagement,
        userWiseShift = request.UserWiseShift
    });
});

app.MapGet("/api/shifts/list", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);

    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }

    var fromDate = (from ?? DateTime.Today.AddDays(-30)).Date;
    var toDate = (to ?? DateTime.Today.AddDays(1)).Date.AddDays(1);

    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
WITH SalesAgg AS (
    SELECT ShiftId, SUM(ISNULL(GrandTotal,0)) TotalSales
    FROM SalesHeader
    WHERE Status='Posted'
    GROUP BY ShiftId
),
ReturnAgg AS (
    SELECT ShiftId, SUM(ISNULL(RefundAmount,0)) TotalReturns
    FROM ReturnHeader
    WHERE Status='Posted'
    GROUP BY ShiftId
)
SELECT
    s.ShiftId,
    s.OpenedAt,
    s.ClosedAt,
    CONVERT(date, s.OpenedAt) OpeningDate,
    CONVERT(varchar(8), s.OpenedAt, 108) OpeningTime,
    CONVERT(date, s.ClosedAt) ClosingDate,
    CASE WHEN s.ClosedAt IS NULL THEN '' ELSE CONVERT(varchar(8), s.ClosedAt, 108) END ClosingTime,
    s.OpeningCash,
    ISNULL(s.ClosingCash,0) ClosingCash,
    s.Status,
    ISNULL(sa.TotalSales,0) TotalSales,
    ISNULL(ra.TotalReturns,0) TotalReturns
FROM Shifts s
LEFT JOIN SalesAgg sa ON sa.ShiftId=s.ShiftId
LEFT JOIN ReturnAgg ra ON ra.ShiftId=s.ShiftId
WHERE
    s.StoreId=@StoreId
    AND s.OpenedAt>=@From
    AND s.OpenedAt<@To
    AND (@UserWiseShift=0 OR s.UserId=@UserId)
ORDER BY s.ShiftId DESC";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    cmd.Parameters.AddWithValue("@From", fromDate);
    cmd.Parameters.AddWithValue("@To", toDate);

    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/shifts/card", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int? shiftId) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);

    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }

    int targetShiftId = shiftId ?? 0;
    if (targetShiftId <= 0)
    {
        await using var findCmd = con.CreateCommand();
        findCmd.CommandText = @"SELECT TOP 1 ShiftId
FROM Shifts
WHERE StoreId=@StoreId AND Status='Open' AND (@UserWiseShift=0 OR UserId=@UserId)
ORDER BY ShiftId DESC";
        findCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
        findCmd.Parameters.AddWithValue("@UserId", user.UserId);
        findCmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
        var shiftObj = await findCmd.ExecuteScalarAsync();
        if (shiftObj == null || shiftObj == DBNull.Value) return Results.NotFound(new { message = "No open shift." });
        targetShiftId = Convert.ToInt32(shiftObj);
    }

    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
WITH
SalesPosted AS (
    SELECT SaleId,ApplicationSource,GrandTotal,PaidAmount
    FROM SalesHeader
    WHERE ShiftId=@ShiftId AND Status='Posted'
),
PaymentBySource AS (
    SELECT
        sp.ApplicationSource,
        SUM(CASE WHEN pm.PaymentMethodName='Cash' THEN pl.Amount ELSE 0 END) CashSales,
        SUM(CASE WHEN pm.PaymentMethodName='Credit' THEN pl.Amount ELSE 0 END) CreditSales,
        SUM(CASE WHEN pm.PaymentMethodName IN('Card','Wallet','Bank Transfer') THEN pl.Amount ELSE 0 END) BankCardPayments
    FROM PaymentLines pl
    INNER JOIN SalesPosted sp ON sp.SaleId=pl.SaleId
    INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId
    GROUP BY sp.ApplicationSource
),
SalesTotalsBySource AS (
    SELECT sp.ApplicationSource,
           SUM(sp.GrandTotal) TotalSales,
           SUM(sp.PaidAmount) TotalAmountCollected
    FROM SalesPosted sp
    GROUP BY sp.ApplicationSource
),
ReturnTotalsBySource AS (
    SELECT rh.ApplicationSource,
           SUM(rh.RefundAmount) TotalReturns
    FROM ReturnHeader rh
    WHERE rh.ShiftId=@ShiftId AND rh.Status='Posted'
    GROUP BY rh.ApplicationSource
),
CashDrawerNet AS (
    SELECT SUM(CASE
        WHEN EntryType='Cash In' THEN Amount
        WHEN EntryType='Cash Out' THEN -Amount
        ELSE 0
    END) CashDrawerNet
    FROM CashDrawerLedger
    WHERE ShiftId=@ShiftId
)
SELECT
    s.ShiftId,
    s.Status,
    s.OpenedAt,
    s.ClosedAt,
    s.OpeningCash,
    ISNULL(s.ClosingCash,0) ClosingCash,
    -- expected/current cash balance
    ISNULL(s.ExpectedCash,
        s.OpeningCash
        + ISNULL((SELECT SUM(p.CashSales) FROM PaymentBySource p),0)
        - ISNULL((SELECT SUM(rt.TotalReturns) FROM ReturnTotalsBySource rt),0)
        + ISNULL((SELECT CashDrawerNet FROM CashDrawerNet),0)
    ) ExpectedCash,
    CASE
        WHEN s.Status='Open' THEN
            ISNULL(s.ExpectedCash,
                s.OpeningCash
                + ISNULL((SELECT SUM(p.CashSales) FROM PaymentBySource p),0)
                - ISNULL((SELECT SUM(rt.TotalReturns) FROM ReturnTotalsBySource rt),0)
                + ISNULL((SELECT CashDrawerNet FROM CashDrawerNet),0)
            )
        ELSE ISNULL(s.ClosingCash,0)
    END CurrentCashBalance,
    -- overall (all sources)
    ISNULL((SELECT SUM(p.CashSales) FROM PaymentBySource p),0) CashSales,
    ISNULL((SELECT SUM(p.CreditSales) FROM PaymentBySource p),0) CreditSales,
    ISNULL((SELECT SUM(p.BankCardPayments) FROM PaymentBySource p),0) BankCardPayments,
    ISNULL((SELECT SUM(st.TotalSales) FROM SalesTotalsBySource st),0) TotalSales,
    ISNULL((SELECT SUM(rt.TotalReturns) FROM ReturnTotalsBySource rt),0) TotalReturns,
    ISNULL((SELECT SUM(st.TotalAmountCollected) FROM SalesTotalsBySource st),0) TotalAmountCollected,
    -- per source
    ISNULL((SELECT st.TotalSales FROM SalesTotalsBySource st WHERE st.ApplicationSource='Cloud'),0) SalesCloud,
    ISNULL((SELECT st.TotalSales FROM SalesTotalsBySource st WHERE st.ApplicationSource='Desktop'),0) SalesDesktop,
    ISNULL((SELECT st.TotalSales FROM SalesTotalsBySource st WHERE st.ApplicationSource='Mobile'),0) SalesMobile,
    ISNULL((SELECT rt.TotalReturns FROM ReturnTotalsBySource rt WHERE rt.ApplicationSource='Cloud'),0) ReturnsCloud,
    ISNULL((SELECT rt.TotalReturns FROM ReturnTotalsBySource rt WHERE rt.ApplicationSource='Desktop'),0) ReturnsDesktop,
    ISNULL((SELECT rt.TotalReturns FROM ReturnTotalsBySource rt WHERE rt.ApplicationSource='Mobile'),0) ReturnsMobile,
    ISNULL((SELECT p.CashSales FROM PaymentBySource p WHERE p.ApplicationSource='Cloud'),0) CashSalesCloud,
    ISNULL((SELECT p.CreditSales FROM PaymentBySource p WHERE p.ApplicationSource='Cloud'),0) CreditSalesCloud,
    ISNULL((SELECT p.BankCardPayments FROM PaymentBySource p WHERE p.ApplicationSource='Cloud'),0) BankCardPaymentsCloud,
    ISNULL((SELECT p.CashSales FROM PaymentBySource p WHERE p.ApplicationSource='Desktop'),0) CashSalesDesktop,
    ISNULL((SELECT p.CreditSales FROM PaymentBySource p WHERE p.ApplicationSource='Desktop'),0) CreditSalesDesktop,
    ISNULL((SELECT p.BankCardPayments FROM PaymentBySource p WHERE p.ApplicationSource='Desktop'),0) BankCardPaymentsDesktop,
    ISNULL((SELECT p.CashSales FROM PaymentBySource p WHERE p.ApplicationSource='Mobile'),0) CashSalesMobile,
    ISNULL((SELECT p.CreditSales FROM PaymentBySource p WHERE p.ApplicationSource='Mobile'),0) CreditSalesMobile,
    ISNULL((SELECT p.BankCardPayments FROM PaymentBySource p WHERE p.ApplicationSource='Mobile'),0) BankCardPaymentsMobile
FROM Shifts s
WHERE
    s.ShiftId=@ShiftId
    AND s.StoreId=@StoreId
    AND (@UserWiseShift=0 OR s.UserId=@UserId)
ORDER BY s.ShiftId DESC";
    cmd.Parameters.AddWithValue("@ShiftId", targetShiftId);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);

    var card = await SqlList.ReadSingleAsync(cmd);
    return card.Count == 0 ? Results.NotFound(new { message = "Shift not found." }) : Results.Ok(card);
});

app.MapGet("/api/shifts/{shiftId:int}/sales", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int shiftId, string? source) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);

    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }

    var sourceValue = (source ?? "Cloud").Trim();
    if (!sourceValue.Equals("Cloud", StringComparison.OrdinalIgnoreCase) &&
        !sourceValue.Equals("Desktop", StringComparison.OrdinalIgnoreCase) &&
        !sourceValue.Equals("Mobile", StringComparison.OrdinalIgnoreCase))
        sourceValue = "Cloud";

    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT
    sh.SaleId,
    sh.InvoiceNo,
    sh.SaleDate,
    sh.ApplicationSource,
    sh.GrandTotal,
    sh.PaidAmount,
    sh.ChangeAmount,
    sh.UserId
FROM SalesHeader sh
WHERE sh.ShiftId=@ShiftId
  AND sh.Status='Posted'
  AND (@UserWiseShift=0 OR sh.UserId=@UserId)
  AND sh.ApplicationSource=@Source
ORDER BY sh.SaleId DESC";
    cmd.Parameters.AddWithValue("@ShiftId", shiftId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    cmd.Parameters.AddWithValue("@Source", sourceValue);

    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/shifts/{shiftId:int}/returns", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int shiftId, string? source) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);

    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }

    var sourceValue = (source ?? "Cloud").Trim();
    if (!sourceValue.Equals("Cloud", StringComparison.OrdinalIgnoreCase) &&
        !sourceValue.Equals("Desktop", StringComparison.OrdinalIgnoreCase) &&
        !sourceValue.Equals("Mobile", StringComparison.OrdinalIgnoreCase))
        sourceValue = "Cloud";

    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT
    rh.ReturnId,
    rh.ReturnNo,
    rh.ReturnDate,
    rh.OriginalInvoiceNo,
    rh.ApplicationSource,
    rh.RefundAmount,
    rh.Reason,
    rh.UserId
FROM ReturnHeader rh
WHERE rh.ShiftId=@ShiftId
  AND rh.Status='Posted'
  AND (@UserWiseShift=0 OR rh.UserId=@UserId)
  AND rh.ApplicationSource=@Source
ORDER BY rh.ReturnId DESC";
    cmd.Parameters.AddWithValue("@ShiftId", shiftId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    cmd.Parameters.AddWithValue("@Source", sourceValue);

    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/returns/sale-lines", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string invoiceNo) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT sh.SaleId,sl.SaleLineId,sh.InvoiceNo,sl.ProductId,sl.ProductName,
sl.Quantity SoldQuantity,ISNULL(ret.ReturnedQty,0) AlreadyReturnedQuantity,
sl.UnitPrice,sl.DiscountPercent,sl.TaxPercent,ISNULL(sl.TaxInclusive,0) TaxInclusive,sl.UnitCost,
(sl.Quantity-ISNULL(ret.ReturnedQty,0)) AvailableToReturn
FROM SalesHeader sh
INNER JOIN SalesLines sl ON sl.SaleId=sh.SaleId
OUTER APPLY(SELECT SUM(ReturnQuantity) ReturnedQty FROM ReturnLines rl INNER JOIN ReturnHeader rh ON rh.ReturnId=rl.ReturnId WHERE rl.SaleLineId=sl.SaleLineId AND rh.Status='Posted') ret
WHERE sh.InvoiceNo=@InvoiceNo AND sh.Status='Posted' AND (sl.Quantity-ISNULL(ret.ReturnedQty,0))>0
ORDER BY sl.SaleLineId";
    cmd.Parameters.AddWithValue("@InvoiceNo", invoiceNo.Trim());
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapPost("/api/returns", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, ReturnPostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.OriginalInvoiceNo)) return Results.BadRequest(new { message = "Original invoice no is required." });
    if (request.Lines.Count == 0) return Results.BadRequest(new { message = "Return lines are required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var shiftId = await PosSql.EnsureOpenShiftAsync(con, tran, user);
        var applicationSource = ResolveApplicationSource(http, user);
        var returnNo = await PosSql.NextNumberAsync(con, tran, "RETURN");
        int returnId;
        await using (var hdr = new SqlCommand(@"INSERT INTO ReturnHeader(ReturnNo,OriginalInvoiceNo,StoreId,BranchCode,TerminalId,ShiftId,UserId,RefundAmount,Reason,Status,ApplicationSource)
OUTPUT INSERTED.ReturnId VALUES(@ReturnNo,@OriginalInvoiceNo,@StoreId,@BranchCode,(SELECT TOP 1 TerminalId FROM Terminals WHERE StoreId=@StoreId AND IsActive=1 ORDER BY TerminalId),@ShiftId,@UserId,0,@Reason,'Posted',@ApplicationSource)", con, tran))
        {
            hdr.Parameters.AddWithValue("@ReturnNo", returnNo);
            hdr.Parameters.AddWithValue("@OriginalInvoiceNo", request.OriginalInvoiceNo.Trim());
            hdr.Parameters.AddWithValue("@StoreId", user.StoreId);
            hdr.Parameters.AddWithValue("@BranchCode", user.BranchCode);
            hdr.Parameters.AddWithValue("@ShiftId", shiftId);
            hdr.Parameters.AddWithValue("@UserId", user.UserId);
            hdr.Parameters.AddWithValue("@Reason", request.Reason ?? "");
            hdr.Parameters.AddWithValue("@ApplicationSource", applicationSource);
            returnId = Convert.ToInt32(await hdr.ExecuteScalarAsync());
        }

        decimal refundTotal = 0, taxTotal = 0, costTotal = 0;
        foreach (var line in request.Lines.Where(x => x.ReturnQuantity > 0))
        {
            await using var src = new SqlCommand(@"
SELECT TOP 1 sh.InvoiceNo,sl.SaleLineId,sl.ProductId,sl.ProductName,sl.Quantity,sl.UnitPrice,sl.DiscountAmount,sl.TaxAmount,sl.LineTotal,sl.UnitCost,
ISNULL((SELECT SUM(rl.ReturnQuantity) FROM ReturnLines rl INNER JOIN ReturnHeader rh ON rh.ReturnId=rl.ReturnId WHERE rl.SaleLineId=sl.SaleLineId AND rh.Status='Posted'),0) ReturnedQty
FROM SalesLines sl INNER JOIN SalesHeader sh ON sh.SaleId=sl.SaleId
WHERE sh.InvoiceNo=@InvoiceNo AND sl.SaleLineId=@SaleLineId", con, tran);
            src.Parameters.AddWithValue("@InvoiceNo", request.OriginalInvoiceNo.Trim());
            src.Parameters.AddWithValue("@SaleLineId", line.SaleLineId);
            await using var r = await src.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException($"Sale line not found: {line.SaleLineId}");
            var soldQty = SqlRead.Decimal(r, "Quantity");
            var returnedQty = SqlRead.Decimal(r, "ReturnedQty");
            var available = soldQty - returnedQty;
            if (line.ReturnQuantity > available) throw new InvalidOperationException($"Return quantity exceeds available quantity for sale line {line.SaleLineId}.");
            var productId = SqlRead.Int(r, "ProductId");
            var productName = SqlRead.String(r, "ProductName");
            var unitPrice = SqlRead.Decimal(r, "UnitPrice");
            var discountAmount = soldQty == 0 ? 0 : Math.Round(SqlRead.Decimal(r, "DiscountAmount") / soldQty * line.ReturnQuantity, 2);
            var taxAmount = soldQty == 0 ? 0 : Math.Round(SqlRead.Decimal(r, "TaxAmount") / soldQty * line.ReturnQuantity, 2);
            var refundAmount = soldQty == 0 ? 0 : Math.Round(SqlRead.Decimal(r, "LineTotal") / soldQty * line.ReturnQuantity, 2);
            var unitCost = SqlRead.Decimal(r, "UnitCost");
            await r.CloseAsync();

            await using var ins = new SqlCommand(@"
INSERT INTO ReturnLines(ReturnId,SaleLineId,ProductId,ProductName,ReturnQuantity,UnitPrice,DiscountAmount,TaxAmount,RefundAmount,UnitCost)
VALUES(@ReturnId,@SaleLineId,@ProductId,@ProductName,@Qty,@UnitPrice,@DiscountAmount,@TaxAmount,@RefundAmount,@UnitCost);
UPDATE Products SET StockOnHand=StockOnHand+@Qty WHERE ProductId=@ProductId;
MERGE StockByStore AS t USING(SELECT @StoreId StoreId,@ProductId ProductId) s ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=Quantity+@Qty
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@StoreId,@ProductId,@Qty,@UnitCost);
INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy)
VALUES(@StoreId,@BranchCode,@ProductId,'Return',@ReturnNo,@Qty,0,@UnitCost,'POS return',@UserId);", con, tran);
            ins.Parameters.AddWithValue("@ReturnId", returnId);
            ins.Parameters.AddWithValue("@SaleLineId", line.SaleLineId);
            ins.Parameters.AddWithValue("@ProductId", productId);
            ins.Parameters.AddWithValue("@ProductName", productName);
            ins.Parameters.AddWithValue("@Qty", line.ReturnQuantity);
            ins.Parameters.AddWithValue("@UnitPrice", unitPrice);
            ins.Parameters.AddWithValue("@DiscountAmount", discountAmount);
            ins.Parameters.AddWithValue("@TaxAmount", taxAmount);
            ins.Parameters.AddWithValue("@RefundAmount", refundAmount);
            ins.Parameters.AddWithValue("@UnitCost", unitCost);
            ins.Parameters.AddWithValue("@StoreId", user.StoreId);
            ins.Parameters.AddWithValue("@BranchCode", user.BranchCode);
            ins.Parameters.AddWithValue("@ReturnNo", returnNo);
            ins.Parameters.AddWithValue("@UserId", user.UserId);
            await ins.ExecuteNonQueryAsync();
            refundTotal += refundAmount;
            taxTotal += taxAmount;
            costTotal += Math.Round(unitCost * line.ReturnQuantity, 2);
        }
        if (refundTotal <= 0) throw new InvalidOperationException("Refund amount is zero.");
        await using (var upd = new SqlCommand("UPDATE ReturnHeader SET RefundAmount=@RefundAmount WHERE ReturnId=@ReturnId", con, tran))
        {
            upd.Parameters.AddWithValue("@RefundAmount", refundTotal);
            upd.Parameters.AddWithValue("@ReturnId", returnId);
            await upd.ExecuteNonQueryAsync();
        }
        var setup = await GetPostingSetupAsync(con, tran);
        await InsertGlAsync(con, tran, setup["SalesReturnAccount"], DateTime.Today, "POS Return", returnNo, refundTotal - taxTotal, 0, "Sales return", returnId, user.BranchCode);
        if (taxTotal > 0) await InsertGlAsync(con, tran, setup["OutputTaxAccount"], DateTime.Today, "POS Return", returnNo, taxTotal, 0, "Output tax reversed", returnId, user.BranchCode);
        await InsertGlAsync(con, tran, setup["CashAccount"], DateTime.Today, "POS Return", returnNo, 0, refundTotal, "Cash refund", returnId, user.BranchCode);
        if (costTotal > 0)
        {
            await InsertGlAsync(con, tran, setup["InventoryAccount"], DateTime.Today, "POS Return", returnNo, costTotal, 0, "Inventory returned", returnId, user.BranchCode);
            await InsertGlAsync(con, tran, setup["CogsAccount"], DateTime.Today, "POS Return", returnNo, 0, costTotal, "COGS reversed", returnId, user.BranchCode);
        }
        await tran.CommitAsync();
        return Results.Ok(new { returnId, returnNo, refundAmount = refundTotal });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/sales-invoices", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to, string? status, int? customerId, bool? openBalance) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    var statusFilter = NormalizeInvoiceStatusFilter(status);
    if (statusFilter == null)
        return Results.BadRequest(new { message = "Status must be Open, Draft, OpenDraft, or Posted." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    var includePos = openBalance != true
        && (string.IsNullOrEmpty(statusFilter) || string.Equals(statusFilter, "Posted", StringComparison.OrdinalIgnoreCase));
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT TOP 1000 * FROM (
SELECT h.SalesInvoiceId,
       CAST(0 AS INT) PosSaleId,
       CAST('SalesInvoice' AS NVARCHAR(20)) DocumentType,
       CAST(ISNULL(NULLIF(LTRIM(RTRIM(h.ApplicationSource)),''),'Cloud') AS NVARCHAR(20)) ApplicationSource,
       h.InvoiceNo,h.CustomerId,c.CustomerCode,c.CustomerName,h.InvoiceDate,h.PostedAt,
       h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,h.BalanceAmount,h.Status,
       ISNULL(h.Remarks,'') Remarks,
       ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy
FROM SalesInvoiceHeader h
INNER JOIN Customers c ON c.CustomerId=h.CustomerId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.StoreId=@StoreId AND h.InvoiceDate>=@From AND h.InvoiceDate<=@To
  AND (@Status='' OR (@Status='OpenDraft' AND h.Status IN ('Open','Draft')) OR (@Status<>'OpenDraft' AND h.Status=@Status))
  AND (@CustomerId=0 OR h.CustomerId=@CustomerId)
  AND (@OpenBalance=0 OR (h.Status='Posted' AND ISNULL(h.BalanceAmount,0)>0))
UNION ALL
SELECT CAST(0 AS INT) SalesInvoiceId,
       h.SaleId PosSaleId,
       CAST('PosSale' AS NVARCHAR(20)) DocumentType,
       CAST(ISNULL(NULLIF(LTRIM(RTRIM(h.ApplicationSource)),''),'Cloud') AS NVARCHAR(20)) ApplicationSource,
       h.InvoiceNo,h.CustomerId,ISNULL(c.CustomerCode,'') CustomerCode,ISNULL(c.CustomerName,'Walk-in Customer') CustomerName,
       CAST(h.SaleDate AS DATE) InvoiceDate,h.SaleDate PostedAt,
       h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,
       CAST(0 AS DECIMAL(18,2)) BalanceAmount,h.Status,
       ISNULL(h.Remarks,'') Remarks,
       ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy
FROM SalesHeader h
LEFT JOIN Customers c ON c.CustomerId=h.CustomerId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE @IncludePos=1
  AND h.StoreId=@StoreId AND h.Status='Posted'
  AND CAST(h.SaleDate AS DATE)>=@From AND CAST(h.SaleDate AS DATE)<=@To
  AND (@CustomerId=0 OR h.CustomerId=@CustomerId)
) x
ORDER BY InvoiceDate DESC, InvoiceNo DESC";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@From", (from ?? new DateTime(1900, 1, 1)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    cmd.Parameters.AddWithValue("@Status", statusFilter);
    cmd.Parameters.AddWithValue("@CustomerId", customerId ?? 0);
    cmd.Parameters.AddWithValue("@OpenBalance", openBalance == true);
    cmd.Parameters.AddWithValue("@IncludePos", includePos ? 1 : 0);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/sales-invoices/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id, string? documentType) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    await using var cmd = con.CreateCommand();
    var posSale = string.Equals(documentType, "PosSale", StringComparison.OrdinalIgnoreCase);
    cmd.CommandText = posSale
        ? @"SELECT TOP 1 CAST(0 AS INT) SalesInvoiceId,h.SaleId PosSaleId,CAST('PosSale' AS NVARCHAR(20)) DocumentType,
CAST(ISNULL(NULLIF(LTRIM(RTRIM(h.ApplicationSource)),''),'Cloud') AS NVARCHAR(20)) ApplicationSource,
h.InvoiceNo,h.CustomerId,ISNULL(c.CustomerCode,'') CustomerCode,ISNULL(c.CustomerName,'Walk-in Customer') CustomerName,ISNULL(c.Mobile,'') Mobile,ISNULL(c.Email,'') Email,ISNULL(c.AddressLine,'') AddressLine,
CAST(h.SaleDate AS DATE) InvoiceDate,h.SaleDate PostedAt,h.StoreId,h.BranchCode,h.UserId,
ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy,
h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,
CAST(0 AS DECIMAL(18,2)) BalanceAmount,h.Status,ISNULL(h.Remarks,'') Remarks
FROM SalesHeader h
LEFT JOIN Customers c ON c.CustomerId=h.CustomerId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.SaleId=@Id AND h.StoreId=@StoreId"
        : @"SELECT TOP 1 h.SalesInvoiceId,CAST(0 AS INT) PosSaleId,CAST('SalesInvoice' AS NVARCHAR(20)) DocumentType,
CAST(ISNULL(NULLIF(LTRIM(RTRIM(h.ApplicationSource)),''),'Cloud') AS NVARCHAR(20)) ApplicationSource,
h.InvoiceNo,h.CustomerId,c.CustomerCode,c.CustomerName,c.Mobile,c.Email,c.AddressLine,h.InvoiceDate,h.PostedAt,h.StoreId,h.BranchCode,h.UserId,
ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy,h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,h.BalanceAmount,h.Status,ISNULL(h.Remarks,'') Remarks
FROM SalesInvoiceHeader h
INNER JOIN Customers c ON c.CustomerId=h.CustomerId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.SalesInvoiceId=@Id AND h.StoreId=@StoreId";
    cmd.Parameters.AddWithValue("@Id", id);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var row = await SqlList.ReadSingleAsync(cmd);
    return row.Count == 0 ? Results.NotFound(new { message = posSale ? "POS sale not found." : "Sales invoice not found." }) : Results.Ok(row);
});

app.MapDelete("/api/sales-invoices/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanUserManagePermission(user, "sales.deleteInvoice"))
        return Results.BadRequest(new { message = "Delete Sales Invoice permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        string? status;
        string? invoiceNo;
        await using (var find = new SqlCommand(
            "SELECT TOP 1 Status, InvoiceNo FROM SalesInvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE SalesInvoiceId=@Id AND StoreId=@StoreId",
            con, tran))
        {
            find.Parameters.AddWithValue("@Id", id);
            find.Parameters.AddWithValue("@StoreId", user.StoreId);
            await using var reader = await find.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await tran.RollbackAsync();
                return Results.NotFound(new { message = "Sales invoice not found." });
            }
            status = Convert.ToString(reader["Status"]);
            invoiceNo = Convert.ToString(reader["InvoiceNo"]);
        }

        if (!string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
        {
            await tran.RollbackAsync();
            return Results.BadRequest(new { message = "Only Open or Draft sales invoices can be deleted. Posted invoices cannot be deleted." });
        }

        await using (var delLines = new SqlCommand("DELETE FROM SalesInvoiceLines WHERE SalesInvoiceId=@Id", con, tran))
        {
            delLines.Parameters.AddWithValue("@Id", id);
            await delLines.ExecuteNonQueryAsync();
        }
        await using (var delHeader = new SqlCommand(
            "DELETE FROM SalesInvoiceHeader WHERE SalesInvoiceId=@Id AND StoreId=@StoreId AND Status IN ('Open','Draft')",
            con, tran))
        {
            delHeader.Parameters.AddWithValue("@Id", id);
            delHeader.Parameters.AddWithValue("@StoreId", user.StoreId);
            var rows = await delHeader.ExecuteNonQueryAsync();
            if (rows == 0)
            {
                await tran.RollbackAsync();
                return Results.BadRequest(new { message = "Sales invoice could not be deleted. It may already be posted." });
            }
        }

        await tran.CommitAsync();
        return Results.Ok(new { message = $"Sales invoice {invoiceNo} deleted.", salesInvoiceId = id });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/purchase-invoices", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to, string? status, int? vendorId, bool? openBalance) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    var statusFilter = NormalizeInvoiceStatusFilter(status);
    if (statusFilter == null)
        return Results.BadRequest(new { message = "Status must be Open, Draft, OpenDraft, or Posted." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 h.PurchaseInvoiceId,h.InvoiceNo,ISNULL(h.VendorId,0) VendorId,ISNULL(v.VendorCode,'') VendorCode,ISNULL(v.VendorName,'Vendor not selected') VendorName,ISNULL(h.VendorInvoiceNo,'') VendorInvoiceNo,h.InvoiceDate,h.PostedAt,h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,h.BalanceAmount,h.Status,ISNULL(h.Remarks,'') Remarks,
ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy
FROM PurchaseInvoiceHeader h
LEFT JOIN Vendors v ON v.VendorId=h.VendorId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.StoreId=@StoreId AND h.InvoiceDate>=@From AND h.InvoiceDate<=@To
  AND (@Status='' OR (@Status='OpenDraft' AND h.Status IN ('Open','Draft')) OR (@Status<>'OpenDraft' AND h.Status=@Status))
  AND (@VendorId=0 OR ISNULL(h.VendorId,0)=@VendorId)
  AND (@OpenBalance=0 OR (h.Status='Posted' AND ISNULL(h.BalanceAmount,0)>0))
ORDER BY h.PurchaseInvoiceId DESC";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@From", (from ?? new DateTime(1900, 1, 1)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    cmd.Parameters.AddWithValue("@Status", statusFilter);
    cmd.Parameters.AddWithValue("@VendorId", vendorId ?? 0);
    cmd.Parameters.AddWithValue("@OpenBalance", openBalance == true);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/purchase-invoices/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 h.PurchaseInvoiceId,h.InvoiceNo,ISNULL(h.VendorId,0) VendorId,ISNULL(v.VendorCode,'') VendorCode,ISNULL(v.VendorName,'Vendor not selected') VendorName,ISNULL(v.Mobile,'') Mobile,ISNULL(v.Email,'') Email,ISNULL(v.AddressLine,'') AddressLine,ISNULL(h.VendorInvoiceNo,'') VendorInvoiceNo,h.InvoiceDate,h.PostedAt,h.StoreId,h.BranchCode,h.UserId,
ISNULL(s.StoreCode,'') StoreCode,ISNULL(s.StoreName,'') StoreName,ISNULL(u.DisplayName,'') PreparedBy,h.SubTotal,h.DiscountAmount,h.TaxAmount,h.GrandTotal,h.PaidAmount,h.BalanceAmount,h.Status,ISNULL(h.Remarks,'') Remarks
FROM PurchaseInvoiceHeader h
LEFT JOIN Vendors v ON v.VendorId=h.VendorId
LEFT JOIN Stores s ON s.StoreId=h.StoreId
LEFT JOIN Users u ON u.UserId=h.UserId
WHERE h.PurchaseInvoiceId=@Id AND h.StoreId=@StoreId";
    cmd.Parameters.AddWithValue("@Id", id);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var row = await SqlList.ReadSingleAsync(cmd);
    return row.Count == 0 ? Results.NotFound(new { message = "Purchase invoice not found." }) : Results.Ok(row);
});

app.MapDelete("/api/purchase-invoices/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!CanUserManagePermission(user, "purchase.deleteInvoice"))
        return Results.BadRequest(new { message = "Delete Purchase Invoice permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        string? status;
        string? invoiceNo;
        await using (var find = new SqlCommand(
            "SELECT TOP 1 Status, InvoiceNo FROM PurchaseInvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE PurchaseInvoiceId=@Id AND StoreId=@StoreId",
            con, tran))
        {
            find.Parameters.AddWithValue("@Id", id);
            find.Parameters.AddWithValue("@StoreId", user.StoreId);
            await using var reader = await find.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await tran.RollbackAsync();
                return Results.NotFound(new { message = "Purchase invoice not found." });
            }
            status = Convert.ToString(reader["Status"]);
            invoiceNo = Convert.ToString(reader["InvoiceNo"]);
        }

        if (!string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
        {
            await tran.RollbackAsync();
            return Results.BadRequest(new { message = "Only Open or Draft purchase invoices can be deleted. Posted invoices cannot be deleted." });
        }

        await using (var delLines = new SqlCommand("DELETE FROM PurchaseInvoiceLines WHERE PurchaseInvoiceId=@Id", con, tran))
        {
            delLines.Parameters.AddWithValue("@Id", id);
            await delLines.ExecuteNonQueryAsync();
        }
        await using (var delHeader = new SqlCommand(
            "DELETE FROM PurchaseInvoiceHeader WHERE PurchaseInvoiceId=@Id AND StoreId=@StoreId AND Status IN ('Open','Draft')",
            con, tran))
        {
            delHeader.Parameters.AddWithValue("@Id", id);
            delHeader.Parameters.AddWithValue("@StoreId", user.StoreId);
            var rows = await delHeader.ExecuteNonQueryAsync();
            if (rows == 0)
            {
                await tran.RollbackAsync();
                return Results.BadRequest(new { message = "Purchase invoice could not be deleted. It may already be posted." });
            }
        }

        await tran.CommitAsync();
        return Results.Ok(new { message = $"Purchase invoice {invoiceNo} deleted.", purchaseInvoiceId = id });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/customer-payments", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CustomerPaymentPostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
    if (request.CustomerId <= 0 || request.Amount <= 0) return Results.BadRequest(new { message = "Customer and positive amount are required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await BankAccountService.EnsureSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
        if (!string.IsNullOrWhiteSpace(clientDocumentId))
        {
            var existing = await DesktopIdempotency.FindCustomerPaymentAsync(con, tran, clientDocumentId);
            if (existing != null)
            {
                await tran.CommitAsync();
                return Results.Ok(new { paymentId = existing.PaymentId, paymentNo = existing.PaymentNo, duplicate = true });
            }
        }
        var posted = await PostCustomerReceiptAsync(con, tran, user, request, applyInvoiceBalance: true);
        await tran.CommitAsync();
        return Results.Ok(new { paymentId = posted.PaymentId, paymentNo = posted.PaymentNo });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/vendor-payments", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, VendorPaymentPostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
    if (request.VendorId <= 0 || request.Amount <= 0) return Results.BadRequest(new { message = "Vendor and positive amount are required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await BankAccountService.EnsureSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
        if (!string.IsNullOrWhiteSpace(clientDocumentId))
        {
            var existing = await DesktopIdempotency.FindVendorPaymentAsync(con, tran, clientDocumentId);
            if (existing != null)
            {
                await tran.CommitAsync();
                return Results.Ok(new { paymentId = existing.PaymentId, paymentNo = existing.PaymentNo, duplicate = true });
            }
        }
        var posted = await PostVendorReceiptAsync(con, tran, user, request, applyInvoiceBalance: true);
        await tran.CommitAsync();
        return Results.Ok(new { paymentId = posted.PaymentId, paymentNo = posted.PaymentNo });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/finance/customer-ledger", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int? customerId, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 le.CustomerLedgerEntryId,le.CustomerId,c.CustomerName,le.PostingDate,le.DocumentType,le.DocumentNo,le.DebitAmount,le.CreditAmount,le.BalanceAfter,le.Description
FROM CustomerLedgerEntries le INNER JOIN Customers c ON c.CustomerId=le.CustomerId
WHERE (@CustomerId=0 OR le.CustomerId=@CustomerId) AND le.PostingDate>=@From AND le.PostingDate<=@To
ORDER BY le.PostingDate DESC, le.CustomerLedgerEntryId DESC";
    cmd.Parameters.AddWithValue("@CustomerId", customerId ?? 0);
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddYears(-1)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/finance/vendor-ledger", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int? vendorId, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 le.VendorLedgerEntryId,le.VendorId,v.VendorName,le.PostingDate,le.DocumentType,le.DocumentNo,le.DebitAmount,le.CreditAmount,le.BalanceAfter,le.Description
FROM VendorLedgerEntries le INNER JOIN Vendors v ON v.VendorId=le.VendorId
WHERE (@VendorId=0 OR le.VendorId=@VendorId) AND le.PostingDate>=@From AND le.PostingDate<=@To
ORDER BY le.PostingDate DESC, le.VendorLedgerEntryId DESC";
    cmd.Parameters.AddWithValue("@VendorId", vendorId ?? 0);
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddYears(-1)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/reports/trial-balance", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT a.AccountNo,a.AccountName,a.AccountType,SUM(g.DebitAmount) Debit,SUM(g.CreditAmount) Credit,SUM(g.DebitAmount-g.CreditAmount) Balance
FROM ChartOfAccounts a LEFT JOIN GLEntries g ON g.AccountId=a.AccountId AND g.PostingDate>=@From AND g.PostingDate<=@To
GROUP BY a.AccountNo,a.AccountName,a.AccountType ORDER BY a.AccountNo";
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/reports/stock-valuation", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Ok(await SqlList.ReadAsync(con, @"SELECT ProductCode,ProductName,StockOnHand Quantity,PurchasePrice UnitCost,StockOnHand*PurchasePrice Value,ReorderLevel,CASE WHEN StockOnHand<=ReorderLevel THEN 'REORDER' ELSE 'OK' END Status FROM Products WHERE IsActive=1 ORDER BY ProductName"));
});

app.MapGet("/api/reports/tax", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT 'Output Tax' TaxType, SUM(GrandTotal-TaxAmount) TaxableAmount, SUM(TaxAmount) TaxAmount FROM SalesHeader WHERE Status='Posted' AND SaleDate>=@From AND SaleDate<DATEADD(DAY,1,@To)
UNION ALL
SELECT 'Input Tax' TaxType, SUM(GrandTotal-TaxAmount) TaxableAmount, SUM(TaxAmount) TaxAmount FROM PurchaseInvoiceHeader WHERE Status='Posted' AND InvoiceDate>=@From AND InvoiceDate<=@To";
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/reports/daily-profit", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT CAST(sh.SaleDate AS DATE) PostingDate,SUM(sh.GrandTotal) Sales,SUM(sh.TaxAmount) Tax,SUM(sl.Quantity*sl.UnitCost) Cost,SUM(sh.GrandTotal)-SUM(sh.TaxAmount)-SUM(sl.Quantity*sl.UnitCost) GrossProfit
FROM SalesHeader sh INNER JOIN SalesLines sl ON sl.SaleId=sh.SaleId
WHERE sh.Status='Posted' AND sh.SaleDate>=@From AND sh.SaleDate<DATEADD(DAY,1,@To)
GROUP BY CAST(sh.SaleDate AS DATE) ORDER BY PostingDate";
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});



app.MapPost("/api/accounting/chart-of-accounts", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, ChartAccountUpsertRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.AccountNo) || string.IsNullOrWhiteSpace(request.AccountName)) return Results.BadRequest(new { message = "Account no. and name are required." });
    var accountType = request.AccountType.Trim();
    var normal = request.NormalBalance.Trim();
    var validTypes = new[] { "Asset", "Liability", "Equity", "Income", "Expense" };
    var validNormal = new[] { "Debit", "Credit" };
    if (!validTypes.Contains(accountType, StringComparer.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "Invalid account type." });
    if (!validNormal.Contains(normal, StringComparer.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "Normal balance must be Debit or Credit." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF @AccountId > 0 AND EXISTS(SELECT 1 FROM ChartOfAccounts WHERE AccountId=@AccountId AND IsSystem=1)
BEGIN
    THROW 50010, 'System account is locked.', 1;
END
IF @AccountId > 0 AND EXISTS(SELECT 1 FROM ChartOfAccounts WHERE AccountId=@AccountId)
BEGIN
    UPDATE ChartOfAccounts SET AccountNo=@AccountNo,AccountName=@AccountName,AccountType=@AccountType,NormalBalance=@NormalBalance,IsActive=@IsActive WHERE AccountId=@AccountId;
    SELECT @AccountId;
END
ELSE
BEGIN
    INSERT INTO ChartOfAccounts(AccountNo,AccountName,AccountType,NormalBalance,IsSystem,IsActive) OUTPUT INSERTED.AccountId VALUES(@AccountNo,@AccountName,@AccountType,@NormalBalance,0,@IsActive);
END";
    cmd.Parameters.AddWithValue("@AccountId", request.AccountId);
    cmd.Parameters.AddWithValue("@AccountNo", request.AccountNo.Trim());
    cmd.Parameters.AddWithValue("@AccountName", request.AccountName.Trim());
    cmd.Parameters.AddWithValue("@AccountType", accountType);
    cmd.Parameters.AddWithValue("@NormalBalance", normal);
    cmd.Parameters.AddWithValue("@IsActive", request.IsActive);
    try { var id = Convert.ToInt32(await cmd.ExecuteScalarAsync()); return Results.Ok(new { accountId = id, message = "Account saved." }); }
    catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
});

app.MapGet("/api/accounting/report/profit-loss", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT a.AccountNo,a.AccountName,a.AccountType,ISNULL(SUM(g.DebitAmount),0) Debit,ISNULL(SUM(g.CreditAmount),0) Credit,
CASE WHEN a.AccountType='Income' THEN ISNULL(SUM(g.CreditAmount-g.DebitAmount),0) ELSE ISNULL(SUM(g.DebitAmount-g.CreditAmount),0) END Balance
FROM ChartOfAccounts a LEFT JOIN GLEntries g ON g.AccountId=a.AccountId AND g.PostingDate>=@From AND g.PostingDate<=@To
WHERE a.AccountType IN('Income','Expense') AND a.IsActive=1
GROUP BY a.AccountNo,a.AccountName,a.AccountType ORDER BY a.AccountType,a.AccountNo";
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/accounting/report/balance-sheet", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? asOf) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT a.AccountNo,a.AccountName,a.AccountType,ISNULL(SUM(g.DebitAmount),0) Debit,ISNULL(SUM(g.CreditAmount),0) Credit,
CASE WHEN a.AccountType IN('Liability','Equity') THEN ISNULL(SUM(g.CreditAmount-g.DebitAmount),0) ELSE ISNULL(SUM(g.DebitAmount-g.CreditAmount),0) END Balance
FROM ChartOfAccounts a LEFT JOIN GLEntries g ON g.AccountId=a.AccountId AND g.PostingDate<=@AsOf
WHERE a.AccountType IN('Asset','Liability','Equity') AND a.IsActive=1
GROUP BY a.AccountNo,a.AccountName,a.AccountType ORDER BY a.AccountType,a.AccountNo";
    cmd.Parameters.AddWithValue("@AsOf", (asOf ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/accounting/report/gl-ledger", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to, string? accountTerm) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 2000 g.GLEntryId,g.PostingDate,a.AccountNo,a.AccountName,g.DocumentType,g.DocumentNo,g.DebitAmount,g.CreditAmount,ISNULL(g.Description,'') Description
FROM GLEntries g INNER JOIN ChartOfAccounts a ON a.AccountId=g.AccountId
WHERE g.PostingDate>=@From AND g.PostingDate<=@To AND (@Term='' OR a.AccountNo LIKE @Like OR a.AccountName LIKE @Like OR g.DocumentNo LIKE @Like)
ORDER BY a.AccountNo,g.PostingDate,g.GLEntryId";
    var term = (accountTerm ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    cmd.Parameters.AddWithValue("@Term", term);
    cmd.Parameters.AddWithValue("@Like", "%" + term + "%");
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/accounting/report/cash-bank", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 g.GLEntryId,g.PostingDate,a.AccountNo,a.AccountName,g.DocumentType,g.DocumentNo,g.DebitAmount,g.CreditAmount,g.Description
FROM GLEntries g INNER JOIN ChartOfAccounts a ON a.AccountId=g.AccountId CROSS JOIN PostingSetup s
WHERE g.PostingDate>=@From AND g.PostingDate<=@To AND a.AccountNo IN(s.CashAccount,s.BankAccount)
ORDER BY g.PostingDate DESC,g.GLEntryId DESC";
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/accounting/report/customer-aging", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? asOf) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT c.CustomerCode Code,c.CustomerName Name,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)<=30 THEN h.BalanceAmount ELSE 0 END) [Current],
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 31 AND 60 THEN h.BalanceAmount ELSE 0 END) Days30,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 61 AND 90 THEN h.BalanceAmount ELSE 0 END) Days60,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 91 AND 120 THEN h.BalanceAmount ELSE 0 END) Days90,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)>120 THEN h.BalanceAmount ELSE 0 END) Over90,
SUM(h.BalanceAmount) Total
FROM SalesInvoiceHeader h INNER JOIN Customers c ON c.CustomerId=h.CustomerId
WHERE h.Status='Posted' AND h.BalanceAmount>0 AND h.InvoiceDate<=@AsOf
GROUP BY c.CustomerCode,c.CustomerName ORDER BY c.CustomerName";
    cmd.Parameters.AddWithValue("@AsOf", (asOf ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/accounting/report/vendor-aging", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? asOf) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT v.VendorCode Code,v.VendorName Name,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)<=30 THEN h.BalanceAmount ELSE 0 END) [Current],
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 31 AND 60 THEN h.BalanceAmount ELSE 0 END) Days30,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 61 AND 90 THEN h.BalanceAmount ELSE 0 END) Days60,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf) BETWEEN 91 AND 120 THEN h.BalanceAmount ELSE 0 END) Days90,
SUM(CASE WHEN DATEDIFF(DAY,h.InvoiceDate,@AsOf)>120 THEN h.BalanceAmount ELSE 0 END) Over90,
SUM(h.BalanceAmount) Total
FROM PurchaseInvoiceHeader h INNER JOIN Vendors v ON v.VendorId=h.VendorId
WHERE h.Status='Posted' AND h.BalanceAmount>0 AND h.InvoiceDate<=@AsOf
GROUP BY v.VendorCode,v.VendorName ORDER BY v.VendorName";
    cmd.Parameters.AddWithValue("@AsOf", (asOf ?? DateTime.Today).Date);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/customer-payments", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await BankAccountService.EnsureSchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 300 p.PaymentId,p.PaymentNo,p.CustomerId,c.CustomerName,p.PaymentDate,p.Amount,p.PaymentMethod,ISNULL(p.ReferenceNo,'') ReferenceNo,ISNULL(p.Remarks,'') Remarks,
ISNULL(p.BankAccountId,0) BankAccountId,ISNULL(b.BankCode,'') BankCode,ISNULL(b.BankName,'') BankName,
ISNULL(p.SalesInvoiceId,0) SalesInvoiceId,ISNULL(si.InvoiceNo,'') InvoiceNo
FROM CustomerPayments p INNER JOIN Customers c ON c.CustomerId=p.CustomerId
LEFT JOIN BankAccounts b ON b.BankAccountId=p.BankAccountId
LEFT JOIN SalesInvoiceHeader si ON si.SalesInvoiceId=p.SalesInvoiceId
WHERE (ISNULL(p.BranchCode,@BranchCode)=@BranchCode) AND (@Term='' OR p.PaymentNo LIKE @Like OR c.CustomerName LIKE @Like OR p.ReferenceNo LIKE @Like)
ORDER BY p.PaymentId DESC";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/vendor-payments", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await BankAccountService.EnsureSchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 300 p.PaymentId,p.PaymentNo,p.VendorId,v.VendorName,p.PaymentDate,p.Amount,p.PaymentMethod,ISNULL(p.ReferenceNo,'') ReferenceNo,ISNULL(p.Remarks,'') Remarks,
ISNULL(p.BankAccountId,0) BankAccountId,ISNULL(b.BankCode,'') BankCode,ISNULL(b.BankName,'') BankName,
ISNULL(p.PurchaseInvoiceId,0) PurchaseInvoiceId,ISNULL(ph.InvoiceNo,'') InvoiceNo
FROM VendorPayments p INNER JOIN Vendors v ON v.VendorId=p.VendorId
LEFT JOIN BankAccounts b ON b.BankAccountId=p.BankAccountId
LEFT JOIN PurchaseInvoiceHeader ph ON ph.PurchaseInvoiceId=p.PurchaseInvoiceId
WHERE (ISNULL(p.BranchCode,@BranchCode)=@BranchCode) AND (@Term='' OR p.PaymentNo LIKE @Like OR v.VendorName LIKE @Like OR p.ReferenceNo LIKE @Like)
ORDER BY p.PaymentId DESC";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapPost("/api/shifts/cash-drawer", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CashDrawerRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (request.Amount <= 0) return Results.BadRequest(new { message = "Amount must be greater than zero." });
    var type = request.EntryType.Equals("Cash Out", StringComparison.OrdinalIgnoreCase) ? "Cash Out" : "Cash In";
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"DECLARE @ShiftId INT=(SELECT TOP 1 ShiftId FROM Shifts WHERE StoreId=@StoreId AND Status='Open' AND (@UserWiseShift=0 OR UserId=@UserId) ORDER BY ShiftId DESC);
IF @ShiftId IS NULL THROW 50020, 'Open shift required.', 1;
INSERT INTO CashDrawerLedger(ShiftId,EntryType,Amount,Remarks,CreatedBy) VALUES(@ShiftId,@EntryType,@Amount,@Remarks,@UserId);";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    cmd.Parameters.AddWithValue("@EntryType", type);
    cmd.Parameters.AddWithValue("@Amount", request.Amount);
    cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
    try { await cmd.ExecuteNonQueryAsync(); return Results.Ok(new { message = type + " posted." }); }
    catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
});

app.MapGet("/api/inventory/stores", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Ok(await SqlList.ReadAsync(con, "SELECT StoreId,StoreCode,StoreName,StoreCode + ' - ' + StoreName DisplayName FROM Stores WHERE IsActive=1 ORDER BY StoreName"));
});

app.MapGet("/api/inventory/products", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int? storeId) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT p.ProductId,p.ProductCode,p.ProductName,ISNULL(s.Quantity,p.StockOnHand) Quantity,ISNULL(s.AverageCost,p.PurchasePrice) Cost,p.ProductCode + ' - ' + p.ProductName DisplayName
FROM Products p LEFT JOIN StockByStore s ON s.ProductId=p.ProductId AND s.StoreId=@StoreId
WHERE p.IsActive=1 ORDER BY p.ProductName";
    cmd.Parameters.AddWithValue("@StoreId", storeId ?? user.StoreId);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapPost("/api/inventory/transfer", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, InventoryTransferRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "inventory.transfer")) return Results.BadRequest(new { message = "Inventory Transfer permission is required." });
    if (request.FromStoreId == request.ToStoreId) return Results.BadRequest(new { message = "From and To stores must be different." });
    if (request.Quantity <= 0) return Results.BadRequest(new { message = "Quantity must be greater than zero." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var no = await PosSql.NextNumberAsync(con, tran, "TRANSFER");
        decimal cost = 0;
        await using (var lockCmd = new SqlCommand("SELECT Quantity,AverageCost FROM StockByStore WITH(UPDLOCK,ROWLOCK) WHERE StoreId=@StoreId AND ProductId=@ProductId", con, tran))
        {
            lockCmd.Parameters.AddWithValue("@StoreId", request.FromStoreId);
            lockCmd.Parameters.AddWithValue("@ProductId", request.ProductId);
            await using var r = await lockCmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException("Source store stock record not found.");
            if (Convert.ToDecimal(r["Quantity"]) < request.Quantity) throw new InvalidOperationException("Insufficient stock in source store.");
            cost = Convert.ToDecimal(r["AverageCost"]);
        }
        long transferId;
        await using (var h = new SqlCommand("INSERT INTO StockTransfers(TransferNo,FromStoreId,ToStoreId,FromBranchCode,ToBranchCode,TransferDate,CreatedBy) OUTPUT INSERTED.TransferId SELECT @No,@From,@To,fs.StoreCode,ts.StoreCode,@Date,@UserId FROM Stores fs CROSS JOIN Stores ts WHERE fs.StoreId=@From AND ts.StoreId=@To", con, tran))
        {
            h.Parameters.AddWithValue("@No", no); h.Parameters.AddWithValue("@From", request.FromStoreId); h.Parameters.AddWithValue("@To", request.ToStoreId); h.Parameters.AddWithValue("@Date", DateTime.Today); h.Parameters.AddWithValue("@UserId", user.UserId);
            transferId = Convert.ToInt64(await h.ExecuteScalarAsync());
        }
        await using (var l = new SqlCommand("INSERT INTO StockTransferLines(TransferId,ProductId,Quantity,UnitCost) VALUES(@Id,@ProductId,@Qty,@Cost)", con, tran))
        {
            l.Parameters.AddWithValue("@Id", transferId); l.Parameters.AddWithValue("@ProductId", request.ProductId); l.Parameters.AddWithValue("@Qty", request.Quantity); l.Parameters.AddWithValue("@Cost", cost); await l.ExecuteNonQueryAsync();
        }
        await using (var u = new SqlCommand(@"UPDATE StockByStore SET Quantity=Quantity-@Qty WHERE StoreId=@From AND ProductId=@ProductId;
MERGE StockByStore t USING(SELECT @To StoreId,@ProductId ProductId) s ON t.StoreId=s.StoreId AND t.ProductId=s.ProductId
WHEN MATCHED THEN UPDATE SET Quantity=t.Quantity+@Qty,AverageCost=@Cost
WHEN NOT MATCHED THEN INSERT(StoreId,ProductId,Quantity,AverageCost) VALUES(@To,@ProductId,@Qty,@Cost);", con, tran))
        {
            u.Parameters.AddWithValue("@Qty", request.Quantity); u.Parameters.AddWithValue("@From", request.FromStoreId); u.Parameters.AddWithValue("@To", request.ToStoreId); u.Parameters.AddWithValue("@ProductId", request.ProductId); u.Parameters.AddWithValue("@Cost", cost); await u.ExecuteNonQueryAsync();
        }
        await tran.CommitAsync();
        return Results.Ok(new { transferNo = no, transferId, message = "Stock transfer posted." });
    }
    catch (Exception ex) { await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});

app.MapPost("/api/inventory/adjust", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, InventoryAdjustmentRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "inventory.stockAdjustment")) return Results.BadRequest(new { message = "Stock Adjustment permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var no = await PosSql.NextNumberAsync(con, tran, "ADJUSTMENT");
        decimal systemQty = 0, cost = 0;
        await using (var q = new SqlCommand("SELECT Quantity,AverageCost FROM StockByStore WITH(UPDLOCK,ROWLOCK) WHERE StoreId=@StoreId AND ProductId=@ProductId", con, tran))
        {
            q.Parameters.AddWithValue("@StoreId", request.StoreId); q.Parameters.AddWithValue("@ProductId", request.ProductId);
            await using var r = await q.ExecuteReaderAsync();
            if (!await r.ReadAsync()) throw new InvalidOperationException("Store stock record not found.");
            systemQty = Convert.ToDecimal(r["Quantity"]); cost = Convert.ToDecimal(r["AverageCost"]);
        }
        var delta = request.CountedQuantity - systemQty;
        long adjustmentId;
        await using (var h = new SqlCommand("INSERT INTO StockAdjustments(AdjustmentNo,StoreId,BranchCode,PostingDate,Reason,CreatedBy) OUTPUT INSERTED.AdjustmentId SELECT @No,@StoreId,s.StoreCode,@Date,@Reason,@UserId FROM Stores s WHERE s.StoreId=@StoreId", con, tran))
        {
            h.Parameters.AddWithValue("@No", no); h.Parameters.AddWithValue("@StoreId", request.StoreId); h.Parameters.AddWithValue("@Date", DateTime.Today); h.Parameters.AddWithValue("@Reason", request.Reason ?? ""); h.Parameters.AddWithValue("@UserId", user.UserId);
            adjustmentId = Convert.ToInt64(await h.ExecuteScalarAsync());
        }
        await using (var l = new SqlCommand("INSERT INTO StockAdjustmentLines(AdjustmentId,ProductId,SystemQuantity,CountedQuantity,DifferenceQuantity,UnitCost,Disposition,BatchNo,SerialNo,ExpiryDate) VALUES(@Id,@ProductId,@SystemQty,@CountedQty,@Delta,@Cost,@Disposition,@BatchNo,@SerialNo,@Expiry)", con, tran))
        {
            l.Parameters.AddWithValue("@Id", adjustmentId); l.Parameters.AddWithValue("@ProductId", request.ProductId); l.Parameters.AddWithValue("@SystemQty", systemQty); l.Parameters.AddWithValue("@CountedQty", request.CountedQuantity); l.Parameters.AddWithValue("@Delta", delta); l.Parameters.AddWithValue("@Cost", cost); l.Parameters.AddWithValue("@Disposition", request.Disposition ?? "Adjustment"); l.Parameters.AddWithValue("@BatchNo", request.BatchNo ?? ""); l.Parameters.AddWithValue("@SerialNo", request.SerialNo ?? "");
            var expiryParam = new SqlParameter("@Expiry", SqlDbType.Date) { Value = request.ExpiryDate.HasValue ? request.ExpiryDate.Value.Date : DBNull.Value };
            l.Parameters.Add(expiryParam);
            await l.ExecuteNonQueryAsync();
        }
        await using (var u = new SqlCommand("UPDATE StockByStore SET Quantity=@CountedQty WHERE StoreId=@StoreId AND ProductId=@ProductId; UPDATE Products SET StockOnHand=StockOnHand+@Delta WHERE ProductId=@ProductId;", con, tran))
        {
            u.Parameters.AddWithValue("@CountedQty", request.CountedQuantity); u.Parameters.AddWithValue("@Delta", delta); u.Parameters.AddWithValue("@StoreId", request.StoreId); u.Parameters.AddWithValue("@ProductId", request.ProductId); await u.ExecuteNonQueryAsync();
        }
        await using (var led = new SqlCommand("INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy) SELECT @StoreId,s.StoreCode,@ProductId,'Adjustment',@DocNo,CASE WHEN @Delta>0 THEN @Delta ELSE 0 END,CASE WHEN @Delta<0 THEN ABS(@Delta) ELSE 0 END,@Cost,@Reason,@UserId FROM Stores s WHERE s.StoreId=@StoreId", con, tran))
        {
            led.Parameters.AddWithValue("@StoreId", request.StoreId); led.Parameters.AddWithValue("@ProductId", request.ProductId); led.Parameters.AddWithValue("@DocNo", no); led.Parameters.AddWithValue("@Delta", delta); led.Parameters.AddWithValue("@Cost", cost); led.Parameters.AddWithValue("@Reason", request.Reason ?? "Stock adjustment"); led.Parameters.AddWithValue("@UserId", user.UserId); await led.ExecuteNonQueryAsync();
        }
        var setup = await GetPostingSetupAsync(con, tran);
        var value = Math.Abs(delta * cost);
        if (value > 0 && delta < 0)
        {
            await InsertGlAsync(con, tran, setup["StockAdjustmentAccount"], DateTime.Today, "Stock Adjustment", no, value, 0, "Stock shortage adjustment", (int)Math.Min(adjustmentId, int.MaxValue), user.BranchCode);
            await InsertGlAsync(con, tran, setup["InventoryAccount"], DateTime.Today, "Stock Adjustment", no, 0, value, "Inventory reduced", (int)Math.Min(adjustmentId, int.MaxValue), user.BranchCode);
        }
        else if (value > 0 && delta > 0)
        {
            await InsertGlAsync(con, tran, setup["InventoryAccount"], DateTime.Today, "Stock Adjustment", no, value, 0, "Inventory increased", (int)Math.Min(adjustmentId, int.MaxValue), user.BranchCode);
            await InsertGlAsync(con, tran, setup["StockAdjustmentAccount"], DateTime.Today, "Stock Adjustment", no, 0, value, "Stock gain adjustment", (int)Math.Min(adjustmentId, int.MaxValue), user.BranchCode);
        }
        await tran.CommitAsync();
        return Results.Ok(new { adjustmentNo = no, adjustmentId, systemQuantity = systemQty, differenceQuantity = delta, message = "Stock adjustment posted." });
    }
    catch (Exception ex) { await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});

app.MapPost("/api/accounting/gl-entries/reverse", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, ReverseGlDocumentRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.DocumentType) || string.IsNullOrWhiteSpace(request.DocumentNo)) return Results.BadRequest(new { message = "Document type and no. are required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var reverseNo = ("REV-" + request.DocumentNo.Trim());
        if (reverseNo.Length > 30) reverseNo = reverseNo[..30];
        await using var cmd = new SqlCommand(@"IF EXISTS(SELECT 1 FROM GLEntries WHERE DocumentType=@DocumentType AND DocumentNo=@ReverseNo) THROW 50030, 'Document already reversed.', 1;
INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId)
SELECT AccountId,CAST(GETDATE() AS DATE),'Reversal',@ReverseNo,CreditAmount,DebitAmount,CONCAT('Reversal of ',DocumentType,' ',DocumentNo,'. ',@Reason),SourceId
FROM GLEntries WHERE DocumentType=@DocumentType AND DocumentNo=@DocumentNo;
SELECT @@ROWCOUNT RowsInserted;", con, tran);
        cmd.Parameters.AddWithValue("@DocumentType", request.DocumentType.Trim());
        cmd.Parameters.AddWithValue("@DocumentNo", request.DocumentNo.Trim());
        cmd.Parameters.AddWithValue("@ReverseNo", reverseNo);
        cmd.Parameters.AddWithValue("@Reason", request.Reason ?? "");
        var rows = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        if (rows == 0) throw new InvalidOperationException("No G/L entries found for this document.");
        await tran.CommitAsync();
        return Results.Ok(new { reverseDocumentNo = reverseNo, rows, message = "Reversal G/L document created." });
    }
    catch (Exception ex) { await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});



// ===================== FINAL REQUIREMENT ENDPOINTS =====================
app.MapGet("/api/settings/summary", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    var company = await SqlList.ReadSingleAsync(con, "SELECT TOP 1 CompanyName,AddressLine,PhoneNo,Email,Website,TaxRegistrationNo,UpdatedAt FROM CompanyInformation ORDER BY CompanyInformationId");
    var shift = await SqlList.ReadSingleAsync(con, $"SELECT TOP 1 Status,OpenedAt,ClosedAt FROM Shifts WHERE UserId={user.UserId} ORDER BY ShiftId DESC");
    var counts = await SqlList.ReadSingleAsync(con, @"SELECT
(SELECT COUNT(1) FROM Products WHERE IsActive=1) Products,
(SELECT COUNT(1) FROM Customers WHERE IsActive=1) Customers,
(SELECT COUNT(1) FROM Vendors WHERE IsActive=1) Vendors,
(SELECT COUNT(1) FROM TaxGroups) TaxGroups,
(SELECT COUNT(1) FROM ChartOfAccounts WHERE IsActive=1) Accounts,
(SELECT COUNT(1) FROM AccountingPeriods WHERE IsClosed=1) ClosedPeriods");
    return Results.Ok(new { user, company, shift, counts });
});

app.MapGet("/api/audit", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, DateTime? from, DateTime? to, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 500
    AuditId AS AuditLogId,
    ActionName AS ActionType,
    EntityName,
    EntityKey,
    OldValues,
    NewValues,
    Description AS Remarks,
    CreatedAt,
    UserId AS CreatedBy
FROM AuditLog
WHERE CreatedAt>=@From AND CreatedAt<DATEADD(DAY,1,@To)
AND (@Term='' OR ActionName LIKE @Like OR EntityName LIKE @Like OR EntityKey LIKE @Like OR Description LIKE @Like)
ORDER BY AuditId DESC";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@From", (from ?? DateTime.Today.AddDays(-30)).Date);
    cmd.Parameters.AddWithValue("@To", (to ?? DateTime.Today).Date);
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    return Results.Ok(await SqlList.ReadAsync(cmd));
});


app.MapGet("/api/product-tax-discounts", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, string? term) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureProductDefaultsColumnsAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1000 p.ProductId,p.ProductCode,p.ProductName,p.SalePrice,ISNULL(p.ProductDiscountPercent,0) ProductDiscountPercent,
ISNULL(t.TaxPercent,0) TaxPercent,ISNULL(t.IsInclusive,0) TaxInclusive
FROM Products p
LEFT JOIN TaxGroups t ON t.TaxGroupId=p.TaxGroupId
WHERE p.IsActive=1 AND (@Term='' OR p.ProductCode LIKE @Like OR p.ProductName LIKE @Like OR p.Barcode LIKE @Like)
ORDER BY p.ProductName";
    var search = (term ?? string.Empty).Trim();
    cmd.Parameters.AddWithValue("@Term", search);
    cmd.Parameters.AddWithValue("@Like", "%" + search + "%");
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapPost("/api/product-tax-discounts", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, ProductTaxDiscountUpdateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "pricing.changeProductDiscount") && !HasSessionPermission(user, "pricing.changeProductPrice"))
        return Results.BadRequest(new { message = "Product price/discount permission is required." });
    if (request.ProductId <= 0 || request.DiscountPercent < 0 || request.DiscountPercent > 100 || request.TaxPercent < 0)
        return Results.BadRequest(new { message = "Select an item. Discount must be 0-100 and tax must be non-negative." });

    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureProductDefaultsColumnsAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        int taxGroupId;
        await using (var tax = new SqlCommand(@"DECLARE @Id INT;
SELECT TOP 1 @Id=TaxGroupId FROM TaxGroups WHERE TaxPercent=@TaxPercent AND ISNULL(IsInclusive,0)=0 AND ISNULL(IsActive,1)=1 ORDER BY TaxGroupId;
IF @Id IS NULL
BEGIN
    INSERT INTO TaxGroups(TaxGroupName,TaxPercent,IsInclusive,IsActive) VALUES(CONCAT('TAX ',FORMAT(@TaxPercent,'0.##'),'%'),@TaxPercent,0,1);
    SET @Id=SCOPE_IDENTITY();
END
SELECT @Id;", con, tran))
        {
            tax.Parameters.AddWithValue("@TaxPercent", request.TaxPercent);
            taxGroupId = Convert.ToInt32(await tax.ExecuteScalarAsync());
        }
        await using (var upd = new SqlCommand(@"UPDATE Products SET ProductDiscountPercent=@DiscountPercent,TaxGroupId=@TaxGroupId WHERE ProductId=@ProductId;
IF @@ROWCOUNT=0 THROW 50001, 'Item not found.', 1;", con, tran))
        {
            upd.Parameters.AddWithValue("@ProductId", request.ProductId);
            upd.Parameters.AddWithValue("@DiscountPercent", request.DiscountPercent);
            upd.Parameters.AddWithValue("@TaxGroupId", taxGroupId);
            await upd.ExecuteNonQueryAsync();
        }
        await tran.CommitAsync();
        return Results.Ok(new { productId = request.ProductId, message = "Item tax and discount updated." });
    }
    catch(Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/tax-groups", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureProductDefaultsColumnsAsync(con);
    return Results.Ok(await SqlList.ReadAsync(con, "SELECT TaxGroupId,TaxGroupName,TaxPercent,IsInclusive,IsActive FROM TaxGroups ORDER BY TaxGroupName"));
});

app.MapPost("/api/tax-groups", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, TaxGroupUpsertRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!IsCompanySecurityAdmin(user)) return Results.BadRequest(new { message = "Company Super Admin permission is required for tax setup." });
    if (string.IsNullOrWhiteSpace(request.TaxGroupName) || request.TaxPercent < 0) return Results.BadRequest(new { message = "Tax name and non-negative tax % are required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureProductDefaultsColumnsAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"IF EXISTS(SELECT 1 FROM TaxGroups WHERE TaxGroupId=@Id)
BEGIN UPDATE TaxGroups SET TaxGroupName=@Name,TaxPercent=@Percent,IsInclusive=@Inclusive,IsActive=@Active WHERE TaxGroupId=@Id; SELECT @Id; END
ELSE BEGIN INSERT INTO TaxGroups(TaxGroupName,TaxPercent,IsInclusive,IsActive) OUTPUT INSERTED.TaxGroupId VALUES(@Name,@Percent,@Inclusive,@Active); END";
    cmd.Parameters.AddWithValue("@Id", request.TaxGroupId);
    cmd.Parameters.AddWithValue("@Name", request.TaxGroupName.Trim());
    cmd.Parameters.AddWithValue("@Percent", request.TaxPercent);
    cmd.Parameters.AddWithValue("@Inclusive", request.IsInclusive);
    cmd.Parameters.AddWithValue("@Active", request.IsActive);
    var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    return Results.Ok(new { taxGroupId = id, message = "Tax setup saved." });
});

app.MapGet("/api/posting-setup", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Ok(await SqlList.ReadSingleAsync(con, "SELECT TOP 1 * FROM PostingSetup WHERE SetupId=1"));
});

app.MapPut("/api/posting-setup", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, PostingSetupUpdateRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!IsCompanySecurityAdmin(user)) return Results.BadRequest(new { message = "Company Super Admin permission is required for posting setup." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"UPDATE PostingSetup SET CashAccount=@Cash,BankAccount=@Bank,ReceivableAccount=@Receivable,PayableAccount=@Payable,
InventoryAccount=@Inventory,SalesAccount=@Sales,CogsAccount=@Cogs,SalesReturnAccount=@SalesReturn,InputTaxAccount=@InputTax,
OutputTaxAccount=@OutputTax,OpeningBalanceAccount=@Opening,StockAdjustmentAccount=@StockAdjustment,CashierDiscountLimit=@DiscountLimit,
CostingMethod=@Costing,BlockNegativeStock=@BlockNegative,ModifiedAt=SYSUTCDATETIME() WHERE SetupId=1";
    cmd.Parameters.AddWithValue("@Cash", request.CashAccount); cmd.Parameters.AddWithValue("@Bank", request.BankAccount); cmd.Parameters.AddWithValue("@Receivable", request.ReceivableAccount); cmd.Parameters.AddWithValue("@Payable", request.PayableAccount);
    cmd.Parameters.AddWithValue("@Inventory", request.InventoryAccount); cmd.Parameters.AddWithValue("@Sales", request.SalesAccount); cmd.Parameters.AddWithValue("@Cogs", request.CogsAccount); cmd.Parameters.AddWithValue("@SalesReturn", request.SalesReturnAccount);
    cmd.Parameters.AddWithValue("@InputTax", request.InputTaxAccount); cmd.Parameters.AddWithValue("@OutputTax", request.OutputTaxAccount); cmd.Parameters.AddWithValue("@Opening", request.OpeningBalanceAccount); cmd.Parameters.AddWithValue("@StockAdjustment", request.StockAdjustmentAccount);
    cmd.Parameters.AddWithValue("@DiscountLimit", request.CashierDiscountLimit); cmd.Parameters.AddWithValue("@Costing", request.CostingMethod); cmd.Parameters.AddWithValue("@BlockNegative", request.BlockNegativeStock);
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { message = "Posting setup saved." });
});

app.MapPost("/api/posting-setup/opening-balances", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, OpeningBalancePostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!IsCompanySecurityAdmin(user)) return Results.BadRequest(new { message = "Company Super Admin permission is required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var no = await PosSql.NextNumberAsync(con, tran, "BACKUP");
        var setup = await GetPostingSetupAsync(con, tran);
        var total = request.Cash + request.Bank;
        if (request.Cash > 0) await InsertGlAsync(con, tran, setup["CashAccount"], request.PostingDate.Date, "Opening Balance", no, request.Cash, 0, "Opening cash", 0);
        if (request.Bank > 0) await InsertGlAsync(con, tran, setup["BankAccount"], request.PostingDate.Date, "Opening Balance", no, request.Bank, 0, "Opening bank", 0);
        if (total > 0) await InsertGlAsync(con, tran, setup["OpeningBalanceAccount"], request.PostingDate.Date, "Opening Balance", no, 0, total, "Opening equity", 0);
        await tran.CommitAsync();
        return Results.Ok(new { documentNo = no, message = "Opening balances posted." });
    }
    catch(Exception ex){ await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});

app.MapGet("/api/accounting/periods", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Ok(await SqlList.ReadAsync(con, "SELECT PeriodId,PeriodName,StartDate,EndDate,IsClosed,ClosedAt,ClosedBy FROM AccountingPeriods ORDER BY StartDate DESC"));
});

app.MapPost("/api/accounting/periods/close", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloseAccountingPeriodRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!IsCompanySecurityAdmin(user)) return Results.BadRequest(new { message = "Company Super Admin permission is required to close accounting period." });
    if (request.EndDate < request.StartDate) return Results.BadRequest(new { message = "End date cannot be before start date." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT INTO AccountingPeriods(PeriodName,StartDate,EndDate,IsClosed,ClosedAt,ClosedBy) OUTPUT INSERTED.PeriodId VALUES(@Name,@Start,@End,1,SYSUTCDATETIME(),@UserId)";
    cmd.Parameters.AddWithValue("@Name", string.IsNullOrWhiteSpace(request.PeriodName) ? $"Closed {request.StartDate:yyyy-MM-dd} to {request.EndDate:yyyy-MM-dd}" : request.PeriodName.Trim());
    cmd.Parameters.AddWithValue("@Start", request.StartDate.Date); cmd.Parameters.AddWithValue("@End", request.EndDate.Date); cmd.Parameters.AddWithValue("@UserId", user.UserId);
    return Results.Ok(new { periodId = Convert.ToInt32(await cmd.ExecuteScalarAsync()), message = "Accounting period closed." });
});

app.MapPost("/api/settings/backup", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, BackupRestoreService backups, BackupActionRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!IsCompanySecurityAdmin(user)) return Results.BadRequest(new { message = "Company Super Admin permission is required." });
    var result = await backups.BackupTenantDatabaseAsync(user, request.Remarks);
    return Results.Ok(result);
});

app.MapPost("/api/settings/restore", async (HttpContext http, AuthTokenService tokens, BackupRestoreService backups, RequestValidationService validator, DatabaseRestoreRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!IsCompanySecurityAdmin(user)) return Results.BadRequest(new { message = "Company Super Admin permission is required." });
    var validation = validator.ValidateRestore(request); if (!validation.Ok) return Results.BadRequest(new { message = validation.Message });
    var result = await backups.RestoreTenantDatabaseAsync(user, request.BackupReference, request.Remarks);
    return Results.Ok(result);
});

app.MapPost("/api/sales-invoices/post", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, SalesInvoicePostRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    if (!HasSessionPermission(user, "sales.postInvoice") && !HasSessionPermission(user, "sales.createInvoice"))
        return Results.Json(new { message = "Post Invoice permission is required." }, statusCode: StatusCodes.Status403Forbidden);
    user = await PosSessionHelper.EnsureTenantPosSessionAsync(db, user);
    if (request.CustomerId <= 0 || request.Lines.Count == 0) return Results.BadRequest(new { message = "Customer and invoice lines are required." });
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await DesktopIdempotency.EnsureSchemaAsync(con);
    await BankAccountService.EnsureSchemaAsync(con);
    await EnsureShiftManagementSchemaAsync(con);
    var applicationSource = ResolveApplicationSource(http, user);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
        if (!string.IsNullOrWhiteSpace(clientDocumentId))
        {
            var existingInvoice = await DesktopIdempotency.FindSalesInvoiceAsync(con, tran, clientDocumentId);
            if (existingInvoice != null)
            {
                await tran.CommitAsync();
                return Results.Ok(new
                {
                    salesInvoiceId = existingInvoice.SalesInvoiceId,
                    invoiceNo = existingInvoice.InvoiceNo,
                    grandTotal = existingInvoice.GrandTotal,
                    paidAmount = existingInvoice.PaidAmount,
                    balanceAmount = existingInvoice.BalanceAmount,
                    duplicate = true,
                    message = "Desktop sale was already posted. Existing InterNex document returned."
                });
            }
        }
        decimal subTotal=0, discount=0, tax=0, grand=0, costTotal=0;
        var computed = new List<(SalesInvoiceLinePostRequest Req, ProductForSale Product, CalculatedSaleLine Line)>();
        foreach(var lineReq in request.Lines)
        {
            var p = await PosSql.GetProductForSaleAsync(con, tran, lineReq.ProductId, user.StoreId) ?? throw new InvalidOperationException("Product not found.");
            if (p.StockOnHand < lineReq.Quantity) throw new InvalidOperationException($"Stock not available for {p.ProductName}.");
            var effective = new ProductForSale(p.ProductId,p.ProductName,p.ProductCode,p.Barcode,lineReq.UnitPrice <= 0 ? p.SalePrice : lineReq.UnitPrice,p.PurchasePrice,p.StockOnHand,lineReq.TaxPercent,p.TaxInclusive,p.DiscountAllowed);
            var calc = PosSql.CalculateLine(effective, lineReq.Quantity, lineReq.DiscountPercent);
            computed.Add((lineReq,p,calc)); subTotal += calc.Gross; discount += calc.DiscountAmount; tax += calc.TaxAmount; grand += calc.LineTotal; costTotal += calc.Quantity * calc.UnitCost;
        }
        var balance = Math.Max(0, grand - request.PaidAmount);
        var invoiceId = request.SalesInvoiceId;
        string invoiceNo;
        if (invoiceId > 0)
        {
            await using (var find = new SqlCommand("SELECT InvoiceNo FROM SalesInvoiceHeader WITH(UPDLOCK,HOLDLOCK) WHERE SalesInvoiceId=@Id AND StoreId=@StoreId AND Status='Open'", con, tran))
            {
                find.Parameters.AddWithValue("@Id", invoiceId);
                find.Parameters.AddWithValue("@StoreId", user.StoreId);
                var existingNo = await find.ExecuteScalarAsync();
                if (existingNo == null || existingNo == DBNull.Value) throw new InvalidOperationException("Open sales invoice draft was not found or has already been posted.");
                invoiceNo = Convert.ToString(existingNo) ?? string.Empty;
            }
            await using var update = new SqlCommand(@"UPDATE SalesInvoiceHeader SET CustomerId=@CustomerId,InvoiceDate=@Date,UserId=@UserId,SubTotal=@SubTotal,DiscountAmount=@Discount,TaxAmount=@Tax,GrandTotal=@Grand,PaidAmount=@Paid,BalanceAmount=@Balance,Status='Posted',Remarks=@Remarks,ApplicationSource=@ApplicationSource,PostedAt=SYSUTCDATETIME() WHERE SalesInvoiceId=@Id AND StoreId=@StoreId AND Status='Open';
DELETE FROM SalesInvoiceLines WHERE SalesInvoiceId=@Id;", con, tran);
            update.Parameters.AddWithValue("@Id", invoiceId); update.Parameters.AddWithValue("@CustomerId", request.CustomerId); update.Parameters.AddWithValue("@Date", request.InvoiceDate.Date); update.Parameters.AddWithValue("@StoreId", user.StoreId); update.Parameters.AddWithValue("@UserId", user.UserId); update.Parameters.AddWithValue("@SubTotal", subTotal); update.Parameters.AddWithValue("@Discount", discount); update.Parameters.AddWithValue("@Tax", tax); update.Parameters.AddWithValue("@Grand", grand); update.Parameters.AddWithValue("@Paid", request.PaidAmount); update.Parameters.AddWithValue("@Balance", balance); update.Parameters.AddWithValue("@Remarks", request.Remarks ?? ""); update.Parameters.AddWithValue("@ApplicationSource", applicationSource);
            await update.ExecuteNonQueryAsync();
        }
        else
        {
            invoiceNo = await PosSql.NextNumberAsync(con, tran, "SALES_INVOICE");
            await using var insert = new SqlCommand(@"INSERT INTO SalesInvoiceHeader(InvoiceNo,CustomerId,InvoiceDate,StoreId,BranchCode,UserId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,PaidAmount,BalanceAmount,Status,Remarks,ExternalClientDocumentId,ApplicationSource)
OUTPUT INSERTED.SalesInvoiceId VALUES(@No,@CustomerId,@Date,@StoreId,@BranchCode,@UserId,@SubTotal,@Discount,@Tax,@Grand,@Paid,@Balance,'Posted',@Remarks,@ClientDocumentId,@ApplicationSource)", con, tran);
            insert.Parameters.AddWithValue("@No", invoiceNo); insert.Parameters.AddWithValue("@CustomerId", request.CustomerId); insert.Parameters.AddWithValue("@Date", request.InvoiceDate.Date); insert.Parameters.AddWithValue("@StoreId", user.StoreId); insert.Parameters.AddWithValue("@BranchCode", user.BranchCode); insert.Parameters.AddWithValue("@UserId", user.UserId); insert.Parameters.AddWithValue("@SubTotal", subTotal); insert.Parameters.AddWithValue("@Discount", discount); insert.Parameters.AddWithValue("@Tax", tax); insert.Parameters.AddWithValue("@Grand", grand); insert.Parameters.AddWithValue("@Paid", request.PaidAmount); insert.Parameters.AddWithValue("@Balance", balance); insert.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
            insert.Parameters.AddWithValue("@ClientDocumentId", string.IsNullOrWhiteSpace(clientDocumentId) ? DBNull.Value : clientDocumentId);
            insert.Parameters.AddWithValue("@ApplicationSource", applicationSource);
            invoiceId = Convert.ToInt32(await insert.ExecuteScalarAsync());
        }
        foreach(var item in computed)
        {
            var l = item.Line;
            await using(var cmd = new SqlCommand(@"INSERT INTO SalesInvoiceLines(SalesInvoiceId,ProductId,ProductName,Quantity,UnitPrice,DiscountPercent,DiscountAmount,TaxPercent,TaxAmount,LineTotal,UnitCost,TaxInclusive)
VALUES(@Id,@ProductId,@Name,@Qty,@Price,@DiscPct,@Disc,@TaxPct,@Tax,@Total,@Cost,@TaxInclusive);
UPDATE Products SET StockOnHand=StockOnHand-@Qty WHERE ProductId=@ProductId;
UPDATE StockByStore SET Quantity=Quantity-@Qty WHERE StoreId=@StoreId AND ProductId=@ProductId;
INSERT INTO InventoryLedger(StoreId,BranchCode,ProductId,MovementType,SourceDocumentNo,QuantityIn,QuantityOut,UnitCost,Remarks,CreatedBy) VALUES(@StoreId,@BranchCode,@ProductId,'Sales Invoice',@No,0,@Qty,@Cost,'Posted sales invoice',@UserId);", con, tran))
            {
                cmd.Parameters.AddWithValue("@Id", invoiceId); cmd.Parameters.AddWithValue("@ProductId", l.ProductId); cmd.Parameters.AddWithValue("@Name", l.ProductName); cmd.Parameters.AddWithValue("@Qty", l.Quantity); cmd.Parameters.AddWithValue("@Price", l.UnitPrice); cmd.Parameters.AddWithValue("@DiscPct", l.DiscountPercent); cmd.Parameters.AddWithValue("@Disc", l.DiscountAmount); cmd.Parameters.AddWithValue("@TaxPct", l.TaxPercent); cmd.Parameters.AddWithValue("@Tax", l.TaxAmount); cmd.Parameters.AddWithValue("@Total", l.LineTotal); cmd.Parameters.AddWithValue("@Cost", l.UnitCost); cmd.Parameters.AddWithValue("@TaxInclusive", l.TaxInclusive); cmd.Parameters.AddWithValue("@StoreId", user.StoreId); cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode); cmd.Parameters.AddWithValue("@No", invoiceNo); cmd.Parameters.AddWithValue("@UserId", user.UserId);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await using(var bal = new SqlCommand(@"UPDATE Customers SET CurrentBalance=CurrentBalance+@Grand WHERE CustomerId=@CustomerId;
DECLARE @Bal DECIMAL(18,2)=(SELECT CurrentBalance FROM Customers WHERE CustomerId=@CustomerId);
INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId) VALUES(@CustomerId,@Date,'Sales Invoice',@No,@Grand,0,@Bal,'Posted sales invoice',@Id);", con, tran))
        {
            bal.Parameters.AddWithValue("@CustomerId", request.CustomerId); bal.Parameters.AddWithValue("@Date", request.InvoiceDate.Date); bal.Parameters.AddWithValue("@No", invoiceNo); bal.Parameters.AddWithValue("@Grand", grand); bal.Parameters.AddWithValue("@Id", invoiceId); await bal.ExecuteNonQueryAsync();
        }
        var setup = await GetPostingSetupAsync(con, tran);
        await InsertGlAsync(con, tran, setup["ReceivableAccount"], request.InvoiceDate.Date, "Sales Invoice", invoiceNo, grand, 0, "Accounts receivable", invoiceId, user.BranchCode);
        await InsertGlAsync(con, tran, setup["SalesAccount"], request.InvoiceDate.Date, "Sales Invoice", invoiceNo, 0, grand-tax, "Sales revenue", invoiceId, user.BranchCode);
        await InsertGlAsync(con, tran, setup["OutputTaxAccount"], request.InvoiceDate.Date, "Sales Invoice", invoiceNo, 0, tax, "Output tax", invoiceId, user.BranchCode);
        await InsertGlAsync(con, tran, setup["CogsAccount"], request.InvoiceDate.Date, "Sales Invoice", invoiceNo, costTotal, 0, "COGS", invoiceId, user.BranchCode);
        await InsertGlAsync(con, tran, setup["InventoryAccount"], request.InvoiceDate.Date, "Sales Invoice", invoiceNo, 0, costTotal, "Inventory issued", invoiceId, user.BranchCode);
        var receiveNow = Math.Min(request.PaidAmount, grand);
        if (receiveNow > 0)
        {
            var receiveMethod = string.IsNullOrWhiteSpace(request.ReceivePaymentMethod) ? "Cash" : request.ReceivePaymentMethod.Trim();
            await PostCustomerReceiptAsync(con, tran, user, new CustomerPaymentPostRequest
            {
                CustomerId = request.CustomerId,
                PaymentDate = request.InvoiceDate.Date,
                Amount = receiveNow,
                PaymentMethod = receiveMethod,
                BankAccountId = request.BankAccountId,
                SalesInvoiceId = invoiceId,
                ReferenceNo = invoiceNo,
                Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? "Receive now" : request.Remarks
            }, applyInvoiceBalance: false);
        }
        await tran.CommitAsync();
        return Results.Ok(new { salesInvoiceId=invoiceId, invoiceNo, grandTotal=grand, paidAmount=request.PaidAmount, balanceAmount=balance, message="Sales invoice posted." });
    }
    catch(Exception ex){ await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});

app.MapGet("/api/sales-invoices/{id:int}/lines", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id, string? documentType) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    var posSale = string.Equals(documentType, "PosSale", StringComparison.OrdinalIgnoreCase);
    cmd.CommandText = posSale
        ? @"SELECT l.SaleLineId SalesInvoiceLineId,l.ProductId,ISNULL(p.ProductCode,'') ProductCode,ISNULL(NULLIF(LTRIM(RTRIM(l.ProductName)),''),ISNULL(p.ProductName,'')) ProductName,l.Quantity,l.UnitPrice,l.DiscountPercent,l.DiscountAmount,l.TaxPercent,l.TaxAmount,l.LineTotal,l.UnitCost,ISNULL(l.TaxInclusive,0) TaxInclusive,ISNULL(sb.Quantity,ISNULL(p.StockOnHand,0)) StockOnHand
FROM SalesLines l
INNER JOIN SalesHeader h ON h.SaleId=l.SaleId
LEFT JOIN Products p ON p.ProductId=l.ProductId
LEFT JOIN StockByStore sb ON sb.ProductId=l.ProductId AND sb.StoreId=h.StoreId
WHERE l.SaleId=@Id AND h.StoreId=@StoreId
ORDER BY l.SaleLineId"
        : @"SELECT l.SalesInvoiceLineId,l.ProductId,p.ProductCode,l.ProductName,l.Quantity,l.UnitPrice,l.DiscountPercent,l.DiscountAmount,l.TaxPercent,l.TaxAmount,l.LineTotal,l.UnitCost,ISNULL(l.TaxInclusive,0) TaxInclusive,ISNULL(sb.Quantity,p.StockOnHand) StockOnHand
FROM SalesInvoiceLines l
INNER JOIN SalesInvoiceHeader h ON h.SalesInvoiceId=l.SalesInvoiceId
INNER JOIN Products p ON p.ProductId=l.ProductId
LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=h.StoreId
WHERE l.SalesInvoiceId=@Id AND h.StoreId=@StoreId
ORDER BY l.SalesInvoiceLineId";
    cmd.Parameters.AddWithValue("@Id", id);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/purchase-invoices/{id:int}/lines", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT l.PurchaseInvoiceLineId,l.ProductId,p.ProductCode,l.ProductName,l.Quantity,l.UnitCost,l.TaxPercent,l.TaxAmount,l.LineTotal,ISNULL(l.TaxInclusive,0) TaxInclusive
FROM PurchaseInvoiceLines l
INNER JOIN PurchaseInvoiceHeader h ON h.PurchaseInvoiceId=l.PurchaseInvoiceId
INNER JOIN Products p ON p.ProductId=l.ProductId
WHERE l.PurchaseInvoiceId=@Id AND h.StoreId=@StoreId
ORDER BY l.PurchaseInvoiceLineId";
    cmd.Parameters.AddWithValue("@Id", id);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapPost("/api/sales-quotes", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, SaveSalesDocumentRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName); await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try{ var no=await PosSql.NextNumberAsync(con,tran,"SALES_QUOTE"); await using var cmd=new SqlCommand("INSERT INTO SalesQuotes(QuoteNo,CustomerId,QuoteDate,TotalAmount,Payload,CreatedBy) OUTPUT INSERTED.QuoteId VALUES(@No,@CustomerId,@Date,@Total,@Payload,@UserId)",con,tran); cmd.Parameters.AddWithValue("@No",no); cmd.Parameters.AddWithValue("@CustomerId",request.CustomerId); cmd.Parameters.AddWithValue("@Date",request.DocumentDate.Date); cmd.Parameters.AddWithValue("@Total",request.TotalAmount); cmd.Parameters.AddWithValue("@Payload",JsonSerializer.Serialize(request.Payload ?? request)); cmd.Parameters.AddWithValue("@UserId",user.UserId); var id=Convert.ToInt64(await cmd.ExecuteScalarAsync()); await tran.CommitAsync(); return Results.Ok(new{quoteId=id,quoteNo=no,message="Quotation saved."}); } catch(Exception ex){ await tran.RollbackAsync(); return Results.BadRequest(new{message=ex.Message}); }
});

app.MapPost("/api/sales-orders", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, SaveSalesDocumentRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName); await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try{ var no=await PosSql.NextNumberAsync(con,tran,"SALES_ORDER"); await using var cmd=new SqlCommand("INSERT INTO SalesOrders(OrderNo,CustomerId,OrderDate,TotalAmount,Payload,CreatedBy) OUTPUT INSERTED.OrderId VALUES(@No,@CustomerId,@Date,@Total,@Payload,@UserId)",con,tran); cmd.Parameters.AddWithValue("@No",no); cmd.Parameters.AddWithValue("@CustomerId",request.CustomerId); cmd.Parameters.AddWithValue("@Date",request.DocumentDate.Date); cmd.Parameters.AddWithValue("@Total",request.TotalAmount); cmd.Parameters.AddWithValue("@Payload",JsonSerializer.Serialize(request.Payload ?? request)); cmd.Parameters.AddWithValue("@UserId",user.UserId); var id=Convert.ToInt64(await cmd.ExecuteScalarAsync()); await tran.CommitAsync(); return Results.Ok(new{orderId=id,orderNo=no,message="Sales order saved."}); } catch(Exception ex){ await tran.RollbackAsync(); return Results.BadRequest(new{message=ex.Message}); }
});

app.MapPost("/api/hold-sales", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, HoldSaleRequest request) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    await EnsureTenantBranchSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        var no = await PosSql.NextNumberAsync(con, tran, "HOLD");
        var shiftId = await PosSql.EnsureOpenShiftAsync(con, tran, user);
        var payload = request.Payload != null
            ? JsonSerializer.Serialize(request.Payload)
            : JsonSerializer.Serialize(request);
        var subTotal = request.SubTotal;
        var discount = request.DiscountAmount;
        var tax = request.TaxAmount;
        var grand = request.GrandTotal;
        if (grand <= 0 && request.Lines.Count > 0)
        {
            // Best-effort totals from lines when client omitted them.
            foreach (var line in request.Lines)
            {
                var gross = line.Quantity * (line.UnitPrice ?? 0m);
                var lineDisc = gross * (line.DiscountPercent / 100m);
                subTotal += gross;
                discount += lineDisc;
                grand += gross - lineDisc;
            }
        }
        await using var cmd = new SqlCommand(@"INSERT INTO HoldSalesHeader(HoldNo,StoreId,BranchCode,TerminalId,ShiftId,UserId,CustomerId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,Remarks,Status)
OUTPUT INSERTED.HoldId VALUES(@No,@StoreId,@BranchCode,(SELECT TOP 1 TerminalId FROM Terminals WHERE StoreId=@StoreId AND IsActive=1 ORDER BY TerminalId),@ShiftId,@UserId,@CustomerId,@SubTotal,@Discount,@Tax,@Grand,@Remarks,'Hold')", con, tran);
        cmd.Parameters.AddWithValue("@No", no);
        cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
        cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
        cmd.Parameters.AddWithValue("@ShiftId", shiftId);
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        cmd.Parameters.AddWithValue("@CustomerId", request.CustomerId);
        cmd.Parameters.AddWithValue("@SubTotal", subTotal);
        cmd.Parameters.AddWithValue("@Discount", discount);
        cmd.Parameters.AddWithValue("@Tax", tax);
        cmd.Parameters.AddWithValue("@Grand", grand);
        cmd.Parameters.AddWithValue("@Remarks", payload);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        await tran.CommitAsync();
        return Results.Ok(new { holdId = id, holdNo = no, message = "Sale held." });
    }
    catch(Exception ex){ await tran.RollbackAsync(); return Results.BadRequest(new { message = ex.Message }); }
});

app.MapGet("/api/hold-sales", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    await EnsureTenantBranchSchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT TOP 100 HoldId,HoldNo,HoldDate,CustomerId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,Remarks,Status FROM HoldSalesHeader WHERE StoreId=@StoreId AND ISNULL(Status,'Hold')='Hold' ORDER BY HoldId DESC";
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    return Results.Ok(await SqlList.ReadAsync(cmd));
});

app.MapGet("/api/hold-sales/{id:int}", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantBranchSchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT TOP 1 HoldId,HoldNo,HoldDate,CustomerId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,Remarks,Status FROM HoldSalesHeader WHERE HoldId=@Id AND StoreId=@StoreId";
    cmd.Parameters.AddWithValue("@Id", id);
    cmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var rows = await SqlList.ReadAsync(cmd);
    var row = rows.FirstOrDefault();
    if (row == null) return Results.NotFound(new { message = "Hold sale was not found." });
    object? payload = null;
    try
    {
        var raw = Convert.ToString(row.TryGetValue("Remarks", out var r) ? r : row.TryGetValue("remarks", out var r2) ? r2 : null) ?? "";
        if (!string.IsNullOrWhiteSpace(raw)) payload = JsonSerializer.Deserialize<object>(raw);
    }
    catch { /* leave null */ }
    return Results.Ok(new { hold = row, payload });
});

app.MapPost("/api/hold-sales/{id:int}/retrieve", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int id) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureTenantBranchSchemaAsync(con);
    await using var tran = (SqlTransaction)await con.BeginTransactionAsync();
    try
    {
        await using var find = new SqlCommand("SELECT TOP 1 HoldId,HoldNo,HoldDate,CustomerId,SubTotal,DiscountAmount,TaxAmount,GrandTotal,Remarks,Status FROM HoldSalesHeader WITH (UPDLOCK, ROWLOCK) WHERE HoldId=@Id AND StoreId=@StoreId", con, tran);
        find.Parameters.AddWithValue("@Id", id);
        find.Parameters.AddWithValue("@StoreId", user.StoreId);
        await using var reader = await find.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            await reader.CloseAsync();
            await tran.RollbackAsync();
            return Results.NotFound(new { message = "Hold sale was not found." });
        }
        var status = Convert.ToString(reader["Status"]) ?? "";
        if (!string.Equals(status, "Hold", StringComparison.OrdinalIgnoreCase))
        {
            await reader.CloseAsync();
            await tran.RollbackAsync();
            return Results.BadRequest(new { message = "This hold was already retrieved or cancelled." });
        }
        var holdNo = Convert.ToString(reader["HoldNo"]);
        var customerId = Convert.ToInt32(reader["CustomerId"]);
        var remarks = Convert.ToString(reader["Remarks"]) ?? "";
        var subTotal = Convert.ToDecimal(reader["SubTotal"]);
        var discount = Convert.ToDecimal(reader["DiscountAmount"]);
        var tax = Convert.ToDecimal(reader["TaxAmount"]);
        var grand = Convert.ToDecimal(reader["GrandTotal"]);
        await reader.CloseAsync();

        await using var upd = new SqlCommand("UPDATE HoldSalesHeader SET Status='Retrieved' WHERE HoldId=@Id AND Status='Hold'", con, tran);
        upd.Parameters.AddWithValue("@Id", id);
        if (await upd.ExecuteNonQueryAsync() != 1)
        {
            await tran.RollbackAsync();
            return Results.BadRequest(new { message = "This hold was already retrieved." });
        }
        await tran.CommitAsync();

        object? payload = null;
        try { if (!string.IsNullOrWhiteSpace(remarks)) payload = JsonSerializer.Deserialize<object>(remarks); } catch { /* ignore */ }
        return Results.Ok(new
        {
            holdId = id,
            holdNo,
            customerId,
            subTotal,
            discountAmount = discount,
            taxAmount = tax,
            grandTotal = grand,
            payload,
            message = "Hold retrieved."
        });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/dashboard/analytics", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, ExpenseManagementService expenseService, int? months) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    var monthCount = Math.Clamp(months ?? 12, 1, 24);
    var periodTo = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
    var periodFrom = periodTo.AddMonths(-monthCount);
    var previousFrom = periodFrom.AddMonths(-monthCount);

    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await expenseService.EnsureSchemaAsync(con);
    await EnsureCurrenciesSchemaAsync(con);
    var currency = await ReadBaseCurrencyAsync(con);

    await using var summaryCmd = con.CreateCommand();
    summaryCmd.CommandText = @"
WITH SalesDocuments AS (
    SELECT CAST(SaleDate AS DATE) PostingDate,GrandTotal,DiscountAmount,TaxAmount,
           CASE WHEN PaidAmount>GrandTotal THEN GrandTotal ELSE PaidAmount END PaidAmount
    FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND SaleDate>=@PreviousFrom AND SaleDate<@PeriodTo
    UNION ALL
    SELECT InvoiceDate,GrandTotal,DiscountAmount,TaxAmount,PaidAmount
    FROM SalesInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=@PreviousFrom AND InvoiceDate<@PeriodTo
),
PurchaseDocuments AS (
    SELECT InvoiceDate PostingDate,GrandTotal,PaidAmount
    FROM PurchaseInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=@PreviousFrom AND InvoiceDate<@PeriodTo
),
GrossProfitLines AS (
    SELECT CAST(h.SaleDate AS DATE) PostingDate,(l.LineTotal-l.TaxAmount-(l.Quantity*l.UnitCost)) Amount
    FROM SalesLines l INNER JOIN SalesHeader h ON h.SaleId=l.SaleId
    WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.SaleDate>=@PreviousFrom AND h.SaleDate<@PeriodTo
    UNION ALL
    SELECT h.InvoiceDate,(l.LineTotal-l.TaxAmount-(l.Quantity*l.UnitCost))
    FROM SalesInvoiceLines l INNER JOIN SalesInvoiceHeader h ON h.SalesInvoiceId=l.SalesInvoiceId
    WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.InvoiceDate>=@PreviousFrom AND h.InvoiceDate<@PeriodTo
),
ReturnProfitLines AS (
    SELECT CAST(h.ReturnDate AS DATE) PostingDate,-((l.RefundAmount-l.TaxAmount)-(l.ReturnQuantity*l.UnitCost)) Amount
    FROM ReturnLines l INNER JOIN ReturnHeader h ON h.ReturnId=l.ReturnId
    WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.ReturnDate>=@PreviousFrom AND h.ReturnDate<@PeriodTo
),
ExpenseDocuments AS (
    SELECT ExpenseDate PostingDate,Amount FROM Expenses
    WHERE IsDeleted=0 AND StoreId=@StoreId AND ExpenseDate>=@PreviousFrom AND ExpenseDate<@PeriodTo
),
ReturnDocuments AS (
    SELECT CAST(ReturnDate AS DATE) PostingDate,RefundAmount FROM ReturnHeader
    WHERE StoreId=@StoreId AND Status='Posted' AND ReturnDate>=@PreviousFrom AND ReturnDate<@PeriodTo
)
SELECT
    ISNULL((SELECT SUM(GrandTotal) FROM SalesDocuments WHERE PostingDate>=@PeriodFrom),0) GrossSales,
    ISNULL((SELECT SUM(GrandTotal) FROM SalesDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousGrossSales,
    ISNULL((SELECT SUM(GrandTotal) FROM SalesDocuments WHERE PostingDate>=@PeriodFrom),0)-ISNULL((SELECT SUM(RefundAmount) FROM ReturnDocuments WHERE PostingDate>=@PeriodFrom),0) TotalSales,
    ISNULL((SELECT SUM(GrandTotal) FROM SalesDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0)-ISNULL((SELECT SUM(RefundAmount) FROM ReturnDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousSales,
    ISNULL((SELECT SUM(GrandTotal) FROM PurchaseDocuments WHERE PostingDate>=@PeriodFrom),0) TotalPurchases,
    ISNULL((SELECT SUM(GrandTotal) FROM PurchaseDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousPurchases,
    ISNULL((SELECT SUM(Amount) FROM GrossProfitLines WHERE PostingDate>=@PeriodFrom),0)+ISNULL((SELECT SUM(Amount) FROM ReturnProfitLines WHERE PostingDate>=@PeriodFrom),0) GrossProfit,
    ISNULL((SELECT SUM(Amount) FROM GrossProfitLines WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0)+ISNULL((SELECT SUM(Amount) FROM ReturnProfitLines WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousGrossProfit,
    ISNULL((SELECT SUM(Amount) FROM ExpenseDocuments WHERE PostingDate>=@PeriodFrom),0) TotalExpenses,
    ISNULL((SELECT SUM(Amount) FROM ExpenseDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousExpenses,
    ISNULL((SELECT SUM(PaidAmount) FROM SalesDocuments WHERE PostingDate>=@PeriodFrom),0) CashInflow,
    ISNULL((SELECT SUM(PaidAmount) FROM SalesDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousCashInflow,
    ISNULL((SELECT SUM(PaidAmount) FROM PurchaseDocuments WHERE PostingDate>=@PeriodFrom),0)+ISNULL((SELECT SUM(Amount) FROM ExpenseDocuments WHERE PostingDate>=@PeriodFrom),0)+ISNULL((SELECT SUM(RefundAmount) FROM ReturnDocuments WHERE PostingDate>=@PeriodFrom),0) CashOutflow,
    ISNULL((SELECT SUM(PaidAmount) FROM PurchaseDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0)+ISNULL((SELECT SUM(Amount) FROM ExpenseDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0)+ISNULL((SELECT SUM(RefundAmount) FROM ReturnDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousCashOutflow,
    ISNULL((SELECT COUNT(1) FROM SalesDocuments WHERE PostingDate>=@PeriodFrom),0) SalesDocuments,
    ISNULL((SELECT COUNT(1) FROM SalesDocuments WHERE PostingDate>=@PreviousFrom AND PostingDate<@PeriodFrom),0) PreviousSalesDocuments,
    ISNULL((SELECT COUNT(1) FROM PurchaseDocuments WHERE PostingDate>=@PeriodFrom),0) PurchaseDocuments,
    ISNULL((SELECT SUM(DiscountAmount) FROM SalesDocuments WHERE PostingDate>=@PeriodFrom),0) SalesDiscounts,
    ISNULL((SELECT SUM(TaxAmount) FROM SalesDocuments WHERE PostingDate>=@PeriodFrom),0) SalesTax,
    ISNULL((SELECT SUM(RefundAmount) FROM ReturnDocuments WHERE PostingDate>=@PeriodFrom),0) ReturnsAmount,
    ISNULL((SELECT COUNT(1) FROM ReturnDocuments WHERE PostingDate>=@PeriodFrom),0) ReturnsCount,
    ISNULL((SELECT SUM(CASE WHEN CurrentBalance>0 THEN CurrentBalance ELSE 0 END) FROM Customers WHERE IsActive=1),0) Receivables,
    ISNULL((SELECT SUM(CASE WHEN CurrentBalance>0 THEN CurrentBalance ELSE 0 END) FROM Vendors WHERE IsActive=1),0) Payables,
    ISNULL((SELECT SUM(ISNULL(sb.Quantity,p.StockOnHand)*ISNULL(NULLIF(sb.AverageCost,0),p.PurchasePrice)) FROM Products p LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId WHERE p.IsActive=1),0) InventoryValue,
    ISNULL((SELECT COUNT(1) FROM Products p LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId WHERE p.IsActive=1 AND ISNULL(sb.Quantity,p.StockOnHand)<=p.ReorderLevel),0) LowStockCount,
    ISNULL((SELECT COUNT(1) FROM Products WHERE IsActive=1),0) ActiveProducts,
    ISNULL((SELECT COUNT(1) FROM Customers WHERE IsActive=1),0) ActiveCustomers,
    ISNULL((SELECT COUNT(1) FROM Vendors WHERE IsActive=1),0) ActiveVendors";
    summaryCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    summaryCmd.Parameters.AddWithValue("@PreviousFrom", previousFrom.Date);
    summaryCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    summaryCmd.Parameters.AddWithValue("@PeriodTo", periodTo.Date);
    var summary = await SqlList.ReadSingleAsync(summaryCmd);
    decimal Metric(string name) => Convert.ToDecimal(summary.GetValueOrDefault(name) ?? 0);
    summary["NetProfit"] = Metric("GrossProfit") - Metric("TotalExpenses");
    summary["PreviousNetProfit"] = Metric("PreviousGrossProfit") - Metric("PreviousExpenses");
    summary["NetCashFlow"] = Metric("CashInflow") - Metric("CashOutflow");
    summary["AverageSale"] = Metric("SalesDocuments") <= 0 ? 0 : Metric("TotalSales") / Metric("SalesDocuments");
    summary["GrossMarginPercent"] = Metric("TotalSales") == 0 ? 0 : Metric("GrossProfit") / Metric("TotalSales") * 100;

    await using var monthlyCmd = con.CreateCommand();
    monthlyCmd.CommandText = @"
WITH n AS (SELECT 0 n UNION ALL SELECT n+1 FROM n WHERE n+1<@Months),
months AS (SELECT DATEADD(MONTH,n,@PeriodFrom) MonthStart FROM n)
SELECT FORMAT(MonthStart,'MMM yy') MonthLabel,MonthStart,
ISNULL((SELECT SUM(GrandTotal) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND SaleDate>=MonthStart AND SaleDate<DATEADD(MONTH,1,MonthStart)),0)+
ISNULL((SELECT SUM(GrandTotal) FROM SalesInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=MonthStart AND InvoiceDate<DATEADD(MONTH,1,MonthStart)),0)-
ISNULL((SELECT SUM(RefundAmount) FROM ReturnHeader WHERE StoreId=@StoreId AND Status='Posted' AND ReturnDate>=MonthStart AND ReturnDate<DATEADD(MONTH,1,MonthStart)),0) SalesAmount,
ISNULL((SELECT SUM(GrandTotal) FROM PurchaseInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=MonthStart AND InvoiceDate<DATEADD(MONTH,1,MonthStart)),0) PurchaseAmount,
ISNULL((SELECT SUM(l.LineTotal-l.TaxAmount-(l.Quantity*l.UnitCost)) FROM SalesLines l JOIN SalesHeader h ON h.SaleId=l.SaleId WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.SaleDate>=MonthStart AND h.SaleDate<DATEADD(MONTH,1,MonthStart)),0)+
ISNULL((SELECT SUM(l.LineTotal-l.TaxAmount-(l.Quantity*l.UnitCost)) FROM SalesInvoiceLines l JOIN SalesInvoiceHeader h ON h.SalesInvoiceId=l.SalesInvoiceId WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.InvoiceDate>=MonthStart AND h.InvoiceDate<DATEADD(MONTH,1,MonthStart)),0)-
ISNULL((SELECT SUM((l.RefundAmount-l.TaxAmount)-(l.ReturnQuantity*l.UnitCost)) FROM ReturnLines l JOIN ReturnHeader h ON h.ReturnId=l.ReturnId WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.ReturnDate>=MonthStart AND h.ReturnDate<DATEADD(MONTH,1,MonthStart)),0) GrossProfitAmount,
ISNULL((SELECT SUM(Amount) FROM Expenses WHERE IsDeleted=0 AND StoreId=@StoreId AND ExpenseDate>=MonthStart AND ExpenseDate<DATEADD(MONTH,1,MonthStart)),0) ExpenseAmount,
ISNULL((SELECT SUM(CASE WHEN PaidAmount>GrandTotal THEN GrandTotal ELSE PaidAmount END) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND SaleDate>=MonthStart AND SaleDate<DATEADD(MONTH,1,MonthStart)),0)+
ISNULL((SELECT SUM(PaidAmount) FROM SalesInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=MonthStart AND InvoiceDate<DATEADD(MONTH,1,MonthStart)),0) CashInAmount,
ISNULL((SELECT SUM(PaidAmount) FROM PurchaseInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=MonthStart AND InvoiceDate<DATEADD(MONTH,1,MonthStart)),0)+
ISNULL((SELECT SUM(Amount) FROM Expenses WHERE IsDeleted=0 AND StoreId=@StoreId AND ExpenseDate>=MonthStart AND ExpenseDate<DATEADD(MONTH,1,MonthStart)),0)+
ISNULL((SELECT SUM(RefundAmount) FROM ReturnHeader WHERE StoreId=@StoreId AND Status='Posted' AND ReturnDate>=MonthStart AND ReturnDate<DATEADD(MONTH,1,MonthStart)),0) CashOutAmount,
ISNULL((SELECT COUNT(1) FROM SalesHeader WHERE StoreId=@StoreId AND Status='Posted' AND SaleDate>=MonthStart AND SaleDate<DATEADD(MONTH,1,MonthStart)),0)+
ISNULL((SELECT COUNT(1) FROM SalesInvoiceHeader WHERE StoreId=@StoreId AND Status='Posted' AND InvoiceDate>=MonthStart AND InvoiceDate<DATEADD(MONTH,1,MonthStart)),0) DocumentCount
FROM months ORDER BY MonthStart OPTION (MAXRECURSION 30)";
    monthlyCmd.Parameters.AddWithValue("@Months", monthCount);
    monthlyCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    monthlyCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var monthly = await SqlList.ReadAsync(monthlyCmd);
    foreach (var row in monthly)
    {
        var gross = Convert.ToDecimal(row.GetValueOrDefault("GrossProfitAmount") ?? 0);
        var expense = Convert.ToDecimal(row.GetValueOrDefault("ExpenseAmount") ?? 0);
        row["NetProfitAmount"] = gross - expense;
    }

    var dashboardNowUtc = DateTime.UtcNow;
    var currentHourUtc = new DateTime(dashboardNowUtc.Year, dashboardNowUtc.Month, dashboardNowUtc.Day, dashboardNowUtc.Hour, 0, 0, DateTimeKind.Utc);
    var hourlyFromUtc = currentHourUtc.AddHours(-23);
    var hourlyToUtc = currentHourUtc.AddHours(1);
    await using var hourlySalesCmd = con.CreateCommand();
    hourlySalesCmd.CommandText = @"
WITH Hours AS (
    SELECT @HourlyFrom HourStart
    UNION ALL
    SELECT DATEADD(HOUR,1,HourStart) FROM Hours WHERE DATEADD(HOUR,1,HourStart)<@HourlyTo
),
SalesEvents AS (
    SELECT DATEADD(HOUR,DATEDIFF(HOUR,0,SaleDate),0) HourStart,
           CAST(GrandTotal AS DECIMAL(18,2)) Amount,1 DocumentCount,0 ReturnCount
    FROM SalesHeader
    WHERE StoreId=@StoreId AND Status='Posted' AND SaleDate>=@HourlyFrom AND SaleDate<@HourlyTo
    UNION ALL
    SELECT DATEADD(HOUR,DATEDIFF(HOUR,0,PostedAt),0),
           CAST(GrandTotal AS DECIMAL(18,2)),1,0
    FROM SalesInvoiceHeader
    WHERE StoreId=@StoreId AND Status='Posted' AND PostedAt>=@HourlyFrom AND PostedAt<@HourlyTo
    UNION ALL
    SELECT DATEADD(HOUR,DATEDIFF(HOUR,0,ReturnDate),0),
           CAST(-RefundAmount AS DECIMAL(18,2)),0,1
    FROM ReturnHeader
    WHERE StoreId=@StoreId AND Status='Posted' AND ReturnDate>=@HourlyFrom AND ReturnDate<@HourlyTo
)
SELECT CONCAT(CONVERT(VARCHAR(19),h.HourStart,126),'Z') HourStartUtc,
       ISNULL(SUM(e.Amount),0) SalesAmount,
       ISNULL(SUM(CASE WHEN e.Amount>0 THEN e.Amount ELSE 0 END),0) GrossSalesAmount,
       ABS(ISNULL(SUM(CASE WHEN e.Amount<0 THEN e.Amount ELSE 0 END),0)) ReturnsAmount,
       ISNULL(SUM(e.DocumentCount),0) DocumentCount,
       ISNULL(SUM(e.ReturnCount),0) ReturnCount
FROM Hours h
LEFT JOIN SalesEvents e ON e.HourStart=h.HourStart
GROUP BY h.HourStart
ORDER BY h.HourStart
OPTION (MAXRECURSION 24)";
    hourlySalesCmd.Parameters.AddWithValue("@HourlyFrom", hourlyFromUtc);
    hourlySalesCmd.Parameters.AddWithValue("@HourlyTo", hourlyToUtc);
    hourlySalesCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var hourlySales = await SqlList.ReadAsync(hourlySalesCmd);

    await using var topProductsCmd = con.CreateCommand();
    topProductsCmd.CommandText = @"
WITH ProductSales AS (
 SELECT l.ProductId,l.ProductName,l.Quantity,l.LineTotal Amount FROM SalesLines l INNER JOIN SalesHeader h ON h.SaleId=l.SaleId
 WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.SaleDate>=@PeriodFrom AND h.SaleDate<@PeriodTo
 UNION ALL
 SELECT l.ProductId,l.ProductName,l.Quantity,l.LineTotal FROM SalesInvoiceLines l INNER JOIN SalesInvoiceHeader h ON h.SalesInvoiceId=l.SalesInvoiceId
 WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.InvoiceDate>=@PeriodFrom AND h.InvoiceDate<@PeriodTo
)
SELECT TOP 10 ProductId,MAX(ProductName) ProductName,SUM(Quantity) Quantity,SUM(Amount) Amount
FROM ProductSales GROUP BY ProductId ORDER BY SUM(Amount) DESC";
    topProductsCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    topProductsCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    topProductsCmd.Parameters.AddWithValue("@PeriodTo", periodTo.Date);
    var topProducts = await SqlList.ReadAsync(topProductsCmd);

    await using var categoryCmd = con.CreateCommand();
    categoryCmd.CommandText = @"
WITH ProductSales AS (
 SELECT l.ProductId,l.LineTotal Amount FROM SalesLines l INNER JOIN SalesHeader h ON h.SaleId=l.SaleId
 WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.SaleDate>=@PeriodFrom AND h.SaleDate<@PeriodTo
 UNION ALL
 SELECT l.ProductId,l.LineTotal FROM SalesInvoiceLines l INNER JOIN SalesInvoiceHeader h ON h.SalesInvoiceId=l.SalesInvoiceId
 WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.InvoiceDate>=@PeriodFrom AND h.InvoiceDate<@PeriodTo
)
SELECT TOP 8 ISNULL(c.CategoryName,'Uncategorized') CategoryName,SUM(ps.Amount) Amount
FROM ProductSales ps LEFT JOIN Products p ON p.ProductId=ps.ProductId LEFT JOIN Categories c ON c.CategoryId=p.CategoryId
GROUP BY ISNULL(c.CategoryName,'Uncategorized') ORDER BY SUM(ps.Amount) DESC";
    categoryCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    categoryCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    categoryCmd.Parameters.AddWithValue("@PeriodTo", periodTo.Date);
    var salesByCategory = await SqlList.ReadAsync(categoryCmd);

    await using var expenseCategoryCmd = con.CreateCommand();
    expenseCategoryCmd.CommandText = @"SELECT TOP 8 c.CategoryName,SUM(e.Amount) Amount,COUNT(1) EntryCount
FROM Expenses e INNER JOIN ExpenseCategories c ON c.ExpenseCategoryId=e.ExpenseCategoryId
WHERE e.IsDeleted=0 AND e.StoreId=@StoreId AND e.ExpenseDate>=@PeriodFrom AND e.ExpenseDate<@PeriodTo
GROUP BY c.CategoryName ORDER BY SUM(e.Amount) DESC";
    expenseCategoryCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    expenseCategoryCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    expenseCategoryCmd.Parameters.AddWithValue("@PeriodTo", periodTo.Date);
    var expensesByCategory = await SqlList.ReadAsync(expenseCategoryCmd);

    await using var paymentCmd = con.CreateCommand();
    paymentCmd.CommandText = @"SELECT TOP 8 pm.PaymentMethodName,SUM(pl.Amount) Amount,COUNT(DISTINCT pl.SaleId) TransactionCount
FROM PaymentLines pl INNER JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId INNER JOIN SalesHeader h ON h.SaleId=pl.SaleId
WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.SaleDate>=@PeriodFrom AND h.SaleDate<@PeriodTo
GROUP BY pm.PaymentMethodName ORDER BY SUM(pl.Amount) DESC";
    paymentCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    paymentCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    paymentCmd.Parameters.AddWithValue("@PeriodTo", periodTo.Date);
    var paymentMethods = await SqlList.ReadAsync(paymentCmd);

    await using var recentCmd = con.CreateCommand();
    recentCmd.CommandText = @"
SELECT TOP 12 * FROM (
 SELECT h.InvoiceNo,c.CustomerName,CAST(h.SaleDate AS DATE) InvoiceDate,h.GrandTotal,h.Status,CAST(0 AS DECIMAL(18,2)) BalanceAmount,'POS Sale' DocumentType
 FROM SalesHeader h LEFT JOIN Customers c ON c.CustomerId=h.CustomerId WHERE h.StoreId=@StoreId AND h.Status='Posted'
 UNION ALL
 SELECT h.InvoiceNo,c.CustomerName,h.InvoiceDate,h.GrandTotal,h.Status,h.BalanceAmount,'Sales Invoice'
 FROM SalesInvoiceHeader h LEFT JOIN Customers c ON c.CustomerId=h.CustomerId WHERE h.StoreId=@StoreId AND h.Status='Posted'
) x ORDER BY InvoiceDate DESC,InvoiceNo DESC";
    recentCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var recentInvoices = await SqlList.ReadAsync(recentCmd);

    await using var lowStockCmd = con.CreateCommand();
    lowStockCmd.CommandText = @"SELECT TOP 10 p.ProductId,p.ProductCode,p.ProductName,ISNULL(sb.Quantity,p.StockOnHand) StockOnHand,p.ReorderLevel,p.MinStockLevel,p.UnitOfMeasure
FROM Products p LEFT JOIN StockByStore sb ON sb.ProductId=p.ProductId AND sb.StoreId=@StoreId
WHERE p.IsActive=1 AND ISNULL(sb.Quantity,p.StockOnHand)<=p.ReorderLevel
ORDER BY CASE WHEN p.ReorderLevel=0 THEN 0 ELSE ISNULL(sb.Quantity,p.StockOnHand)/NULLIF(p.ReorderLevel,0) END,p.ProductName";
    lowStockCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    var lowStock = await SqlList.ReadAsync(lowStockCmd);

    await using var customerCmd = con.CreateCommand();
    customerCmd.CommandText = @"
WITH CustomerSales AS (
 SELECT h.CustomerId,h.GrandTotal Amount FROM SalesHeader h WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.SaleDate>=@PeriodFrom AND h.SaleDate<@PeriodTo
 UNION ALL
 SELECT h.CustomerId,h.GrandTotal FROM SalesInvoiceHeader h WHERE h.StoreId=@StoreId AND h.Status='Posted' AND h.InvoiceDate>=@PeriodFrom AND h.InvoiceDate<@PeriodTo
)
SELECT TOP 8 c.CustomerCode,c.CustomerName,SUM(cs.Amount) Amount,COUNT(1) DocumentCount
FROM CustomerSales cs INNER JOIN Customers c ON c.CustomerId=cs.CustomerId
GROUP BY c.CustomerCode,c.CustomerName ORDER BY SUM(cs.Amount) DESC";
    customerCmd.Parameters.AddWithValue("@StoreId", user.StoreId);
    customerCmd.Parameters.AddWithValue("@PeriodFrom", periodFrom.Date);
    customerCmd.Parameters.AddWithValue("@PeriodTo", periodTo.Date);
    var topCustomers = await SqlList.ReadAsync(customerCmd);

    return Results.Ok(new
    {
        period = new { from = periodFrom.Date, to = periodTo.Date.AddDays(-1), previousFrom = previousFrom.Date, months = monthCount },
        currency,
        summary,
        monthly,
        hourlyPeriod = new { fromUtc = hourlyFromUtc, toUtc = hourlyToUtc, intervalMinutes = 60, slots = 24 },
        hourlySales,
        topProducts,
        salesByCategory,
        expensesByCategory,
        paymentMethods,
        recentInvoices,
        lowStock,
        topCustomers
    });
});

app.MapGet("/api/shifts/z-report", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, int? shiftId) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    await EnsureShiftManagementSchemaAsync(con);
    bool userWiseShift = true;
    await using (var settingsCmd = new SqlCommand("SELECT TOP 1 ISNULL(UserWiseShift,1) UserWiseShift FROM ShiftManagementSettings WHERE SettingId=1", con))
    {
        var obj = await settingsCmd.ExecuteScalarAsync();
        if (obj != null && obj != DBNull.Value) userWiseShift = Convert.ToBoolean(obj);
    }
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 s.ShiftId,s.OpeningCash,ISNULL(s.ExpectedCash,0) ExpectedCash,ISNULL(s.ClosingCash,0) ClosingCash,ISNULL(s.DifferenceAmount,0) DifferenceAmount,s.Status,s.OpenedAt,s.ClosedAt,
ISNULL((SELECT SUM(pl.Amount) FROM PaymentLines pl JOIN SalesHeader h ON h.SaleId=pl.SaleId JOIN PaymentMethods pm ON pm.PaymentMethodId=pl.PaymentMethodId WHERE h.ShiftId=s.ShiftId AND pm.PaymentMethodName='Cash'),0) CashSales,
ISNULL((SELECT SUM(GrandTotal) FROM SalesHeader WHERE ShiftId=s.ShiftId AND Status='Posted'),0) TotalSales,
ISNULL((SELECT COUNT(1) FROM SalesHeader WHERE ShiftId=s.ShiftId AND Status='Posted'),0) InvoiceCount,
ISNULL((SELECT SUM(RefundAmount) FROM ReturnHeader WHERE ShiftId=s.ShiftId AND Status='Posted'),0) Refunds
FROM Shifts s WHERE (@ShiftId=0 OR s.ShiftId=@ShiftId) AND s.StoreId=@StoreId AND (@UserWiseShift=0 OR s.UserId=@UserId) ORDER BY s.ShiftId DESC";
    cmd.Parameters.AddWithValue("@ShiftId", shiftId ?? 0); cmd.Parameters.AddWithValue("@StoreId", user.StoreId); cmd.Parameters.AddWithValue("@UserId", user.UserId);
    cmd.Parameters.AddWithValue("@UserWiseShift", userWiseShift ? 1 : 0);
    return Results.Ok(await SqlList.ReadSingleAsync(cmd));
});
// =================== END FINAL REQUIREMENT ENDPOINTS ===================


static async Task EnsureProductDefaultsColumnsAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF COL_LENGTH('Products','ProductDiscountPercent') IS NULL
BEGIN
    ALTER TABLE Products ADD ProductDiscountPercent DECIMAL(9,2) NOT NULL CONSTRAINT DF_Products_ProductDiscountPercent DEFAULT 0;
END;
IF OBJECT_ID('TaxGroups') IS NOT NULL AND COL_LENGTH('TaxGroups','IsActive') IS NULL
BEGIN
    ALTER TABLE TaxGroups ADD IsActive BIT NOT NULL CONSTRAINT DF_TaxGroups_IsActive DEFAULT 1;
END";
    await cmd.ExecuteNonQueryAsync();
}

static string? NormalizeInvoiceStatusFilter(string? status)
{
    var raw = (status ?? string.Empty).Trim();
    if (raw.Length == 0) return string.Empty;

    var compact = raw
        .Replace(" ", string.Empty)
        .Replace("/", string.Empty)
        .Replace("-", string.Empty)
        .Replace("_", string.Empty)
        .ToLowerInvariant();

    return compact switch
    {
        "open" => "Open",
        "draft" => "Draft",
        "opendraft" or "draftopen" => "OpenDraft",
        "posted" => "Posted",
        _ => null
    };
}

static async Task EnsureCurrenciesSchemaAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF OBJECT_ID('Currencies') IS NULL
BEGIN
    CREATE TABLE Currencies(
        CurrencyId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CurrencyCode NVARCHAR(10) NOT NULL,
        CurrencyName NVARCHAR(80) NOT NULL,
        Symbol NVARCHAR(12) NOT NULL,
        DecimalPlaces TINYINT NOT NULL CONSTRAINT DF_Currencies_DecimalPlaces DEFAULT 2,
        ExchangeRate DECIMAL(18,6) NOT NULL CONSTRAINT DF_Currencies_ExchangeRate DEFAULT 1,
        IsBase BIT NOT NULL CONSTRAINT DF_Currencies_IsBase DEFAULT 0,
        IsActive BIT NOT NULL CONSTRAINT DF_Currencies_IsActive DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_Currencies_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,
        CONSTRAINT UQ_Currencies_Code UNIQUE(CurrencyCode)
    );
END;
IF NOT EXISTS(SELECT 1 FROM Currencies)
    INSERT INTO Currencies(CurrencyCode,CurrencyName,Symbol,DecimalPlaces,ExchangeRate,IsBase,IsActive)
    VALUES('PKR','Pakistani Rupee','Rs.',2,1,1,1);
;WITH ExtraBase AS (
    SELECT CurrencyId,ROW_NUMBER() OVER(ORDER BY CurrencyId) RowNo FROM Currencies WHERE IsBase=1
)
UPDATE c SET IsBase=0,UpdatedAt=SYSUTCDATETIME()
FROM Currencies c INNER JOIN ExtraBase b ON b.CurrencyId=c.CurrencyId WHERE b.RowNo>1;
IF NOT EXISTS(SELECT 1 FROM Currencies WHERE IsBase=1 AND IsActive=1)
BEGIN
    DECLARE @BaseCurrencyId INT=(SELECT TOP 1 CurrencyId FROM Currencies ORDER BY IsActive DESC,CurrencyId);
    UPDATE Currencies SET IsBase=CASE WHEN CurrencyId=@BaseCurrencyId THEN 1 ELSE 0 END,
        IsActive=CASE WHEN CurrencyId=@BaseCurrencyId THEN 1 ELSE IsActive END,
        ExchangeRate=CASE WHEN CurrencyId=@BaseCurrencyId THEN 1 ELSE ExchangeRate END
    WHERE CurrencyId=@BaseCurrencyId OR IsBase=1;
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('Currencies') AND name='UX_Currencies_Base')
    CREATE UNIQUE INDEX UX_Currencies_Base ON Currencies(IsBase) WHERE IsBase=1;";
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureShiftManagementSchemaAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF OBJECT_ID('ShiftManagementSettings') IS NULL
BEGIN
    CREATE TABLE ShiftManagementSettings(
        SettingId INT NOT NULL PRIMARY KEY CHECK (SettingId = 1),
        EnableShiftManagement BIT NOT NULL DEFAULT 0,
        EnableShift BIT NOT NULL DEFAULT 1,
        UserWiseShift BIT NOT NULL DEFAULT 1,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
    INSERT INTO ShiftManagementSettings(SettingId,EnableShiftManagement,EnableShift,UserWiseShift) VALUES(1,0,1,1);
END;

IF OBJECT_ID('SalesHeader') IS NOT NULL AND COL_LENGTH('SalesHeader','ApplicationSource') IS NULL
    ALTER TABLE SalesHeader ADD ApplicationSource NVARCHAR(20) NOT NULL DEFAULT 'Cloud';

IF OBJECT_ID('ReturnHeader') IS NOT NULL AND COL_LENGTH('ReturnHeader','ApplicationSource') IS NULL
    ALTER TABLE ReturnHeader ADD ApplicationSource NVARCHAR(20) NOT NULL DEFAULT 'Cloud';

IF OBJECT_ID('SalesInvoiceHeader') IS NOT NULL AND COL_LENGTH('SalesInvoiceHeader','ApplicationSource') IS NULL
    ALTER TABLE SalesInvoiceHeader ADD ApplicationSource NVARCHAR(20) NOT NULL DEFAULT 'Cloud';

IF OBJECT_ID('ShiftManagementSettings') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ShiftManagementSettings WHERE SettingId=1)
    INSERT INTO ShiftManagementSettings(SettingId,EnableShiftManagement,EnableShift,UserWiseShift) VALUES(1,0,1,1);

IF COL_LENGTH('SalesHeader','ApplicationSource') IS NOT NULL
    EXEC('UPDATE SalesHeader SET ApplicationSource=''Cloud'' WHERE ApplicationSource IS NULL OR LTRIM(RTRIM(ApplicationSource))=''''');

IF COL_LENGTH('ReturnHeader','ApplicationSource') IS NOT NULL
    EXEC('UPDATE ReturnHeader SET ApplicationSource=''Cloud'' WHERE ApplicationSource IS NULL OR LTRIM(RTRIM(ApplicationSource))=''''');

IF COL_LENGTH('SalesInvoiceHeader','ApplicationSource') IS NOT NULL
    EXEC('UPDATE SalesInvoiceHeader SET ApplicationSource=''Cloud'' WHERE ApplicationSource IS NULL OR LTRIM(RTRIM(ApplicationSource))=''''');

IF OBJECT_ID('SalesHeader') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_SalesHeader_Shift_Source' AND object_id=OBJECT_ID('SalesHeader'))
    CREATE INDEX IX_SalesHeader_Shift_Source ON SalesHeader(ShiftId, ApplicationSource) INCLUDE(GrandTotal,PaidAmount,Status);
IF OBJECT_ID('ReturnHeader') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_ReturnHeader_Shift_Source' AND object_id=OBJECT_ID('ReturnHeader'))
    CREATE INDEX IX_ReturnHeader_Shift_Source ON ReturnHeader(ShiftId, ApplicationSource) INCLUDE(RefundAmount,Status);
IF OBJECT_ID('Shifts') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Shifts_Store_Status_User' AND object_id=OBJECT_ID('Shifts'))
    CREATE INDEX IX_Shifts_Store_Status_User ON Shifts(StoreId, Status, UserId, ShiftId DESC);
";
    await cmd.ExecuteNonQueryAsync();
}

static string ResolveApplicationSource(HttpContext http, UserSession user)
    => ApiAuth.ResolveApplicationSource(http, user);

static bool ReadFlag(Dictionary<string, object?> row, string key, bool fallback = false)
{
    if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value) return fallback;
    if (value is bool b) return b;
    if (value is byte by) return by != 0;
    if (value is short s) return s != 0;
    if (value is int i) return i != 0;
    if (value is long l) return l != 0;
    var text = Convert.ToString(value);
    if (bool.TryParse(text, out var parsed)) return parsed;
    return text == "1";
}

static object ShiftRowSnapshot(Dictionary<string, object?> row)
{
    object? Get(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && value != null && value != DBNull.Value)
                return value;
        }
        return null;
    }

    return new
    {
        shiftId = Convert.ToInt32(Get("ShiftId", "shiftId") ?? 0),
        storeId = Convert.ToInt32(Get("StoreId", "storeId") ?? 0),
        terminalId = Convert.ToInt32(Get("TerminalId", "terminalId") ?? 0),
        userId = Convert.ToInt32(Get("UserId", "userId") ?? 0),
        openingCash = Convert.ToDecimal(Get("OpeningCash", "openingCash") ?? 0m),
        status = Convert.ToString(Get("Status", "status") ?? "Open"),
        openedAt = Get("OpenedAt", "openedAt"),
        closedAt = Get("ClosedAt", "closedAt")
    };
}

static Task<Dictionary<string, object?>> ReadBaseCurrencyAsync(SqlConnection con) =>
    SqlList.ReadSingleAsync(con, @"SELECT TOP 1 CurrencyId,CurrencyCode,CurrencyName,Symbol,DecimalPlaces,ExchangeRate,IsBase,IsActive
FROM Currencies WHERE IsBase=1 AND IsActive=1 ORDER BY CurrencyId");

static async Task<Dictionary<string,string>> GetPostingSetupAsync(SqlConnection con, SqlTransaction tran)
{
    await using var cmd = new SqlCommand("SELECT TOP 1 CashAccount,BankAccount,ReceivableAccount,InventoryAccount,InputTaxAccount,PayableAccount,OutputTaxAccount,OpeningBalanceAccount,SalesAccount,SalesReturnAccount,CogsAccount,StockAdjustmentAccount FROM PostingSetup WHERE SetupId=1", con, tran);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) throw new InvalidOperationException("Posting setup is missing.");
    return new Dictionary<string,string>
    {
        ["CashAccount"] = SqlRead.String(r,"CashAccount"),
        ["BankAccount"] = SqlRead.String(r,"BankAccount"),
        ["ReceivableAccount"] = SqlRead.String(r,"ReceivableAccount"),
        ["InventoryAccount"] = SqlRead.String(r,"InventoryAccount"),
        ["InputTaxAccount"] = SqlRead.String(r,"InputTaxAccount"),
        ["PayableAccount"] = SqlRead.String(r,"PayableAccount"),
        ["OutputTaxAccount"] = SqlRead.String(r,"OutputTaxAccount"),
        ["OpeningBalanceAccount"] = SqlRead.String(r,"OpeningBalanceAccount"),
        ["SalesAccount"] = SqlRead.String(r,"SalesAccount"),
        ["SalesReturnAccount"] = SqlRead.String(r,"SalesReturnAccount"),
        ["CogsAccount"] = SqlRead.String(r,"CogsAccount"),
        ["StockAdjustmentAccount"] = SqlRead.String(r,"StockAdjustmentAccount")
    };
}

static async Task InsertGlAsync(SqlConnection con, SqlTransaction tran, string accountNo, DateTime postingDate, string documentType, string documentNo, decimal debit, decimal credit, string description, int sourceId, string branchCode = "")
{
    if (debit == 0 && credit == 0) return;
    await using (var period = new SqlCommand("IF OBJECT_ID('AccountingPeriods') IS NULL SELECT 0 ELSE SELECT COUNT(1) FROM AccountingPeriods WHERE IsClosed=1 AND @PostingDate BETWEEN StartDate AND EndDate", con, tran))
    {
        period.Parameters.AddWithValue("@PostingDate", postingDate.Date);
        if (Convert.ToInt32(await period.ExecuteScalarAsync()) > 0)
            throw new InvalidOperationException($"Posting date {postingDate:yyyy-MM-dd} is in a closed accounting period.");
    }
    await using var cmd = new SqlCommand(@"
IF COL_LENGTH('GLEntries','BranchCode') IS NOT NULL
BEGIN
    INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId,BranchCode)
    SELECT AccountId,@PostingDate,@DocumentType,@DocumentNo,@Debit,@Credit,@Description,@SourceId,@BranchCode FROM ChartOfAccounts WHERE AccountNo=@AccountNo;
END
ELSE
BEGIN
    INSERT INTO GLEntries(AccountId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,Description,SourceId)
    SELECT AccountId,@PostingDate,@DocumentType,@DocumentNo,@Debit,@Credit,@Description,@SourceId FROM ChartOfAccounts WHERE AccountNo=@AccountNo;
END", con, tran);
    cmd.Parameters.AddWithValue("@AccountNo", accountNo);
    cmd.Parameters.AddWithValue("@PostingDate", postingDate.Date);
    cmd.Parameters.AddWithValue("@DocumentType", documentType);
    cmd.Parameters.AddWithValue("@DocumentNo", documentNo);
    cmd.Parameters.AddWithValue("@Debit", debit);
    cmd.Parameters.AddWithValue("@Credit", credit);
    cmd.Parameters.AddWithValue("@Description", description);
    cmd.Parameters.AddWithValue("@SourceId", sourceId);
    cmd.Parameters.AddWithValue("@BranchCode", branchCode ?? "");
    if (await cmd.ExecuteNonQueryAsync() == 0) throw new InvalidOperationException($"G/L account missing: {accountNo}");
}

static async Task<(int PaymentId, string PaymentNo)> PostCustomerReceiptAsync(
    SqlConnection con, SqlTransaction tran, UserSession user, CustomerPaymentPostRequest request, bool applyInvoiceBalance)
{
    var paymentNo = await PosSql.NextNumberAsync(con, tran, "CUSTOMER_PAYMENT");
    var setup = await GetPostingSetupAsync(con, tran);
    var dest = await BankAccountService.ResolvePaymentGlAsync(con, tran, request.PaymentMethod, request.BankAccountId, setup["CashAccount"]);
    var invoiceId = request.SalesInvoiceId is > 0 ? request.SalesInvoiceId : null;
    if (applyInvoiceBalance && invoiceId is > 0)
        await BankAccountService.ApplySalesInvoicePaymentAsync(con, tran, request.CustomerId, invoiceId.Value, request.Amount);

    int id;
    var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
    await using (var cmd = new SqlCommand(@"
INSERT INTO CustomerPayments(PaymentNo,CustomerId,PaymentDate,Amount,PaymentMethod,ReferenceNo,Remarks,CreatedBy,BranchCode,BankAccountId,SalesInvoiceId,ExternalClientDocumentId)
OUTPUT INSERTED.PaymentId VALUES(@PaymentNo,@CustomerId,@PaymentDate,@Amount,@PaymentMethod,@ReferenceNo,@Remarks,@UserId,@BranchCode,@BankAccountId,@SalesInvoiceId,@ClientDocumentId)", con, tran))
    {
        cmd.Parameters.AddWithValue("@PaymentNo", paymentNo);
        cmd.Parameters.AddWithValue("@CustomerId", request.CustomerId);
        cmd.Parameters.AddWithValue("@PaymentDate", request.PaymentDate.Date);
        cmd.Parameters.AddWithValue("@Amount", request.Amount);
        cmd.Parameters.AddWithValue("@PaymentMethod", string.IsNullOrWhiteSpace(request.PaymentMethod) ? "Cash" : request.PaymentMethod.Trim());
        cmd.Parameters.AddWithValue("@ReferenceNo", request.ReferenceNo ?? "");
        cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
        cmd.Parameters.Add(new SqlParameter("@BankAccountId", SqlDbType.Int) { Value = dest.BankAccountId ?? (object)DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@SalesInvoiceId", SqlDbType.Int) { Value = invoiceId ?? (object)DBNull.Value });
        cmd.Parameters.AddWithValue("@ClientDocumentId", string.IsNullOrWhiteSpace(clientDocumentId) ? DBNull.Value : clientDocumentId);
        id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
    await using (var cmd = new SqlCommand(@"
UPDATE Customers SET CurrentBalance=CurrentBalance-@Amount WHERE CustomerId=@CustomerId;
DECLARE @Bal DECIMAL(18,2)=(SELECT CurrentBalance FROM Customers WHERE CustomerId=@CustomerId);
INSERT INTO CustomerLedgerEntries(CustomerId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@CustomerId,@PaymentDate,'Customer Payment',@PaymentNo,0,@Amount,@Bal,@Remarks,@Id);", con, tran))
    {
        cmd.Parameters.AddWithValue("@Amount", request.Amount);
        cmd.Parameters.AddWithValue("@CustomerId", request.CustomerId);
        cmd.Parameters.AddWithValue("@PaymentDate", request.PaymentDate.Date);
        cmd.Parameters.AddWithValue("@PaymentNo", paymentNo);
        cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync();
    }
    await InsertGlAsync(con, tran, dest.AccountNo, request.PaymentDate.Date, "Customer Payment", paymentNo, request.Amount, 0, "Customer payment received", id, user.BranchCode);
    await InsertGlAsync(con, tran, setup["ReceivableAccount"], request.PaymentDate.Date, "Customer Payment", paymentNo, 0, request.Amount, "Receivable cleared", id, user.BranchCode);
    if (dest.BankAccountId is int bankId)
        await BankAccountService.InsertBankLedgerAsync(con, tran, bankId, request.PaymentDate.Date, "Customer Payment", paymentNo, request.Amount, 0, request.Remarks ?? "Customer payment received", id);
    return (id, paymentNo);
}

static async Task<(int PaymentId, string PaymentNo)> PostVendorReceiptAsync(
    SqlConnection con, SqlTransaction tran, UserSession user, VendorPaymentPostRequest request, bool applyInvoiceBalance)
{
    var paymentNo = await PosSql.NextNumberAsync(con, tran, "VENDOR_PAYMENT");
    var setup = await GetPostingSetupAsync(con, tran);
    var dest = await BankAccountService.ResolvePaymentGlAsync(con, tran, request.PaymentMethod, request.BankAccountId, setup["CashAccount"]);
    var invoiceId = request.PurchaseInvoiceId is > 0 ? request.PurchaseInvoiceId : null;
    if (applyInvoiceBalance && invoiceId is > 0)
        await BankAccountService.ApplyPurchaseInvoicePaymentAsync(con, tran, request.VendorId, invoiceId.Value, request.Amount);

    int id;
    var clientDocumentId = DesktopIdempotency.NormalizeClientDocumentId(request.ClientDocumentId);
    await using (var cmd = new SqlCommand(@"
INSERT INTO VendorPayments(PaymentNo,VendorId,PaymentDate,Amount,PaymentMethod,ReferenceNo,Remarks,CreatedBy,BranchCode,BankAccountId,PurchaseInvoiceId,ExternalClientDocumentId)
OUTPUT INSERTED.PaymentId VALUES(@PaymentNo,@VendorId,@PaymentDate,@Amount,@PaymentMethod,@ReferenceNo,@Remarks,@UserId,@BranchCode,@BankAccountId,@PurchaseInvoiceId,@ClientDocumentId)", con, tran))
    {
        cmd.Parameters.AddWithValue("@PaymentNo", paymentNo);
        cmd.Parameters.AddWithValue("@VendorId", request.VendorId);
        cmd.Parameters.AddWithValue("@PaymentDate", request.PaymentDate.Date);
        cmd.Parameters.AddWithValue("@Amount", request.Amount);
        cmd.Parameters.AddWithValue("@PaymentMethod", string.IsNullOrWhiteSpace(request.PaymentMethod) ? "Cash" : request.PaymentMethod.Trim());
        cmd.Parameters.AddWithValue("@ReferenceNo", request.ReferenceNo ?? "");
        cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
        cmd.Parameters.AddWithValue("@UserId", user.UserId);
        cmd.Parameters.AddWithValue("@BranchCode", user.BranchCode);
        cmd.Parameters.Add(new SqlParameter("@BankAccountId", SqlDbType.Int) { Value = dest.BankAccountId ?? (object)DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@PurchaseInvoiceId", SqlDbType.Int) { Value = invoiceId ?? (object)DBNull.Value });
        cmd.Parameters.AddWithValue("@ClientDocumentId", string.IsNullOrWhiteSpace(clientDocumentId) ? DBNull.Value : clientDocumentId);
        id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
    await using (var cmd = new SqlCommand(@"
UPDATE Vendors SET CurrentBalance=CurrentBalance-@Amount WHERE VendorId=@VendorId;
DECLARE @Bal DECIMAL(18,2)=(SELECT CurrentBalance FROM Vendors WHERE VendorId=@VendorId);
INSERT INTO VendorLedgerEntries(VendorId,PostingDate,DocumentType,DocumentNo,DebitAmount,CreditAmount,BalanceAfter,Description,SourceId)
VALUES(@VendorId,@PaymentDate,'Vendor Payment',@PaymentNo,@Amount,0,@Bal,@Remarks,@Id);", con, tran))
    {
        cmd.Parameters.AddWithValue("@Amount", request.Amount);
        cmd.Parameters.AddWithValue("@VendorId", request.VendorId);
        cmd.Parameters.AddWithValue("@PaymentDate", request.PaymentDate.Date);
        cmd.Parameters.AddWithValue("@PaymentNo", paymentNo);
        cmd.Parameters.AddWithValue("@Remarks", request.Remarks ?? "");
        cmd.Parameters.AddWithValue("@Id", id);
        await cmd.ExecuteNonQueryAsync();
    }
    await InsertGlAsync(con, tran, setup["PayableAccount"], request.PaymentDate.Date, "Vendor Payment", paymentNo, request.Amount, 0, "Payable cleared", id, user.BranchCode);
    await InsertGlAsync(con, tran, dest.AccountNo, request.PaymentDate.Date, "Vendor Payment", paymentNo, 0, request.Amount, "Vendor payment paid", id, user.BranchCode);
    if (dest.BankAccountId is int bankId)
        await BankAccountService.InsertBankLedgerAsync(con, tran, bankId, request.PaymentDate.Date, "Vendor Payment", paymentNo, 0, request.Amount, request.Remarks ?? "Vendor payment paid", id);
    return (id, paymentNo);
}


app.MapGet("/api/reports/templates", (IWebHostEnvironment env, CloudReportHtmlService reports) => Results.Ok(reports.GetTemplateNames(env)));

app.MapGet("/api/reports/templates/{fileName}", (IWebHostEnvironment env, CloudReportHtmlService reports, string fileName) =>
{
    var path = reports.GetTemplatePath(env, fileName);
    return path == null ? Results.NotFound(new { message = "RDLC template not found." }) : Results.File(path, "application/xml", Path.GetFileName(path));
});

app.MapGet("/api/reports/sales-invoice/{saleId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int saleId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.SalesInvoiceHtmlAsync(con, saleId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/formal-sales-invoice/{salesInvoiceId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int salesInvoiceId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.FormalSalesInvoiceHtmlAsync(con, salesInvoiceId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/pos-receipt/{saleId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int saleId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.PosReceiptHtmlAsync(con, saleId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/cashier-invoice/{saleId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int saleId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.CashierSalesInvoiceHtmlAsync(con, saleId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/purchase-invoice/{purchaseInvoiceId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int purchaseInvoiceId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.PurchaseInvoiceHtmlAsync(con, purchaseInvoiceId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/customer-payment/{paymentId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int paymentId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.CustomerPaymentHtmlAsync(con, paymentId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/vendor-payment/{paymentId:int}/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int paymentId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.VendorPaymentHtmlAsync(con, paymentId, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/customer-ledger/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int? customerId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.CustomerLedgerHtmlAsync(con, customerId ?? 0, layout), "text/html; charset=utf-8");
});

app.MapGet("/api/reports/vendor-ledger/html", async (HttpContext http, ConnectionFactory db, AuthTokenService tokens, CloudReportHtmlService reports, int? vendorId, string? layout) =>
{
    var user = ApiAuth.RequireUser(http, tokens); if (user == null) return Results.Unauthorized();
    await using var con = await db.OpenTenantAsync(user.DatabaseName);
    return Results.Content(await reports.VendorLedgerHtmlAsync(con, vendorId ?? 0, layout), "text/html; charset=utf-8");
});

app.MapPayNexProfessionalEndpoints();
app.MapConfigurationPackageEndpoints();
app.MapExpenseManagementEndpoints();




static (string Category, string Key, string Label)[] PermissionCatalog() => new[]
{
    ("Sales", "sales.createInvoice", "Create Sales Invoice"),
    ("Sales", "sales.editInvoice", "Edit Sales Invoice"),
    ("Sales", "sales.deleteInvoice", "Delete Sales Invoice"),
    ("Sales", "sales.postInvoice", "Post Sales Invoice"),
    ("Sales", "sales.createReturn", "Create Sales Return"),
    ("Purchases", "purchase.createInvoice", "Create Purchase Invoice"),
    ("Purchases", "purchase.editInvoice", "Edit Purchase Invoice"),
    ("Purchases", "purchase.deleteInvoice", "Delete Purchase Invoice"),
    ("Purchases", "purchase.postInvoice", "Post Purchase Invoice"),
    ("Purchases", "purchase.createReturn", "Create Purchase Return"),
    ("Inventory", "inventory.createItems", "Create Items"),
    ("Inventory", "inventory.editItems", "Edit Items"),
    ("Inventory", "inventory.deleteItems", "Delete Items"),
    ("Inventory", "inventory.stockAdjustment", "Stock Adjustment"),
    ("Inventory", "inventory.transfer", "Inventory Transfer"),
    ("Inventory", "inventory.goodsReceipt", "Goods Receipt"),
    ("Inventory", "inventory.goodsIssue", "Goods Issue"),
    ("Pricing", "pricing.changeProductPrice", "Change Product Price"),
    ("Pricing", "pricing.changeProductDiscount", "Change Product Discount"),
    ("Pricing", "pricing.overrideSellingPrice", "Override Selling Price"),
    ("Finance", "finance.createExpense", "Create Expense"),
    ("Finance", "finance.approveExpense", "Approve Expense"),
    ("Finance", "finance.viewFinancialReports", "View Financial Reports"),
    ("User Management", "users.createUser", "Create User"),
    ("User Management", "users.editUser", "Edit User"),
    ("User Management", "users.deleteUser", "Delete User"),
    ("User Management", "users.resetPassword", "Reset Password"),
    ("User Management", "users.assignPermissions", "Assign Permissions"),
    ("User Management", "users.promoteCompanySuperAdmin", "Promote to Company Super Admin"),
    ("Reports", "reports.viewReports", "View Reports"),
    ("Reports", "reports.exportReports", "Export Reports"),
    ("Reports", "reports.printReports", "Print Reports"),
    ("System", "system.companySettings", "Company Settings"),
    ("System", "system.branchManagement", "Branch Management"),
    ("System", "system.backupRestore", "Backup & Restore"),
    ("System", "system.generalConfiguration", "General Configuration"),
    ("Data Management", "configurationPackages.view", "View Configuration Packages"),
    ("Data Management", "configurationPackages.manage", "Create and Edit Configuration Packages"),
    ("Data Management", "configurationPackages.export", "Export Configuration Packages"),
    ("Data Management", "configurationPackages.import", "Import and Validate Configuration Packages"),
    ("Data Management", "configurationPackages.apply", "Apply Configuration Packages"),
    ("Expense Management", "expenses.view", "View Expenses"),
    ("Expense Management", "expenses.create", "Create Expenses"),
    ("Expense Management", "expenses.edit", "Edit Expenses"),
    ("Expense Management", "expenses.delete", "Delete Expenses"),
    ("Expense Management", "expenses.manageCategories", "Manage Expense Categories"),
    ("Expense Management", "expenses.viewReport", "View Expense Report"),
    ("Expense Management", "expenses.printReport", "Print and Export Expense Report")
};

static Dictionary<string, bool> PermissionMap(bool allowAll)
{
    var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in PermissionCatalog()) map[p.Key] = allowAll;
    return map;
}

static Dictionary<string, bool> ReadSessionPermissionMap(UserSession user)
{
    if (IsCompanySecurityAdmin(user)) return PermissionMap(true);
    try
    {
        var map = RolePermissionService.ParseBoolPermissionMap(user.PermissionsJson);
        foreach (var p in PermissionCatalog()) if (!map.ContainsKey(p.Key)) map[p.Key] = false;
        return new Dictionary<string, bool>(map, StringComparer.OrdinalIgnoreCase);
    }
    catch { return PermissionMap(false); }
}

static bool HasSessionPermission(UserSession user, string key)
{
    if (IsCompanySecurityAdmin(user)) return true;
    var map = ReadSessionPermissionMap(user);
    return map.TryGetValue(key, out var allowed) && allowed;
}

static bool CanUserManagePermission(UserSession user, string key) => IsCompanySecurityAdmin(user) || HasSessionPermission(user, key);

static bool CanOpenUserManagement(UserSession user) =>
    IsCompanySecurityAdmin(user) ||
    HasSessionPermission(user, "users.createUser") ||
    HasSessionPermission(user, "users.editUser") ||
    HasSessionPermission(user, "users.assignPermissions") ||
    HasSessionPermission(user, "users.resetPassword") ||
    HasSessionPermission(user, "users.promoteCompanySuperAdmin");

static async Task<Dictionary<string, bool>> ReadUserPermissionMapAsync(SqlConnection con, int userId, bool isCompanySuperAdmin)
{
    var map = PermissionMap(isCompanySuperAdmin);
    if (isCompanySuperAdmin) return map;
    await EnsureTenantUserSecuritySchemaAsync(con);
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT PermissionKey,IsAllowed FROM UserPermissions WHERE UserId=@UserId";
    cmd.Parameters.AddWithValue("@UserId", userId);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) map[SqlRead.String(r, "PermissionKey")] = SqlRead.Bool(r, "IsAllowed");
    return map;
}

static async Task<string> ReadUserPermissionsJsonAsync(SqlConnection con, int userId, bool isCompanySuperAdmin)
{
    var map = await ReadUserPermissionMapAsync(con, userId, isCompanySuperAdmin);
    return JsonSerializer.Serialize(map);
}

static async Task SaveUserPermissionsAsync(SqlConnection con, int userId, Dictionary<string, bool>? permissions)
{
    await EnsureTenantUserSecuritySchemaAsync(con);
    var catalog = PermissionCatalog().Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    permissions ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    await using var del = con.CreateCommand();
    del.CommandText = "DELETE FROM UserPermissions WHERE UserId=@UserId";
    del.Parameters.AddWithValue("@UserId", userId);
    await del.ExecuteNonQueryAsync();
    foreach (var kv in permissions.Where(x => catalog.Contains(x.Key)))
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO UserPermissions(UserId,PermissionKey,IsAllowed,UpdatedAt) VALUES(@UserId,@Key,@Allowed,SYSUTCDATETIME())";
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@Key", kv.Key);
        cmd.Parameters.AddWithValue("@Allowed", kv.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}

static async Task SaveUserBranchAssignmentsAsync(SqlConnection con, int userId, int defaultStoreId, List<int>? branchIds)
{
    await EnsureTenantUserSecuritySchemaAsync(con);
    var ids = (branchIds ?? new List<int>()).Where(x => x > 0).Distinct().ToList();
    if (defaultStoreId > 0 && !ids.Contains(defaultStoreId)) ids.Insert(0, defaultStoreId);
    await using var del = con.CreateCommand();
    del.CommandText = "DELETE FROM UserBranchAssignments WHERE UserId=@UserId";
    del.Parameters.AddWithValue("@UserId", userId);
    await del.ExecuteNonQueryAsync();
    foreach (var id in ids)
    {
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO UserBranchAssignments(UserId,StoreId,IsDefault) VALUES(@UserId,@StoreId,@Default)";
        cmd.Parameters.AddWithValue("@UserId", userId);
        cmd.Parameters.AddWithValue("@StoreId", id);
        cmd.Parameters.AddWithValue("@Default", id == defaultStoreId);
        await cmd.ExecuteNonQueryAsync();
    }
}

static async Task<List<Dictionary<string, object?>>> FilterBranchesForUserAsync(SqlConnection con, int userId, List<Dictionary<string, object?>> branches) =>
    await BranchAccessHelper.FilterBranchesForUserAsync(con, userId, branches, isSecurityAdmin: false);

static async Task<bool> IsBranchAssignedToUserAsync(SqlConnection con, int userId, int branchId) =>
    await BranchAccessHelper.IsBranchAssignedToUserAsync(con, userId, branchId, isSecurityAdmin: false);


static async Task<IResult> LoginFailureAsync(AuthenticationSecurityService authSecurity, string loginKey, string ipAddress, string userAgent, string reason)
{
    await authSecurity.RecordFailedLoginAsync(loginKey, ipAddress, userAgent, reason);
    var lockedUntil = await authSecurity.GetLockoutUntilAsync(loginKey, ipAddress);
    if (lockedUntil.HasValue)
        return Results.Json(new
        {
            message = $"Account sign-in is temporarily locked because of repeated failed attempts. Try again after {lockedUntil.Value:yyyy-MM-dd HH:mm:ss} UTC.",
            lockedUntil = lockedUntil.Value
        }, statusCode: StatusCodes.Status429TooManyRequests);
    return Results.BadRequest(new { message = "Invalid email or password." });
}

static async Task<IResult> StartOrCompleteLoginAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, UserSession pendingSession, bool forceOtp = false)
{
    var trustedToken = http.Request.Cookies["paynex_trusted_device"];
    var trusted = !forceOtp && await authSecurity.IsTrustedDeviceAsync(
            pendingSession.Email,
            pendingSession.CompanyCode,
            trustedToken,
            http.Request.Headers["User-Agent"].ToString());

    if (trusted)
    {
        var authenticated = await CompleteAuthenticationAsync(http, db, tokens, authSecurity, pendingSession);
        return Results.Ok(authenticated);
    }

    try
    {
        var challenge = await authSecurity.CreateLoginChallengeAsync(pendingSession, http);
        return Results.Ok(new
        {
            authenticated = false,
            requiresVerification = true,
            challengeId = challenge.ChallengeId,
            maskedEmail = challenge.MaskedEmail,
            expiresInSeconds = challenge.ExpiresInSeconds,
            fromEmail = challenge.Delivery.FromEmail,
            deliveryStatus = challenge.Delivery.Message
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
}

static async Task<IResult> CompleteMobileLoginAsync(
    HttpContext http,
    ConnectionFactory db,
    AuthTokenService tokens,
    AuthenticationSecurityService authSecurity,
    UserSession pendingSession,
    long mobileAppUserId,
    string? mobile,
    string? appName,
    string? platform,
    string? apiKey,
    string? appStatus,
    string? deviceName,
    string? appVersion,
    object[]? branchesPayload = null)
{
    if (mobileAppUserId <= 0) mobileAppUserId = ReadMobileAppUserId(pendingSession);
    await MarkMobileEmailVerifiedAsync(db, mobileAppUserId, pendingSession.Email, pendingSession.CompanyCode);

    var sessionId = "MOB-" + Guid.NewGuid().ToString("N");
    var permissionsJson = BuildMobilePermissionsJson(mobileAppUserId);
    var session = pendingSession with { SessionId = sessionId, PermissionsJson = permissionsJson };

    // Ensure branch list is present (OTP path may not have precomputed payload).
    if (branchesPayload == null || branchesPayload.Length == 0)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(session.DatabaseName) && session.UserId > 0)
            {
                await using var tenantCon = await db.OpenTenantAsync(session.DatabaseName);
                var isAdmin = IsCompanySecurityAdmin(session);
                var (preferred, assigned) = await BranchAccessHelper.ResolveBranchesForUserAsync(tenantCon, session.UserId, isAdmin);
                session = session with
                {
                    StoreId = preferred.BranchId,
                    StoreName = preferred.BranchName,
                    BranchId = preferred.BranchId,
                    BranchCode = preferred.BranchCode,
                    BranchName = preferred.BranchName
                };
                branchesPayload = BranchAccessHelper.ToPayload(assigned);
            }
        }
        catch { /* keep session branch as-is */ }
    }

    if (branchesPayload == null || branchesPayload.Length == 0)
    {
        branchesPayload = BranchAccessHelper.ToPayload(new[]
        {
            new BranchAccessHelper.BranchInfo(
                session.BranchId > 0 ? session.BranchId : 1,
                string.IsNullOrWhiteSpace(session.BranchCode) ? "MAIN" : session.BranchCode,
                string.IsNullOrWhiteSpace(session.BranchName) ? "Main Store" : session.BranchName,
                true)
        });
    }

    var token = tokens.Create(session);
    var refresh = await authSecurity.ReplaceRefreshTokenAsync(null, session);

    // Best-effort enrichment when verify-otp path did not pass app metadata.
    if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(apiKey))
    {
        try
        {
            await using var master = await db.OpenMasterAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandText = @"
SELECT TOP 1 u.Mobile, a.AppName, a.Platform, a.ApiKey, a.Status
FROM CompanyMobileAppUsers u
INNER JOIN CompanyMobileApps a ON a.AppId=u.AppId AND a.CompanyCode=u.CompanyCode
WHERE u.CompanyCode=@CompanyCode AND (u.MobileAppUserId=@MobileAppUserId OR LOWER(LTRIM(RTRIM(ISNULL(u.Email,''))))=@Email)
ORDER BY u.MobileAppUserId";
            cmd.Parameters.AddWithValue("@CompanyCode", session.CompanyCode);
            cmd.Parameters.AddWithValue("@MobileAppUserId", mobileAppUserId);
            cmd.Parameters.AddWithValue("@Email", NormalizeCloudEmail(session.Email));
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                mobile ??= SqlRead.String(reader, "Mobile");
                appName ??= SqlRead.String(reader, "AppName");
                platform ??= SqlRead.String(reader, "Platform");
                apiKey ??= SqlRead.String(reader, "ApiKey");
                appStatus ??= SqlRead.String(reader, "Status");
            }
        }
        catch { /* keep nulls */ }
    }

    var allowMulti = session.AllowMultipleBranches && branchesPayload.Length > 1;
    return Results.Ok(new
    {
        ok = true,
        code = "LOGIN_OK",
        message = "Login successful. Company resolved automatically from email.",
        requiresVerification = false,
        token,
        refreshToken = refresh.PlainToken,
        expiresInMinutes = authSecurity.AccessTokenExpiryMinutes,
        allowMultipleBranches = session.AllowMultipleBranches,
        showSelector = allowMulti,
        branches = branchesPayload,
        storeId = session.StoreId,
        branchId = session.BranchId,
        branchCode = session.BranchCode,
        branchName = session.BranchName,
        company = new
        {
            companyCode = session.CompanyCode,
            companyName = session.CompanyName
        },
        user = new
        {
            mobileAppUserId,
            userId = session.UserId,
            userName = session.UserName,
            displayName = session.DisplayName,
            email = session.Email,
            mobile,
            roleName = session.RoleName,
            storeId = session.StoreId,
            branchId = session.BranchId,
            branchCode = session.BranchCode,
            branchName = session.BranchName
        },
        app = new
        {
            appName,
            platform,
            apiKey,
            status = appStatus
        },
        device = new
        {
            deviceName,
            appVersion
        },
        sync = new
        {
            autoConnect = true,
            noClientSetupRequired = true,
            authHeader = "Authorization: Bearer {token}",
            endpoints = new
            {
                health = "/api/mobile/health",
                me = "/api/mobile/me",
                company = "/api/mobile/company",
                logout = "/api/mobile/logout",
                refresh = "/api/mobile/refresh",
                branchContext = "/api/branches/context",
                switchBranch = "/api/branches/switch"
            }
        }
    });
}

static async Task<AuthenticatedLoginResponse> CompleteAuthenticationAsync(HttpContext http, ConnectionFactory db, AuthTokenService tokens, AuthenticationSecurityService authSecurity, UserSession pendingSession)
{
    var sessionId = await CreateAuthenticatedLoginSessionAsync(db, pendingSession, http);
    var session = pendingSession with { SessionId = sessionId };
    var accessToken = tokens.Create(session);
    var refreshToken = await authSecurity.CreateRefreshTokenAsync(session);
    SetTenantAuthCookie(http, accessToken, authSecurity.AccessTokenExpiryMinutes);
    SetRefreshTokenCookie(http, refreshToken.PlainToken, refreshToken.ExpiresAt);
    SetCsrfCookie(http);
    return new AuthenticatedLoginResponse(accessToken, session);
}

static async Task<string> CreateAuthenticatedLoginSessionAsync(ConnectionFactory db, UserSession session, HttpContext http)
{
    await EnsureMasterUserDirectorySchemaAsync(db);
    var sessionId = session.IsPlatformOwner ? "OWN" + Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
    var tenant = session.IsPlatformOwner ? null : await db.GetTenantByCodeAsync(session.CompanyCode);
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
INSERT INTO AuthSessionAudit(SessionId,TenantId,CompanyCode,UserId,UserName,Email,EventName,EnvironmentName,BranchCode,IpAddress,UserAgent)
VALUES(@SessionId,@TenantId,@CompanyCode,@UserId,@UserName,@Email,'LOGIN',@EnvironmentName,@BranchCode,@IpAddress,@UserAgent);
IF @IsPlatformOwner=0
BEGIN
    UPDATE Tenants SET LastLoginAt=SYSUTCDATETIME() WHERE CompanyCode=@CompanyCode;
    UPDATE CentralUserDirectory SET LastLoginAt=SYSUTCDATETIME(),EmailVerified=1,UpdatedAt=SYSUTCDATETIME() WHERE Email=@Email AND CompanyCode=@CompanyCode;
    INSERT INTO TenantAuditLog(TenantId,CompanyCode,ActionName,Description)
    VALUES(@TenantId,@CompanyCode,'CLIENT_LOGIN','Client user completed secure authentication and opened the company workspace.');
END;";
    cmd.Parameters.AddWithValue("@SessionId", sessionId);
    var tenantIdParameter = cmd.Parameters.Add("@TenantId", SqlDbType.UniqueIdentifier);
    tenantIdParameter.Value = tenant == null ? DBNull.Value : tenant.TenantId;
    cmd.Parameters.AddWithValue("@CompanyCode", session.CompanyCode ?? string.Empty);
    cmd.Parameters.AddWithValue("@UserId", session.UserId);
    cmd.Parameters.AddWithValue("@UserName", session.UserName ?? string.Empty);
    cmd.Parameters.AddWithValue("@Email", session.Email ?? string.Empty);
    cmd.Parameters.AddWithValue("@EnvironmentName", session.Environment ?? "Production");
    cmd.Parameters.AddWithValue("@BranchCode", session.BranchCode ?? string.Empty);
    cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
    cmd.Parameters.AddWithValue("@UserAgent", http.Request.Headers["User-Agent"].ToString());
    cmd.Parameters.AddWithValue("@IsPlatformOwner", session.IsPlatformOwner);
    await cmd.ExecuteNonQueryAsync();

    if (!session.IsPlatformOwner && !string.IsNullOrWhiteSpace(session.DatabaseName))
    {
        await using var tenantCon = await db.OpenTenantAsync(session.DatabaseName);
        await EnsureTenantUserSecuritySchemaAsync(tenantCon);
        await using var verifyUser = tenantCon.CreateCommand();
        verifyUser.CommandText = "UPDATE Users SET EmailVerified=1,UpdatedAt=SYSUTCDATETIME() WHERE UserId=@UserId AND LOWER(ISNULL(Email,''))=@Email";
        verifyUser.Parameters.AddWithValue("@UserId", session.UserId);
        verifyUser.Parameters.AddWithValue("@Email", NormalizeCloudEmail(session.Email));
        await verifyUser.ExecuteNonQueryAsync();
    }
    return sessionId;
}

static void SetTenantAuthCookie(HttpContext http, string token, int expiryMinutes = 15)
{
    http.Response.Cookies.Append("paynex_auth", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = IsSecureRequest(http),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(expiryMinutes, 5, 120)),
        IsEssential = true
    });
}

static void SetRefreshTokenCookie(HttpContext http, string token, DateTimeOffset expiresAt)
{
    http.Response.Cookies.Append("paynex_refresh", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = IsSecureRequest(http),
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Expires = expiresAt,
        IsEssential = true
    });
}

static void SetTrustedDeviceCookie(HttpContext http, string token, DateTimeOffset expiresAt)
{
    http.Response.Cookies.Append("paynex_trusted_device", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = IsSecureRequest(http),
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true
    });
}

static void SetCsrfCookie(HttpContext http)
{
    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    http.Response.Cookies.Append("paynex_csrf", csrf, new CookieOptions
    {
        HttpOnly = false,
        Secure = IsSecureRequest(http),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddDays(7),
        IsEssential = true
    });
}

static void ClearTenantAuthCookie(HttpContext http)
{
    http.Response.Cookies.Delete("paynex_auth", new CookieOptions { Path = "/", Secure = IsSecureRequest(http), SameSite = SameSiteMode.Lax });
}

static void ClearAuthenticationCookies(HttpContext http)
{
    ClearTenantAuthCookie(http);
    http.Response.Cookies.Delete("paynex_refresh", new CookieOptions { Path = "/api/auth", Secure = IsSecureRequest(http), SameSite = SameSiteMode.Strict });
    http.Response.Cookies.Delete("paynex_csrf", new CookieOptions { Path = "/", Secure = IsSecureRequest(http), SameSite = SameSiteMode.Lax });
}

static void ClearTrustedDeviceCookie(HttpContext http)
{
    http.Response.Cookies.Delete("paynex_trusted_device", new CookieOptions { Path = "/", Secure = IsSecureRequest(http), SameSite = SameSiteMode.Strict });
}

static bool IsSecureRequest(HttpContext http) =>
    http.Request.IsHttps || string.Equals(http.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase);

static bool FixedTimeEqualsString(string expected, string actual)
{
    var a = Encoding.UTF8.GetBytes(expected ?? string.Empty);
    var b = Encoding.UTF8.GetBytes(actual ?? string.Empty);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static async Task<string> CreatePlatformOwnerLoginSessionAsync(ConnectionFactory db, SuperAdminSession owner, HttpContext http, string environmentName)
{
    await EnsureMasterUserDirectorySchemaAsync(db);
    var sessionId = "OWN" + Guid.NewGuid().ToString("N");
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"INSERT INTO AuthSessionAudit(SessionId,TenantId,CompanyCode,UserId,UserName,Email,EventName,EnvironmentName,BranchCode,IpAddress,UserAgent)
VALUES(@SessionId,NULL,'PAYNEX',@UserId,@UserName,@Email,'LOGIN',@EnvironmentName,'PLATFORM',@IpAddress,@UserAgent)";
    cmd.Parameters.AddWithValue("@SessionId", sessionId);
    cmd.Parameters.AddWithValue("@UserId", owner.SuperAdminUserId);
    cmd.Parameters.AddWithValue("@UserName", owner.UserName);
    cmd.Parameters.AddWithValue("@Email", owner.Email ?? string.Empty);
    cmd.Parameters.AddWithValue("@EnvironmentName", environmentName);
    cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
    cmd.Parameters.AddWithValue("@UserAgent", http.Request.Headers["User-Agent"].ToString());
    await cmd.ExecuteNonQueryAsync();
    return sessionId;
}

static async Task<string> CreateTenantLoginSessionAsync(ConnectionFactory db, TenantInfo tenant, int userId, string userName, string email, HttpContext http, string environmentName, string branchCode)
{
    await EnsureMasterUserDirectorySchemaAsync(db);
    var sessionId = Guid.NewGuid().ToString("N");
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"INSERT INTO AuthSessionAudit(SessionId,TenantId,CompanyCode,UserId,UserName,Email,EventName,EnvironmentName,BranchCode,IpAddress,UserAgent)
VALUES(@SessionId,@TenantId,@CompanyCode,@UserId,@UserName,@Email,'LOGIN',@EnvironmentName,@BranchCode,@IpAddress,@UserAgent)";
    cmd.Parameters.AddWithValue("@SessionId", sessionId);
    cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
    cmd.Parameters.AddWithValue("@CompanyCode", tenant.CompanyCode);
    cmd.Parameters.AddWithValue("@UserId", userId);
    cmd.Parameters.AddWithValue("@UserName", userName);
    cmd.Parameters.AddWithValue("@Email", email ?? string.Empty);
    cmd.Parameters.AddWithValue("@EnvironmentName", environmentName);
    cmd.Parameters.AddWithValue("@BranchCode", branchCode);
    cmd.Parameters.AddWithValue("@IpAddress", http.Connection.RemoteIpAddress?.ToString() ?? string.Empty);
    cmd.Parameters.AddWithValue("@UserAgent", http.Request.Headers["User-Agent"].ToString());
    await cmd.ExecuteNonQueryAsync();
    return sessionId;
}

static async Task CloseTenantLoginSessionAsync(ConnectionFactory db, string? sessionId, string companyCode, int userId)
{
    await EnsureMasterUserDirectorySchemaAsync(db);
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"INSERT INTO AuthSessionAudit(SessionId,CompanyCode,UserId,EventName)
VALUES(@SessionId,@CompanyCode,@UserId,'LOGOUT')";
    cmd.Parameters.AddWithValue("@SessionId", sessionId ?? string.Empty);
    cmd.Parameters.AddWithValue("@CompanyCode", companyCode);
    cmd.Parameters.AddWithValue("@UserId", userId);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<bool> IsTenantSessionLoggedOutAsync(ConnectionFactory db, string? sessionId)
{
    if (string.IsNullOrWhiteSpace(sessionId)) return false;
    await EnsureMasterUserDirectorySchemaAsync(db);
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(1) FROM AuthSessionAudit WHERE SessionId=@SessionId AND EventName='LOGOUT'";
    cmd.Parameters.AddWithValue("@SessionId", sessionId);
    return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
}

static bool IsAcceptablePassword(string? password)
{
    if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 128) return false;
    return password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit);
}

static async Task<int> ResolvePosCustomerIdAsync(SqlConnection con, SqlTransaction tran, int requestedCustomerId)
{
    if (requestedCustomerId > 0)
    {
        await using var find = new SqlCommand("SELECT TOP 1 CustomerId FROM Customers WHERE CustomerId=@Id AND IsActive=1", con, tran);
        find.Parameters.AddWithValue("@Id", requestedCustomerId);
        var found = await find.ExecuteScalarAsync();
        if (found != null && found != DBNull.Value) return Convert.ToInt32(found);
    }
    return await PosSql.GetWalkInCustomerIdAsync(con, tran);
}

static string NormalizeCloudEmail(string? email)
{
    var value = (email ?? string.Empty).Trim().ToLowerInvariant();
    if (value.Length < 6 || value.Length > 180) return string.Empty;
    if (!value.Contains('@') || !value.Contains('.') || value.Contains(' ')) return string.Empty;
    return value;
}

/// <summary>
/// Email may be used on Cloud, Desktop, and Mobile inside one company.
/// Conflict only when the same email already belongs to a different company.
/// </summary>
static async Task<string?> FindGlobalEmailConflictAsync(
    ConnectionFactory db,
    string email,
    string? forCompanyCode = null,
    int? excludeTenantUserId = null,
    bool allowSameCompanyDirectory = false)
{
    email = NormalizeCloudEmail(email);
    if (string.IsNullOrWhiteSpace(email))
        return "A valid email address is required.";

    await EnsureMasterUserDirectorySchemaAsync(db);
    await EnsureCompanyMobileAppSchemaAsync(db);
    await using var master = await db.OpenMasterAsync();
    var company = (forCompanyCode ?? string.Empty).Trim();

    await using (var cmd = master.CreateCommand())
    {
        cmd.CommandText = @"
SELECT TOP 1 CompanyCode
FROM CentralUserDirectory
WHERE LOWER(LTRIM(RTRIM(Email))) = @Email
  AND (@ForCompanyCode = '' OR CompanyCode <> @ForCompanyCode)";
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@ForCompanyCode", company);
        var hit = Convert.ToString(await cmd.ExecuteScalarAsync());
        if (!string.IsNullOrWhiteSpace(hit))
            return $"This email is already registered for company {hit}. An email can be used on Cloud, Desktop, and Mobile for one company only.";
    }

    await using (var cmd = master.CreateCommand())
    {
        cmd.CommandText = @"
SELECT TOP 1 CompanyCode
FROM CompanyMobileAppUsers
WHERE LOWER(LTRIM(RTRIM(ISNULL(Email,'')))) = @Email
  AND (@ForCompanyCode = '' OR CompanyCode <> @ForCompanyCode)";
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@ForCompanyCode", company);
        var hit = Convert.ToString(await cmd.ExecuteScalarAsync());
        if (!string.IsNullOrWhiteSpace(hit))
            return $"This email is already registered for company {hit}. An email can be used on Cloud, Desktop, and Mobile for one company only.";
    }

    if (!allowSameCompanyDirectory && !string.IsNullOrWhiteSpace(company))
    {
        await using var cmd = master.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1 CompanyCode
FROM CentralUserDirectory
WHERE LOWER(LTRIM(RTRIM(Email))) = @Email
  AND CompanyCode = @ForCompanyCode
  AND (@ExcludeUserId IS NULL OR UserId <> @ExcludeUserId)";
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@ForCompanyCode", company);
        cmd.Parameters.AddWithValue("@ExcludeUserId", (object?)excludeTenantUserId ?? DBNull.Value);
        var hit = Convert.ToString(await cmd.ExecuteScalarAsync());
        if (!string.IsNullOrWhiteSpace(hit))
            return "This email is already a Cloud/Desktop login for this company.";
    }

    return null;
}

static string BuildMobilePermissionsJson(long mobileAppUserId)
{
    var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["mobile.access"] = true,
        ["mobile.sync"] = true,
        ["sales.createInvoice"] = true,
        ["sales.editInvoice"] = true,
        ["sales.postInvoice"] = true,
        ["sales.createReturn"] = true,
        ["purchase.createInvoice"] = true,
        ["purchase.editInvoice"] = true,
        ["purchase.postInvoice"] = true,
        ["finance.createExpense"] = true,
        ["__authChannel"] = "mobile",
        ["__mobileAppUserId"] = mobileAppUserId
    };
    return JsonSerializer.Serialize(payload);
}

static string? ReadAuthChannel(UserSession session)
{
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(session.PermissionsJson) ? "{}" : session.PermissionsJson);
        if (doc.RootElement.TryGetProperty("__authChannel", out var ch) && ch.ValueKind == JsonValueKind.String)
            return ch.GetString();
    }
    catch { /* ignore */ }
    if (session.SessionId.StartsWith("MOB-", StringComparison.OrdinalIgnoreCase)) return "mobile";
    if (session.SessionId.StartsWith("DESK-", StringComparison.OrdinalIgnoreCase)) return "desktop";
    return null;
}

static long ReadMobileAppUserId(UserSession session)
{
    try
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(session.PermissionsJson) ? "{}" : session.PermissionsJson);
        if (doc.RootElement.TryGetProperty("__mobileAppUserId", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n)) return n;
            if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out var s)) return s;
        }
    }
    catch { /* ignore */ }
    return 0;
}

static async Task MarkMobileEmailVerifiedAsync(ConnectionFactory db, long mobileAppUserId, string email, string companyCode)
{
    if (mobileAppUserId <= 0 && string.IsNullOrWhiteSpace(email)) return;
    await EnsureCompanyMobileAppSchemaAsync(db);
    await using var master = await db.OpenMasterAsync();
    await using var cmd = master.CreateCommand();
    cmd.CommandText = @"
UPDATE CompanyMobileAppUsers
SET EmailVerified=1, UpdatedAt=SYSUTCDATETIME()
WHERE (@MobileAppUserId > 0 AND MobileAppUserId=@MobileAppUserId)
   OR (CompanyCode=@CompanyCode AND LOWER(LTRIM(RTRIM(ISNULL(Email,''))))=@Email)";
    cmd.Parameters.AddWithValue("@MobileAppUserId", mobileAppUserId);
    cmd.Parameters.AddWithValue("@CompanyCode", companyCode ?? string.Empty);
    cmd.Parameters.AddWithValue("@Email", NormalizeCloudEmail(email));
    await cmd.ExecuteNonQueryAsync();
}

static bool IsPlatformOwnerEmail(string? email, PayNexOptions options) =>
    !string.IsNullOrWhiteSpace(email) &&
    string.Equals(NormalizeCloudEmail(email), NormalizeCloudEmail(options.PlatformOwnerEmail), StringComparison.OrdinalIgnoreCase);

static bool IsPlatformOwnerSession(UserSession user, PayNexOptions options) =>
    user.IsPlatformOwner;

static bool IsCompanySecurityAdmin(UserSession user) =>
    user.IsCompanySuperAdmin ||
    user.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
    user.RoleName.Equals("Company Super Admin", StringComparison.OrdinalIgnoreCase);

static string NormalizeMobileAppPlatform(string? platform)
{
    var value = (platform ?? string.Empty).Trim();
    if (value.Equals("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
    if (value.Equals("iOS", StringComparison.OrdinalIgnoreCase)) return "iOS";
    return "Both";
}

static async Task EnsureCompanyMobileAppSchemaAsync(ConnectionFactory db)
{
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF OBJECT_ID('CompanyMobileApps') IS NULL
BEGIN
CREATE TABLE CompanyMobileApps(
    AppId BIGINT IDENTITY(1,1) PRIMARY KEY,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL UNIQUE,
    AppName NVARCHAR(150) NOT NULL,
    Platform NVARCHAR(30) NOT NULL DEFAULT 'Both',
    PackageName NVARCHAR(180) NULL,
    BundleId NVARCHAR(180) NULL,
    AppVersion NVARCHAR(40) NULL,
    ApiKey NVARCHAR(80) NOT NULL,
    Status NVARCHAR(30) NOT NULL DEFAULT 'Active',
    IsBlocked BIT NOT NULL DEFAULT 0,
    BlockReason NVARCHAR(500) NULL,
    Notes NVARCHAR(500) NULL,
    RegisteredAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL,
    BlockedAt DATETIME2 NULL
);
END;
IF OBJECT_ID('CompanyMobileAppUsers') IS NULL
BEGIN
CREATE TABLE CompanyMobileAppUsers(
    MobileAppUserId BIGINT IDENTITY(1,1) PRIMARY KEY,
    AppId BIGINT NOT NULL,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL,
    UserName NVARCHAR(80) NOT NULL,
    DisplayName NVARCHAR(150) NOT NULL,
    Email NVARCHAR(180) NULL,
    Mobile NVARCHAR(40) NULL,
    PasswordHash NVARCHAR(500) NOT NULL,
    OwnerVisiblePassword NVARCHAR(128) NULL,
    RoleName NVARCHAR(80) NOT NULL DEFAULT 'Mobile User',
    IsBlocked BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    BlockReason NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL,
    BlockedAt DATETIME2 NULL,
    CONSTRAINT UQ_CompanyMobileAppUsers_CompanyUser UNIQUE(CompanyCode, UserName)
);
END;
IF COL_LENGTH('CompanyMobileAppUsers','OwnerVisiblePassword') IS NULL ALTER TABLE CompanyMobileAppUsers ADD OwnerVisiblePassword NVARCHAR(128) NULL;
IF COL_LENGTH('CompanyMobileAppUsers','EmailVerified') IS NULL ALTER TABLE CompanyMobileAppUsers ADD EmailVerified BIT NOT NULL CONSTRAINT DF_CompanyMobileAppUsers_EmailVerified DEFAULT 0;
BEGIN TRY
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_CompanyMobileAppUsers_Email' AND object_id=OBJECT_ID('CompanyMobileAppUsers'))
        EXEC(N'CREATE UNIQUE INDEX UX_CompanyMobileAppUsers_Email ON CompanyMobileAppUsers(Email) WHERE Email IS NOT NULL AND LTRIM(RTRIM(Email)) <> ''''');
END TRY BEGIN CATCH END CATCH";
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureMasterUserDirectorySchemaAsync(ConnectionFactory db)
{
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF OBJECT_ID('CentralUserDirectory') IS NULL
BEGIN
CREATE TABLE CentralUserDirectory(
    DirectoryUserId BIGINT IDENTITY(1,1) PRIMARY KEY,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL,
    UserId INT NOT NULL,
    Email NVARCHAR(180) NOT NULL,
    UserName NVARCHAR(80) NOT NULL,
    DisplayName NVARCHAR(150) NOT NULL,
    PasswordHash NVARCHAR(500) NOT NULL,
    EmailVerified BIT NOT NULL DEFAULT 0,
    RoleName NVARCHAR(80) NOT NULL DEFAULT 'Standard User',
    IsCompanySuperAdmin BIT NOT NULL DEFAULT 0,
    IsDefaultCompany BIT NOT NULL DEFAULT 1,
    IsActive BIT NOT NULL DEFAULT 1,
    LastLoginAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL
);
END;
IF COL_LENGTH('CentralUserDirectory','EmailVerified') IS NULL ALTER TABLE CentralUserDirectory ADD EmailVerified BIT NOT NULL CONSTRAINT DF_CUD_EmailVerified DEFAULT 0;
IF COL_LENGTH('CentralUserDirectory','IsCompanySuperAdmin') IS NULL ALTER TABLE CentralUserDirectory ADD IsCompanySuperAdmin BIT NOT NULL CONSTRAINT DF_CUD_IsCompanySuperAdmin DEFAULT 0;
IF COL_LENGTH('CentralUserDirectory','IsDefaultCompany') IS NULL ALTER TABLE CentralUserDirectory ADD IsDefaultCompany BIT NOT NULL CONSTRAINT DF_CUD_IsDefaultCompany DEFAULT 1;
IF COL_LENGTH('CentralUserDirectory','LastLoginAt') IS NULL ALTER TABLE CentralUserDirectory ADD LastLoginAt DATETIME2 NULL;
IF COL_LENGTH('CentralUserDirectory','UpdatedAt') IS NULL ALTER TABLE CentralUserDirectory ADD UpdatedAt DATETIME2 NULL;
BEGIN TRY
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_CentralUserDirectory_Email' AND object_id=OBJECT_ID('CentralUserDirectory'))
        EXEC(N'CREATE UNIQUE INDEX UX_CentralUserDirectory_Email ON CentralUserDirectory(Email)');
END TRY BEGIN CATCH END CATCH;
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

static (byte[] Bytes, string ContentType)? ParseUserProfileImage(string? source)
{
    if (string.IsNullOrWhiteSpace(source)) return null;
    var raw = source.Trim();
    var contentType = "image/png";
    if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        var comma = raw.IndexOf(',');
        if (comma <= 5) throw new InvalidOperationException("Invalid profile image data.");
        var metadata = raw[5..comma];
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Profile image must use base64 encoding.");
        contentType = metadata.Split(';', 2)[0].Trim().ToLowerInvariant();
        raw = raw[(comma + 1)..];
    }
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };
    if (!allowed.Contains(contentType)) throw new InvalidOperationException("Profile photo must be PNG, JPG, WEBP or GIF.");
    byte[] bytes;
    try { bytes = Convert.FromBase64String(raw); }
    catch (FormatException) { throw new InvalidOperationException("Invalid profile image file."); }
    if (bytes.Length == 0) throw new InvalidOperationException("Profile image file is empty.");
    if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Profile photo cannot exceed 2 MB.");
    return (bytes, contentType);
}

static async Task EnsureTenantUserSecuritySchemaAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF COL_LENGTH('Users','Email') IS NULL ALTER TABLE Users ADD Email NVARCHAR(180) NULL;
IF COL_LENGTH('Users','PhoneNumber') IS NULL ALTER TABLE Users ADD PhoneNumber NVARCHAR(40) NULL;
IF COL_LENGTH('Users','EmailVerified') IS NULL ALTER TABLE Users ADD EmailVerified BIT NOT NULL CONSTRAINT DF_Users_EmailVerified DEFAULT 0;
IF COL_LENGTH('Users','IsCompanySuperAdmin') IS NULL ALTER TABLE Users ADD IsCompanySuperAdmin BIT NOT NULL CONSTRAINT DF_Users_IsCompanySuperAdmin DEFAULT 0;
IF COL_LENGTH('Users','UpdatedAt') IS NULL ALTER TABLE Users ADD UpdatedAt DATETIME2 NULL;
IF COL_LENGTH('Users','ProfileImage') IS NULL ALTER TABLE Users ADD ProfileImage VARBINARY(MAX) NULL;
IF COL_LENGTH('Users','ProfileImageContentType') IS NULL ALTER TABLE Users ADD ProfileImageContentType NVARCHAR(80) NULL;
IF COL_LENGTH('Users','OwnerVisiblePassword') IS NULL ALTER TABLE Users ADD OwnerVisiblePassword NVARCHAR(128) NULL;
IF OBJECT_ID('UserPermissions') IS NULL
BEGIN
CREATE TABLE UserPermissions(
    UserId INT NOT NULL,
    PermissionKey NVARCHAR(120) NOT NULL,
    IsAllowed BIT NOT NULL DEFAULT 0,
    UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_UserPermissions PRIMARY KEY(UserId, PermissionKey)
);
END;
IF OBJECT_ID('UserBranchAssignments') IS NULL
BEGIN
CREATE TABLE UserBranchAssignments(
    UserId INT NOT NULL,
    StoreId INT NOT NULL,
    IsDefault BIT NOT NULL DEFAULT 0,
    CONSTRAINT PK_UserBranchAssignments PRIMARY KEY(UserId, StoreId)
);
END;
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Admin') INSERT INTO Roles(RoleName) VALUES('Admin');
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Company Super Admin') INSERT INTO Roles(RoleName) VALUES('Company Super Admin');
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Manager') INSERT INTO Roles(RoleName) VALUES('Manager');
IF NOT EXISTS(SELECT 1 FROM Roles WHERE RoleName='Cashier') INSERT INTO Roles(RoleName) VALUES('Cashier');
EXEC(N'UPDATE Users SET IsCompanySuperAdmin=1 WHERE UserName=''admin'' OR UserId=(SELECT MIN(UserId) FROM Users);');";
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string> AllocateNextCustomerCodeAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT ISNULL(MAX(TRY_CONVERT(INT,
    CASE
        WHEN CustomerCode LIKE 'C-%' THEN SUBSTRING(CustomerCode, 3, 32)
        WHEN CustomerCode LIKE 'C[0-9]%' THEN SUBSTRING(CustomerCode, 2, 32)
        ELSE NULL
    END)), 0) + 1
FROM Customers
WHERE CustomerCode LIKE 'C-%' OR CustomerCode LIKE 'C[0-9]%'";
    var next = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 1);
    if (next < 1) next = 1;
    string code;
    do
    {
        code = $"C-{next:0000}";
        await using var exists = con.CreateCommand();
        exists.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM Customers WHERE UPPER(LTRIM(RTRIM(CustomerCode)))=UPPER(@Code)) THEN 1 ELSE 0 END";
        exists.Parameters.AddWithValue("@Code", code);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0) return code;
        next++;
    } while (next < 1_000_000);
    return $"C-{DateTime.UtcNow:HHmmss}";
}

static async Task<string> AllocateNextVendorCodeAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
SELECT ISNULL(MAX(TRY_CONVERT(INT,
    CASE
        WHEN VendorCode LIKE 'V-%' THEN SUBSTRING(VendorCode, 3, 32)
        WHEN VendorCode LIKE 'V[0-9]%' THEN SUBSTRING(VendorCode, 2, 32)
        ELSE NULL
    END)), 0) + 1
FROM Vendors
WHERE VendorCode LIKE 'V-%' OR VendorCode LIKE 'V[0-9]%'";
    var next = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 1);
    if (next < 1) next = 1;
    string code;
    do
    {
        code = $"V-{next:0000}";
        await using var exists = con.CreateCommand();
        exists.CommandText = "SELECT CASE WHEN EXISTS(SELECT 1 FROM Vendors WHERE UPPER(LTRIM(RTRIM(VendorCode)))=UPPER(@Code)) THEN 1 ELSE 0 END";
        exists.Parameters.AddWithValue("@Code", code);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0) return code;
        next++;
    } while (next < 1_000_000);
    return $"V-{DateTime.UtcNow:HHmmss}";
}

static async Task UpsertCentralUserDirectoryAsync(ConnectionFactory db, UserSession actor, int tenantUserId, string email, string userName, string displayName, string passwordHash, string roleName, bool emailVerified, bool isCompanySuperAdmin, bool isActive)
{
    await EnsureMasterUserDirectorySchemaAsync(db);
    var tenant = await db.GetTenantByCodeAsync(actor.CompanyCode) ?? throw new InvalidOperationException("Company code not found.");
    await using var con = await db.OpenMasterAsync();
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"
IF EXISTS(SELECT 1 FROM CentralUserDirectory WHERE UserId=@UserId AND CompanyCode=@CompanyCode)
BEGIN
    UPDATE CentralUserDirectory
    SET TenantId=@TenantId,Email=@Email,UserName=@UserName,DisplayName=@DisplayName,
        PasswordHash=CASE WHEN @PasswordHash='' THEN PasswordHash ELSE @PasswordHash END,
        EmailVerified=@EmailVerified,RoleName=@RoleName,IsCompanySuperAdmin=@IsCompanySuperAdmin,IsActive=@IsActive,UpdatedAt=SYSUTCDATETIME()
    WHERE UserId=@UserId AND CompanyCode=@CompanyCode;
END
ELSE
BEGIN
    INSERT INTO CentralUserDirectory(TenantId,CompanyCode,UserId,Email,UserName,DisplayName,PasswordHash,EmailVerified,RoleName,IsCompanySuperAdmin,IsDefaultCompany,IsActive)
    VALUES(@TenantId,@CompanyCode,@UserId,@Email,@UserName,@DisplayName,@PasswordHash,@EmailVerified,@RoleName,@IsCompanySuperAdmin,1,@IsActive);
END";
    cmd.Parameters.AddWithValue("@TenantId", tenant.TenantId);
    cmd.Parameters.AddWithValue("@CompanyCode", actor.CompanyCode);
    cmd.Parameters.AddWithValue("@UserId", tenantUserId);
    cmd.Parameters.AddWithValue("@Email", email);
    cmd.Parameters.AddWithValue("@UserName", userName);
    cmd.Parameters.AddWithValue("@DisplayName", displayName);
    cmd.Parameters.AddWithValue("@PasswordHash", passwordHash ?? string.Empty);
    cmd.Parameters.AddWithValue("@RoleName", roleName);
    cmd.Parameters.AddWithValue("@EmailVerified", emailVerified);
    cmd.Parameters.AddWithValue("@IsCompanySuperAdmin", isCompanySuperAdmin);
    cmd.Parameters.AddWithValue("@IsActive", isActive);
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureTenantBranchSchemaAsync(SqlConnection con)
{
    static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // SQL Server compiles one batch before ALTER TABLE columns are visible.
    // Keep every ALTER/UPDATE in a separate command so existing old tenant DBs repair correctly.
    await ExecAsync(con, @"
IF OBJECT_ID('Stores') IS NULL
BEGIN
    CREATE TABLE Stores(
        StoreId INT IDENTITY(1,1) PRIMARY KEY,
        StoreCode NVARCHAR(30) NOT NULL UNIQUE,
        StoreName NVARCHAR(100) NOT NULL,
        AddressLine NVARCHAR(250) NULL,
        IsActive BIT NOT NULL DEFAULT 1
    );
END");
    await ExecAsync(con, "IF NOT EXISTS(SELECT 1 FROM Stores) INSERT INTO Stores(StoreCode,StoreName,AddressLine,IsActive) VALUES('MAIN','Main Branch','Main Branch',1);");
    await ExecAsync(con, "IF COL_LENGTH('Stores','BranchCode') IS NULL ALTER TABLE Stores ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF COL_LENGTH('Stores','BranchName') IS NULL ALTER TABLE Stores ADD BranchName NVARCHAR(100) NULL;");
    await ExecAsync(con, "IF COL_LENGTH('Stores','IsMainBranch') IS NULL ALTER TABLE Stores ADD IsMainBranch BIT NOT NULL CONSTRAINT DF_Stores_IsMainBranch DEFAULT 0;");
    await ExecAsync(con, "UPDATE Stores SET BranchCode=StoreCode WHERE BranchCode IS NULL OR BranchCode='';");
    await ExecAsync(con, "UPDATE Stores SET BranchName=StoreName WHERE BranchName IS NULL OR BranchName='';");
    await ExecAsync(con, "UPDATE Stores SET IsMainBranch=1 WHERE StoreCode='MAIN' OR StoreId=(SELECT MIN(StoreId) FROM Stores);");

    await ExecAsync(con, "IF OBJECT_ID('Shifts') IS NOT NULL AND COL_LENGTH('Shifts','BranchCode') IS NULL ALTER TABLE Shifts ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('SalesHeader') IS NOT NULL AND COL_LENGTH('SalesHeader','BranchCode') IS NULL ALTER TABLE SalesHeader ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('SalesInvoiceHeader') IS NOT NULL AND COL_LENGTH('SalesInvoiceHeader','BranchCode') IS NULL ALTER TABLE SalesInvoiceHeader ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('PurchaseInvoiceHeader') IS NOT NULL AND COL_LENGTH('PurchaseInvoiceHeader','BranchCode') IS NULL ALTER TABLE PurchaseInvoiceHeader ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('InventoryLedger') IS NOT NULL AND COL_LENGTH('InventoryLedger','BranchCode') IS NULL ALTER TABLE InventoryLedger ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('CustomerPayments') IS NOT NULL AND COL_LENGTH('CustomerPayments','BranchCode') IS NULL ALTER TABLE CustomerPayments ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('VendorPayments') IS NOT NULL AND COL_LENGTH('VendorPayments','BranchCode') IS NULL ALTER TABLE VendorPayments ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('GLEntries') IS NOT NULL AND COL_LENGTH('GLEntries','BranchCode') IS NULL ALTER TABLE GLEntries ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('StockAdjustments') IS NOT NULL AND COL_LENGTH('StockAdjustments','BranchCode') IS NULL ALTER TABLE StockAdjustments ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('StockTransfers') IS NOT NULL AND COL_LENGTH('StockTransfers','FromBranchCode') IS NULL ALTER TABLE StockTransfers ADD FromBranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('StockTransfers') IS NOT NULL AND COL_LENGTH('StockTransfers','ToBranchCode') IS NULL ALTER TABLE StockTransfers ADD ToBranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('HoldSalesHeader') IS NOT NULL AND COL_LENGTH('HoldSalesHeader','BranchCode') IS NULL ALTER TABLE HoldSalesHeader ADD BranchCode NVARCHAR(30) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('HoldSalesHeader') IS NOT NULL AND COL_LENGTH('HoldSalesHeader','Status') IS NULL ALTER TABLE HoldSalesHeader ADD Status NVARCHAR(30) NOT NULL CONSTRAINT DF_HoldSalesHeader_Status DEFAULT 'Hold';");
    await ExecAsync(con, "IF OBJECT_ID('HoldSalesHeader') IS NOT NULL AND COL_LENGTH('HoldSalesHeader','Remarks') IS NULL ALTER TABLE HoldSalesHeader ADD Remarks NVARCHAR(MAX) NULL;");
    await ExecAsync(con, "IF OBJECT_ID('HoldSalesHeader') IS NOT NULL AND COL_LENGTH('HoldSalesHeader','HoldDate') IS NULL ALTER TABLE HoldSalesHeader ADD HoldDate DATETIME2 NOT NULL CONSTRAINT DF_HoldSalesHeader_HoldDate DEFAULT SYSUTCDATETIME();");
    await ExecAsync(con, "IF OBJECT_ID('ReturnHeader') IS NOT NULL AND COL_LENGTH('ReturnHeader','BranchCode') IS NULL ALTER TABLE ReturnHeader ADD BranchCode NVARCHAR(30) NULL;");

    await ExecAsync(con, "IF OBJECT_ID('SalesHeader') IS NOT NULL UPDATE h SET BranchCode=s.StoreCode FROM SalesHeader h INNER JOIN Stores s ON s.StoreId=h.StoreId WHERE h.BranchCode IS NULL OR h.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('SalesInvoiceHeader') IS NOT NULL UPDATE h SET BranchCode=s.StoreCode FROM SalesInvoiceHeader h INNER JOIN Stores s ON s.StoreId=h.StoreId WHERE h.BranchCode IS NULL OR h.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('PurchaseInvoiceHeader') IS NOT NULL UPDATE h SET BranchCode=s.StoreCode FROM PurchaseInvoiceHeader h INNER JOIN Stores s ON s.StoreId=h.StoreId WHERE h.BranchCode IS NULL OR h.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('InventoryLedger') IS NOT NULL UPDATE l SET BranchCode=s.StoreCode FROM InventoryLedger l INNER JOIN Stores s ON s.StoreId=l.StoreId WHERE l.BranchCode IS NULL OR l.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('Shifts') IS NOT NULL UPDATE sh SET BranchCode=s.StoreCode FROM Shifts sh INNER JOIN Stores s ON s.StoreId=sh.StoreId WHERE sh.BranchCode IS NULL OR sh.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('ReturnHeader') IS NOT NULL UPDATE rh SET BranchCode=s.StoreCode FROM ReturnHeader rh INNER JOIN Stores s ON s.StoreId=rh.StoreId WHERE rh.BranchCode IS NULL OR rh.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('StockAdjustments') IS NOT NULL UPDATE a SET BranchCode=s.StoreCode FROM StockAdjustments a INNER JOIN Stores s ON s.StoreId=a.StoreId WHERE a.BranchCode IS NULL OR a.BranchCode='';");
    await ExecAsync(con, "IF OBJECT_ID('StockTransfers') IS NOT NULL UPDATE t SET FromBranchCode=fs.StoreCode, ToBranchCode=ts.StoreCode FROM StockTransfers t INNER JOIN Stores fs ON fs.StoreId=t.FromStoreId INNER JOIN Stores ts ON ts.StoreId=t.ToStoreId WHERE t.FromBranchCode IS NULL OR t.FromBranchCode='' OR t.ToBranchCode IS NULL OR t.ToBranchCode='';");
    await BankAccountService.EnsureSchemaAsync(con);
}

static async Task<List<Dictionary<string, object?>>> ReadBranchesAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT StoreId BranchId,StoreCode BranchCode,StoreName BranchName,AddressLine,IsActive,ISNULL(IsMainBranch,0) IsMainBranch,StoreCode + ' - ' + StoreName DisplayName FROM Stores WHERE IsActive=1 ORDER BY IsMainBranch DESC,StoreName";
    return await SqlList.ReadAsync(cmd);
}

static async Task<(int BranchId,string BranchCode,string BranchName)> GetMainBranchAsync(SqlConnection con)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 StoreId,StoreCode,StoreName FROM Stores WHERE IsActive=1 ORDER BY ISNULL(IsMainBranch,0) DESC, CASE WHEN StoreCode='MAIN' THEN 0 ELSE 1 END, StoreId";
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return (1,"MAIN","Main Branch");
    return (SqlRead.Int(r,"StoreId"), SqlRead.String(r,"StoreCode"), SqlRead.String(r,"StoreName"));
}

static async Task<(int BranchId,string BranchCode,string BranchName)> GetBranchByIdAsync(SqlConnection con, int branchId)
{
    await using var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT TOP 1 StoreId,StoreCode,StoreName FROM Stores WHERE StoreId=@BranchId AND IsActive=1";
    cmd.Parameters.AddWithValue("@BranchId", branchId);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return (0,"","");
    return (SqlRead.Int(r,"StoreId"), SqlRead.String(r,"StoreCode"), SqlRead.String(r,"StoreName"));
}

static async Task<(int BranchId,string BranchCode,string BranchName)> GetBranchOrMainAsync(SqlConnection con, int branchId)
{
    var branch = await GetBranchByIdAsync(con, branchId);
    return branch.BranchId > 0 ? branch : await GetMainBranchAsync(con);
}

static string NormalizeTenantEnvironment(string? environment)
{
    return string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase) ? "Sandbox" : "Production";
}

static string ResolveTenantDatabase(TenantInfo tenant, string environmentName)
{
    if (string.Equals(environmentName, "Sandbox", StringComparison.OrdinalIgnoreCase))
    {
        if (!tenant.AllowSandbox) throw new InvalidOperationException("Sandbox is not allowed for this company. Enable sandbox from Super Admin company card first.");
        if (string.IsNullOrWhiteSpace(tenant.SandboxDatabaseName)) throw new InvalidOperationException("Sandbox database is not created yet. Create sandbox from Super Admin company card first.");
        return tenant.SandboxDatabaseName;
    }
    return string.IsNullOrWhiteSpace(tenant.ProductionDatabaseName) ? tenant.DatabaseName : tenant.ProductionDatabaseName;
}

app.MapFallbackToFile("index.html");
try
{
    await app.Services.GetRequiredService<CredentialUniquenessService>().RepairSharedPasswordsAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Credential uniqueness repair did not complete.");
}

app.Run();
