let currentShift = null;
let counters = [];
function setText(id, value){ const el=document.getElementById(id); if(el) el.textContent=value; }
function setBadge(text, cls){ shiftBadge.textContent=text; shiftBadge.className='status-pill ' + cls; }
function resetKpis(){ setText('kOpening', money(0)); setText('kExpected', money(0)); setText('kDifference', money(0)); }
function renderShift(s){
  currentShift = s || null;
  if(!s){
    setBadge('No Open Shift','neutral');
    setText('shiftTitle','No open shift');
    setText('shiftText','Choose a POS counter, enter opening cash, then Open Shift.');
    resetKpis();
    return;
  }
  const counter = s.terminalName || s.TerminalName || s.terminalCode || s.TerminalCode || '';
  setBadge('Open','ok-pill');
  setText('shiftTitle',`Shift #${s.shiftId || s.ShiftId} is open`);
  setText('shiftText',`Counter: ${counter || '—'} | Opened: ${fmtDate(s.openedAt || s.OpenedAt)} | Status: ${s.status || s.Status}`);
  setText('kOpening', money(s.openingCash || s.OpeningCash));
  setText('kExpected', money(s.expectedCash || s.ExpectedCash));
  setText('kDifference', money(s.differenceAmount || s.DifferenceAmount));
  closingCash.value = Number(s.expectedCash || s.ExpectedCash || 0).toFixed(2);
  const tid = Number(s.terminalId || s.TerminalId || 0);
  if(tid && terminalId) terminalId.value = String(tid);
}
async function loadCounters(){
  try{
    const r = await api.get('/api/counters');
    counters = r.counters || r.Counters || [];
    const active = counters.filter(c => (c.isActive ?? c.IsActive) !== false);
    terminalId.innerHTML = active.length
      ? active.map(c => `<option value="${c.terminalId||c.TerminalId}">${esc(c.terminalName||c.TerminalName||c.terminalCode||c.TerminalCode)}</option>`).join('')
      : '<option value="0">Auto Counter</option>';
  }catch{
    terminalId.innerHTML = '<option value="0">Auto Counter</option>';
  }
}
async function init(){
  try{ const me=await api.get('/api/me'); who.textContent=`${me.companyName} | ${me.displayName} | ${me.roleName} | ${me.branchName||me.storeName||''}`; }catch{ location.href='/login.html'; return; }
  await loadCounters();
  await loadCurrent();
  await loadZ();
}
async function loadCurrent(){
  try{ const s = await api.get('/api/shifts/current'); renderShift(s); }
  catch{ renderShift(null); }
}
async function openShift(){
  try{
    const r = await api.post('/api/shifts/open',{openingCash:Number(openingCash.value||0),terminalId:Number(terminalId.value||0)});
    msg('shiftStatus', r.message || 'Shift opened.', true);
    await loadCurrent(); await loadZ();
  }
  catch(e){ msg('shiftStatus', e.message, false); }
}
async function cashDrawer(){
  try{
    if(!currentShift) throw new Error('Open shift first.');
    const r = await api.post('/api/shifts/cash-drawer',{entryType:entryType.value,amount:Number(cashAmount.value||0),remarks:cashRemarks.value});
    msg('cashStatus', r.message || 'Cash entry posted.', true);
    cashAmount.value=''; cashRemarks.value='';
    await loadCurrent(); await loadZ();
  }catch(e){ msg('cashStatus', e.message, false); }
}
async function closeShift(){
  try{
    if(!currentShift) throw new Error('No open shift to close.');
    const r = await api.post('/api/shifts/close',{closingCash:Number(closingCash.value||0),remarks:closingRemarks.value});
    msg('shiftStatus', `Shift closed. Difference: ${money(r.differenceAmount || r.DifferenceAmount)}`, true);
    closingRemarks.value='';
    await loadCurrent(); await loadZ();
  }catch(e){ msg('shiftStatus', e.message, false); }
}
function reportValue(r, key){ return r?.[key] ?? r?.[key.charAt(0).toUpperCase()+key.slice(1)] ?? 0; }
function renderZ(r){
  if(!r || !Object.keys(r).length){ zreport.innerHTML='<p class="muted">No shift report available yet.</p>'; return; }
  const cost = Number(reportValue(r,'totalCost')||0);
  const sales = Number(reportValue(r,'totalSales')||0);
  const tax = Number(reportValue(r,'totalTax')||0);
  const refunds = Number(reportValue(r,'refunds')||0);
  const profit = sales - tax - cost - refunds;
  const rows = [
    ['Shift No', reportValue(r,'shiftId')],
    ['Counter', reportValue(r,'counterName') || ''],
    ['Status', reportValue(r,'status') || ''],
    ['Opening Cash', money(reportValue(r,'openingCash'))],
    ['Cash Sales', money(reportValue(r,'cashSales'))],
    ['Total Sales', money(sales)],
    ['Total Tax', money(tax)],
    ['Total Cost', money(cost)],
    ['Est. Profit', money(profit)],
    ['Invoices', reportValue(r,'invoiceCount')],
    ['Refunds', money(refunds)],
    ['Expected Cash', money(reportValue(r,'expectedCash'))],
    ['Closing Cash', money(reportValue(r,'closingCash'))],
    ['Difference', money(reportValue(r,'differenceAmount'))]
  ];
  zreport.innerHTML = rows.map(x=>`<div><span>${x[0]}</span><b>${x[1]}</b></div>`).join('');
}
async function loadZ(){
  try{ renderZ(await api.get('/api/shifts/z-report')); }
  catch(e){ zreport.innerHTML=`<p class="muted">${e.message || 'No report available.'}</p>`; }
}
function esc(s){ return String(s??'').replace(/[&<>\"]/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[m])); }
init();
