# PayNex Production Deployment Guidelines for Contabo

Configure PayNex on a **Contabo Windows VPS** with a strict split between application code, SQL Server databases, and backups.

Primary rule:

```text
CODE        C:\PayNex\app
DATABASE    SQL Server
BACKUPS     D:\SqlBackups
```

A normal code update must **not** wipe or replace SQL Server data.

## Directory structure

```text
C:\PayNex\app
├── PayNex.Cloud.Api.dll
├── wwwroot
├── database
├── appsettings.json
├── appsettings.Production.json   ← do not overwrite on deploy
└── other published application files

D:\SqlBackups
├── PayNex_MasterDB_*.bak
├── PayNex_PNX000001_DB_*.bak
└── ...
```

Copy `src/PayNex.Cloud.Api/appsettings.Production.sample.json` to the server as `C:\PayNex\app\appsettings.Production.json` and fill secrets there. Keep that file out of git.

## Database separation

```text
SQL Server
├── PayNex_MasterDB
├── PayNex_PNX000001_DB
├── PayNex_PNX000002_DB
└── ...
```

Application files and SQL databases are not the same deployment artifact.

Startup and provisioning only **create a missing database or missing table**. Existing databases, tables, and rows are preserved. There is no `DROP DATABASE` / recreate path in normal startup.

## IIS

| Setting | Value |
|---------|--------|
| Site name | PayNex |
| Physical path | `C:\PayNex\app` |
| App pool | No Managed Code |
| Environment | `ASPNETCORE_ENVIRONMENT=Production` (see `deploy/web.config`) |

Install the **ASP.NET Core 8 Hosting Bundle** on the server.

Tenant connections must keep `{database}` in `PayNex:TenantConnectionTemplate`. Do not hard-code one company database.

## Secrets

Keep these on the server only, not in source:

- SQL password
- Token secret (set once, keep stable)
- SMTP password
- Bootstrap passwords

`ResetBootstrapPasswordsOnStartup` must stay `false` in production so IIS restarts do not reset Owner / Super Admin passwords.

## Normal code deployment

```text
1. Backup SQL databases (deploy/backup-all-sql.sql)
        ↓
2. Stop IIS PayNex site
        ↓
3. Publish new build (deploy/publish-contabo.ps1)
        ↓
4. Replace application files
        ↓
5. Preserve appsettings.Production.json
        ↓
6. Never modify SQL Server DATA / MDF / LDF files
        ↓
7. Start IIS PayNex site
        ↓
8. Test website and /api/health
        ↓
9. Verify existing company / customer / invoice data
```

Replace: `*.dll`, `wwwroot`, published app files.  
Preserve: `appsettings.Production.json`, production secrets, `D:\SqlBackups`.

On the VPS:

```powershell
.\deploy\publish-contabo.ps1 -DeployToSite
```

## SQL backup

Run `deploy/backup-all-sql.sql` in SSMS before every production deploy and before schema migrations.

Do not keep the only backup copy inside `C:\PayNex\app`.

## Schema changes

Before any production schema change:

1. Backup SQL Server.
2. Test on a copy / staging database.
3. Apply additive scripts (`IF OBJECT_ID ... IS NULL`, `IF COL_LENGTH ... IS NULL`).
4. Verify existing data.

Do not use automatic destructive database recreation in production.
