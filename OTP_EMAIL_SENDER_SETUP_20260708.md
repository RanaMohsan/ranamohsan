# Owner-Managed OTP Email Sender Setup

New-user login details and login OTP codes are sent to the user's registered email address. The sender mailbox is centrally managed by the **Platform Owner**.

## Owner-only setup page

After signing in as the Platform Owner, open:

```text
/owner-email-security.html
```

Or select **Email & OTP Setup** from the owner portal.

Only the Platform Owner can view or update this page and its APIs. Company administrators and normal ERP users cannot access it.

The owner enters:

- From email address
- From display name
- SMTP host
- SMTP port
- SMTP login email/user name
- SMTP password or app password
- SSL/TLS option
- Login OTP expiry minutes
- Trusted-device duration in days (30 by default)
- Test recipient email

Select **Save Settings**, then use **Send Test Email** to validate the mailbox configuration.

## Email flow

- When a company is registered, the first administrator receives the login email and initial password.
- When a company administrator creates another user, that new user receives the same type of login-details email.
- No OTP is required on User Card.
- On first login, PayNex sends a 6-digit OTP from this mailbox.
- After successful verification, the browser is trusted for 30 days by default. OTP is requested again when that period expires or a new browser/device is used.

## Password protection

The SMTP password is encrypted before storage in `PlatformEmailSecuritySettings`. The stored password is never returned to the browser. The owner page only shows whether a password is already configured.

## Local development fallback

If no owner database setting exists, the application uses the `OtpEmail` section in `src/PayNex.Cloud.Api/appsettings.json`.

```json
"OtpEmail": {
  "FromEmail": "no-reply@paynex.local",
  "FromName": "PayNex Cloud ERP",
  "SmtpHost": "",
  "SmtpPort": 587,
  "SmtpUser": "",
  "SmtpPassword": "",
  "EnableSsl": true,
  "ReturnDevOtp": true
}
```

With an empty SMTP host in local development, the login screen can show the development OTP when `ReturnDevOtp` is enabled. New-user credential email still requires a working SMTP configuration.

## Production requirements

- Configure a dedicated sender mailbox through the owner page or deployment secret-backed settings.
- Use the SMTP provider's app password or approved relay credential when required.
- Keep `ReturnDevOtp` set to `false`.
- Serve the ERP over HTTPS.
- Keep `PayNex:TokenSecret` stable and secret because it protects stored SMTP credentials and signs authentication tokens.
