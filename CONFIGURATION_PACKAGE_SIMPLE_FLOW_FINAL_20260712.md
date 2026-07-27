# Configuration Package - Simplified Final Flow

## List Page

The Configuration Packages list is now created automatically with exactly these master-data entries:

1. Customer
2. Vendor
3. Item

The user does not need to create or configure package definitions manually.

## Card Page

Opening Customer, Vendor, or Item displays all currently saved records for that master-data type in a searchable grid.

The action sequence is:

1. **Export Excel** - always downloads an Excel workbook. If no records exist, the workbook still contains the correct worksheet and column headings.
2. **Import Excel** - selects the completed or edited `.xlsx` file.
3. **Validate** - checks worksheet names, columns, required fields, duplicates, values, and database conflicts.
4. **Save** - remains disabled until validation succeeds. Saving inserts new records and updates matching records in one SQL transaction.

After Save, the current-record grid and import history refresh automatically.

## Backend Changes

- Automatic Customer, Vendor, and Item package seeding per company database.
- Package list restricted to the three simplified master-data packages.
- Record-count display on the list page.
- New records-preview API for showing current master data on the package card.
- Existing export supports both populated and empty datasets.
- Existing validation and transactional apply logic is retained; the UI now presents Apply as Save.
