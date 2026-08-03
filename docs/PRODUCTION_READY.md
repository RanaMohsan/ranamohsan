# PayNex Cloud — Production Deploy Ready

## What was finalized
- Owner-only **API** page at `/api.html` (left sidebar under **Owner → API**)
- Live catalog from `GET /api/platform/api-catalog` (all registered `/api/*` routes)
- Swagger **disabled in Production** unless `PayNex__EnableSwagger=true`
- Production sample settings and Azure notes updated

## Run locally
```powershell
cd src\PayNex.Cloud.Api
dotnet run --urls "http://localhost:5000"
```
Open: `http://localhost:5000/api.html` (login as Platform Owner)

## Deploy checklist
1. Set `ASPNETCORE_ENVIRONMENT=Production`
2. Set strong secrets (`PayNex__TokenSecret`, owner/admin bootstrap passwords, SQL passwords)
3. Set `PayNex__EnableSwagger=false`
4. Set `PayNex__AllowedCorsOrigins__0=https://your-domain`
5. Use HTTPS (Azure App Service / reverse proxy)
6. Follow `docs/DEPLOYMENT_HARDENING.md` and `deploy/azure-app-service-notes.md`

## Docker
```bash
cd deploy
docker compose up -d --build
```
API listens on container port **8080**.

## API page access
Only **Platform Owner** sessions can open `/api.html` and call `/api/platform/api-catalog`.
Company users do not see the **API** menu item.
