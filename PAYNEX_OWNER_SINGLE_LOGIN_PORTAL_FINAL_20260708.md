# PayNex Owner Single Login Portal Fix

Implemented the corrected owner access architecture:

- There is only one login page: `/login.html`.
- `ranamohsanali3@gmail.com / PayNex@123` logs in through the same login page.
- This owner account is not inserted into any client/company tenant database.
- The owner receives a platform-owner session only.
- `/admin.html` is no longer a public/separate admin portal. It redirects authenticated users to the integrated portal/workspace.
- The integrated owner-only page is `/platform-portal.html` and is visible only to the platform owner session.
- Company users log in with email/password, verify the registered email by OTP on first login, and are automatically routed to their assigned company database.

Owner portal includes:

- All client/company list.
- Company details card.
- Company users list.
- Company branch list.
- Central user directory view.
- Passwords are not displayed because they are securely stored as hashes; use reset password for recovery.

Security:

- Static ERP pages require authentication.
- Platform endpoints require `IsPlatformOwner=true`.
- Owner account cannot access tenant/company ERP as a tenant user unless explicitly created separately under a different authorized company account.
