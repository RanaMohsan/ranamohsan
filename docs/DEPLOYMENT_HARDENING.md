# Deployment Hardening Checklist

Use this checklist before exposing PayNex Cloud publicly.

1. Set strong secrets through environment variables:
   - `PayNex__TokenSecret`
   - `PayNex__SuperAdminBootstrapPassword`
   - SQL connection strings
2. Set `PayNex__AllowedCorsOrigins__0=https://yourdomain.com`.
3. Run SQL Server with least privilege; the application login should not be `sa`.
4. Ensure SQL Server backup folder exists and is writable by the SQL Server service account.
5. Use HTTPS only and place the app behind a reverse proxy or Azure App Service.
6. Keep `PayNex__EnableSwagger=false` in production. Use the owner-only page `/api.html` (left menu **API**) for the live PayNex API catalog.
7. Configure centralized logging: Application Insights, Seq, ELK, or Azure Monitor.
8. Schedule database backups and test restore quarterly.
9. Review the role permission matrix with the client before go-live.
10. Lock accounting periods after monthly closing.
