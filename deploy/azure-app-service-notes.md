# Azure deployment - PayNex Cloud

Recommended resources:

1. Azure SQL Server
2. Azure SQL Database for `PayNex_MasterDB`
3. Azure App Service for `PayNex.Cloud.Api`
4. Custom domain: `app.paynex.com`

In Azure App Service > Configuration, add these Application Settings:

```text
ASPNETCORE_ENVIRONMENT=Production
PayNex__TokenSecret=<strong-token-secret>
PayNex__EnableSwagger=false
PayNex__MasterDatabaseName=PayNex_MasterDB
PayNex__MasterServerConnection=Server=tcp:<server>.database.windows.net,1433;Database=master;User ID=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
PayNex__MasterDbConnection=Server=tcp:<server>.database.windows.net,1433;Database=PayNex_MasterDB;User ID=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
PayNex__TenantConnectionTemplate=Server=tcp:<server>.database.windows.net,1433;Database={database};User ID=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
PayNex__AllowedCorsOrigins__0=https://app.paynex.com
PayNex__PlatformOwnerBootstrapPassword=<strong-owner-password>
PayNex__SuperAdminBootstrapPassword=<strong-admin-password>
```

Owner-only live API list after deploy: `https://<your-app>/api.html` (left menu **API**).

First run creates/updates `PayNex_MasterDB` schema. When you create a company from `/admin.html`, it creates that company's own database, applies tenant schema, and seeds default POS data.
