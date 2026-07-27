# PayNex Professional Reporting + Lookup Upgrade

This build upgrades PayNex Cloud UI/reporting toward a professional SaaS ERP experience.

## Added / Updated

- Professional A4 report design for:
  - POS Sales Invoice
  - Formal Sales Invoice
  - Posted Purchase Invoice
  - Customer Payment Receipt
  - Vendor Payment Receipt
  - Customer Ledger Entries
  - Vendor Ledger Entries
- New compact 80mm-style counter sales thermal receipt:
  - `/api/reports/pos-receipt/{saleId}/html`
- New formal sales invoice print endpoint:
  - `/api/reports/formal-sales-invoice/{salesInvoiceId}/html`
- Updated reports hub page:
  - `/reports.html`
- Dedicated G/L Account Setup page:
  - `/gl-setup.html`
- Dedicated Tax Setup page:
  - `/tax-setup.html`
- Professional lookup modal added globally for select fields.
- Dedicated G/L account lookup buttons added for every posting account field.
- POS cart area enlarged and made more cashier friendly.
- Buttons, fields, tables, cards and forms compacted to a more professional ERP size.
- Finance in-page reports now include print-preview output.

## Important Run Command

```bash
cd src/PayNex.Cloud.Api
dotnet restore
dotnet run
```

## Print Notes

- A4 invoice pages include print button and A4 print CSS.
- POS receipt page includes narrow receipt CSS for counter/thermal printers.
- In browser print dialog, choose the appropriate paper size for thermal receipt printer.
