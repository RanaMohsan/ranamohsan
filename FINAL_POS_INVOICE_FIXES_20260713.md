# Final POS and Invoice Fixes — 2026-07-13

## Completed

1. POS product popup now adds the item with both the **Enter** key and the existing **Entry / Add to Cart** button.
2. POS cart, paid amount, server posting, and receipt now use the same tax-inclusive/exclusive calculation. Exclusive tax is included in the displayed Grand Total before posting.
3. Sales Invoices and Purchase Invoices list pages show only **Open** documents.
4. Posted Sales Invoices and Posted Purchase Invoices pages show only **Posted** documents.
5. New Sales and Purchase invoices auto-save as **Open** after lines are added or changed.
6. The Back and New commands wait for auto-save before leaving the card.
7. Opening an **Open** invoice keeps it editable; opening a **Posted** invoice is read-only.
8. Posting an Open invoice converts the same database record to Posted, avoiding duplicate invoice records.

## Validation

- JavaScript syntax validation completed with Node.js.
- Final ZIP integrity test completed.
- The current build environment does not contain the .NET SDK; run `dotnet build` on the target machine before deployment.
