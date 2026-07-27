# Business Central-Style List Filtering — 2026-07-21

## Completed

- Added one reusable column-filtering engine to every eligible list table in the application.
- Added a filter/sort arrow to each data column heading.
- Added a collapsible Filter pane showing all active field filters.
- Added `+ Add filter`, edit, remove, and clear-all actions.
- Added distinct-value search and multi-value selection for each field.
- Added text, number, amount, and date comparisons.
- Added Business Central filter expressions:
  - `A*` — begins with A
  - `<>Closed` — not Closed
  - `100..500` — range
  - `Open|Posted` — either value
  - `A*&<>AB-200` — combined conditions
- Added right-click actions for Filter to this value, Exclude this value, Clear field filter, and Copy value.
- Added `Alt+F3` to filter to the selected cell and `Shift+F3` to toggle the Filter pane.
- Added ascending/descending column sorting.
- Filters automatically reapply when a page reloads table rows from its API.
- Active filters persist for the current browser session per page and table.
- Existing page-level filters (search, date, status, and other fixed filters) continue to work alongside column filters.

## Invoice List Views

The canonical Sales Invoice and Purchase Invoice list pages now include:

- All invoices
- Draft / Open invoices
- Posted invoices

Legacy Posted Invoice links open the Posted view. Dedicated Draft Invoice routes were also added:

- `/draft-sales-invoices.html`
- `/draft-purchase-invoices.html`

## Main Files

- `src/PayNex.Cloud.Api/wwwroot/js/bc-list-filter.js`
- `src/PayNex.Cloud.Api/wwwroot/js/app-shell.js`
- `src/PayNex.Cloud.Api/wwwroot/css/app.css`
- `src/PayNex.Cloud.Api/wwwroot/sales.html`
- `src/PayNex.Cloud.Api/wwwroot/purchases.html`
- `src/PayNex.Cloud.Api/wwwroot/js/sales.js`
- `src/PayNex.Cloud.Api/wwwroot/js/purchases.js`
- `src/PayNex.Cloud.Api/wwwroot/posted-sales-invoices.html`
- `src/PayNex.Cloud.Api/wwwroot/posted-purchase-invoices.html`
- `src/PayNex.Cloud.Api/wwwroot/draft-sales-invoices.html`
- `src/PayNex.Cloud.Api/wwwroot/draft-purchase-invoices.html`
- `tests/ListPageColumnFiltering.test.mjs`

## Verification

Run the source regression suite:

```bash
node --test tests/*.test.mjs
```

Run the .NET build in an environment with the .NET 8 SDK:

```bash
dotnet build PayNex_Cloud_Final.sln
```
