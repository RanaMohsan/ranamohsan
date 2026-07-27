# PayNex User Management, Permissions, Authentication Final

## Completed

- Users List page converted to a standard ERP list page.
- User Card page supports user details, password setup/reset, branch assignment and permissions; the old pre-save email OTP panel has been removed.
- Email is mandatory for user creation.
- New users are saved immediately and receive their login email and initial password through the owner-managed sender mailbox.
- OTP verifies the registered email on first login and again after the trusted-device period expires.
- Permission toggles added by category:
  - Sales
  - Purchases
  - Inventory
  - Pricing
  - Finance
  - User Management
  - Reports
  - System
- Permission model is stored in tenant database table `UserPermissions`.
- Branch assignments are stored in tenant database table `UserBranchAssignments`.
- Company Super Admin/Admin bypasses normal permission checks inside their own company only.
- Server-side route authorization added for protected APIs.
- Static ERP HTML pages require authenticated session and redirect to `/login.html` when opened directly without login.
- Login uses email + password and automatically resolves company/database from central directory. First-login OTP marks the email as verified.
- Session token contains company, branch, role, permissions and session id.
- Logout invalidates tenant session, clears auth cookie and clears browser token.
- Login/logout audit table added in master DB: `AuthSessionAudit`.
- Protected pages use no-cache headers to reduce back-button access after logout.

## Notes

- The PayNex owner email remains restricted to the Super Admin portal only and is not added to tenant/company user access.
- Credential and OTP email sending uses the Platform Owner's **Email & OTP Security Setup**, with `appsettings.json` as a local fallback.
