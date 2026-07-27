# Dashboard Server Error Fix — 14 July 2026

## Root cause

The monthly gross-profit SQL joined header and line tables but referenced `TaxAmount` without the line alias. Both header and line tables contain `TaxAmount`, so SQL Server raised **Ambiguous column name 'TaxAmount'** and the global exception middleware returned **An unexpected server error occurred.**

## Fix

- Qualified all monthly gross-profit line fields with alias `l` for both POS sales and posted sales invoices.
- Added regression tests to prevent the ambiguous SQL from returning.
- Updated dashboard asset cache keys so the browser loads the corrected files.

Correct expressions:

```sql
SUM(l.LineTotal - l.TaxAmount - (l.Quantity * l.UnitCost))
```
