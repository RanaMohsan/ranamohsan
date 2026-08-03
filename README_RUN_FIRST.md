# PayNex Cloud Final - All Requirements Build

## Run
```bash
cd src/PayNex.Cloud.Api
dotnet restore
dotnet run
```

Open:
- Login: `http://localhost:5000/login.html`
- Platform Owner Portal: `http://localhost:5000/platform-portal.html`
- Owner API Catalog: `http://localhost:5000/api.html` (left menu **API**, Platform Owner only)
- Owner Email & OTP Setup: `http://localhost:5000/owner-email-security.html`
- Super Admin: `http://localhost:5000/admin.html`
- Workspace: `http://localhost:5000/workspace.html`

## First login
Create a company from Super Admin. The system auto-generates Company ID such as `PNX000001`, creates the client database, and emails the first administrator's login details from **Email & OTP Security Setup**.

Default login for each new company:
- Username: `admin`
- Password: `Admin@123`

Demo users also seeded:
- `manager` / `Admin@123`
- `cashier` / `Admin@123`

## Main pages
- `/workspace.html` ERP workspace
- `/pos.html` POS billing
- `/picture-sales.html` picture sales
- `/sales.html` sales invoice and posted sales
- `/purchases.html` purchase invoice and posted purchase
- `/customers.html` customer master/payment/ledger
- `/vendors.html` vendor master/payment/ledger
- `/inventory.html` stock transfer and physical adjustment
- `/shift.html` shift/cash drawer/Z report
- `/finance.html` chart of accounts, G/L, accounting reports
- `/dashboard.html` business analytics
- `/company.html` company information
- `/settings.html` tax setup, posting setup, periods, audit, backup/restore actions
- `/reports.html` printable documents
- `/configuration-packages.html` Excel package import/export for Item, Customer and Vendor master data and balances
- `/expenses.html` lightweight branch-aware expense management and category setup
- `/expense-report.html` filtered expense report with print preview and Save as PDF


## Final UI Design Update
The final package includes the modern ERP/POS workspace theme, dark navy sidebar, white cards, cyan/green/red button system, dashboard styling, POS cashier layout, picture sales tiles, finance grids, login design and print-friendly reports. See `docs/UI_DESIGN_REQUIREMENTS.md`.

## Multi-Client SaaS Flow

Use `/admin.html` to register a new client/company. The system will auto-generate a company code, create a separate client database, apply default setup, seed roles/users/accounts/tax/payment methods/number series, and activate the client.

Use `/login.html` for client login. Users enter their registered **email address and password**. The server resolves the company from the central user directory, so the browser does not select or submit a trusted company context.

On first login, on a new browser/device, or after the trust period expires, the user must enter the verification code sent to the registered email. Successful verification automatically trusts that browser for 30 days by default.

Each client opens only its own company database and cannot access another client's data.

Business design details are available in:

```text
docs/MULTI_CLIENT_SAAS_DESIGN.md
docs/SUPER_ADMIN_CLIENT_ONBOARDING_CHECKLIST.md
```

---

## Professional hardened build notes

This package now includes protected Super Admin onboarding and production-hardening features.

### Super Admin portal

Open:

```text
/src/PayNex.Cloud.Api/wwwroot/admin.html
```

Fresh local bootstrap login:

```text
Username: Mohsin-PayNex
Password: PayNex@123
```

Fresh local Platform Owner login:

```text
Email: configured in PayNex:PlatformOwnerEmail
Password: PayNex@123
```

Existing databases keep their current password hash; startup no longer resets it from configuration.

Change this immediately before first production run using:

```text
PayNex:SuperAdminBootstrapPassword
```

or environment variable:

```text
PayNex__SuperAdminBootstrapPassword
```

### Secure login and owner OTP sender

The browser login now uses email/password, first-login OTP, automatic 30-day browser trust, rotating refresh tokens, HTTP-only cookies, CSRF checks, failed-login lockout, and authentication auditing.

Only the Platform Owner can open `/owner-email-security.html` and configure the sender email, SMTP login/app password, OTP expiry, and trusted-device duration.

Read:

```text
LOGIN_LOGOUT_AUTH_SECURITY_ENHANCEMENT_20260712.md
OTP_EMAIL_SENDER_SETUP_20260708.md
```

### Important new APIs

```text
POST /api/admin/auth/login
GET  /api/admin/tenants
POST /api/admin/tenants
POST /api/admin/tenants/{companyCode}/status
GET  /api/security/permission-matrix
GET  /api/approvals
POST /api/approvals/{id}/approve
POST /api/approvals/{id}/reject
POST /api/settings/backup
POST /api/settings/restore
GET  /api/accounting/report/trial-balance
GET  /api/accounting/report/account-summary
GET  /api/accounting/report/inventory-valuation-detail
```

### Restore confirmation payload

```json
{
  "backupReference": "BKP-20260707120000",
  "confirmText": "RESTORE DATABASE",
  "remarks": "Approved restore"
}
```

### Production checklist

Read:

```text
docs/PROFESSIONAL_HARDENING_CHANGELOG.md
docs/DEPLOYMENT_HARDENING.md
```

Use the smoke test file:

```text
tests/PayNex.Api.SmokeTests.http
```
