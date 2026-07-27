# Sales/Purchase Entry and Posted Invoice Separation Fix

This package separates document entry from posted document lists.

## Entry pages
- `/sales.html` is only for new Sales Invoice entry and posting.
- `/purchases.html` is only for new Purchase Invoice entry and posting.
- Posted invoice side panels and posted tables are not included on these pages.

## Posted list pages
- `/posted-sales-invoices.html` is a list-only page with Lines and Print actions.
- `/posted-purchase-invoices.html` is a list-only page with Lines and Print actions.
- No data entry fields are included on posted pages.

## Development cache fix
Static files are served with no-cache headers to avoid old browser-cached layouts showing after a package update.
