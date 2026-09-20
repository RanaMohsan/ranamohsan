const val = id => document.getElementById(id);

async function init(){
  try{
    const me = await api.get('/api/me');
    const whoEl = val('who');
    if(whoEl) whoEl.textContent = `${me.companyName || me.CompanyName || 'Company'} | ${me.displayName || me.DisplayName || me.userName || me.UserName || 'User'} | ${me.roleName || me.RoleName || ''}`;
  }catch{ location.href='/login.html'; return; }
  if(val('fromDate')) val('fromDate').value = days(-30);
  if(val('toDate')) val('toDate').value = today();
}

async function runReport(kind){
  try{
    const f = val('fromDate').value, t = val('toDate').value, term = encodeURIComponent(val('accountTerm').value || '');
    const urls = {
      trial:`/api/reports/trial-balance?from=${f}&to=${t}`,
      ledger:`/api/accounting/report/gl-ledger?from=${f}&to=${t}&accountTerm=${term}`,
      pl:`/api/accounting/report/profit-loss?from=${f}&to=${t}`,
      bs:`/api/accounting/report/balance-sheet?asOf=${t}`,
      cash:`/api/accounting/report/cash-bank?from=${f}&to=${t}`,
      custaging:`/api/accounting/report/customer-aging?asOf=${t}`,
      vendaging:`/api/accounting/report/vendor-aging?asOf=${t}`,
      stock:'/api/reports/stock-valuation',
      tax:`/api/reports/tax?from=${f}&to=${t}`,
      profit:`/api/reports/daily-profit?from=${f}&to=${t}`
    };
    const titles = {trial:'Trial Balance',ledger:'G/L by Account',pl:'Profit & Loss',bs:'Balance Sheet',cash:'Cash / Bank Book',custaging:'Customer Aging',vendaging:'Vendor Aging',stock:'Stock Valuation',tax:'Tax Report',profit:'Daily Profit'};
    const rows = await api.get(urls[kind]);
    val('reportTitle').textContent = titles[kind] || 'Report';
    renderTable(rows);
    msg('reportStatus','',true);
  }catch(e){ msg('reportStatus',e.message,false); }
}

function renderTable(rows){
  const head = val('reportHead'), body = val('reportBody');
  if(!rows || !rows.length){ head.innerHTML=''; body.innerHTML='<tr><td class="muted">No data found for selected filter.</td></tr>'; return; }
  const cols = Object.keys(rows[0]).filter(c => c[0] === c[0].toUpperCase());
  const useCols = cols.length ? cols : Object.keys(rows[0]);
  head.innerHTML = '<tr>' + useCols.map(c => `<th>${c}</th>`).join('') + '</tr>';
  body.innerHTML = rows.map(r => '<tr>' + useCols.map(c => `<td>${typeof r[c] === 'number' ? money(r[c]) : (r[c] ?? '')}</td>`).join('') + '</tr>').join('');
}

function printCurrentReport(){
  const title = val('reportTitle').textContent || 'Accounting Report';
  const head = val('reportHead').innerHTML, body = val('reportBody').innerHTML;
  if(!body){ msg('reportStatus','Run a report first.',false); return; }
  const who = (val('who') && val('who').textContent) || '';
  const companyName = who.split('|')[0].trim() || 'InterNex Cloud';
  const period = `${val('fromDate').value} to ${val('toDate').value}`;
  try{
    api.openA4Sheet({
      title,
      docNo: period,
      companyName,
      subtitle: 'Accounting report',
      tableHtml: `<table><thead>${head}</thead><tbody>${body}</tbody></table>`
    });
  }catch(e){ msg('reportStatus', e.message, false); }
}

init();
