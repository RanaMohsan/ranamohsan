# Package Configuration Module

## Scope

The initial Package Configuration release supports Business Central-style Excel migration packages for:

- Item Master
- Customer Master
- Vendor Master
- Item opening quantity and unit cost
- Customer opening balance
- Vendor opening balance

## User Flow

1. Open **Data Management > Package Configuration**.
2. Create a package and select Item, Customer and/or Vendor.
3. Select whether opening balances should be included and applied.
4. Export a blank Excel template or export the current company data.
5. Edit the Excel workbook without changing worksheet or column names.
6. Choose the workbook and select **Validate Package**.
7. Review worksheet, row and field-level validation messages.
8. Select **Apply Package** after validation succeeds.

## Balance Rules

- Item quantity is applied to the current branch/store.
- Product company stock is adjusted by the branch quantity difference.
- Inventory ledger entries record quantity differences.
- Customer and vendor current balances are adjusted by the difference between the imported and existing opening balance.
- Customer/vendor opening balance ledger entries are created for the difference.
- Balanced G/L entries use Inventory, Receivable, Payable and Opening Balance Equity accounts from Posting Setup.
- Reapplying the same validated import is blocked.
- The complete Apply operation runs in one SQL transaction and rolls back on any error.
- `CurrentBalance` is exported for customer/vendor reference; imports apply `OpeningBalance` only.

## Permissions

- View Configuration Packages
- Create and Edit Configuration Packages
- Export Configuration Packages
- Import and Validate Configuration Packages
- Apply Configuration Packages

Company Super Admin and Admin users have full access. Other users require assigned permissions.

## APIs

- `GET /api/configuration-packages`
- `GET /api/configuration-packages/{id}`
- `POST /api/configuration-packages`
- `GET /api/configuration-packages/{id}/export?templateOnly=true|false`
- `POST /api/configuration-packages/{id}/validate`
- `POST /api/configuration-packages/{id}/apply`
- `GET /api/configuration-packages/{id}/history`

## Compatibility

The module uses standard `.xlsx` files without adding an external NuGet package. Existing tenant databases are upgraded automatically when the module is first opened, and new tenant databases receive the tables through `TenantSchema.sql`.
