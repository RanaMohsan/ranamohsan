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
    CONSTRAINT UQ_CompanyMobileAppUsers_CompanyUser UNIQUE(CompanyCode, UserName),
    CONSTRAINT FK_CompanyMobileAppUsers_App FOREIGN KEY(AppId) REFERENCES CompanyMobileApps(AppId)
);
END;
GO

-- Owner-only SaaS client subscription management (PayNex_MasterDB).
IF OBJECT_ID('SubscriptionPlans') IS NULL
BEGIN
CREATE TABLE SubscriptionPlans(
    SubscriptionPlanId INT IDENTITY(1,1) PRIMARY KEY,
    PlanCode NVARCHAR(30) NOT NULL UNIQUE,
    PlanName NVARCHAR(100) NOT NULL,
    DurationType NVARCHAR(20) NOT NULL CONSTRAINT DF_SubscriptionPlans_DurationType DEFAULT 'Month',
    DurationValue INT NOT NULL CONSTRAINT DF_SubscriptionPlans_DurationValue DEFAULT 1,
    DefaultAmount DECIMAL(18,2) NULL,
    IsActive BIT NOT NULL CONSTRAINT DF_SubscriptionPlans_IsActive DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SubscriptionPlans_CreatedAt DEFAULT SYSUTCDATETIME()
);
END;

IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'MONTHLY')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('MONTHLY', 'Monthly', 'Month', 1, NULL, 1);
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'QUARTERLY')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('QUARTERLY', 'Quarterly', 'Month', 3, NULL, 1);
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'HALFYEARLY')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('HALFYEARLY', 'Half Yearly', 'Month', 6, NULL, 1);
IF NOT EXISTS (SELECT 1 FROM SubscriptionPlans WHERE PlanCode = 'ANNUAL')
    INSERT INTO SubscriptionPlans(PlanCode, PlanName, DurationType, DurationValue, DefaultAmount, IsActive)
    VALUES ('ANNUAL', 'Annual', 'Month', 12, NULL, 1);

IF OBJECT_ID('ClientSubscriptions') IS NULL
BEGIN
CREATE TABLE ClientSubscriptions(
    SubscriptionId INT IDENTITY(1,1) PRIMARY KEY,
    SubscriptionNo NVARCHAR(30) NOT NULL UNIQUE,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL,
    SubscriptionPlanId INT NOT NULL,
    StartDate DATE NOT NULL,
    ExpiryDate DATE NOT NULL,
    Amount DECIMAL(18,2) NOT NULL CONSTRAINT DF_ClientSubscriptions_Amount DEFAULT 0,
    Currency NVARCHAR(10) NOT NULL CONSTRAINT DF_ClientSubscriptions_Currency DEFAULT 'PKR',
    PaymentStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_ClientSubscriptions_PaymentStatus DEFAULT 'Pending',
    SubscriptionStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_ClientSubscriptions_Status DEFAULT 'Active',
    CancellationDate DATE NULL,
    CancellationReason NVARCHAR(250) NULL,
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_ClientSubscriptions_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt DATETIME2 NULL,
    CONSTRAINT FK_ClientSubscriptions_Plan FOREIGN KEY(SubscriptionPlanId) REFERENCES SubscriptionPlans(SubscriptionPlanId)
);
CREATE INDEX IX_ClientSubscriptions_CompanyCode ON ClientSubscriptions(CompanyCode, SubscriptionStatus);
CREATE INDEX IX_ClientSubscriptions_ExpiryDate ON ClientSubscriptions(ExpiryDate, SubscriptionStatus);
END;

IF OBJECT_ID('SubscriptionHistory') IS NULL
BEGIN
CREATE TABLE SubscriptionHistory(
    HistoryId BIGINT IDENTITY(1,1) PRIMARY KEY,
    SubscriptionId INT NOT NULL,
    TenantId UNIQUEIDENTIFIER NOT NULL,
    CompanyCode NVARCHAR(40) NOT NULL,
    ActionType NVARCHAR(40) NOT NULL,
    PreviousExpiryDate DATE NULL,
    NewExpiryDate DATE NULL,
    Amount DECIMAL(18,2) NOT NULL CONSTRAINT DF_SubscriptionHistory_Amount DEFAULT 0,
    PlanId INT NULL,
    Notes NVARCHAR(500) NULL,
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SubscriptionHistory_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_SubscriptionHistory_Subscription FOREIGN KEY(SubscriptionId) REFERENCES ClientSubscriptions(SubscriptionId)
);
CREATE INDEX IX_SubscriptionHistory_SubscriptionId ON SubscriptionHistory(SubscriptionId, HistoryId DESC);
END;

IF OBJECT_ID('SubscriptionPayments') IS NULL
BEGIN
CREATE TABLE SubscriptionPayments(
    PaymentId BIGINT IDENTITY(1,1) PRIMARY KEY,
    SubscriptionId INT NOT NULL,
    HistoryId BIGINT NULL,
    PaymentDate DATE NOT NULL,
    Amount DECIMAL(18,2) NOT NULL CONSTRAINT DF_SubscriptionPayments_Amount DEFAULT 0,
    PaymentMethod NVARCHAR(40) NOT NULL CONSTRAINT DF_SubscriptionPayments_Method DEFAULT 'Cash',
    ReferenceNo NVARCHAR(80) NULL,
    Status NVARCHAR(20) NOT NULL CONSTRAINT DF_SubscriptionPayments_Status DEFAULT 'Paid',
    CreatedBy NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_SubscriptionPayments_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_SubscriptionPayments_Subscription FOREIGN KEY(SubscriptionId) REFERENCES ClientSubscriptions(SubscriptionId)
);
CREATE INDEX IX_SubscriptionPayments_SubscriptionId ON SubscriptionPayments(SubscriptionId, PaymentDate DESC);
CREATE INDEX IX_SubscriptionPayments_PaymentDate ON SubscriptionPayments(PaymentDate, Status);
END;

IF COL_LENGTH('Tenants','LastSubscriptionAmount') IS NULL
    ALTER TABLE Tenants ADD LastSubscriptionAmount DECIMAL(18,2) NULL;
GO
