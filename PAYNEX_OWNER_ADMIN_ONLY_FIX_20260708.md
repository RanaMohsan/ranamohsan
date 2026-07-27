# PayNex Owner Admin-Only Fix

Implemented correction requested by owner:

- `ranamohsanali3@gmail.com` with password `PayNex@123` is for PayNex Super Admin Portal only (`/admin.html`).
- This email is not inserted into tenant/company databases.
- This email is not inserted into `CentralUserDirectory` for client company access.
- Client login (`/login.html`) blocks this email and instructs the user to use `/admin.html`.
- Startup cleanup removes/deactivates accidental previous tenant access for this email.
- Platform company selector was removed from client workspace.

Super Admin portal credentials:

```text
Company ID: 3032720768
Username/Email: ranamohsanali3@gmail.com
Password: PayNex@123
```
