# Super Admin Simple List + Sandbox Final Fix

Completed items:

1. Super Admin page converted from large registration/dashboard layout into a simple company list page.
2. Clicking a company row opens a company card on the right side.
3. Company card now includes:
   - Company Start Date
   - License Expiry Date
   - Renewal Date
   - Status
   - License Status
   - Plan
   - Owner details
   - Production DB
   - Sandbox DB
   - Allow Sandbox
4. New company creation moved into a collapsible panel.
5. Sandbox setup added:
   - Allow Sandbox flag
   - Create Sandbox Now option
   - Create / Repair Sandbox button on company card
   - Sandbox database name pattern: PayNex_{CompanyCode}_SBX_DB
   - Sandbox database has separate schema and default admin user.
6. Production/Sandbox environment selector added to the 9-dot launcher on the top-left brand area.
7. Login page now supports Production/Sandbox selection.
8. Tenant user session now stores the selected environment and selected database.
9. New endpoints added:
   - GET /api/admin/tenants/{companyCode}
   - PUT /api/admin/tenants/{companyCode}
   - POST /api/admin/tenants/{companyCode}/sandbox
   - POST /api/auth/environment

Sandbox management approach:
- Production DB remains the live database.
- Sandbox DB is a separate database for testing/training.
- Sandbox is created only after enabling/creating it from Super Admin.
- The 9-dot launcher switches the current user session between Production and Sandbox.
