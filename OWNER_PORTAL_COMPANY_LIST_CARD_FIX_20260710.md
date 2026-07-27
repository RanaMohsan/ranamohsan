# Owner Portal Company List/Card Update - 10 July 2026

The PayNex Owner Portal company management UI now follows the same Business Central-style list/card pattern used by Sales and Purchase Invoices.

## Implemented

- `platform-portal.html` is now a dedicated registered-company List Page.
- Added New Client, Open Card, Print and Refresh command actions.
- Added company search, status and license filters.
- Company Code and Company Name are clickable.
- Double-clicking or pressing Enter on a selected row opens the Company Card.
- Added `platform-company-card.html` as a dedicated Company Card Page.
- New client registration is handled in Company Card new mode.
- Existing client company data is loaded and edited in Company Card edit mode.
- Preserved company users, branches, central directory and security information.
- Preserved Create/Repair Sandbox and Open Production actions.
- Added print-friendly Company Card output.
- Updated owner-only shell routing so the new Company Card remains accessible to platform-only owner sessions.

## Main Files

- `src/PayNex.Cloud.Api/wwwroot/platform-portal.html`
- `src/PayNex.Cloud.Api/wwwroot/js/platform-portal.js`
- `src/PayNex.Cloud.Api/wwwroot/platform-company-card.html`
- `src/PayNex.Cloud.Api/wwwroot/js/platform-company-card.js`
- `src/PayNex.Cloud.Api/wwwroot/js/app-shell.js`
- `src/PayNex.Cloud.Api/wwwroot/css/app.css`
