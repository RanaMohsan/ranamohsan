# Secure Password Management Update — 2026-07-13

## Implemented

- Platform Company Card now shows whether every company user has a password set.
- The PayNex Platform Owner can select **Set / Reset** for a company user without first opening the tenant workspace.
- After a successful set/reset, the newly entered password is available through **View / Copy once** for the current page only.
- User Card password entry now has a **Show / Hide** control.
- Saving or resetting a password on User Card displays a one-time password receipt with **Show**, **Copy**, and **Forget now** actions.
- Password validation requires 8–128 characters with upper-case, lower-case, and a number.
- Password changes synchronize the tenant user and Central User Directory password hashes and revoke existing login sessions/trusted devices.

## Security behavior

- Plain-text passwords are not stored in SQL Server, local storage, or session storage.
- Existing passwords cannot be retrieved because BCrypt password hashes are one-way.
- A one-time receipt contains only the password entered during the current successful save/reset.
- One-time receipts are erased on refresh, navigation/page hide, or **Forget now**.
- API responses never return the plain-text password.

## Local verification

JavaScript syntax checks:

```powershell
node --check .\src\PayNex.Cloud.Api\wwwroot\js\platform-company-card.js
node --check .\src\PayNex.Cloud.Api\wwwroot\js\user-card.js
```

.NET build and run:

```powershell
dotnet restore .\PayNex_Cloud_Final.sln
dotnet build .\PayNex_Cloud_Final.sln
dotnet run --project .\src\PayNex.Cloud.Api\PayNex.Cloud.Api.csproj --urls "http://localhost:5000"
```

The delivery environment did not contain the .NET SDK, so the final `dotnet build` must be run on the Windows machine with .NET 8 SDK installed.
