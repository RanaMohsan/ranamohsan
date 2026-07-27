import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const read = path => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

test('the seven requested final fixes remain wired end to end', async () => {
  const [
    shell, workspace, purchaseCard, purchaseDrafts, program, schema, packageService,
    customerLedger, vendorLedger, css, dashboardHtml, dashboardJs, currencyHtml, currencyJs
  ] = await Promise.all([
    read('src/PayNex.Cloud.Api/wwwroot/js/app-shell.js'),
    read('src/PayNex.Cloud.Api/wwwroot/js/workspace.js'),
    read('src/PayNex.Cloud.Api/wwwroot/js/purchase-invoice-card.js'),
    read('src/PayNex.Cloud.Api/Endpoints/InvoiceDraftEndpoints.cs'),
    read('src/PayNex.Cloud.Api/Program.cs'),
    read('database/TenantSchema.sql'),
    read('src/PayNex.Cloud.Api/Services/ConfigurationPackageService.cs'),
    read('src/PayNex.Cloud.Api/wwwroot/customer-ledger.html'),
    read('src/PayNex.Cloud.Api/wwwroot/vendor-ledger.html'),
    read('src/PayNex.Cloud.Api/wwwroot/css/app.css'),
    read('src/PayNex.Cloud.Api/wwwroot/dashboard.html'),
    read('src/PayNex.Cloud.Api/wwwroot/js/dashboard.js'),
    read('src/PayNex.Cloud.Api/wwwroot/currency-setup.html'),
    read('src/PayNex.Cloud.Api/wwwroot/js/currency-setup.js')
  ]);

  // 1. The top-right picture is fetched for the authenticated user through the
  // refresh-aware API helper, and both header/panel avatars use the returned blob.
  assert.match(shell, /fetchWithRefresh\('\/api\/me\/photo/);
  assert.match(shell, /paynexUserAvatarImage/);
  assert.match(shell, /paynexUserPanelImage/);
  assert.match(shell, /URL\.createObjectURL\(blob\)/);

  // 2. Home is a flat, zero-gap action grid and includes both posted-invoice lists.
  assert.match(workspace, /\/posted-sales-invoices\.html/);
  assert.match(workspace, /\/posted-purchase-invoices\.html/);
  assert.doesNotMatch(workspace, /fo-module-section-title/);
  assert.match(css, /\.fo-module-grid\{grid-template-columns:[^}]*gap:0!important/);
  assert.match(css, /\.fo-module-section-title\{display:none!important;?\}/);

  // 3. The left rail contains exactly the requested five normal-workspace actions.
  const menuSource = shell.slice(shell.indexOf('const menuGroups'), shell.indexOf('function isActive'));
  const menuPaths = [...menuSource.matchAll(/\['(\/[^']+)'\s*,/g)].map(match => match[1]);
  assert.deepEqual(menuPaths, [
    '/workspace.html', '/dashboard.html', '/pos.html', '/company.html', '/branches.html'
  ]);

  // 4. Vendor is optional for an Open purchase draft, remains mandatory for posting,
  // and vendor-less drafts remain visible/readable through LEFT JOIN queries.
  assert.doesNotMatch(purchaseDrafts, /request\.VendorId\s*<=\s*0/);
  assert.match(purchaseDrafts, /request\.VendorId > 0 \? request\.VendorId : DBNull\.Value/);
  assert.match(purchaseDrafts, /ALTER COLUMN VendorId INT NULL/);
  assert.match(purchaseCard, /Vendor is required for posting[\s\S]*saved as an Open draft/);
  assert.match(program, /FROM PurchaseInvoiceHeader h\s+LEFT JOIN Vendors v ON v\.VendorId=h\.VendorId/);
  assert.match(schema, /PurchaseInvoiceHeader\([\s\S]*VendorId INT NULL FOREIGN KEY/);

  // 5. The Vendor master-data package is seeded and supports export/import/apply.
  assert.match(packageService, /PackageCode='VENDOR'/);
  assert.match(packageService, /ExportVendorsAsync/);
  assert.match(packageService, /ValidateSheet\("Vendors"/);
  assert.match(packageService, /ApplyVendorAsync/);

  // 6. Both requested ledger pages use the centered heading rule.
  assert.match(customerLedger, /ledger-entry-table/);
  assert.match(vendorLedger, /ledger-entry-table/);
  assert.match(css, /\.ledger-entry-table thead th\{text-align:center!important\}/);

  // 7. Currency setup persists a base currency and dashboard rendering consumes it.
  assert.match(workspace, /\/currency-setup\.html/);
  assert.match(program, /MapGet\("\/api\/currencies\/base"/);
  assert.match(program, /MapPost\("\/api\/currencies"/);
  assert.match(program, /currency,\s*summary/);
  assert.match(schema, /CREATE TABLE Currencies/);
  assert.match(currencyHtml, /base currency/i);
  assert.match(currencyJs, /\/api\/currencies/);
  assert.match(dashboardHtml, /dashboardCurrencyBadge/);
  assert.match(dashboardJs, /data\.currency/);
  assert.match(dashboardJs, /currency\.symbol/);
});
