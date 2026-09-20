const $ = id => document.getElementById(id);
let currentShiftId = 0;
let currentCard = null;
let activeSource = 'Cloud';

const sourceLabels = {
  Cloud: 'InterNex / Cloud',
  Desktop: 'Desktop Application',
  Mobile: 'Mobile Application'
};

function queryShiftId(){
  const params = new URLSearchParams(location.search);
  return Number(params.get('shiftId') || 0);
}

function val(row, key){
  if(!row) return '';
  return row[key] ?? row[key.charAt(0).toUpperCase() + key.slice(1)] ?? '';
}

function fmtDateTime(x){
  if(!x) return '';
  const d = new Date(x);
  return isNaN(d) ? String(x) : d.toLocaleString();
}

function setBadge(text, cls){
  $('shiftBadge').textContent = text;
  $('shiftBadge').className = 'status-pill ' + cls;
}

function setKpi(id, amount){
  $(id).textContent = money(amount);
}

function highlightSourceButtons(){
  ['Cloud','Desktop','Mobile'].forEach(src => {
    const btn = $('salesTab' + src);
    if(!btn) return;
    btn.classList.toggle('secondary', src !== activeSource);
  });
  if($('sourceCaption'))
    $('sourceCaption').textContent = `Showing ${sourceLabels[activeSource] || activeSource} invoices and returns for this shift.`;
}

async function init(){
  try{
    const me = await api.get('/api/me');
    $('who').textContent = `${me.companyName} | ${me.displayName} | ${me.roleName}`;
  }catch{
    location.href = '/login.html?returnUrl=' + encodeURIComponent(location.pathname + location.search);
    return;
  }
  $('openShiftBtn').onclick = openShift;
  $('closeShiftBtn').onclick = closeShift;
  $('refreshCardBtn').onclick = loadCard;
  $('salesTabCloud').onclick = () => setSource('Cloud');
  $('salesTabDesktop').onclick = () => setSource('Desktop');
  $('salesTabMobile').onclick = () => setSource('Mobile');
  currentShiftId = queryShiftId();
  await loadCard();
}

async function loadCard(){
  try{
    const url = currentShiftId > 0 ? `/api/shifts/card?shiftId=${currentShiftId}` : '/api/shifts/card';
    currentCard = await api.get(url);
    currentShiftId = Number(val(currentCard, 'shiftId') || currentShiftId || 0);
    renderCard(currentCard);
    if(currentShiftId > 0){
      await Promise.all([loadSales(), loadReturns()]);
    }
  }catch(e){
    currentCard = null;
    setBadge('No Open Shift', 'neutral');
    $('shiftTitle').textContent = 'No open shift';
    $('shiftText').textContent = e.message || 'Open a new shift to start.';
    $('shiftTimes').textContent = '';
    $('openShiftBtn').disabled = false;
    $('closeShiftBtn').disabled = true;
    $('salesBody').innerHTML = '<tr><td colspan="5" class="bc-empty-row">Open a shift to view sales.</td></tr>';
    $('returnsBody').innerHTML = '<tr><td colspan="5" class="bc-empty-row">Open a shift to view returns.</td></tr>';
  }
}

function renderCard(s){
  const status = String(val(s, 'status') || '');
  const isOpen = status.toLowerCase() === 'open';
  setBadge(status || 'Unknown', isOpen ? 'ok-pill' : 'neutral');
  $('shiftTitle').textContent = `Shift #${val(s,'shiftId')}`;
  $('shiftText').textContent = `Current shift status: ${status || '-'}`;
  $('shiftTimes').textContent = `Opening time: ${fmtDateTime(val(s,'openedAt'))}${val(s,'closedAt') ? ' | Closing time: ' + fmtDateTime(val(s,'closedAt')) : ' | Closing time: still open'}`;
  setKpi('kOpeningCash', val(s,'openingCash'));
  setKpi('kCurrentCashBalance', val(s,'currentCashBalance') || val(s,'expectedCash'));
  setKpi('kTotalSales', val(s,'totalSales'));
  setKpi('kTotalReturns', val(s,'totalReturns'));
  setKpi('kTotalCollected', val(s,'totalAmountCollected'));
  setKpi('kCashSales', val(s,'cashSales'));
  setKpi('kCreditSales', val(s,'creditSales'));
  setKpi('kBankCardSales', val(s,'bankCardPayments') || val(s,'bankCardSales'));
  setKpi('kClosingCash', val(s,'closingCash'));
  setKpi('kSalesCloud', val(s,'salesCloud'));
  setKpi('kSalesDesktop', val(s,'salesDesktop'));
  setKpi('kSalesMobile', val(s,'salesMobile'));
  setKpi('kReturnsCloud', val(s,'returnsCloud'));
  setKpi('kReturnsDesktop', val(s,'returnsDesktop'));
  setKpi('kReturnsMobile', val(s,'returnsMobile'));
  const expected = Number(val(s,'expectedCash') || 0);
  const closing = Number(val(s,'closingCash') || 0);
  setKpi('kDifference', isOpen ? 0 : (closing - expected));
  $('openShiftBtn').disabled = isOpen;
  $('closeShiftBtn').disabled = !isOpen;
  if(isOpen) $('closingCash').value = Number(expected || 0).toFixed(2);
  highlightSourceButtons();
}

