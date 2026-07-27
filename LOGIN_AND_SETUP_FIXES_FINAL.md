# Final Login and Setup Fixes

Applied fixes:

1. Users page error `fmtDate is not defined` fixed by adding a date formatting helper.
2. Tax & Discount error `Invalid column name IsActive` fixed by auto-upgrading the tenant TaxGroups table before tax/discount reads and updates.
3. Login page links to Super Admin and SaaS Flow removed.
4. Super Admin credentials updated:
   - Company ID: 3032720768
   - Username: Mohsin-PayNex
   - Password: PayNex@123
5. The normal login page now detects these Super Admin credentials and opens the Super Admin control center.
6. Super Admin page now includes Company ID, Username, and Password fields.
7. Bootstrap logic now upserts the configured Super Admin user at startup, so the new credentials work even if an older superadmin user already exists.
