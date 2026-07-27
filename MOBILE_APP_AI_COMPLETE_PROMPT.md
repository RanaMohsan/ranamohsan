# PayNex Mobile App — Complete AI Build Prompt

Copy **everything below this line** and paste it to the Mobile App AI.

---

## ROLE

You are a senior mobile engineer. Update / build the existing PayNex Mobile App so it connects to **PayNex Cloud API** with **zero per-client integration setup**.

One app build works for ALL clients.
Client only enters **User Name + Password**.
The API finds which company that user belongs to and logs them into that company automatically.

Do NOT ask each client for company code, API key, database name, or custom integration setup.

---

## BUSINESS FLOW (MUST MATCH)

1. Client installs the **same** PayNex Mobile App from store / APK.
2. PayNex Owner opens Owner Portal → Company Card → **Mobile App Detail**.
3. Owner **Registers Mobile App** for that company.
4. Owner **Adds Mobile User** (User Name is globally unique across all companies).
5. Client opens app → enters User Name + Password only.
6. App calls PayNex API → API finds company from User Name → login succeeds for that company.
7. If company mobile app is **Blocked** in PayNex → login must fail with clear message.
8. If user is **Blocked** → login must fail.
9. If user / company mobile app is **not registered** → login must fail.
10. After login, app stores token and syncs company profile automatically.

---

## AUTO CONNECTION RULE (CRITICAL)

| Wrong (do NOT do) | Correct (do this) |
|---|---|
| Each client enters company code | No company code on login screen |
| Each client enters API key manually | API key returned by login response; store after login |
| Different app builds per company | One build for all companies |
| Hardcode one company DB | Resolve company from username via API |
| Manual Postman setup per client | App uses fixed PayNex Base URL only |

**Only config in the app:**
- `PAYNEX_BASE_URL` (example: `https://your-paynex-domain.com` or local `http://YOUR_PC_IP:5000`)
- Nothing else per client.

---

## PAYNEX API BASE

Base URL example:
- Production: `https://<paynex-cloud-host>`
- Local test: `http://<LAN-IP>:5000`

All mobile calls use JSON.
Authenticated calls use header:

```http
Authorization: Bearer <token>
Content-Type: application/json
```

---

## APIs TO USE (COMPLETE)

### 1) Health / Auto connectivity check
`GET /api/mobile/health`  
Auth: **None**

Purpose: App startup check that PayNex server is reachable.

Success example:
```json
{
  "status": "OK",
  "service": "PayNex Mobile API",
  "utc": "2026-07-27T05:00:00Z",
  "message": "Mobile app can connect. Login with username + password. Company is resolved automatically."
}
```

App behavior:
- On open / before login, call health.
- If fail → show “Cannot connect to PayNex. Check internet / server URL.”
- If OK → show login form.

---

### 2) Login (AUTO company resolve)
`POST /api/mobile/login`  
Auth: **None**

Request body:
```json
{
  "userName": "ranamohsin",
  "password": "Admin@123",
  "deviceName": "Samsung A54",
  "appVersion": "1.0.0"
}
```

Success (`200`):
```json
{
  "ok": true,
  "code": "LOGIN_OK",
  "message": "Login successful. Company resolved automatically from user name.",
  "token": "<JWT>",
  "refreshToken": "<refresh>",
  "expiresInMinutes": 15,
  "company": {
    "companyCode": "PNX000001",
    "companyName": "Demo Company",
    "status": "Active",
    "licenseStatus": "Active",
    "subscriptionPlan": "Standard"
  },
  "user": {
    "mobileAppUserId": 1,
    "userName": "ranamohsin",
    "displayName": "Rana Mohsin",
    "email": "user@example.com",
    "mobile": "+92...",
    "roleName": "Mobile User"
  },
  "app": {
    "appName": "PayNex Mobile",
    "platform": "Both",
    "apiKey": "PNXMOB-....",
    "status": "Active"
  },
  "sync": {
    "autoConnect": true,
    "noClientSetupRequired": true,
    "authHeader": "Authorization: Bearer {token}",
    "endpoints": {
      "health": "/api/mobile/health",
      "me": "/api/mobile/me",
      "company": "/api/mobile/company",
      "logout": "/api/mobile/logout"
    }
  }
}
```

App must save securely after login:
- `token`
- `refreshToken` (optional now; keep for later)
- `company.companyCode`
- `company.companyName`
- `user.*`
- `app.apiKey` (for future calls if needed)

Then navigate to Home / Dashboard of **that company**.

---

### Fail codes (MUST HANDLE IN UI)

| HTTP | `code` | Meaning | UI message idea |
|---|---|---|---|
| 400 | `MISSING_CREDENTIALS` | Empty username/password | Enter user name and password |
| 401 | `USER_NOT_FOUND` | Username not in any company | User not registered for mobile app |
| 401 | `INVALID_PASSWORD` | Wrong password | Invalid user name or password |
| 403 | `USER_BLOCKED` | User blocked in PayNex | Your mobile user is blocked |
| 403 | `COMPANY_APP_BLOCKED` | Company mobile app blocked | Company mobile access is blocked |
| 403 | `COMPANY_INACTIVE` | Tenant Inactive/Suspended | Company is inactive |
| 403 | `LICENSE_BLOCKED` | License Expired/Suspended | Company license blocked |
| 403 | `COMPANY_NOT_FOUND` | Tenant missing | Company not found in PayNex |
| 403 | `COMPANY_APP_NOT_REGISTERED` | No mobile registration | Mobile app not registered for company |

