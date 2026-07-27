# Business Central-style Master Data List + Card Pages

Implemented on 10-Jul-2026.

## Converted modules
- Item Master: `items.html` list + `item-card.html` card
- Customers: `customers.html` list + `customer-card.html` card
- Vendors: `vendors.html` list + `vendor-card.html` card
- Chart of Accounts: `finance.html` list + `account-card.html` card

## List page behavior
- New, Open, Print and Refresh command actions
- Business Central-style filters, selection, record count and summary footer
- Click record number/name or double-click row to open card
- Active/inactive status display
- Inactive records available on master lists without changing active-only transactional lookups

## Card page behavior
- Separate new/edit card pages
- General FastTabs and FactBoxes
- Save, Print, Back and New actions
- Item picture, inventory, costs and prices
- Customer/vendor balance summaries and payment/ledger shortcuts
- Protected read-only system G/L accounts

## API additions
- GET `/api/products/{id}`
- GET `/api/customers/{id}`
- GET `/api/vendors/{id}`
- GET `/api/accounting/chart-of-accounts/{id}`
- Optional `includeInactive=true` on product/customer/vendor list endpoints
