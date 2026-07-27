# PayNex Multi-Client Cloud SaaS POS/ERP Design

## SaaS Model

PayNex is designed as one cloud application running on one main domain:

```text
app.paynex.com
```

Every client/company logs in from the same application using:

```text
Company Code
Username
Password
```

The Company Code identifies the client and routes the user to that client/company workspace only.

## Database Separation

The platform uses one master database and one separate database per client/company.

```text
PayNex_MasterDB
PayNex_ABCSTORE_DB
PayNex_XYZMART_DB
PayNex_RETAILMART_DB
```

## Master Database Stores Only Platform-Level Data

The master database stores:

```text
Client/company list
Company code
Company database name
Subscription package
License status
Active/inactive status
Trial date
Renewal date
Expiry date
Admin contact details
Database creation status
Provisioning status
Last login date
Super Admin audit log
```

## Client Database Stores Only Client Operational Data

Each client database stores:

```text
Users
Roles
Company information
Products
Customers
Vendors
Sales
Purchases
Inventory
POS transactions
Customer ledger
Vendor ledger
G/L entries
Chart of accounts
Posting setup
Tax setup
Reports
Dashboard data
Shift/cash drawer data
Settings
Audit trail
```

## Super Admin Onboarding Flow

When Super Admin creates a new company, PayNex automatically performs:

```text
1. Register company in master database
2. Generate unique company code
3. Create separate client database
4. Create all default tables
5. Create Admin, Manager, Cashier roles
6. Create default client admin user
7. Create default chart of accounts
8. Create default posting setup
9. Create default tax setup
10. Create number series for invoices, receipts, purchases, returns and payments
11. Create default payment methods
12. Create default company information placeholder
13. Create default report/dashboard setup
14. Mark database creation status as Ready
15. Mark provisioning status as Completed
16. Activate client/company
```

## Client Login Flow

```text
1. User opens app.paynex.com
2. User enters Company Code, Username and Password
3. System checks company in master database
4. System checks active/inactive status, license status and expiry date
5. System opens only the mapped client database
6. User sees only their assigned company workspace
```

## Client Data Isolation Rule

A client can never see another client’s data. All operational modules, reports and settings are isolated by the client’s own database.

## Roles

### Super Admin
Controls all clients, licenses, company activation, database status and onboarding.

### Client Admin
Controls only their own company workspace, users, setup and reports.

### Manager
Approves returns, stock adjustments, G/L reversals and finance review inside the assigned company.

### Cashier
Works only inside assigned company POS/counter screens.

## User Experience

Super Admin sees the SaaS control center. Client users see their own ERP workspace after login.

