IF DB_ID('PayNex_MasterDB') IS NULL
BEGIN
    PRINT 'Create PayNex_MasterDB from application startup using configured master database name.';
END;

IF OBJECT_ID('Tenants') IS NULL
BEGIN
CREATE TABLE Tenants(
    TenantId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
    CompanyCode NVARCHAR(40) NOT NULL UNIQUE,
    CompanyName NVARCHAR(180) NOT NULL,
    Slug NVARCHAR(80) NOT NULL UNIQUE,
    DatabaseName NVARCHAR(128) NOT NULL UNIQUE,
    ProductionDatabaseName NVARCHAR(128) NULL,
    SandboxDatabaseName NVARCHAR(128) NULL,
    SandboxCreatedAt DATETIME2 NULL,
    AllowSandbox BIT NOT NULL DEFAULT 0,
    AllowMultipleBranches BIT NOT NULL DEFAULT 0,
    ActiveEnvironment NVARCHAR(20) NOT NULL DEFAULT 'Production',
    OwnerName NVARCHAR(150) NULL,
    OwnerEmail NVARCHAR(180) NULL,
    OwnerMobile NVARCHAR(40) NULL,
    AdminContactName NVARCHAR(150) NULL,
    AdminContactEmail NVARCHAR(180) NULL,
    AdminContactMobile NVARCHAR(40) NULL,
    SubscriptionPlan NVARCHAR(50) NOT NULL DEFAULT 'Standard',
    LicenseStatus NVARCHAR(30) NOT NULL DEFAULT 'Active',
    MaxBranches INT NOT NULL DEFAULT 1,
    MaxUsers INT NOT NULL DEFAULT 5,
    MaxCounters INT NOT NULL DEFAULT 2,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Active',
    TrialStartDate DATE NULL,
    TrialEndDate DATE NULL,
    RenewalDate DATE NULL,
    ExpiryDate DATE NULL,
    CompanyStartDate DATE NULL,
    LicenseExpiryDate DATE NULL,
    DatabaseCreationStatus NVARCHAR(30) NOT NULL DEFAULT 'Pending',
    ProvisioningStatus NVARCHAR(30) NOT NULL DEFAULT 'Pending',
    LastProvisionedAt DATETIME2 NULL,
    LastLoginAt DATETIME2 NULL,
    Notes NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL
);
END;

