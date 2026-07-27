# PayNex Cloud ERP Final Fixes - 2026-07-08

## Completed changes

1. **Picture Sales item cubes**
   - Picture Sales now shows uploaded item pictures from the Item Master card.
   - If an item has no image, a clean initials placeholder is shown instead of plain `ITEM` text.
   - Product tiles/cubes were redesigned for a cleaner cashier experience.

2. **Sales/Purchase print buttons and layouts**
   - Sales Invoice page now has a print layout selector and **Print Last Invoice** button.
   - Purchase Invoice page now has a print layout selector and **Print Last Purchase** button.
   - Posted Sales Invoices and Posted Purchase Invoices now have print layout selectors.
   - Print layouts supported:
     - Professional A4
     - Standard A4
     - Compact A4

3. **Document Print Center**
   - Renamed `Document Print Center` to **Document Print Center**.
   - Redesigned page with clear report cards and default layout selection.
   - Report selectors still support posted sales, posted purchases, payments, ledgers, POS receipts and RDLC template download.

4. **Report output**
   - All HTML print reports now include company information and company logo/placeholder.
   - Reports have a cleaner A4 shell with professional header, metadata, table, tax summary, totals and signature footer.
   - Customer Ledger Entries and Vendor Ledger Entries now run cleanly even when there is no data.

5. **Home workspace cleanup**
   - Removed `Company Information` from the home page action tiles because it already exists in the left-side panel.
   - Removed old `Additional ERP Actions` grouping.
   - Separated Accounting Reports from Chart of Accounts.
   - Renamed home tile to **Document Print Center**.

6. **Shift / Cash Drawer simplification**
   - Removed raw JSON display from the shift page.
   - Rebuilt the page into a simple 3-step flow:
     - Open Shift
     - Cash In / Cash Out
     - Close Shift
   - Added clean KPI cards and printable Z Report summary.

## Important note
The sandbox used to edit this project does not have the .NET SDK installed, so `dotnet build` could not be executed here. JavaScript syntax checks were executed successfully using `node --check` for the updated frontend scripts.
