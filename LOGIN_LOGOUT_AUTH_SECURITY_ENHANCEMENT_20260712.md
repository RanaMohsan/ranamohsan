# Login, Logout & Authentication Security Enhancement

## Implemented authentication flow

1. The ERP opens on `/login.html` for unauthenticated users.
2. The user enters the registered email address and password.
3. The server resolves the user's company from the central user directory; the company is not trusted from a browser-supplied value.
4. If the current browser is not trusted, a one-time code is generated and sent to the registered email address.
5. After correct OTP verification, the server creates the authenticated session and redirects the user to the ERP workspace.
6. After successful OTP verification, a protected random device token is automatically stored in an HTTP-only cookie for the configured duration (30 days by default).
7. A different browser/device, an expired trust record, password reset, user deactivation, or explicit device removal requires OTP verification again.
8. Logout closes the server session, revokes the refresh token, clears authentication cookies, and requires a new login.

## Security controls added

- Server-side protection for all tenant `/api/*` endpoints except explicitly public authentication and health endpoints.
- Static ERP HTML pages redirect unauthenticated requests to the login page, including direct URL access.
- Short-lived signed JWT access tokens and rotating refresh tokens.
- HTTP-only, SameSite authentication cookies; the browser UI does not persist tenant JWTs in localStorage or sessionStorage.
- Double-submit CSRF validation for unsafe cookie-authenticated API requests.
- BCrypt hashing for new/changed passwords, with verification compatibility for existing PBKDF2 and legacy hashes.
- Email/password login OTP with expiry, attempt limits, resend cooldown, single use, and browser user-agent binding.
- Trusted-device records use hashed random tokens and expire after 30 days by default.
- Account and IP failed-login tracking, temporary lockout, and fixed-window authentication rate limiting.
- Login, OTP, logout, failed-login, settings-change, session-revocation, and related authentication audit events.
- Security response headers for clickjacking, MIME sniffing, permissions policy, referrer policy, and Content Security Policy.
- Existing role and permission authorization checks remain in force after authentication.

## Company isolation

- Company membership is resolved from the server-side central directory after credential validation.
- The authenticated `UserSession` contains the resolved company code and database context.
- Tenant API access uses the authenticated company context rather than accepting an arbitrary company from the URL or browser.
- Platform-owner company switching is handled through an owner-authorized endpoint and refreshes the signed session context.
- Normal users cannot open or query another company's tenant database.

## Owner-only Email & OTP Setup

Only the Platform Owner can open:

```text
/owner-email-security.html
```

The page is also available from the owner portal through **Email & OTP Setup**.

The owner can configure:

- Sender email address and display name
- SMTP host and port
- SMTP login email/user name
- SMTP password or provider app password
- SSL/TLS setting
- Login OTP expiry duration
- Trusted-device duration
- Local-development OTP display setting
- Test-email recipient

The SMTP password is encrypted before it is stored in the master database. The API never returns the stored password to the browser; it returns only whether a password is configured. Non-owner users receive `403 Forbidden` from all owner email-security endpoints.

## Authentication database tables

The master schema and startup migration now create:

- `PlatformEmailSecuritySettings`
- `LoginSecurityState`
- `LoginMfaChallenges`
- `TrustedLoginDevices`
- `AuthRefreshTokens`

## Important production configuration

- Replace `PayNex:TokenSecret` with a long, stable secret supplied through the deployment secret store. Changing it invalidates signed sessions and requires the encrypted SMTP password to be entered again.
- Set `OtpEmail:ReturnDevOtp` to `false` in production.
- Configure HTTPS at the application host/reverse proxy so secure cookies are used.
- Configure the SMTP provider with a dedicated mailbox or app password; do not store a normal personal password in source control.
- Supply owner and super-admin bootstrap passwords through deployment environment settings before the first production run.
- Existing owner/super-admin password hashes are no longer overwritten at application startup.

## Verification checklist

1. Open a protected HTML URL while logged out; confirm redirect to `/login.html`.
2. Call a protected tenant API without credentials; confirm `401 Unauthorized`.
3. Log in from a new browser; confirm OTP is required.
4. Enter an incorrect/expired OTP; confirm access is rejected and the attempt is audited.
5. Verify the correct OTP and confirm the browser is trusted automatically.
6. Log out and log in again from the same trusted browser; confirm email/password login works without OTP during the trust period.
7. Use another browser/private window; confirm OTP is required immediately.
8. Use **Forget this device**, reset the password, or deactivate the user; confirm device trust and existing sessions are revoked.
9. Attempt access to another company; confirm the authenticated tenant context prevents it.
10. Log in as a non-owner and request owner email-setting APIs; confirm `403 Forbidden`.

## Build validation command

Run in an environment with the .NET 8 SDK installed:

```bash
cd src/PayNex.Cloud.Api
dotnet restore
dotnet build
dotnet run
```