IF COL_LENGTH('Tenants','AdminContactName') IS NULL ALTER TABLE Tenants ADD AdminContactName NVARCHAR(150) NULL;
IF COL_LENGTH('Tenants','AdminContactEmail') IS NULL ALTER TABLE Tenants ADD AdminContactEmail NVARCHAR(180) NULL;
IF COL_LENGTH('Tenants','AdminContactMobile') IS NULL ALTER TABLE Tenants ADD AdminContactMobile NVARCHAR(40) NULL;
IF COL_LENGTH('Tenants','LicenseStatus') IS NULL ALTER TABLE Tenants ADD LicenseStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_Tenants_LicenseStatus DEFAULT 'Active';
IF COL_LENGTH('Tenants','TrialStartDate') IS NULL ALTER TABLE Tenants ADD TrialStartDate DATE NULL;
IF COL_LENGTH('Tenants','TrialEndDate') IS NULL ALTER TABLE Tenants ADD TrialEndDate DATE NULL;
IF COL_LENGTH('Tenants','RenewalDate') IS NULL ALTER TABLE Tenants ADD RenewalDate DATE NULL;
IF COL_LENGTH('Tenants','DatabaseCreationStatus') IS NULL ALTER TABLE Tenants ADD DatabaseCreationStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_Tenants_DatabaseCreationStatus DEFAULT 'Ready';
IF COL_LENGTH('Tenants','ProvisioningStatus') IS NULL ALTER TABLE Tenants ADD ProvisioningStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_Tenants_ProvisioningStatus DEFAULT 'Completed';
IF COL_LENGTH('Tenants','LastProvisionedAt') IS NULL ALTER TABLE Tenants ADD LastProvisionedAt DATETIME2 NULL;
IF COL_LENGTH('Tenants','LastLoginAt') IS NULL ALTER TABLE Tenants ADD LastLoginAt DATETIME2 NULL;
IF COL_LENGTH('Tenants','Notes') IS NULL ALTER TABLE Tenants ADD Notes NVARCHAR(500) NULL;
IF COL_LENGTH('Tenants','ProductionDatabaseName') IS NULL ALTER TABLE Tenants ADD ProductionDatabaseName NVARCHAR(128) NULL;
IF COL_LENGTH('Tenants','SandboxDatabaseName') IS NULL ALTER TABLE Tenants ADD SandboxDatabaseName NVARCHAR(128) NULL;
IF COL_LENGTH('Tenants','SandboxCreatedAt') IS NULL ALTER TABLE Tenants ADD SandboxCreatedAt DATETIME2 NULL;
IF COL_LENGTH('Tenants','AllowSandbox') IS NULL ALTER TABLE Tenants ADD AllowSandbox BIT NOT NULL CONSTRAINT DF_Tenants_AllowSandbox DEFAULT 0;
IF COL_LENGTH('Tenants','AllowMultipleBranches') IS NULL ALTER TABLE Tenants ADD AllowMultipleBranches BIT NOT NULL CONSTRAINT DF_Tenants_AllowMultipleBranches DEFAULT 0;
IF COL_LENGTH('Tenants','MaxBranches') IS NULL ALTER TABLE Tenants ADD MaxBranches INT NOT NULL CONSTRAINT DF_Tenants_MaxBranches DEFAULT 1;
IF COL_LENGTH('Tenants','ActiveEnvironment') IS NULL ALTER TABLE Tenants ADD ActiveEnvironment NVARCHAR(20) NOT NULL CONSTRAINT DF_Tenants_ActiveEnvironment DEFAULT 'Production';
IF COL_LENGTH('Tenants','CompanyStartDate') IS NULL ALTER TABLE Tenants ADD CompanyStartDate DATE NULL;
IF COL_LENGTH('Tenants','LicenseExpiryDate') IS NULL ALTER TABLE Tenants ADD LicenseExpiryDate DATE NULL;
GO
EXEC('UPDATE Tenants SET ProductionDatabaseName=DatabaseName WHERE ProductionDatabaseName IS NULL OR ProductionDatabaseName=''''');
EXEC('UPDATE Tenants SET CompanyStartDate=CAST(CreatedAt AS DATE) WHERE CompanyStartDate IS NULL');
EXEC('UPDATE Tenants SET LicenseExpiryDate=ExpiryDate WHERE LicenseExpiryDate IS NULL AND ExpiryDate IS NOT NULL');

IF OBJECT_ID('TenantAuditLog') IS NULL
BEGIN
CREATE TABLE TenantAuditLog(
    AuditId BIGINT IDENTITY(1,1) PRIMARY KEY,
    TenantId UNIQUEIDENTIFIER NULL,
    CompanyCode NVARCHAR(40) NULL,
    ActionName NVARCHAR(80) NOT NULL,
    Description NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF COL_LENGTH('TenantAuditLog','CompanyCode') IS NULL ALTER TABLE TenantAuditLog ADD CompanyCode NVARCHAR(40) NULL;


-- Professional security hardening: platform super-admin users and audit trail.
IF OBJECT_ID('SuperAdminUsers') IS NULL
BEGIN
CREATE TABLE SuperAdminUsers(
    SuperAdminUserId INT IDENTITY(1,1) PRIMARY KEY,
    UserName NVARCHAR(80) NOT NULL UNIQUE,
    DisplayName NVARCHAR(150) NOT NULL,
    Email NVARCHAR(180) NULL,
    PasswordHash NVARCHAR(500) NOT NULL,
    RoleName NVARCHAR(40) NOT NULL DEFAULT 'SuperAdmin',
    IsActive BIT NOT NULL DEFAULT 1,
    LastLoginAt DATETIME2 NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;

IF OBJECT_ID('SecurityAuditLog') IS NULL
BEGIN
CREATE TABLE SecurityAuditLog(
    SecurityAuditId BIGINT IDENTITY(1,1) PRIMARY KEY,
    UserName NVARCHAR(100) NULL,
    ActionName NVARCHAR(120) NOT NULL,
    Path NVARCHAR(250) NULL,
    IpAddress NVARCHAR(80) NULL,
    Result NVARCHAR(30) NOT NULL,
    Details NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
END;
GO


-- Central cloud ERP user directory. Users log in with email/password; company DB is resolved automatically.
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
    UpdatedAt DATETIME2 NULL,
    CONSTRAINT UQ_CentralUserDirectory_EmailCompany UNIQUE(Email, CompanyCode)
);
END;
IF COL_LENGTH('CentralUserDirectory','TenantId') IS NULL ALTER TABLE CentralUserDirectory ADD TenantId UNIQUEIDENTIFIER NULL;
IF COL_LENGTH('CentralUserDirectory','CompanyCode') IS NULL ALTER TABLE CentralUserDirectory ADD CompanyCode NVARCHAR(40) NULL;
IF COL_LENGTH('CentralUserDirectory','EmailVerified') IS NULL ALTER TABLE CentralUserDirectory ADD EmailVerified BIT NOT NULL CONSTRAINT DF_CentralUserDirectory_EmailVerified DEFAULT 0;
IF COL_LENGTH('CentralUserDirectory','IsCompanySuperAdmin') IS NULL ALTER TABLE CentralUserDirectory ADD IsCompanySuperAdmin BIT NOT NULL CONSTRAINT DF_CentralUserDirectory_IsCompanySuperAdmin DEFAULT 0;
IF COL_LENGTH('CentralUserDirectory','IsDefaultCompany') IS NULL ALTER TABLE CentralUserDirectory ADD IsDefaultCompany BIT NOT NULL CONSTRAINT DF_CentralUserDirectory_IsDefaultCompany DEFAULT 1;
IF COL_LENGTH('CentralUserDirectory','LastLoginAt') IS NULL ALTER TABLE CentralUserDirectory ADD LastLoginAt DATETIME2 NULL;
IF COL_LENGTH('CentralUserDirectory','UpdatedAt') IS NULL ALTER TABLE CentralUserDirectory ADD UpdatedAt DATETIME2 NULL;

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
END;

GO

-- Enterprise authentication security: owner-managed OTP sender, login MFA, trusted devices and refresh-token rotation.
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
GO

-- Owner portal: per-company mobile app registration, users, and block controls.
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
    RoleName NVARCHAR(80) NOT NULL DEFAULT 'Mobile User',
    IsBlocked BIT NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1,
    BlockReason NVARCHAR(500) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL,
    BlockedAt DATETIME2 NULL,
    CONSTRAINT UQ_CompanyMobileAppUsers_UserName UNIQUE(UserName),
    CONSTRAINT FK_CompanyMobileAppUsers_App FOREIGN KEY(AppId) REFERENCES CompanyMobileApps(AppId)
);
END;
IF COL_LENGTH('CompanyMobileAppUsers','PasswordProtected') IS NULL
    ALTER TABLE CompanyMobileAppUsers ADD PasswordProtected NVARCHAR(MAX) NULL;
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_CompanyMobileAppUsers_CompanyUser' AND parent_object_id = OBJECT_ID('CompanyMobileAppUsers'))
    ALTER TABLE CompanyMobileAppUsers DROP CONSTRAINT UQ_CompanyMobileAppUsers_CompanyUser;
IF OBJECT_ID('CompanyMobileAppUsers') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_CompanyMobileAppUsers_UserName' AND parent_object_id = OBJECT_ID('CompanyMobileAppUsers'))
   AND NOT EXISTS (SELECT 1 FROM CompanyMobileAppUsers GROUP BY UserName HAVING COUNT(1) > 1)
    ALTER TABLE CompanyMobileAppUsers ADD CONSTRAINT UQ_CompanyMobileAppUsers_UserName UNIQUE(UserName);
GO
