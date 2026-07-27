# Final Requested Fixes — 2026-07-15

This package includes the seven requested PayNex Cloud corrections.

1. The signed-in user's User Card photo is loaded in the top-right header and user panel through the refresh-aware authenticated API flow.
2. Home action-group headings and tile spacing are removed. Posted Sales Invoices and Posted Purchase Invoices are included as Home actions.
3. The normal left Actions rail is limited to Home, Dashboard, POS, Company Information, and Branches.
4. A Purchase Invoice with lines can be saved as an Open draft without a vendor. A vendor is still required when the invoice is posted. Existing tenant schemas are upgraded automatically by making `PurchaseInvoiceHeader.VendorId` nullable.
5. The Vendor Configuration Package is retained and covered across list, template/export, validation, import, and apply paths.
6. Customer Ledger Entry and Vendor Ledger Entry table headings are centered.
7. Currency Setup is available from Home. The selected active base currency is persisted per tenant and is returned by Dashboard analytics for all monetary labels and values.

## Database upgrade behavior

No separate manual migration is required for these changes. Runtime schema guards create/seed `Currencies` and make the purchase-draft Vendor field nullable. `database/TenantSchema.sql` contains the same definitions for new tenant provisioning.

## Verification

Run the source regression suite from the project root:

```bash
node --test tests/*.test.mjs
```

For deployment, also run the normal .NET 8 build in an environment with the .NET SDK installed:

```bash
dotnet build PayNex_Cloud_Final.sln
```
