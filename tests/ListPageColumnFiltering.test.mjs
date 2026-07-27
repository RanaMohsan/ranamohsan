import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const require = createRequire(import.meta.url);
const filters = require(path.join(root, 'src/PayNex.Cloud.Api/wwwroot/js/bc-list-filter.js'));

test('column filter operators support text, values, numeric amounts and blanks', () => {
  assert.equal(filters.matchesFilter('Posted Sales Invoice', { operator: 'contains', value: 'sales' }), true);
  assert.equal(filters.matchesFilter('Open', { operator: 'values', values: ['Draft', 'Open'] }), true);
  assert.equal(filters.matchesFilter('Rs. 1,250.00', { operator: 'between', value: '1000', value2: '1500' }), true);
  assert.equal(filters.matchesFilter('2026-07-21', { operator: 'greaterOrEqual', value: '2026-07-01' }), true);
  assert.equal(filters.matchesFilter('', { operator: 'empty' }), true);
  assert.equal(filters.matchesFilter('Vendor', { operator: 'startsWith', value: 'ven' }), true);
  assert.equal(filters.matchesFilter('Draft', { operator: 'equals', value: 'Posted' }), false);
});

test('Business Central expression syntax supports wildcard, exclusion, range, OR and AND', () => {
  assert.equal(filters.matchesExpression('PNX000123', 'PNX*'), true);
  assert.equal(filters.matchesExpression('Posted', '<>Open'), true);
  assert.equal(filters.matchesExpression('250', '100..500'), true);
  assert.equal(filters.matchesExpression('Posted', 'Open|Posted'), true);
  assert.equal(filters.matchesExpression('AB-100', 'AB*&<>AB-200'), true);
  assert.equal(filters.matchesExpression('Closed', 'Open|Posted'), false);
});

test('shared filter engine is loaded globally for every table-bearing application page', async () => {
  const webRoot = path.join(root, 'src/PayNex.Cloud.Api/wwwroot');
  const files = (await readdir(webRoot)).filter(file => file.endsWith('.html'));
  const tablePages = [];
  for (const file of files) {
    const html = await readFile(path.join(webRoot, file), 'utf8');
    if (!/<table\b/i.test(html)) continue;
    tablePages.push(file);
    const isRedirectPage = /http-equiv=["']refresh["']/i.test(html);
    assert.ok(isRedirectPage || /app-shell\.js/i.test(html), `${file} must load the shared app shell`);
  }
  assert.ok(tablePages.includes('sales.html'));
  assert.ok(tablePages.includes('purchases.html'));
  assert.ok(tablePages.includes('finance.html'));
  assert.ok(tablePages.includes('customers.html'));
  assert.ok(tablePages.includes('vendors.html'));

  const shell = await readFile(path.join(webRoot, 'js/app-shell.js'), 'utf8');
  assert.match(shell, /bc-list-filter\.js/);
  assert.match(shell, /data-paynex-list-filters|paynexListFilters/);

  const css = await readFile(path.join(webRoot, 'css/app.css'), 'utf8');
  assert.match(css, /\.bc-column-filter-toolbar/);
  assert.match(css, /\.bc-column-filter-editor/);
  assert.match(css, /\.bc-column-filter-hidden/);
});

test('sales and purchase canonical lists expose Open/Draft, exact Draft, Open and Posted views', async () => {
  const webRoot = path.join(root, 'src/PayNex.Cloud.Api/wwwroot');
  for (const area of ['sales', 'purchases']) {
    const html = await readFile(path.join(webRoot, `${area}.html`), 'utf8');
    const script = await readFile(path.join(webRoot, `js/${area}.js`), 'utf8');
    assert.match(html, /id="documentStatus"/);
    assert.match(html, /value="OpenDraft">Open \/ Draft/);
    assert.match(html, /value="Open">Open/);
    assert.match(html, /value="Draft">Draft/);
    assert.match(html, /value="Posted">Posted/);
    assert.match(script, /requestedStatus/);
    assert.match(script, /documentStatus/);
    assert.match(script, /return 'OpenDraft'/);
  }

  const postedSales = await readFile(path.join(webRoot, 'posted-sales-invoices.html'), 'utf8');
  const draftSales = await readFile(path.join(webRoot, 'draft-sales-invoices.html'), 'utf8');
  const postedPurchases = await readFile(path.join(webRoot, 'posted-purchase-invoices.html'), 'utf8');
  const draftPurchases = await readFile(path.join(webRoot, 'draft-purchase-invoices.html'), 'utf8');
  assert.match(postedSales, /sales\.html\?status=Posted/);
  assert.match(draftSales, /sales\.html\?status=Draft/);
  assert.match(postedPurchases, /purchases\.html\?status=Posted/);
  assert.match(draftPurchases, /purchases\.html\?status=Draft/);
});

test('invoice API source enforces status-only list predicates', async () => {
  const source = await readFile(path.join(root, 'src/PayNex.Cloud.Api/Program.cs'), 'utf8');
  assert.match(source, /NormalizeInvoiceStatusFilter\(status\)/);
  assert.match(source, /h\.Status IN \('Open','Draft'\)/);
  assert.match(source, /@Status<>'OpenDraft' AND h\.Status=@Status/);
  assert.match(source, /"draft" => "Draft"/);
});

test('column filter header icon only shows a neutral box on hover or active state', async () => {
  const webRoot = path.join(root, 'src/PayNex.Cloud.Api/wwwroot');
  const script = await readFile(path.join(webRoot, 'js/bc-list-filter.js'), 'utf8');
  const css = await readFile(path.join(webRoot, 'css/app.css'), 'utf8');
  assert.doesNotMatch(script, /bc-column-filter-trigger secondary/);
  assert.match(script, /bc-column-filter-trigger/);
  assert.match(css, /\.bc-column-filter-trigger\{[\s\S]*color:transparent!important/);
  assert.match(css, /\.bc-column-filter-trigger\{[\s\S]*opacity:0!important/);
  assert.match(css, /\.bc-filterable-column:hover>\.bc-column-filter-trigger[\s\S]*background-color:#F3F2F1!important/);
  assert.match(css, /\.bc-column-filter-trigger\.active[\s\S]*color:#605e5c!important/);
  assert.match(css, /\.bc-column-filter-active\{box-shadow:none!important\}/);
});