Always show `message` from API if present.

---

### 3) Current session user
`GET /api/mobile/me`  
Auth: **Bearer token required**

Use after login / app resume to confirm session still valid and not blocked.

---

### 4) Company + app sync profile
`GET /api/mobile/company`  
Auth: **Bearer token required**

Use after login for first sync:
- company name, status, license
- mobile app registration details

If returns `COMPANY_APP_BLOCKED` or `COMPANY_APP_NOT_REGISTERED` → force logout and show message.

---

### 5) Logout
`POST /api/mobile/logout`  
Auth: **Bearer token required**

Then clear local token/session storage on device.

---

## DATA SYNC RULE (SIMPLE / AUTO)

Keep first version simple to avoid issues:

1. App start → `GET /api/mobile/health`
2. Login screen → user enters username/password only
3. `POST /api/mobile/login`
4. On success → save session → `GET /api/mobile/company` (bootstrap sync)
5. On app resume → `GET /api/mobile/me`
6. If 401/403 with block codes → clear session → login screen

Do **not** invent per-client sync URLs.
Do **not** require QR setup / company picker for v1.
Do **not** hardcode company codes.

Later sync modules (sales/stock/etc.) must also use the same Bearer token and same Base URL. Company context already comes from the token.

---

## OWNER PORTAL SIDE (ALREADY DONE IN PAYNEX)

These are Owner-only web APIs (not used by mobile login directly):

- Register app: `POST /api/platform/companies/{code}/mobile-app`
- Update app: `PUT /api/platform/companies/{code}/mobile-app`
- Block company app: `POST /api/platform/companies/{code}/mobile-app/block`
- Add user: `POST /api/platform/companies/{code}/mobile-app/users`
- Update user: `PUT /api/platform/companies/{code}/mobile-app/users/{id}`
- Block user: `POST /api/platform/companies/{code}/mobile-app/users/{id}/block`
- Get detail: `GET /api/platform/companies/{code}/mobile-app`

Username uniqueness: **global across all companies**.

---

## UI REQUIREMENTS

Login screen fields only:
- User Name
- Password
- Login button
- Optional: server status indicator from health check

After login Home must show:
- Company Name
- Company Code
- Logged-in Display Name / User Name
- App Status
- Logout

Blocked states must be clear and non-technical.

---

## SECURITY RULES

- Never log passwords.
- Store token in secure storage (Keychain / Keystore / Flutter Secure Storage).
- On USER_BLOCKED / COMPANY_APP_BLOCKED / LICENSE_BLOCKED → wipe session.
- Timeout / 401 → return to login.
- HTTPS in production.

---

## IMPLEMENTATION CHECKLIST

- [ ] Add single `PAYNEX_BASE_URL` config
- [ ] Implement health check on startup
- [ ] Implement login API with username+password only
- [ ] Auto-bind company from login response (no picker)
- [ ] Handle all fail codes in table above
- [ ] Save token + company/user profile locally
- [ ] Call `/api/mobile/company` after login for sync bootstrap
- [ ] Call `/api/mobile/me` on resume
- [ ] Logout clears local session
- [ ] No per-client API key / company code setup screen

---

## AFTER YOU FINISH — ANSWER THESE QUESTIONS

Reply with clear answers to every question below (so the PayNex backend owner can verify and continue):

### A) App / project
1. What mobile framework is the app using? (Flutter / React Native / native Android / iOS / other)
2. What is the exact project path / main module you changed?
3. Which files did you create or edit? (list paths)

### B) Connection
4. Where is `PAYNEX_BASE_URL` configured in the app?
5. What default Base URL did you set for local testing?
6. Does the app require company code or API key from the user at login? (must be NO)

### C) API wiring
7. Confirm these endpoints are implemented in app code:
   - `GET /api/mobile/health`
   - `POST /api/mobile/login`
   - `GET /api/mobile/me`
   - `GET /api/mobile/company`
   - `POST /api/mobile/logout`
8. Paste the exact login request JSON your app sends.
9. Paste the exact headers used after login for authenticated calls.

### D) Fail handling
10. How does UI show `USER_NOT_FOUND`?
11. How does UI show `COMPANY_APP_BLOCKED`?
12. How does UI show `USER_BLOCKED`?
13. How does UI show network / health failure?

### E) Sync / session
14. What local keys/storage do you save after login?
15. What happens on app resume if token is expired / blocked?
16. Is sync currently online-only, or is offline cache also implemented?

### F) Test evidence
17. Did you successfully login with a PayNex-registered mobile user?
18. What company code was auto-resolved?
19. Did blocked-company test fail correctly?
20. Any remaining TODO / blocker for next sync features (sales/inventory)?

### G) Need from PayNex owner
21. What Base URL / IP should PayNex owner provide for your next test?
22. Do you need CORS / HTTPS / reverse-proxy changes on server?
23. Any extra API you need next (example: products list, invoices, payments)? If yes, list exact endpoints wanted.

---

## IMPORTANT CONSTRAINTS

- Keep the process simple.
- Prefer stable login + session + company bootstrap first.
- Do not over-engineer multi-tenant setup screens.
- Do not break the existing app UI style; integrate into current screens.
- If something in the existing app conflicts with auto-login, adapt it to this PayNex contract and explain what you changed in the answers above.

Start implementation now. When done, return the checklist status + full answers to questions A–G.
