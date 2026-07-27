# PayNex Cloud ERP - Final UI/Process Fixes

Applied fixes:

1. Sales posted invoice and purchase posted invoice moved to separate read-only list pages:
   - /posted-sales-invoices.html
   - /posted-purchase-invoices.html
   These pages contain list, line view and authenticated print only.

2. Sales Invoice and Purchase Invoice pages now contain only new document entry and posting.

3. Customer and vendor master pages are now separated from payment and ledger processing.

4. Customer Payment and Vendor Payment are separate entry pages:
   - /customer-payment.html
   - /vendor-payment.html

5. Customer Ledger Entry and Vendor Ledger Entry are separate read-only pages:
   - /customer-ledger.html
   - /vendor-ledger.html

6. G/L Account Setup and Tax Setup are shown as separate menu actions, not under benefits/settings.

7. Document Print Center page now uses lookup selectors for posted sales invoice, posted purchase invoice, customer payment, vendor payment, customer ledger and vendor ledger.

8. Print actions now open reports through authenticated report preview helper.

9. Cloud Modules page JSON/pre debug output replaced with clean table views.

10. Item Master now supports:
    - auto item code generation
    - item image upload
    - image preview
    - item image display in item list

11. Table/list rows now use alternating light blue and very light blue rows.

12. Third-party ERP product branding wording removed from user-facing UI.
