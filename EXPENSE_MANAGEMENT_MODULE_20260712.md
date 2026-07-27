# Expense Management Module

## Scope

The Expense Management module is a lightweight operational expense register integrated with PayNex company, user, branch, permission, audit and reporting architecture.

## Features

- Create, view, edit and soft-delete expense records.
- Automatic expense numbers using the `EXPENSE` number series.
- Expense date, category, description, amount, branch, created by and optional remarks.
- Branch-aware access based on the signed-in user's branch assignments.
- Expense category setup with default categories.
- Audit trail coverage through the existing API mutation audit middleware.
- No automatic G/L posting; the module intentionally remains a simple expense register.

## Expense Categories

Default categories:

- General Expense
- Travel & Conveyance
- Utilities
- Rent
- Marketing
- Repairs & Maintenance
- Office Supplies

Authorized users can create additional categories from the Expense Card.

## Expense Report

The Expense Management Report supports:

- From Date / To Date
- Monthly Expenses
- Yearly Expenses
- Branch
- Expense Category
- Professional A4, Standard A4 and Compact A4 layouts
- HTML/CSS print preview
- Browser Print / Save as PDF
- RDLC source file: `Reports/Expense_Management_Report.rdlc`

Report columns:

- Expense Date
- Expense No.
- Expense Category
- Description
- Amount
- Branch
- Created By
- Remarks
- Grand Total of Expenses

## Permissions

- View Expenses
- Create Expenses
- Edit Expenses
- Delete Expenses
- Manage Expense Categories
- View Expense Report
- Print and Export Expense Report

The existing `finance.createExpense` permission remains accepted for creating expenses for backward compatibility.

## APIs

- `GET /api/expenses/lookups`
- `GET /api/expenses`
- `GET /api/expenses/{id}`
- `POST /api/expenses`
- `PUT /api/expenses/{id}`
- `DELETE /api/expenses/{id}`
- `GET /api/expense-categories`
- `POST /api/expense-categories`
- `GET /api/expense-report`
- `GET /api/reports/expenses/html`

Existing tenant databases are upgraded when Expense Management is first opened. New tenant databases receive the schema through `TenantSchema.sql`.
