# PayNex Cloud Final Implementation Checklist

This package includes the final cloud-only PayNex POS + Mini ERP feature set mapped from the uploaded requirements.

## Included cloud screens
- Super Admin company registration with auto company ID and auto tenant DB creation
- Login with Company ID, username and password
- ERP Workspace with all major action tiles
- Web POS / Sales Billing
- Picture Sales
- Sales Invoice and Posted Sales Invoices
- Customer Master, Customer Payment, Customer Ledger
- Vendor Master, Vendor Payment, Vendor Ledger
- Purchase Invoice and Posted Purchase Invoices
- Product Master
- Inventory Stock Transfer and Physical Count / Adjustment
- Shift / Cash Drawer / Z Report
- Company Information with logo
- Chart of Accounts and G/L Entries
- Posting Setup
- Tax Setup
- Dashboard / Business Analytics
- Accounting Reports
- Printable report previews
- Settings, backup/restore action placeholders, audit trail

## Included business rules
- Role-aware UI labels for Admin, Manager and Cashier
- Manager-only operational actions are marked and checked on server for stock adjustment and G/L reversal
- Posted documents update stock, ledger and G/L records
- Sales invoice posts Accounts Receivable, Sales Revenue, Output Tax, COGS and Inventory
- Purchase invoice posts Inventory, Input Tax and Accounts Payable
- Customer payment posts Cash/Bank and Accounts Receivable
- Vendor payment posts Accounts Payable and Cash/Bank
- Sales return posts reversal entries and stock increase
- Stock transfer moves inventory between stores
- Stock adjustment posts inventory/stock-adjustment G/L entries
- Closed period support is available through Accounting Periods
- Audit table and audit screen are included

## Demo data
The tenant setup seeds company information, roles, users, stores, terminal, payment methods, tax groups, chart of accounts, posting setup, number series and sample products.

## Run
```bash
cd src/PayNex.Cloud.Api
dotnet restore
dotnet run
```
Open `http://localhost:5000/admin.html`, create company, then login with generated Company ID, `admin`, `Admin@123`.
