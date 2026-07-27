# PayNex Professional Hardening Changelog

This build adds the requested professional ERP hardening items while keeping the existing SMB ERP/POS functionality stable.

## Added

### Security
- Super Admin authentication for `/api/admin/*` routes.
- Separate Super Admin token stored in `paynex_admin_token` on the admin portal.
- JWT-style HMAC-SHA256 token format with token type separation for tenant users vs super admins.
- Salted PBKDF2-HMAC-SHA256 password hashing with legacy SHA256 verification support for old tenant users.
- Restricted CORS: configured origins are used; fallback allows localhost only.
- Global exception middleware to avoid leaking raw exception details.

### Authorization and permissions
- Role permission middleware for Admin, Manager, and Cashier.
- Admin-only controls for settings, accounting setup, backup/restore, period close, and reversals.
- Manager/Admin controls for purchases, vendors, inventory adjustment/transfer, and posting setup.

### Audit and governance
- Mutation audit middleware records successful non-GET API changes into `AuditLog`.
- Master security audit log records Super Admin login attempts.
- Approval endpoints added:
  - `GET /api/approvals`
  - `POST /api/approvals/{id}/approve`
  - `POST /api/approvals/{id}/reject`

### Backup and restore
- Real SQL Server `BACKUP DATABASE` operation added behind `/api/settings/backup`.
- Confirmed restore added behind `/api/settings/restore`; request must include `ConfirmText = "RESTORE DATABASE"`.
- Backup/restore history tables added.
- Backup path is controlled by `PayNex:BackupFolder`.

### Accounting controls and financial reports
- Closed accounting periods now block G/L posting through the shared posting helper.
- Existing reversal endpoint remains protected and uses the same closed-period control.
- Additional reports added:
  - Trial Balance
  - Account Summary
  - Inventory Valuation Detail

### Program structure
- New code is split into professional folders:
  - `Security`
  - `Middleware`
  - `Validation`
  - `Endpoints`
  - `Services`
- Super Admin endpoints are now moved out of `Program.cs` into `Endpoints/SuperAdminEndpoints.cs`.
- New professional finance/governance endpoints are in `Endpoints/ProfessionalEndpointExtensions.cs`.

## Important production notes

1. Change `PayNex:TokenSecret` before deployment.
2. Change `PayNex:SuperAdminBootstrapPassword` before first run.
3. Configure `PayNex:AllowedCorsOrigins` to the real domain only.
4. SQL Server service account must have write permission to `PayNex:BackupFolder` or the backup will fail.
5. For production, store secrets in Azure App Service Configuration, Key Vault, Docker secrets, or environment variables. Do not keep production secrets in `appsettings.json`.


## Focused ERP Home Actions Update
- Added dedicated Item Master page (`/items.html`) using existing product/item APIs.
- Replaced the professional home tiles with focused SMB ERP actions only: Item, Customer, Vendor, Sales Invoice, Posted Sales Invoices, Purchase Invoice, Posted Purchase Invoices, Customer Payment, Vendor Payment, Customer Ledger Entry, Vendor Ledger Entry.
- Simplified the left navigation to core ERP actions and Super Admin. Existing extra operational pages remain in the project but are no longer promoted on the home page.

## Branding and Home Actions Update
- Removed third-party ERP branding wording from user-facing UI and project notes.
- Updated application subtitle to neutral Cloud SaaS ERP branding.
- Added previously removed home actions back after the current core ERP actions under Additional ERP Actions.
- Added Additional ERP Actions to the left Actions menu after the current core menu.
