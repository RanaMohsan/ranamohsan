# Business Central-style Sales and Purchase Invoice Pages

Implemented on 2026-07-10.

## Completed

- Sales action now opens a Sales Invoice list page.
- Purchase action now opens a Purchase Invoice list page.
- Added Business Central-style command bars with New, Open, Print, Refresh, filters, and print-layout selection.
- Invoice numbers are clickable and open dedicated card pages.
- Rows can be selected and double-clicked to open the card.
- Added new Sales Invoice Card and Purchase Invoice Card pages.
- New cards support header entry, line entry, totals, posting, and document facts.
- Posted cards open in read-only mode and support printing.
- Corrected the Sales Invoice list API to read from SalesInvoiceHeader rather than the POS SalesHeader table.
- Added Sales and Purchase invoice header-detail API endpoints.
- Legacy posted invoice URLs redirect to the new canonical list pages.
- Updated sidebar and workspace navigation to remove duplicate invoice list entries.
- Extended customer lookup data with email and address for the card display.

## Main Files

- `src/PayNex.Cloud.Api/Program.cs`
- `src/PayNex.Cloud.Api/wwwroot/sales.html`
- `src/PayNex.Cloud.Api/wwwroot/purchases.html`
- `src/PayNex.Cloud.Api/wwwroot/sales-invoice-card.html`
- `src/PayNex.Cloud.Api/wwwroot/purchase-invoice-card.html`
- `src/PayNex.Cloud.Api/wwwroot/js/sales.js`
- `src/PayNex.Cloud.Api/wwwroot/js/purchases.js`
- `src/PayNex.Cloud.Api/wwwroot/js/sales-invoice-card.js`
- `src/PayNex.Cloud.Api/wwwroot/js/purchase-invoice-card.js`
- `src/PayNex.Cloud.Api/wwwroot/js/app-shell.js`
- `src/PayNex.Cloud.Api/wwwroot/js/workspace.js`
- `src/PayNex.Cloud.Api/wwwroot/css/app.css`

## Validation

- JavaScript syntax validation passed for all project JavaScript files.
- Mocked browser rendering passed for all Sales/Purchase list and card states.
- List selection, print routing, line addition, and total calculations passed interaction tests.
