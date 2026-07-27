# PayNex Cloud ERP - Authentication & Company Access Final

Implemented modern cloud ERP authentication design:

- Client users sign in using Email Address + Password only.
- Login no longer asks users to manually select Company ID / Company Code.
- Central user directory resolves Client/Tenant, Company, Database, User, Role and Company Super Admin flag automatically.
- Tenant user table now stores mandatory Email, EmailVerified and IsCompanySuperAdmin fields.
- User creation no longer requires OTP on User Card. The user is saved immediately and receives login details by email.
- Email ownership is verified by OTP on first login, then again after the 30-day trusted-browser period expires.
- Company Super Admins can create/edit/deactivate users, reset passwords, assign roles and promote additional Company Super Admins within their own company only.
- Platform Super Admin still owns the Super Admin Portal and assigns the first Company Super Admin during company registration.
- Sensitive operations are protected through Company Super Admin permission checks.

Local demo note: login OTP can return `devOtp` when the development option is enabled. Production must use the owner-managed Email & OTP Security Setup and keep the development option disabled.
