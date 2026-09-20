-- Master database tables for PayNex Desktop access control.
-- Created automatically by DesktopAccessService.EnsureSchemaAsync on first use.

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
END
