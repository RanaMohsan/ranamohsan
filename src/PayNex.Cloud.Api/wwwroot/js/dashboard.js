(() => {
  const $ = id => document.getElementById(id);
  const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  const pick = (obj, ...keys) => {
    for (const key of keys) if (obj && obj[key] !== undefined && obj[key] !== null) return obj[key];
    return null;
  };
  const num = (obj, ...keys) => Number(pick(obj, ...keys) || 0);
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  let currency = {code:'PKR',name:'Pakistani Rupee',symbol:'Rs.',decimalPlaces:2};
  const currencyPrefix = () => String(currency.symbol || currency.code || '').trim();
  const money = value => `${currencyPrefix()} ${Number(value || 0).toLocaleString(undefined, {minimumFractionDigits:currency.decimalPlaces,maximumFractionDigits:currency.decimalPlaces})}`.trim();
  const compactNumber = value => {
    const n=Number(value||0),a=Math.abs(n);
    if(a>=1e9)return `${(n/1e9).toFixed(1)}B`;
    if(a>=1e6)return `${(n/1e6).toFixed(1)}M`;
    if(a>=1e3)return `${(n/1e3).toFixed(1)}K`;
    return n.toLocaleString(undefined,{maximumFractionDigits:currency.decimalPlaces});
  };
  const compactMoney = value => {
    return `${currencyPrefix()} ${compactNumber(value)}`.trim();
  };
  const qty = value => Number(value || 0).toLocaleString(undefined, {maximumFractionDigits: 2});
  const pct = value => `${Number(value || 0).toFixed(1)}%`;
  const formatDate = value => value ? new Date(value).toLocaleDateString(undefined, {day:'2-digit', month:'short', year:'numeric'}) : '-';
  const formatHour = (value, includeDate = false) => {
    const date = value ? new Date(value) : null;
    if (!date || Number.isNaN(date.getTime())) return '-';
    return date.toLocaleString(undefined, includeDate
      ? {month:'short', day:'2-digit', hour:'2-digit', minute:'2-digit'}
      : {hour:'2-digit', minute:'2-digit'});
  };
  const getRows = (data, camel, pascal) => data?.[camel] || data?.[pascal] || [];

  let currentData = null;
  let hourlyRefreshTimer = null;
  let dashboardLoading = false;

  function trendInfo(current, previous, inverse = false) {
    current = Number(current || 0); previous = Number(previous || 0);
    if (!previous && !current) return { text: 'No previous activity', cls: 'neutral', arrow: '-' };
    if (!previous) return { text: 'New activity', cls: inverse ? 'down' : 'up', arrow: '+' };
    const change = ((current - previous) / Math.abs(previous)) * 100;
    const isGood = inverse ? change <= 0 : change >= 0;
    return { text: `${Math.abs(change).toFixed(1)}% vs previous period`, cls: change === 0 ? 'neutral' : (isGood ? 'up' : 'down'), arrow: change >= 0 ? '+' : '-' };
  }

  function hasAnyValue(rows, keyGroups) {
    return rows.some(row => keyGroups.some(keys => Math.abs(num(row, ...keys)) > 0));
  }

  function premiumEmptyChart(title, subtext) {
    const bars = [42, 68, 50, 78, 58, 86, 64].map((height, index) => `<i style="height:${height}%" class="ghost-bar-${index % 4}"></i>`).join('');
    return `<div class="dashboard-market-empty">
      <div class="dashboard-market-empty-visual" aria-hidden="true">
        <span class="ghost-line"></span>
        <div>${bars}</div>
      </div>
      <strong>${esc(title)}</strong>
      <span>${esc(subtext)}</span>
    </div>`;
  }

  function renderInsightStrip(summary, monthly) {
    const target = $('dashboardInsightStrip');
    if (!target) return;

    const sales = num(summary, 'totalSales', 'TotalSales');
    const gross = num(summary, 'grossProfit', 'GrossProfit');
    const net = num(summary, 'netProfit', 'NetProfit');
    const expenses = num(summary, 'totalExpenses', 'TotalExpenses');
    const cash = num(summary, 'netCashFlow', 'NetCashFlow');
    const receivables = num(summary, 'receivables', 'Receivables');
    const payables = num(summary, 'payables', 'Payables');
    const lowStock = num(summary, 'lowStockCount', 'LowStockCount');
    const documents = num(summary, 'salesDocuments', 'SalesDocuments');
    const activeCustomers = num(summary, 'activeCustomers', 'ActiveCustomers');
    const last = monthly[monthly.length - 1] || {};
    const prev = monthly[monthly.length - 2] || {};
    const velocity = trendInfo(num(last, 'salesAmount', 'SalesAmount'), num(prev, 'salesAmount', 'SalesAmount'));
    const grossMargin = sales ? gross / sales * 100 : 0;
    const netMargin = sales ? net / sales * 100 : 0;
    const expenseRatio = sales ? expenses / sales * 100 : 0;
    const riskCount = (cash < 0 ? 1 : 0) + (lowStock > 0 ? 1 : 0) + (sales > 0 && receivables > sales * .35 ? 1 : 0) + (payables > receivables && payables > 0 ? 1 : 0);

    const cards = [
      {
        tone: 'cyan',
        label: 'Revenue engine',
        value: compactMoney(sales),
        meta: `${qty(documents)} posted document(s)`,
        verdict: documents ? velocity.text : 'Waiting for posted sales',
        state: velocity.cls
      },
      {
        tone: 'green',
        label: 'Profit quality',
        value: pct(grossMargin),
        meta: `Net margin ${pct(netMargin)}`,
        verdict: grossMargin >= 25 ? 'Strong margin profile' : grossMargin > 0 ? 'Margin needs attention' : 'No gross profit yet',
        state: grossMargin >= 25 ? 'up' : grossMargin > 0 ? 'neutral' : 'down'
      },
      {
        tone: 'amber',
        label: 'Cost control',
        value: pct(expenseRatio),
        meta: `Expenses ${compactMoney(expenses)}`,
        verdict: expenseRatio > 35 ? 'Expense pressure is high' : expenseRatio > 0 ? 'Costs are controlled' : 'No expense load',
        state: expenseRatio > 35 ? 'down' : 'up'
      },
      {
        tone: 'violet',
        label: 'Liquidity watch',
        value: compactMoney(cash),
        meta: `AR ${compactMoney(receivables)} / AP ${compactMoney(payables)}`,
        verdict: riskCount ? `${riskCount} risk signal(s) active` : `${qty(activeCustomers)} active customer(s)`,
        state: riskCount ? 'down' : 'up'
      }
    ];

    target.innerHTML = cards.map(card => `<article class="dashboard-terminal-card tone-${card.tone}">
      <div>
        <span>${esc(card.label)}</span>
        <strong>${esc(card.value)}</strong>
      </div>
      <p>${esc(card.meta)}</p>
      <em class="${card.state}">${esc(card.verdict)}</em>
    </article>`).join('');
  }

  function sparkline(values, cssClass = 'blue') {
    const clean = values.map(Number);
    if (!clean.length) return '';
    const width = 112, height = 38, pad = 3;
    const min = Math.min(...clean), max = Math.max(...clean), range = max - min || 1;
    const points = clean.map((v, i) => {
      const x = pad + (i / Math.max(1, clean.length - 1)) * (width - pad * 2);
      const y = height - pad - ((v - min) / range) * (height - pad * 2);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(' ');
    return `<svg class="dashboard-sparkline ${cssClass}" viewBox="0 0 ${width} ${height}" aria-hidden="true"><polyline points="${points}"></polyline></svg>`;
  }

  function renderKpis(summary, monthly) {
    const salesSeries = monthly.map(r => num(r, 'salesAmount', 'SalesAmount'));
    const grossSeries = monthly.map(r => num(r, 'grossProfitAmount', 'GrossProfitAmount'));
    const netSeries = monthly.map(r => num(r, 'netProfitAmount', 'NetProfitAmount'));
    const expenseSeries = monthly.map(r => num(r, 'expenseAmount', 'ExpenseAmount'));
    const purchaseSeries = monthly.map(r => num(r, 'purchaseAmount', 'PurchaseAmount'));
    const cashSeries = monthly.map(r => num(r, 'cashInAmount', 'CashInAmount') - num(r, 'cashOutAmount', 'CashOutAmount'));

    const shortTrend = (current, previous, inverse = false) => {
      const t = trendInfo(current, previous, inverse);
      if (t.text === 'New activity') return { text: 'New activity', cls: t.cls, arrow: '+' };
      if (t.text === 'No previous activity') return { text: 'No prior data', cls: 'neutral', arrow: '' };
      return { text: t.text.replace(' vs previous period', ''), cls: t.cls, arrow: t.arrow };
    };

    const kpis = [
      {label:'Net Revenue', value:compactMoney(num(summary,'totalSales','TotalSales')), current:num(summary,'totalSales','TotalSales'), previous:num(summary,'previousSales','PreviousSales'), tone:'blue', series:salesSeries},
      {label:'Gross Profit', value:compactMoney(num(summary,'grossProfit','GrossProfit')), current:num(summary,'grossProfit','GrossProfit'), previous:num(summary,'previousGrossProfit','PreviousGrossProfit'), tone:'green', series:grossSeries},
      {label:'Net Profit', value:compactMoney(num(summary,'netProfit','NetProfit')), current:num(summary,'netProfit','NetProfit'), previous:num(summary,'previousNetProfit','PreviousNetProfit'), tone:'navy', series:netSeries},
      {label:'Expenses', value:compactMoney(num(summary,'totalExpenses','TotalExpenses')), current:num(summary,'totalExpenses','TotalExpenses'), previous:num(summary,'previousExpenses','PreviousExpenses'), inverse:true, tone:'orange', series:expenseSeries},
      {label:'Purchases', value:compactMoney(num(summary,'totalPurchases','TotalPurchases')), current:num(summary,'totalPurchases','TotalPurchases'), previous:num(summary,'previousPurchases','PreviousPurchases'), tone:'violet', series:purchaseSeries},
      {label:'Net Cash Flow', value:compactMoney(num(summary,'netCashFlow','NetCashFlow')), current:num(summary,'cashInflow','CashInflow')-num(summary,'cashOutflow','CashOutflow'), previous:num(summary,'previousCashInflow','PreviousCashInflow')-num(summary,'previousCashOutflow','PreviousCashOutflow'), tone:'teal', series:cashSeries},
      {label:'Receivables', value:compactMoney(num(summary,'receivables','Receivables')), sub:`Payables ${compactMoney(num(summary,'payables','Payables'))}`, tone:'gray'}
    ];

    $('dashboardKpis').innerHTML = kpis.map(k => {
      const t = k.current === undefined ? null : shortTrend(k.current, k.previous, k.inverse);
      return `<article class="dashboard-kpi-card tone-${k.tone}">
        <div class="dashboard-kpi-label">${esc(k.label)}</div>
        <div class="dashboard-kpi-main"><strong>${esc(k.value)}</strong>${k.series ? sparkline(k.series, k.tone) : ''}</div>
        <div class="dashboard-kpi-foot ${t ? t.cls : 'neutral'}">${t ? `${t.arrow ? `<span>${t.arrow}</span>` : ''}${esc(t.text)}` : esc(k.sub || '')}</div>
      </article>`;
    }).join('');
  }

  function lineChart(rows) {
    const width = 960, height = 310, left = 62, right = 18, top = 24, bottom = 46;
    const series = [
      {key:['salesAmount','SalesAmount'], cls:'sales-line'},
      {key:['grossProfitAmount','GrossProfitAmount'], cls:'gross-line'},
      {key:['netProfitAmount','NetProfitAmount'], cls:'net-line'}
    ];
    const all = rows.flatMap(r => series.map(s => num(r, ...s.key)));
    const max = Math.max(1, ...all), min = Math.min(0, ...all);
    const range = max - min || 1;
    const x = i => left + (i / Math.max(1, rows.length - 1)) * (width - left - right);
    const y = v => top + ((max - v) / range) * (height - top - bottom);
    const grid = Array.from({length:5}, (_, i) => {
      const value = max - (range / 4) * i;
      const yy = y(value);
      return `<line x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}" class="grid-line"/><text x="${left-10}" y="${yy+4}" text-anchor="end">${esc(compactNumber(value))}</text>`;
    }).join('');
    const paths = series.map(s => {
      const points = rows.map((r,i)=>`${x(i).toFixed(1)},${y(num(r,...s.key)).toFixed(1)}`).join(' ');
      return `<polyline class="chart-line ${s.cls}" points="${points}"/>`;
    }).join('');
    const salesPoints = rows.map((r,i)=>`${x(i).toFixed(1)},${y(num(r,'salesAmount','SalesAmount')).toFixed(1)}`).join(' ');
    const area = rows.length ? `<polygon class="sales-area" points="${left},${height-bottom} ${salesPoints} ${width-right},${height-bottom}"/>` : '';
    const grossPoints = rows.map((r,i)=>`${x(i).toFixed(1)},${y(num(r,'grossProfitAmount','GrossProfitAmount')).toFixed(1)}`).join(' ');
    const grossArea = rows.length ? `<polygon class="gross-area" points="${left},${height-bottom} ${grossPoints} ${width-right},${height-bottom}"/>` : '';
    const every = Math.max(1, Math.ceil(rows.length / 7));
    const labels = rows.map((r,i)=> i % every === 0 || i === rows.length-1 ? `<text x="${x(i)}" y="${height-16}" text-anchor="middle">${esc(pick(r,'monthLabel','MonthLabel')||'')}</text>` : '').join('');
    const dots = rows.map((r,i)=>`<circle class="sales-dot" cx="${x(i)}" cy="${y(num(r,'salesAmount','SalesAmount'))}" r="3.2"><title>${esc(pick(r,'monthLabel','MonthLabel'))}: ${esc(money(num(r,'salesAmount','SalesAmount')))}</title></circle>`).join('');
    return `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Monthly sales and profit trend">${grid}${area}${grossArea}${paths}${dots}${labels}</svg>`;
  }

  function movingAverage(values, size) {
    return values.map((_, index) => {
      const slice = values.slice(Math.max(0, index - size + 1), index + 1);
      return slice.reduce((sum, value) => sum + value, 0) / Math.max(1, slice.length);
    });
  }

  function salesMarketChart(rows) {
    const width = 980, height = 330, left = 62, right = 18, top = 26, bottom = 48;
    const closes = rows.map(row => num(row, 'salesAmount', 'SalesAmount'));
    const candles = rows.map((row, index) => {
      const close = closes[index];
      const open = index ? closes[index - 1] : close;
      return {
        label: formatHour(pick(row, 'hourStartUtc', 'HourStartUtc'), true),
        axisLabel: formatHour(pick(row, 'hourStartUtc', 'HourStartUtc')),
        open,
        close,
        high: Math.max(open, close),
        low: Math.min(open, close),
        documents: num(row, 'documentCount', 'DocumentCount'),
        returns: num(row, 'returnsAmount', 'ReturnsAmount')
      };
    });
    const max = Math.max(1, ...candles.map(c => c.high));
    const min = Math.min(0, ...candles.map(c => c.low));
    const range = max - min || 1;
    const plotW = width - left - right;
    const plotH = height - top - bottom;
    const step = plotW / Math.max(1, candles.length);
    const candleW = Math.max(8, Math.min(28, step * .48));
    const x = index => left + step * index + step / 2;
    const y = value => top + ((max - value) / range) * plotH;
    const linePath = values => values.map((value, index) => `${index ? 'L' : 'M'}${x(index).toFixed(1)} ${y(value).toFixed(1)}`).join(' ');

    const grid = Array.from({length: 5}, (_, index) => {
      const value = max - (range / 4) * index;
      const yy = y(value);
      return `<line x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}" class="market-grid-line"/><text x="${left-10}" y="${yy+4}" text-anchor="end">${esc(compactNumber(value))}</text>`;
    }).join('');
    const candlesSvg = candles.map((candle, index) => {
      const cx = x(index);
      const openY = y(candle.open);
      const closeY = y(candle.close);
      const topY = Math.min(openY, closeY);
      const bodyH = Math.max(4, Math.abs(openY - closeY));
      const bodyY = bodyH === 4 ? clamp(topY - 2, top, top + plotH - 4) : topY;
      const direction = candle.close > candle.open ? 'up' : (candle.close < candle.open ? 'down' : 'flat');
      return `<g class="market-candle ${direction}">
        <line x1="${cx}" y1="${y(candle.high)}" x2="${cx}" y2="${y(candle.low)}"></line>
        <rect x="${cx - candleW / 2}" y="${bodyY}" width="${candleW}" height="${bodyH}"><title>${esc(candle.label)} | open ${esc(money(candle.open))} | close ${esc(money(candle.close))} | ${esc(qty(candle.documents))} sale(s) | returns ${esc(money(candle.returns))}</title></rect>
      </g>`;
    }).join('');
    const fast = movingAverage(closes, 3);
    const slow = movingAverage(closes, 6);
    const reference = movingAverage(closes, 9);
    const labels = candles.map((candle, index) => {
      const every = Math.max(1, Math.ceil(candles.length / 7));
      return index % every === 0 || index === candles.length - 1 ? `<text x="${x(index)}" y="${height-16}" text-anchor="middle">${esc(candle.axisLabel)}</text>` : '';
    }).join('');

    return `<svg class="sales-market-chart" viewBox="0 0 ${width} ${height}" role="img" aria-label="60-minute sales candlestick chart">
      ${grid}
      ${candlesSvg}
      <path class="market-ma market-ma-fast" d="${linePath(fast)}"></path>
      <path class="market-ma market-ma-slow" d="${linePath(slow)}"></path>
      <path class="market-ma market-ma-reference" d="${linePath(reference)}"></path>
      ${labels}
    </svg>`;
  }

  function groupedBarChart(rows, keys, classes, aria) {
    const width = 980, height = 300, left = 58, right = 16, top = 20, bottom = 50;
    const values = rows.flatMap(r => keys.map(k => num(r, ...k)));
    const max = Math.max(1, ...values);
    const plotW = width-left-right, plotH=height-top-bottom;
    const groupW = plotW / Math.max(1, rows.length), inner = Math.min(groupW*.78, 58), barW = inner / keys.length;
    const grid = Array.from({length:5},(_,i)=>{const v=max-(max/4*i), yy=top+(i/4)*plotH;return `<line x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}" class="grid-line"/><text x="${left-8}" y="${yy+4}" text-anchor="end">${esc(compactNumber(v))}</text>`}).join('');
    const bars = rows.map((r,i)=>{
      const gx=left+i*groupW+(groupW-inner)/2;
      const rects=keys.map((k,j)=>{const v=num(r,...k), h=(v/max)*plotH, x=gx+j*barW;return `<rect class="chart-bar ${classes[j]}" x="${x}" y="${top+plotH-h}" width="${Math.max(3,barW-3)}" height="${h}"><title>${esc(pick(r,'monthLabel','MonthLabel'))} - ${esc(money(v))}</title></rect>`}).join('');
      const label=(i===rows.length-1||i%Math.max(1,Math.ceil(rows.length/7))===0)?`<text x="${left+(i+.5)*groupW}" y="${height-18}" text-anchor="middle">${esc(pick(r,'monthLabel','MonthLabel'))}</text>`:'';
      return rects+label;
    }).join('');
    return `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(aria)}">${grid}${bars}</svg>`;
  }

  function renderDonut(rows) {
    const values=rows.map(r=>num(r,'amount','Amount'));
    const total=values.reduce((a,b)=>a+b,0);
    if(!rows.length||total<=0){$('salesCategoryChart').innerHTML='<div class="dashboard-empty">No categorized sales are available for this period.</div>';return;}
    const colors=['#1570ef','#12b76a','#f79009','#7f56d9','#06aed4','#e54d7b','#667085','#84ad00'];
    let cursor=0;
    const segments=values.map((v,i)=>{const from=cursor,to=cursor+(v/total*100);cursor=to;return `${colors[i%colors.length]} ${from.toFixed(2)}% ${to.toFixed(2)}%`}).join(',');
    const legend=rows.map((r,i)=>`<div><span class="legend-dot" style="--dot:${colors[i%colors.length]}"></span><b>${esc(pick(r,'categoryName','CategoryName')||'Uncategorized')}</b><strong>${pct(num(r,'amount','Amount')/total*100)}</strong></div>`).join('');
    $('salesCategoryChart').innerHTML=`<div class="dashboard-donut" style="background:conic-gradient(${segments})"><div><strong>${esc(compactMoney(total))}</strong><span>Total sales</span></div></div><div class="dashboard-donut-legend">${legend}</div>`;
  }

  function renderRankedBars(targetId, rows, labelKeys, amountKeys, formatter = compactMoney) {
    const target=$(targetId);
    if(!rows.length){target.innerHTML='<div class="dashboard-empty">No data is available for this period.</div>';return;}
    const max=Math.max(1,...rows.map(r=>num(r,...amountKeys)));
    target.innerHTML=rows.map((r,i)=>{
      const amount=num(r,...amountKeys), width=Math.max(3,amount/max*100);
      return `<div class="dashboard-ranked-row"><div class="dashboard-ranked-label"><span>${i+1}</span><b>${esc(pick(r,...labelKeys)||'Unspecified')}</b><strong>${esc(formatter(amount))}</strong></div><div class="dashboard-ranked-track"><i style="width:${width}%"></i></div></div>`;
    }).join('');
  }

  function renderHealth(summary) {
    const sales=num(summary,'totalSales','TotalSales'), gross=num(summary,'grossProfit','GrossProfit'), net=num(summary,'netProfit','NetProfit');
    const cash=num(summary,'netCashFlow','NetCashFlow'), low=num(summary,'lowStockCount','LowStockCount'), active=num(summary,'activeProducts','ActiveProducts');
    const returns=num(summary,'returnsAmount','ReturnsAmount'), receivables=num(summary,'receivables','Receivables');
    const margin=sales?gross/sales*100:0, returnRate=sales?returns/sales*100:0, stockRisk=active?low/active*100:0, receivableRisk=sales?receivables/sales*100:0;
    let score=45+clamp(margin,0,30)*1.1+(net>=0?10:-15)+(cash>=0?8:-10)-clamp(returnRate*2.5,0,14)-clamp(stockRisk*.35,0,12)-clamp(receivableRisk*.08,0,10);
    score=Math.round(clamp(score,0,100));
    const label=score>=80?'Excellent':score>=65?'Healthy':score>=50?'Watch':score>=35?'At Risk':'Critical';
    $('businessHealthGauge').innerHTML=`<div class="health-ring" style="--score:${score}"><div><strong>${score}</strong><span>${label}</span></div></div>`;
    $('businessHealthFacts').innerHTML=`<div><span>Gross margin</span><b>${pct(margin)}</b></div><div><span>Net cash</span><b class="${cash>=0?'positive':'negative'}">${esc(compactMoney(cash))}</b></div><div><span>Return rate</span><b>${pct(returnRate)}</b></div>`;
  }

  function renderProfitability(summary) {
    const sales=num(summary,'totalSales','TotalSales'), gross=num(summary,'grossProfit','GrossProfit'), expense=num(summary,'totalExpenses','TotalExpenses'), net=num(summary,'netProfit','NetProfit');
    const discounts=num(summary,'salesDiscounts','SalesDiscounts'), returns=num(summary,'returnsAmount','ReturnsAmount');
    const metrics=[
      ['Gross margin',sales?gross/sales*100:0,'percent'],
      ['Expense ratio',sales?expense/sales*100:0,'percent'],
      ['Net margin',sales?net/sales*100:0,'percent'],
      ['Discount ratio',sales?discounts/sales*100:0,'percent'],
      ['Return ratio',sales?returns/sales*100:0,'percent']
    ];
    $('profitabilityPanel').innerHTML=metrics.map(([label,value])=>`<div class="profitability-row"><span>${esc(label)}</span><div><i style="width:${clamp(Math.abs(value),0,100)}%"></i></div><strong class="${label==='Net margin'&&value<0?'negative':''}">${pct(value)}</strong></div>`).join('')+`<div class="profitability-highlight"><span>Net profit after expenses</span><strong class="${net>=0?'positive':'negative'}">${esc(compactMoney(net))}</strong></div>`;
  }

  function renderLowStock(rows) {
    if(!rows.length){$('lowStockList').innerHTML='<div class="dashboard-empty success-empty">All active items are above their reorder level.</div>';return;}
    $('lowStockList').innerHTML=rows.map(r=>{
      const stock=num(r,'stockOnHand','StockOnHand'), reorder=num(r,'reorderLevel','ReorderLevel'), critical=stock<=num(r,'minStockLevel','MinStockLevel');
      return `<a class="dashboard-alert-item ${critical?'critical':'warning'}" href="/item-card.html?id=${num(r,'productId','ProductId')}"><span class="alert-symbol">${critical?'!':'Low'}</span><div><b>${esc(pick(r,'productName','ProductName'))}</b><small>${esc(pick(r,'productCode','ProductCode'))} - Reorder ${qty(reorder)}</small></div><strong>${qty(stock)} ${esc(pick(r,'unitOfMeasure','UnitOfMeasure')||'')}</strong></a>`;
    }).join('');
  }

  function renderRecentInvoices(rows) {
    if(!rows.length){$('recentInvoiceBody').innerHTML='<tr><td colspan="5" class="dashboard-empty-cell">No posted sales invoices were found.</td></tr>';return;}
    $('recentInvoiceBody').innerHTML=rows.map(r=>{
      const balance=num(r,'balanceAmount','BalanceAmount'), raw=String(pick(r,'status','Status')||'Posted');
      const status=balance>0?'Pending':raw;
      const cls=/paid|posted/i.test(status)?'paid':/pending|open/i.test(status)?'pending':'neutral';
      return `<tr><td><b>${esc(pick(r,'invoiceNo','InvoiceNo'))}</b></td><td>${esc(pick(r,'customerName','CustomerName')||'Walk-in Customer')}</td><td>${esc(formatDate(pick(r,'invoiceDate','InvoiceDate')))}</td><td class="number">${esc(money(num(r,'grandTotal','GrandTotal')))}</td><td><span class="dashboard-status-pill ${cls}">${esc(status)}</span></td></tr>`;
    }).join('');
  }

  function renderTopProducts(rows) {
    if(!rows.length){$('topProductsList').innerHTML='<div class="dashboard-empty">No product sales were found.</div>';return;}
    const max=Math.max(1,...rows.map(r=>num(r,'amount','Amount')));
    $('topProductsList').innerHTML=rows.map((r,i)=>`<a href="/item-card.html?id=${num(r,'productId','ProductId')}" class="dashboard-product-row"><span class="product-rank">${i+1}</span><div><b>${esc(pick(r,'productName','ProductName'))}</b><small>${qty(num(r,'quantity','Quantity'))} units sold</small><i><em style="width:${num(r,'amount','Amount')/max*100}%"></em></i></div><strong>${esc(compactMoney(num(r,'amount','Amount')))}</strong></a>`).join('');
  }

  function renderTopCustomers(rows) {
    if(!rows.length){$('topCustomersList').innerHTML='<div class="dashboard-empty">No customer sales were found.</div>';return;}
    $('topCustomersList').innerHTML=rows.map((r,i)=>`<div class="dashboard-customer-row"><span>${esc(userInitials(pick(r,'customerName','CustomerName')))}</span><div><b>${esc(pick(r,'customerName','CustomerName'))}</b><small>${esc(pick(r,'customerCode','CustomerCode'))} - ${qty(num(r,'documentCount','DocumentCount'))} documents</small></div><strong>${esc(compactMoney(num(r,'amount','Amount')))}</strong></div>`).join('');
  }

  function userInitials(value){return String(value||'C').trim().split(/\s+/).slice(0,2).map(x=>x[0]).join('').toUpperCase()||'C'}

  function renderMonthly(rows) {
    if(!rows.length){$('monthlyBody').innerHTML='<tr><td colspan="8" class="dashboard-empty-cell">No monthly analytics are available.</td></tr>';return;}
    $('monthlyBody').innerHTML=rows.map(r=>{
      const sales=num(r,'salesAmount','SalesAmount'), gross=num(r,'grossProfitAmount','GrossProfitAmount'), expense=num(r,'expenseAmount','ExpenseAmount'), net=num(r,'netProfitAmount','NetProfitAmount');
      return `<tr><td><b>${esc(pick(r,'monthLabel','MonthLabel'))}</b></td><td class="number">${esc(money(sales))}</td><td class="number">${esc(money(num(r,'purchaseAmount','PurchaseAmount')))}</td><td class="number positive">${esc(money(gross))}</td><td class="number">${esc(money(expense))}</td><td class="number ${net>=0?'positive':'negative'}">${esc(money(net))}</td><td class="number">${pct(sales?gross/sales*100:0)}</td><td class="number">${qty(num(r,'documentCount','DocumentCount'))}</td></tr>`;
    }).join('');
  }

  function miniEmpty(msg){
    return `<div class="dash-mini-empty">${esc(msg)}</div>`;
  }

  function miniGrid(width, height, left, right, top, bottom, max, min=0){
    const range=max-min||1;
    return Array.from({length:4},(_,i)=>{
      const value=max-(range/3)*i;
      const yy=top+((max-value)/range)*(height-top-bottom);
      return `<line x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}" class="mini-grid"/><text x="${left-4}" y="${yy+3}" text-anchor="end" class="mini-axis">${esc(compactNumber(value))}</text>`;
    }).join('');
  }

  function miniMultiLine(rows){
    if(!rows.length) return miniEmpty('No trend data yet.');
    const width=520,height=150,left=36,right=10,top=12,bottom=28;
    const sales=rows.map(r=>num(r,'salesAmount','SalesAmount'));
    const gross=rows.map(r=>num(r,'grossProfitAmount','GrossProfitAmount'));
    const max=Math.max(1,...sales,...gross);
    const x=i=>left+(i/Math.max(1,rows.length-1))*(width-left-right);
    const y=v=>top+((max-v)/max)*(height-top-bottom);
    const path=vals=>vals.map((v,i)=>`${i?'L':'M'}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join(' ');
    const every=Math.max(1,Math.ceil(rows.length/6));
    const labels=rows.map((r,i)=> (i%every===0||i===rows.length-1)?`<text x="${x(i)}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(String(pick(r,'monthLabel','MonthLabel')||'').slice(0,6))}</text>`:'').join('');
    const peaks=sales.map((v,i)=> i%Math.max(1,Math.ceil(rows.length/5))===0?`<text x="${x(i)}" y="${y(v)-4}" text-anchor="middle" class="mini-peak">${esc(compactNumber(v))}</text>`:'').join('');
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${miniGrid(width,height,left,right,top,bottom,max)}
      <path class="mini-line mini-line-a" d="${path(sales)}" fill="none"/>
      <path class="mini-line mini-line-b" d="${path(gross)}" fill="none"/>
      ${peaks}${labels}</svg>`;
  }

  function miniTargetDonut(actual, target, labelActual, labelTarget){
    const goal=Math.max(actual, target, 1);
    const pctDone=clamp((actual/goal)*100,0,100);
    const remain=100-pctDone;
    return `<div class="dash-mini-target">
      <div class="dash-mini-ring" style="background:conic-gradient(#1b4f72 0 ${pctDone}%, #d9e2ec ${pctDone}% 100%)">
        <div><strong>${pctDone.toFixed(1)}%</strong></div>
      </div>
      <div class="dash-mini-target-meta">
        <span>Target: ${esc(compactMoney(target||goal))}</span>
        <span>${esc(labelActual)}: ${esc(compactMoney(actual))}</span>
      </div>
    </div>`;
  }

  function miniAreaScore(rows){
    if(!rows.length) return miniEmpty('No margin trend yet.');
    const width=520,height=150,left=28,right=10,top=18,bottom=28;
    const scores=rows.map(r=>{
      const sales=num(r,'salesAmount','SalesAmount');
      const gross=num(r,'grossProfitAmount','GrossProfitAmount');
      return sales>0?clamp((gross/sales)*10,0,10):0;
    });
    const max=10,min=0;
    const x=i=>left+(i/Math.max(1,rows.length-1))*(width-left-right);
    const y=v=>top+((max-v)/(max-min))*(height-top-bottom);
    const line=scores.map((v,i)=>`${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
    const area=`${left},${height-bottom} ${line} ${width-right},${height-bottom}`;
    const every=Math.max(1,Math.ceil(rows.length/5));
    const labels=rows.map((r,i)=> (i%every===0||i===rows.length-1)?`<text x="${x(i)}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(String(pick(r,'monthLabel','MonthLabel')||'').slice(0,6))}</text>`:'').join('');
    const points=scores.map((v,i)=>`<circle cx="${x(i)}" cy="${y(v)}" r="2.5" class="mini-area-dot"/><text x="${x(i)}" y="${y(v)-6}" text-anchor="middle" class="mini-score-label">${v.toFixed(2)}</text>`).join('');
    const grid=Array.from({length:5},(_,i)=>{
      const value=10-i*2.5; const yy=y(value);
      return `<line x1="${left}" y1="${yy}" x2="${width-right}" y2="${yy}" class="mini-grid"/><text x="${left-4}" y="${yy+3}" text-anchor="end" class="mini-axis">${value}</text>`;
    }).join('');
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${grid}
      <polygon class="mini-area-fill" points="${area}"/>
      <polyline class="mini-area-line" points="${line}"/>
      ${points}${labels}</svg>`;
  }

  function miniWaterfall(rows){
    if(rows.length<2) return miniEmpty('Need more months for variance.');
    const width=520,height=150,left=36,right=14,top=12,bottom=30;
    const changes=[];
    for(let i=1;i<rows.length;i++){
      const cur=num(rows[i],'salesAmount','SalesAmount');
      const prev=num(rows[i-1],'salesAmount','SalesAmount');
      changes.push({label:String(pick(rows[i],'monthLabel','MonthLabel')||'').slice(0,3), delta:cur-prev});
    }
    const slice=changes.slice(-6);
    let running=0;
    const steps=slice.map(s=>{
      const start=running; running+=s.delta;
      return {...s, start, end:running};
    });
    const total=running;
    const values=[0,...steps.flatMap(s=>[s.start,s.end]),total];
    const max=Math.max(1,...values.map(Math.abs),...values);
    const min=Math.min(0,...values);
    const range=max-min||1;
    const y=v=>top+((max-v)/range)*(height-top-bottom);
    const cols=steps.length+1;
    const plotW=width-left-right;
    const colW=plotW/cols;
    const barW=Math.min(22,colW*0.55);
    const bars=steps.map((s,i)=>{
      const cx=left+i*colW+colW/2;
      const topY=y(Math.max(s.start,s.end));
      const botY=y(Math.min(s.start,s.end));
      const cls=s.delta>=0?'mini-wf-up':'mini-wf-down';
      return `<rect class="${cls}" x="${cx-barW/2}" y="${topY}" width="${barW}" height="${Math.max(2,botY-topY)}"/><text x="${cx}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(s.label)}</text>`;
    }).join('');
    const tx=left+(cols-1)*colW+colW/2;
    const totalY=y(Math.max(0,total));
    const totalH=Math.max(2,Math.abs(y(0)-y(total)));
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${miniGrid(width,height,left,right,top,bottom,max,min)}
      ${bars}
      <rect class="mini-wf-total" x="${tx-barW/2}" y="${Math.min(totalY,y(0))}" width="${barW}" height="${totalH}"/>
      <text x="${tx}" y="${height-8}" text-anchor="middle" class="mini-axis">Total</text>
    </svg>`;
  }

  function miniGroupedCategory(categories){
    const rows=(categories||[]).slice(0,8);
    if(!rows.length) return miniEmpty('No category sales yet.');
    const width=520,height=150,left=28,right=8,top=10,bottom=36;
    const data=rows.map(r=>{
      const sales=num(r,'amount','Amount');
      const profit=sales*0.18;
      const cost=Math.max(0,sales-profit);
      return {label:String(pick(r,'categoryName','CategoryName')||'Other').slice(0,8), sales, cost, profit};
    });
    const max=Math.max(1,...data.flatMap(d=>[d.sales,d.cost,d.profit]));
    const groupW=(width-left-right)/data.length;
    const barW=Math.min(8,(groupW-6)/3);
    const y=v=>top+((max-v)/max)*(height-top-bottom);
    const bars=data.map((d,i)=>{
      const gx=left+i*groupW+4;
      const items=[['sales','mini-bar-teal',d.sales],['cost','mini-bar-ink',d.cost],['profit','mini-bar-coral',d.profit]];
      return items.map((it,j)=>{
        const h=Math.max(1,(height-top-bottom)*(it[2]/max));
        return `<rect class="${it[1]}" x="${gx+j*(barW+2)}" y="${height-bottom-h}" width="${barW}" height="${h}"/>`;
      }).join('')+`<text x="${left+(i+.5)*groupW}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(d.label)}</text>`;
    }).join('');
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${miniGrid(width,height,left,right,top,bottom,max)}${bars}</svg>`;
  }

  function miniStackedMonthly(rows){
    if(!rows.length) return miniEmpty('No monthly mix yet.');
    const width=520,height=150,left=32,right=8,top=10,bottom=28;
    const data=rows.map(r=>{
      const sales=num(r,'salesAmount','SalesAmount');
      const gross=Math.max(0,num(r,'grossProfitAmount','GrossProfitAmount'));
      const cost=Math.max(0,sales-gross);
      return {label:String(pick(r,'monthLabel','MonthLabel')||'').slice(0,3), cost, gross, total:sales};
    });
    const max=Math.max(1,...data.map(d=>d.total));
    const groupW=(width-left-right)/data.length;
    const barW=Math.min(14,groupW*0.55);
    const bars=data.map((d,i)=>{
      const cx=left+i*groupW+groupW/2;
      const costH=(d.cost/max)*(height-top-bottom);
      const grossH=(d.gross/max)*(height-top-bottom);
      const base=height-bottom;
      return `<rect class="mini-bar-ink" x="${cx-barW/2}" y="${base-costH}" width="${barW}" height="${Math.max(1,costH)}"/>
        <rect class="mini-bar-teal" x="${cx-barW/2}" y="${base-costH-grossH}" width="${barW}" height="${Math.max(1,grossH)}"/>
        <text x="${cx}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(d.label)}</text>`;
    }).join('');
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${miniGrid(width,height,left,right,top,bottom,max)}${bars}</svg>`;
  }

  function miniProductBars(products){
    const rows=(products||[]).slice(0,6);
    if(!rows.length) return miniEmpty('No product sales yet.');
    const width=520,height=150,left=28,right=8,top=10,bottom=40;
    const data=rows.map(r=>({
      label:String(pick(r,'productName','ProductName','itemName','ItemName')||'Item').slice(0,10),
      value:num(r,'amount','Amount','salesAmount','SalesAmount','qty','Qty')
    }));
    const max=Math.max(1,...data.map(d=>d.value));
    const groupW=(width-left-right)/data.length;
    const barW=Math.min(28,groupW*0.55);
    const bars=data.map((d,i)=>{
      const cx=left+i*groupW+groupW/2;
      const h=(d.value/max)*(height-top-bottom);
      return `<rect class="mini-bar-teal" x="${cx-barW/2}" y="${height-bottom-h}" width="${barW}" height="${Math.max(2,h)}"/>
        <text x="${cx}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(d.label)}</text>`;
    }).join('');
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${miniGrid(width,height,left,right,top,bottom,max)}${bars}</svg>`;
  }

  function miniSingleLine(rows){
    if(!rows.length) return miniEmpty('No sales trend yet.');
    const width=520,height=150,left=36,right=10,top=12,bottom=28;
    const vals=rows.map(r=>num(r,'salesAmount','SalesAmount'));
    const max=Math.max(1,...vals);
    const min=Math.min(...vals,0);
    const range=max-min||1;
    const x=i=>left+(i/Math.max(1,rows.length-1))*(width-left-right);
    const y=v=>top+((max-v)/range)*(height-top-bottom);
    const d=vals.map((v,i)=>`${i?'L':'M'}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join(' ');
    const every=Math.max(1,Math.ceil(rows.length/6));
    const labels=rows.map((r,i)=> (i%every===0||i===rows.length-1)?`<text x="${x(i)}" y="${height-8}" text-anchor="middle" class="mini-axis">${esc(String(pick(r,'monthLabel','MonthLabel')||'').slice(0,6))}</text>`:'').join('');
    return `<svg viewBox="0 0 ${width} ${height}" class="dash-mini-svg">${miniGrid(width,height,left,right,top,bottom,max,min)}
      <path class="mini-area-line" d="${d}" fill="none"/>
      ${labels}</svg>`;
  }

  function renderMiniGallery(summary, monthly, categories, products){
    const sales=num(summary,'totalSales','TotalSales');
    const gross=num(summary,'grossProfit','GrossProfit');
    const prevSales=num(summary,'previousSales','PreviousSales');
    const prevGross=num(summary,'previousGrossProfit','PreviousGrossProfit');
    const salesTarget=Math.max(sales, prevSales*1.12, sales*1.08, 1);
    const grossTarget=Math.max(gross, prevGross*1.12, gross*1.08, 1);

    const set=(id,html)=>{ const el=$(id); if(el) el.innerHTML=html; };
    set('miniRevGpChart', miniMultiLine(monthly));
    set('miniRevenueTarget', miniTargetDonut(sales, salesTarget, 'Revenue', 'Target'));
    set('miniGrossTarget', miniTargetDonut(gross, grossTarget, 'Gross Profit', 'Target'));
    set('miniMarginTrend', miniAreaScore(monthly));
    set('miniVarianceChart', miniWaterfall(monthly));
    set('miniCategoryBars', miniGroupedCategory(categories));
    set('miniStackedSales', miniStackedMonthly(monthly));
    set('miniTopProductsBars', miniProductBars(products));
    set('miniSalesLine', miniSingleLine(monthly));
  }

  function renderAll(data) {
    currentData=data;
    const configured=data.currency||data.Currency||{};
    currency={
      code:String(pick(configured,'currencyCode','CurrencyCode')||'PKR'),
      name:String(pick(configured,'currencyName','CurrencyName')||'Pakistani Rupee'),
      symbol:String(pick(configured,'symbol','Symbol')||pick(configured,'currencyCode','CurrencyCode')||'PKR'),
      decimalPlaces:clamp(Number(pick(configured,'decimalPlaces','DecimalPlaces')??2),0,4)
    };
    $('dashboardCurrencyBadge').textContent=`${currency.code} - ${currency.symbol}`;
    $('dashboardCurrencyNote').textContent=`All financial amounts are shown in ${currency.code} (${currency.name}) from posted ERP transactions.`;
    const summary=data.summary||data.Summary||{};
    const monthly=getRows(data,'monthly','Monthly');
    renderMiniGallery(summary, monthly, getRows(data,'salesByCategory','SalesByCategory'), getRows(data,'topProducts','TopProducts'));
    renderKpis(summary,monthly);
    const hasOperating = hasAnyValue(monthly, [['salesAmount','SalesAmount'], ['purchaseAmount','PurchaseAmount'], ['expenseAmount','ExpenseAmount']]);
    const hasCashFlow = hasAnyValue(monthly, [['cashInAmount','CashInAmount'], ['cashOutAmount','CashOutAmount']]);
    const hasTrend = hasAnyValue(monthly, [['salesAmount','SalesAmount'], ['grossProfitAmount','GrossProfitAmount'], ['netProfitAmount','NetProfitAmount']]);
    $('revenueProfitChart').innerHTML=hasTrend?lineChart(monthly):premiumEmptyChart('Revenue & profit trend', 'Posted monthly sales will appear here.');
    $('operatingChart').innerHTML=hasOperating?groupedBarChart(monthly,[['salesAmount','SalesAmount'],['purchaseAmount','PurchaseAmount'],['expenseAmount','ExpenseAmount']],['sales-bar','purchase-bar','expense-bar'],'Monthly sales, purchases and expenses'):premiumEmptyChart('Operations comparison', 'Sales, purchases and expenses will appear here.');
    $('cashFlowChart').innerHTML=hasCashFlow?groupedBarChart(monthly,[['cashInAmount','CashInAmount'],['cashOutAmount','CashOutAmount']],['cash-in-bar','cash-out-bar'],'Monthly cash inflow and outflow'):premiumEmptyChart('Cash flow', 'Receipts and payments will build this view.');
    renderDonut(getRows(data,'salesByCategory','SalesByCategory'));
    renderRankedBars('paymentMethodsList',getRows(data,'paymentMethods','PaymentMethods'),['paymentMethodName','PaymentMethodName'],['amount','Amount']);
    renderRecentInvoices(getRows(data,'recentInvoices','RecentInvoices'));
    renderTopProducts(getRows(data,'topProducts','TopProducts'));
    renderTopCustomers(getRows(data,'topCustomers','TopCustomers'));
    $('trendPeriod').textContent=`Last ${Number($('months').value||12)} months`;
    $('dashboardUpdatedAt').textContent=`Last updated ${new Date().toLocaleString()}`;
    $('dashboardStatus').hidden=true;
  }

  async function loadDashboard({quiet = false} = {}) {
    if (dashboardLoading) return;
    dashboardLoading = true;
    const button=$('refreshDashboardBtn');
    if (!quiet) {
      button.disabled=true; button.textContent='Refreshing...';
      $('dashboardStatus').hidden=false; $('dashboardStatus').className='dashboard-status'; $('dashboardStatus').textContent='Loading posted sales, purchases, profit, expenses, inventory and cash-flow analytics...';
    }
    try {
      const data=await api.get(`/api/dashboard/analytics?months=${Number($('months').value||12)}`);
      renderAll(data);
    } catch (error) {
      if (!quiet) {
        $('dashboardStatus').hidden=false; $('dashboardStatus').className='dashboard-status error'; $('dashboardStatus').textContent=error.message||'Unable to load dashboard analytics.';
      }
    } finally {
      dashboardLoading = false;
      if (!quiet) {
        button.disabled=false; button.textContent='Refresh';
      }
    }
  }

  function scheduleHourlyRefresh() {
    if (hourlyRefreshTimer) window.clearTimeout(hourlyRefreshTimer);
    const now = new Date();
    const nextHour = new Date(now);
    nextHour.setMinutes(60, 5, 0);
    hourlyRefreshTimer = window.setTimeout(async () => {
      await loadDashboard({quiet:true});
      scheduleHourlyRefresh();
    }, Math.max(1000, nextHour.getTime() - now.getTime()));
  }

  async function init() {
    try {
      const me=await api.get('/api/me');
      $('who').textContent=`${me.displayName||me.DisplayName||'User'} • ${me.storeName||me.StoreName||me.branchName||me.BranchName||'Main Store'} • ${me.roleName||me.RoleName||''}`.replace(/\s+•\s+$/, '');
    } catch {
      location.href='/login.html?returnUrl='+encodeURIComponent(location.pathname+location.search);
      return;
    }
    $('refreshDashboardBtn').addEventListener('click',loadDashboard);
    $('months').addEventListener('change',loadDashboard);
    await loadDashboard();
    scheduleHourlyRefresh();
  }

  init();
})();
