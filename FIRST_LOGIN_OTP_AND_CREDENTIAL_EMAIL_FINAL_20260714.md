# First Login OTP & Credential Email — Final 2026-07-14

## Final flow

1. The Platform Owner registers a client company and enters the first administrator email and password.
2. PayNex creates the company/user and emails those login details from **Email & OTP Security Setup**.
3. Company administrators can create users directly from User Card; pre-save email verification has been removed.
4. A newly created user receives the login email and initial password from the same configured sender mailbox.
5. On first login, PayNex validates email/password and sends a 6-digit OTP to the registered email.
6. Successful OTP verification marks the email as verified and automatically trusts that browser for 30 days.
7. OTP is required again after 30 days, on a new browser/device, after password reset, or after trusted-device revocation.

## Security behavior

- Passwords are hashed with BCrypt in SQL; the plain initial password is used only for the immediate credential email and the existing one-time on-screen receipt.
- Credential email delivery failure does not roll back a user/company that was already created. The UI displays the SMTP delivery result so the administrator can correct the setup and share the one-time receipt safely.
- Login OTP codes remain hashed, single-use, expiry-limited, attempt-limited, and browser-bound.
- SMTP credentials remain encrypted in `PlatformEmailSecuritySettings` and are visible only to the Platform Owner.
- `Return OTP in API response` is for local development only and must be disabled in production.

## Main files changed

- `src/PayNex.Cloud.Api/Program.cs`
- `src/PayNex.Cloud.Api/Services/AuthenticationSecurityService.cs`
- `src/PayNex.Cloud.Api/Services/CoreServices.cs`
- `src/PayNex.Cloud.Api/Models/Models.cs`
- `src/PayNex.Cloud.Api/wwwroot/user-card.html`
- `src/PayNex.Cloud.Api/wwwroot/js/user-card.js`
- `src/PayNex.Cloud.Api/wwwroot/login.html`
- `src/PayNex.Cloud.Api/wwwroot/owner-email-security.html`
- `database/MasterSchema.sql`
