(() => {
  const $ = id => document.getElementById(id);
  const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const pick = (o, ...keys) => { for (const k of keys) if (o && o[k] != null) return o[k]; return null; };
  const num = (o, ...keys) => Number(pick(o, ...keys) || 0);
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const rowsOf = (data, a, b) => data?.[a] || data?.[b] || [];
  const PBI = ['#118DFF','#12239E','#E66C37','#6B007B','#E044A7','#744EC2','#D9B300','#D64550','#00B7C3','#107C10'];

  let currency = { code:'PKR', name:'Pakistani Rupee', symbol:'Rs.', decimalPlaces:2 };
  let loading = false;
  let hourlyTimer = null;

  const prefix = () => String(currency.symbol || currency.code || '').trim();
  const money = v => `${prefix()} ${Number(v||0).toLocaleString(undefined,{minimumFractionDigits:currency.decimalPlaces,maximumFractionDigits:currency.decimalPlaces})}`.trim();
  const compact = v => {
    const n = Number(v||0), a = Math.abs(n);
    if (a >= 1e9) return `${(n/1e9).toFixed(1)}B`;
    if (a >= 1e6) return `${(n/1e6).toFixed(1)}M`;
    if (a >= 1e3) return `${(n/1e3).toFixed(1)}K`;
    return n.toLocaleString(undefined,{maximumFractionDigits:currency.decimalPlaces});
  };
  const compactMoney = v => `${prefix()} ${compact(v)}`.trim();
  const qty = v => Number(v||0).toLocaleString(undefined,{maximumFractionDigits:2});
  const pct = v => `${Number(v||0).toFixed(1)}%`;
  const formatDate = v => v ? new Date(v).toLocaleDateString(undefined,{day:'2-digit',month:'short',year:'numeric'}) : '—';

  function trend(current, previous, inverse = false) {
    current = Number(current||0); previous = Number(previous||0);
    if (!previous && !current) return { text:'No prior data', cls:'neutral' };
    if (!previous) return { text:'New activity', cls: inverse ? 'down' : 'up' };
    const change = ((current - previous) / Math.abs(previous)) * 100;
    const good = inverse ? change <= 0 : change >= 0;
    return {
      text: `${change >= 0 ? '+' : ''}${change.toFixed(1)}% vs prior`,
      cls: change === 0 ? 'neutral' : (good ? 'up' : 'down')
    };
  }

  function spark(values, color) {
    const clean = values.map(Number);
    if (clean.length < 2) return '';
    const w = 56, h = 22, pad = 1;
    const min = Math.min(...clean), max = Math.max(...clean), range = max - min || 1;
    const pts = clean.map((v,i) => {
      const x = pad + (i / (clean.length - 1)) * (w - pad * 2);
      const y = h - pad - ((v - min) / range) * (h - pad * 2);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(' ');
    return `<svg viewBox="0 0 ${w} ${h}" aria-hidden="true"><polyline points="${pts}" style="stroke:${color}"></polyline></svg>`;
  }

  function empty(msg) {
    return `<div class="pbi-empty">${esc(msg)}</div>`;
  }

  function renderKpis(summary, monthly) {
    const salesS = monthly.map(r => num(r,'salesAmount','SalesAmount'));
    const grossS = monthly.map(r => num(r,'grossProfitAmount','GrossProfitAmount'));
    const netS = monthly.map(r => num(r,'netProfitAmount','NetProfitAmount'));
    const expS = monthly.map(r => num(r,'expenseAmount','ExpenseAmount'));
    const cashS = monthly.map(r => num(r,'cashInAmount','CashInAmount') - num(r,'cashOutAmount','CashOutAmount'));
    const purchS = monthly.map(r => num(r,'purchaseAmount','PurchaseAmount'));

    const items = [
      { label:'Net Revenue', value:compactMoney(num(summary,'totalSales','TotalSales')), cur:num(summary,'totalSales','TotalSales'), prev:num(summary,'previousSales','PreviousSales'), tone:'t-blue', color:'#118DFF', series:salesS },
      { label:'Gross Profit', value:compactMoney(num(summary,'grossProfit','GrossProfit')), cur:num(summary,'grossProfit','GrossProfit'), prev:num(summary,'previousGrossProfit','PreviousGrossProfit'), tone:'t-green', color:'#107C10', series:grossS },
      { label:'Net Profit', value:compactMoney(num(summary,'netProfit','NetProfit')), cur:num(summary,'netProfit','NetProfit'), prev:num(summary,'previousNetProfit','PreviousNetProfit'), tone:'t-navy', color:'#12239E', series:netS },
      { label:'Expenses', value:compactMoney(num(summary,'totalExpenses','TotalExpenses')), cur:num(summary,'totalExpenses','TotalExpenses'), prev:num(summary,'previousExpenses','PreviousExpenses'), inverse:true, tone:'t-orange', color:'#E66C37', series:expS },
      { label:'Purchases', value:compactMoney(num(summary,'totalPurchases','TotalPurchases')), cur:num(summary,'totalPurchases','TotalPurchases'), prev:num(summary,'previousPurchases','PreviousPurchases'), tone:'t-purple', color:'#744EC2', series:purchS },
      { label:'Net Cash Flow', value:compactMoney(num(summary,'netCashFlow','NetCashFlow')), cur:num(summary,'cashInflow','CashInflow')-num(summary,'cashOutflow','CashOutflow'), prev:num(summary,'previousCashInflow','PreviousCashInflow')-num(summary,'previousCashOutflow','PreviousCashOutflow'), tone:'t-teal', color:'#00B7C3', series:cashS },
      { label:'Receivables', value:compactMoney(num(summary,'receivables','Receivables')), sub:`Payables ${compactMoney(num(summary,'payables','Payables'))}`, tone:'t-pink', color:'#E044A7' }
    ];

    $('dashboardKpis').innerHTML = items.map(k => {
      const t = k.cur === undefined ? null : trend(k.cur, k.prev, k.inverse);
      return `<article class="pbi-kpi ${k.tone}">
        <span class="k-label">${esc(k.label)}</span>
        <span class="k-value">${esc(k.value)}</span>
        <div class="k-meta">
          <span class="k-trend ${t ? t.cls : 'neutral'}">${t ? esc(t.text) : esc(k.sub || '')}</span>
          ${k.series ? spark(k.series, k.color) : ''}
        </div>
      </article>`;
    }).join('');
  }

  function lineAreaChart(rows) {
    if (!rows.length) return empty('Post sales to build the monthly trend.');
    const width = 900, height = 280, left = 52, right = 14, top = 18, bottom = 40;
    const series = [
      { keys:['salesAmount','SalesAmount'], cls:'sales' },
      { keys:['grossProfitAmount','GrossProfitAmount'], cls:'gross' },
      { keys:['netProfitAmount','NetProfitAmount'], cls:'net' }
    ];
    const all = rows.flatMap(r => series.map(s => num(r, ...s.keys)));
    const max = Math.max(1, ...all);
    const min = Math.min(0, ...all);
    const range = max - min || 1;
    const x = i => left + (i / Math.max(1, rows.length - 1)) * (width - left - right);
    const y = v => top + ((max - v) / range) * (height - top - bottom);
    const grid = Array.from({ length:5 }, (_, i) => {
      const value = max - (range / 4) * i;
      const yy = y(value);
      return `<line class="pbi-grid-line" x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}"/><text x="${left-8}" y="${yy+3}" text-anchor="end">${esc(compact(value))}</text>`;
    }).join('');
    const salesPts = rows.map((r,i) => `${x(i).toFixed(1)},${y(num(r,'salesAmount','SalesAmount')).toFixed(1)}`).join(' ');
    const area = `<polygon class="pbi-area" points="${left},${height-bottom} ${salesPts} ${width-right},${height-bottom}"/>`;
    const lines = series.map(s => {
      const pts = rows.map((r,i) => `${x(i).toFixed(1)},${y(num(r,...s.keys)).toFixed(1)}`).join(' ');
      return `<polyline class="pbi-line ${s.cls}" points="${pts}"/>`;
    }).join('');
    const every = Math.max(1, Math.ceil(rows.length / 7));
    const labels = rows.map((r,i) => (i % every === 0 || i === rows.length - 1)
      ? `<text x="${x(i)}" y="${height-14}" text-anchor="middle">${esc(pick(r,'monthLabel','MonthLabel')||'')}</text>` : '').join('');
    const dots = rows.map((r,i) => `<circle class="pbi-chart-dot" cx="${x(i)}" cy="${y(num(r,'salesAmount','SalesAmount'))}" r="3"><title>${esc(pick(r,'monthLabel','MonthLabel'))}: ${esc(money(num(r,'salesAmount','SalesAmount')))}</title></circle>`).join('');
    return `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Revenue and profit trend">${grid}${area}${lines}${dots}${labels}</svg>`;
  }

  function barChart(rows, keyGroups, classes, aria) {
    if (!rows.length) return empty('No data for this period.');
    const has = rows.some(r => keyGroups.some(k => Math.abs(num(r, ...k)) > 0));
    if (!has) return empty('No posted activity in this period.');
    const width = 720, height = 230, left = 48, right = 12, top = 14, bottom = 38;
    const values = rows.flatMap(r => keyGroups.map(k => num(r, ...k)));
    const max = Math.max(1, ...values);
    const plotW = width - left - right, plotH = height - top - bottom;
    const groupW = plotW / Math.max(1, rows.length);
    const inner = Math.min(groupW * .78, 52);
    const barW = inner / keyGroups.length;
    const grid = Array.from({ length:5 }, (_, i) => {
      const v = max - (max / 4) * i;
      const yy = top + (i / 4) * plotH;
      return `<line class="pbi-grid-line" x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}"/><text x="${left-6}" y="${yy+3}" text-anchor="end">${esc(compact(v))}</text>`;
    }).join('');
    const bars = rows.map((r, i) => {
      const gx = left + i * groupW + (groupW - inner) / 2;
      const rects = keyGroups.map((k, j) => {
        const v = num(r, ...k);
        const h = (v / max) * plotH;
        return `<rect class="pbi-bar ${classes[j]}" x="${gx + j * barW}" y="${top + plotH - h}" width="${Math.max(3, barW - 2)}" height="${h}"><title>${esc(pick(r,'monthLabel','MonthLabel'))}: ${esc(money(v))}</title></rect>`;
      }).join('');
      const label = (i === rows.length - 1 || i % Math.max(1, Math.ceil(rows.length / 6)) === 0)
        ? `<text x="${left + (i + .5) * groupW}" y="${height-12}" text-anchor="middle">${esc(pick(r,'monthLabel','MonthLabel')||'')}</text>` : '';
      return rects + label;
    }).join('');
    return `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(aria)}">${grid}${bars}</svg>`;
  }

  function donutChart(rows, labelKeys, amountKeys, centerLabel) {
    const values = rows.map(r => num(r, ...amountKeys));
    const total = values.reduce((a, b) => a + b, 0);
    if (!rows.length || total <= 0) return empty('No data available.');
    const cx = 75, cy = 75, r = 58, ir = 36;
    let angle = -Math.PI / 2;
    const slices = values.map((v, i) => {
      const sweep = (v / total) * Math.PI * 2;
      const a0 = angle, a1 = angle + sweep;
      angle = a1;
      const x0 = cx + r * Math.cos(a0), y0 = cy + r * Math.sin(a0);
      const x1 = cx + r * Math.cos(a1), y1 = cy + r * Math.sin(a1);
      const xi0 = cx + ir * Math.cos(a1), yi0 = cy + ir * Math.sin(a1);
      const xi1 = cx + ir * Math.cos(a0), yi1 = cy + ir * Math.sin(a0);
      const large = sweep > Math.PI ? 1 : 0;
      const d = `M ${x0} ${y0} A ${r} ${r} 0 ${large} 1 ${x1} ${y1} L ${xi0} ${yi0} A ${ir} ${ir} 0 ${large} 0 ${xi1} ${yi1} Z`;
      const color = PBI[i % PBI.length];
      return `<path d="${d}" fill="${color}"><title>${esc(pick(rows[i], ...labelKeys) || 'Other')}: ${esc(money(v))}</title></path>`;
    }).join('');
    const legend = rows.map((r, i) => `<div>
      <i class="pbi-swatch" style="--c:${PBI[i % PBI.length]}"></i>
      <b>${esc(pick(r, ...labelKeys) || 'Other')}</b>
      <strong>${pct(num(r, ...amountKeys) / total * 100)}</strong>
    </div>`).join('');
    return `<svg class="pbi-donut-svg" viewBox="0 0 150 150" role="img">${slices}
      <circle class="pbi-donut-center" cx="75" cy="75" r="34"/>
      <text class="pbi-donut-center-text" x="75" y="72">${esc(compactMoney(total))}</text>
      <text class="pbi-donut-center-sub" x="75" y="88">${esc(centerLabel)}</text>
    </svg><div class="pbi-donut-legend">${legend}</div>`;
  }

  function renderHealth(summary) {
    const sales = num(summary,'totalSales','TotalSales');
    const gross = num(summary,'grossProfit','GrossProfit');
    const net = num(summary,'netProfit','NetProfit');
    const cash = num(summary,'netCashFlow','NetCashFlow');
    const low = num(summary,'lowStockCount','LowStockCount');
    const active = num(summary,'activeProducts','ActiveProducts');
    const returns = num(summary,'returnsAmount','ReturnsAmount');
    const receivables = num(summary,'receivables','Receivables');
    const margin = sales ? gross / sales * 100 : 0;
    const returnRate = sales ? returns / sales * 100 : 0;
    const stockRisk = active ? low / active * 100 : 0;
    const receivableRisk = sales ? receivables / sales * 100 : 0;
    let score = 45 + clamp(margin, 0, 30) * 1.1 + (net >= 0 ? 10 : -15) + (cash >= 0 ? 8 : -10)
      - clamp(returnRate * 2.5, 0, 14) - clamp(stockRisk * .35, 0, 12) - clamp(receivableRisk * .08, 0, 10);
    score = Math.round(clamp(score, 0, 100));
    const label = score >= 80 ? 'Excellent' : score >= 65 ? 'Healthy' : score >= 50 ? 'Watch' : score >= 35 ? 'At Risk' : 'Critical';
    $('businessHealthGauge').innerHTML = `<div class="pbi-health-ring" style="--score:${score}"><div><strong>${score}</strong><span>${label}</span></div></div>`;
    $('businessHealthFacts').innerHTML = `
      <div><span>Gross margin</span><b>${pct(margin)}</b></div>
      <div><span>Net cash</span><b class="${cash>=0?'pbi-pos':'pbi-neg'}">${esc(compactMoney(cash))}</b></div>
      <div><span>Return rate</span><b>${pct(returnRate)}</b></div>`;
  }

  function renderProfit(summary) {
    const sales = num(summary,'totalSales','TotalSales');
    const gross = num(summary,'grossProfit','GrossProfit');
    const expense = num(summary,'totalExpenses','TotalExpenses');
    const net = num(summary,'netProfit','NetProfit');
    const discounts = num(summary,'salesDiscounts','SalesDiscounts');
    const returns = num(summary,'returnsAmount','ReturnsAmount');
    const metrics = [
      ['Gross margin', sales ? gross / sales * 100 : 0],
      ['Expense ratio', sales ? expense / sales * 100 : 0],
      ['Net margin', sales ? net / sales * 100 : 0],
      ['Discount ratio', sales ? discounts / sales * 100 : 0],
      ['Return ratio', sales ? returns / sales * 100 : 0]
    ];
    $('profitabilityPanel').innerHTML = metrics.map(([label, value]) => `
      <div class="pbi-profit-row"><span>${esc(label)}</span><div><i style="width:${clamp(Math.abs(value),0,100)}%"></i></div><strong class="${label==='Net margin'&&value<0?'pbi-neg':''}">${pct(value)}</strong></div>
    `).join('') + `<div class="pbi-profit-foot"><span>Net profit after expenses</span><strong class="${net>=0?'pbi-pos':'pbi-neg'}">${esc(compactMoney(net))}</strong></div>`;
  }

  function renderRanked(targetId, rows, labelKeys, amountKeys) {
    const target = $(targetId);
    if (!rows.length) { target.innerHTML = empty('No data for this period.'); return; }
    const max = Math.max(1, ...rows.map(r => num(r, ...amountKeys)));
    target.innerHTML = rows.slice(0, 8).map((r, i) => {
      const amount = num(r, ...amountKeys);
      return `<div class="pbi-ranked-row">
        <div class="lab"><span>${i+1}</span><b>${esc(pick(r,...labelKeys)||'Unspecified')}</b><strong>${esc(compactMoney(amount))}</strong></div>
        <div class="track"><i style="width:${Math.max(3, amount/max*100)}%"></i></div>
      </div>`;
    }).join('');
  }

  function renderInvoices(rows) {
    if (!rows.length) { $('recentInvoiceBody').innerHTML = `<tr><td colspan="5" class="pbi-empty" style="border:0">No recent sales found.</td></tr>`; return; }
    $('recentInvoiceBody').innerHTML = rows.slice(0, 10).map(r => {
      const balance = num(r,'balanceAmount','BalanceAmount');
      const raw = String(pick(r,'status','Status') || 'Posted');
      const status = balance > 0 ? 'Pending' : raw;
      const cls = /paid|posted/i.test(status) ? 'ok' : /pending|open/i.test(status) ? 'warn' : 'mute';
      return `<tr>
        <td><b>${esc(pick(r,'invoiceNo','InvoiceNo'))}</b></td>
        <td>${esc(pick(r,'customerName','CustomerName')||'Walk-in')}</td>
        <td>${esc(formatDate(pick(r,'invoiceDate','InvoiceDate')))}</td>
        <td class="num">${esc(money(num(r,'grandTotal','GrandTotal')))}</td>
        <td><span class="pbi-pill ${cls}">${esc(status)}</span></td>
      </tr>`;
    }).join('');
  }

  function renderTopProducts(rows) {
    if (!rows.length) { $('topProductsList').innerHTML = empty('No product sales yet.'); return; }
    $('topProductsList').innerHTML = rows.slice(0, 8).map((r, i) => `
      <a class="pbi-rank-item" href="/item-card.html?id=${num(r,'productId','ProductId')}">
        <span>${i+1}</span>
        <div><b>${esc(pick(r,'productName','ProductName'))}</b><small>${qty(num(r,'quantity','Quantity'))} units</small></div>
        <strong>${esc(compactMoney(num(r,'amount','Amount')))}</strong>
      </a>`).join('');
  }

  function renderTopCustomers(rows) {
    if (!rows.length) { $('topCustomersList').innerHTML = empty('No customer sales yet.'); return; }
    $('topCustomersList').innerHTML = rows.slice(0, 8).map((r, i) => `
      <div class="pbi-rank-item">
        <span>${i+1}</span>
        <div><b>${esc(pick(r,'customerName','CustomerName'))}</b><small>${esc(pick(r,'customerCode','CustomerCode'))} · ${qty(num(r,'documentCount','DocumentCount'))} docs</small></div>
        <strong>${esc(compactMoney(num(r,'amount','Amount')))}</strong>
      </div>`).join('');
  }

  function renderMonthly(rows) {
    if (!rows.length) { $('monthlyBody').innerHTML = `<tr><td colspan="8" class="pbi-empty" style="border:0">No monthly analytics.</td></tr>`; return; }
    $('monthlyBody').innerHTML = rows.map(r => {
      const sales = num(r,'salesAmount','SalesAmount');
      const gross = num(r,'grossProfitAmount','GrossProfitAmount');
      const net = num(r,'netProfitAmount','NetProfitAmount');
      return `<tr>
        <td><b>${esc(pick(r,'monthLabel','MonthLabel'))}</b></td>
        <td class="num">${esc(money(sales))}</td>
        <td class="num">${esc(money(num(r,'purchaseAmount','PurchaseAmount')))}</td>
        <td class="num pbi-pos">${esc(money(gross))}</td>
        <td class="num">${esc(money(num(r,'expenseAmount','ExpenseAmount')))}</td>
        <td class="num ${net>=0?'pbi-pos':'pbi-neg'}">${esc(money(net))}</td>
        <td class="num">${pct(sales ? gross/sales*100 : 0)}</td>
        <td class="num">${qty(num(r,'documentCount','DocumentCount'))}</td>
      </tr>`;
    }).join('');
  }

  function renderAll(data) {
    const configured = data.currency || data.Currency || {};
    currency = {
      code: String(pick(configured,'currencyCode','CurrencyCode') || 'PKR'),
      name: String(pick(configured,'currencyName','CurrencyName') || 'Pakistani Rupee'),
      symbol: String(pick(configured,'symbol','Symbol') || pick(configured,'currencyCode','CurrencyCode') || 'PKR'),
      decimalPlaces: clamp(Number(pick(configured,'decimalPlaces','DecimalPlaces') ?? 2), 0, 4)
    };
    $('dashboardCurrencyBadge').textContent = `${currency.code} · ${currency.symbol}`;
    $('dashboardCurrencyNote').textContent = `All amounts in ${currency.code} (${currency.name}) from posted ERP transactions.`;

    const summary = data.summary || data.Summary || {};
    const monthly = rowsOf(data, 'monthly', 'Monthly');
    renderKpis(summary, monthly);
    $('revenueProfitChart').innerHTML = lineAreaChart(monthly);
    $('operatingChart').innerHTML = barChart(monthly,
      [['salesAmount','SalesAmount'],['purchaseAmount','PurchaseAmount'],['expenseAmount','ExpenseAmount']],
      ['sales','purchase','expense'], 'Sales purchases expenses');
    $('cashFlowChart').innerHTML = barChart(monthly,
      [['cashInAmount','CashInAmount'],['cashOutAmount','CashOutAmount']],
      ['cashin','cashout'], 'Cash flow');
    $('salesCategoryChart').innerHTML = donutChart(rowsOf(data,'salesByCategory','SalesByCategory'), ['categoryName','CategoryName'], ['amount','Amount'], 'Sales');
    $('paymentMethodsChart').innerHTML = donutChart(rowsOf(data,'paymentMethods','PaymentMethods'), ['paymentMethodName','PaymentMethodName'], ['amount','Amount'], 'Receipts');
    renderHealth(summary);
    renderProfit(summary);
    renderRanked('expenseCategoryList', rowsOf(data,'expensesByCategory','ExpensesByCategory'), ['categoryName','CategoryName'], ['amount','Amount']);
    renderInvoices(rowsOf(data,'recentInvoices','RecentInvoices'));
    renderTopProducts(rowsOf(data,'topProducts','TopProducts'));
    renderTopCustomers(rowsOf(data,'topCustomers','TopCustomers'));
    renderMonthly(monthly);
    $('trendPeriod').textContent = `Last ${monthly.length || 0} months`;
    $('dashboardUpdatedAt').textContent = `Updated ${new Date().toLocaleString()}`;
    $('dashboardStatus').hidden = true;
  }

  async function loadDashboard({ quiet = false } = {}) {
    if (loading) return;
    loading = true;
    const btn = $('refreshDashboardBtn');
    if (!quiet) {
      btn.disabled = true; btn.textContent = 'Refreshing...';
      $('dashboardStatus').hidden = false;
      $('dashboardStatus').className = 'pbi-status';
      $('dashboardStatus').textContent = 'Loading executive analytics...';
    }
    try {
      const data = await api.get(`/api/dashboard/analytics?months=${Number($('months').value || 12)}`);
      renderAll(data);
    } catch (e) {
      if (!quiet) {
        $('dashboardStatus').hidden = false;
        $('dashboardStatus').className = 'pbi-status error';
        $('dashboardStatus').textContent = e.message || 'Unable to load dashboard.';
      }
    } finally {
      loading = false;
      if (!quiet) { btn.disabled = false; btn.textContent = 'Refresh'; }
    }
  }

  function scheduleRefresh() {
    if (hourlyTimer) window.clearTimeout(hourlyTimer);
    const now = new Date();
    const next = new Date(now);
    next.setMinutes(60, 5, 0);
    hourlyTimer = window.setTimeout(async () => {
      await loadDashboard({ quiet:true });
      scheduleRefresh();
    }, Math.max(1000, next.getTime() - now.getTime()));
  }

  async function init() {
    try {
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName||me.CompanyName} · ${me.storeName||me.StoreName||me.branchName||me.BranchName||'Branch'} · ${me.displayName||me.DisplayName}`;
    } catch {
      location.href = '/login.html?returnUrl=' + encodeURIComponent(location.pathname + location.search);
      return;
    }
    $('refreshDashboardBtn').addEventListener('click', loadDashboard);
    $('months').addEventListener('change', loadDashboard);
    await loadDashboard();
    scheduleRefresh();
  }

  init();
})();
