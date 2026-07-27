import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const read = path => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

test('dashboard menu, analytics and profile photo integration are present', async () => {
  const [program, models, schema, shell, dashboardHtml, dashboardJs, dashboardCss, userCardHtml, userCardJs] = await Promise.all([
    read('src/PayNex.Cloud.Api/Program.cs'),
    read('src/PayNex.Cloud.Api/Models/Models.cs'),
    read('database/TenantSchema.sql'),
    read('src/PayNex.Cloud.Api/wwwroot/js/app-shell.js'),
    read('src/PayNex.Cloud.Api/wwwroot/dashboard.html'),
    read('src/PayNex.Cloud.Api/wwwroot/js/dashboard.js'),
    read('src/PayNex.Cloud.Api/wwwroot/css/app.css'),
    read('src/PayNex.Cloud.Api/wwwroot/user-card.html'),
    read('src/PayNex.Cloud.Api/wwwroot/js/user-card.js')
  ]);

  assert.match(shell, /workspace\.html','HOME','Home'[\s\S]*dashboard\.html','DASH','Dashboard'/);
  assert.match(shell, /paynexHeaderUserId/);
  assert.match(shell, /\/api\/me\/photo/);
  assert.match(shell, /paynexMyUserCardLink/);

  assert.match(program, /MapGet\("\/api\/dashboard\/analytics"/);
  assert.match(program, /DATEADD\(HOUR,DATEDIFF\(HOUR,0,SaleDate\),0\)/);
  assert.match(program, /DATEADD\(HOUR,DATEDIFF\(HOUR,0,PostedAt\),0\)/);
  assert.match(program, /hourlyPeriod = new \{ fromUtc = hourlyFromUtc, toUtc = hourlyToUtc, intervalMinutes = 60, slots = 24 \}/);
  assert.match(program, /hourlySales/);
  assert.match(program, /GrossProfit/);
  assert.match(program, /TotalPurchases/);
  assert.match(program, /TotalExpenses/);
  assert.match(program, /NetCashFlow/);
  assert.match(program, /SUM\(l\.LineTotal-l\.TaxAmount-\(l\.Quantity\*l\.UnitCost\)\) FROM SalesLines l/);
  assert.match(program, /SUM\(l\.LineTotal-l\.TaxAmount-\(l\.Quantity\*l\.UnitCost\)\) FROM SalesInvoiceLines l/);
  assert.doesNotMatch(program, /SUM\(LineTotal-TaxAmount-\(Quantity\*UnitCost\)\) FROM Sales(?:Invoice)?Lines l/);
  assert.match(program, /MapGet\("\/api\/me\/photo"/);
  assert.match(program, /MapGet\("\/api\/users\/\{id:int\}\/photo"/);
  assert.match(models, /ProfileImageBase64/);
  assert.match(schema, /ProfileImage VARBINARY\(MAX\)/);

  assert.match(dashboardHtml, /Business Dashboard/);
  assert.match(dashboardHtml, /dashboardInsightStrip/);
  assert.match(dashboardHtml, /Sales Candlestick Analysis/);
  assert.match(dashboardHtml, /60 min \| Last 24 hours/);
  assert.match(dashboardHtml, /60 min sales candle/);
  assert.match(dashboardHtml, /Sales, Purchases &amp; Expenses/);
  assert.match(dashboardHtml, /Monthly Analysis Matrix/);
  assert.match(dashboardJs, /renderInsightStrip/);
  assert.match(dashboardJs, /salesMarketChart/);
  assert.match(dashboardJs, /market-candle/);
  assert.match(dashboardJs, /getRows\(data,'hourlySales','HourlySales'\)/);
  assert.match(dashboardJs, /aria-label="60-minute sales candlestick chart"/);
  assert.match(dashboardJs, /scheduleHourlyRefresh/);
  assert.match(dashboardJs, /nextHour\.setMinutes\(60, 5, 0\)/);
  assert.match(dashboardJs, /premiumEmptyChart/);
  assert.match(dashboardJs, /hasAnyValue/);
  assert.match(dashboardJs, /renderProfitability/);
  assert.match(dashboardJs, /renderLowStock/);
  assert.match(dashboardCss, /dashboard-terminal-card/);
  assert.match(dashboardCss, /dashboard-market-empty/);
  assert.match(dashboardCss, /Business dashboard premium finance cockpit refresh/);
  assert.match(dashboardCss, /Business dashboard light analyst cockpit refresh/);
  assert.match(dashboardCss, /sales-market-chart/);
  assert.match(dashboardCss, /Compact dashboard KPI cards/);
  assert.match(dashboardCss, /grid-template-columns:repeat\(5,minmax\(0,1fr\)\)/);
  assert.match(dashboardCss, /min-height:108px/);
  assert.match(dashboardCss, /font-size:clamp\(22px,1\.65vw,29px\)/);
  assert.doesNotMatch(`${dashboardHtml}\n${dashboardJs}`, /sales by region|region-wise|region wise/i);

  assert.match(userCardHtml, /Upload Photo/);
  assert.match(userCardJs, /profileImageBase64/);
  assert.match(userCardJs, /2\*1024\*1024/);
});