async function openShift(){
  try{
    const r = await api.post('/api/shifts/open', {
      openingCash: Number($('openingCash').value || 0),
      terminalId: Number($('terminalId').value || 1)
    });
    msg('openShiftStatus', r.message || 'Shift opened.', true);
    currentShiftId = Number(r.shiftId || r.ShiftId || 0);
    if(currentShiftId) history.replaceState(null, '', '/shift-card.html?shiftId=' + currentShiftId);
    await loadCard();
  }catch(e){
    msg('openShiftStatus', e.message, false);
  }
}

async function closeShift(){
  try{
    if(!currentShiftId) throw new Error('No open shift to close.');
    const r = await api.post('/api/shifts/close', {
      closingCash: Number($('closingCash').value || 0),
      remarks: $('closingRemarks').value
    });
    msg('closeShiftStatus', `Shift closed. Difference: ${money(r.differenceAmount || r.DifferenceAmount)}`, true);
    await loadCard();
  }catch(e){
    msg('closeShiftStatus', e.message, false);
  }
}

function setSource(source){
  activeSource = source || 'Cloud';
  highlightSourceButtons();
  loadSales();
  loadReturns();
}

async function loadSales(){
  if(!currentShiftId){
    $('salesBody').innerHTML = '<tr><td colspan="5" class="bc-empty-row">No shift selected.</td></tr>';
    return;
  }
  try{
    const rows = await api.get(`/api/shifts/${currentShiftId}/sales?source=${encodeURIComponent(activeSource)}`);
    if(!rows || !rows.length){
      $('salesBody').innerHTML = `<tr><td colspan="5" class="bc-empty-row">No ${sourceLabels[activeSource] || activeSource} sales for this shift.</td></tr>`;
      return;
    }
    $('salesBody').innerHTML = rows.map(r => `<tr>
      <td>${val(r,'invoiceNo')}</td>
      <td>${fmtDateTime(val(r,'saleDate'))}</td>
      <td>${val(r,'applicationSource') || activeSource}</td>
      <td class="number">${money(val(r,'grandTotal'))}</td>
      <td class="number">${money(val(r,'paidAmount'))}</td>
    </tr>`).join('');
  }catch(e){
    $('salesBody').innerHTML = `<tr><td colspan="5" class="bc-empty-row">${e.message}</td></tr>`;
  }
}

async function loadReturns(){
  if(!currentShiftId){
    $('returnsBody').innerHTML = '<tr><td colspan="5" class="bc-empty-row">No shift selected.</td></tr>';
    return;
  }
  try{
    const rows = await api.get(`/api/shifts/${currentShiftId}/returns?source=${encodeURIComponent(activeSource)}`);
    if(!rows || !rows.length){
      $('returnsBody').innerHTML = `<tr><td colspan="5" class="bc-empty-row">No ${sourceLabels[activeSource] || activeSource} returns for this shift.</td></tr>`;
      return;
    }
    $('returnsBody').innerHTML = rows.map(r => `<tr>
      <td>${val(r,'returnNo')}</td>
      <td>${fmtDateTime(val(r,'returnDate'))}</td>
      <td>${val(r,'applicationSource') || activeSource}</td>
      <td class="number">${money(val(r,'refundAmount'))}</td>
      <td>${val(r,'reason') || ''}</td>
    </tr>`).join('');
  }catch(e){
    $('returnsBody').innerHTML = `<tr><td colspan="5" class="bc-empty-row">${e.message}</td></tr>`;
  }
}

init();
