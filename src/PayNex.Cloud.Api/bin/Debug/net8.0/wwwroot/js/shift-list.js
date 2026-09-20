const $ = id => document.getElementById(id);

function val(row, key){
  if(!row) return '';
  return row[key] ?? row[key.charAt(0).toUpperCase() + key.slice(1)] ?? '';
}

function fmtDateOnly(x){
  if(!x) return '';
  const d = new Date(x);
  return isNaN(d) ? String(x).slice(0,10) : d.toLocaleDateString();
}

function fmtTimeOnly(x){
  if(!x) return '';
  const d = new Date(x);
  return isNaN(d) ? String(x) : d.toLocaleTimeString([], {hour:'2-digit', minute:'2-digit', second:'2-digit'});
}

function boolFromSelect(id){
  return String($(id).value || '').toLowerCase() === 'true';
}

async function init(){
  try{
    const me = await api.get('/api/me');
    $('who').textContent = `${me.companyName} | ${me.displayName} | ${me.roleName}`;
  }catch{
    location.href = '/login.html?returnUrl=' + encodeURIComponent('/shift-list.html');
    return;
  }

  $('fromDate').value = days(-30);
  $('toDate').value = today();
  $('newShiftBtn').onclick = () => location.href = '/shift-card.html';
  $('refreshBtn').onclick = loadShifts;
  $('applyFilterBtn').onclick = loadShifts;
  $('clearFilterBtn').onclick = () => {
    $('fromDate').value = days(-30);
    $('toDate').value = today();
    loadShifts();
  };
  $('setupBtn').onclick = toggleSetup;
  $('cancelSettingsBtn').onclick = () => { $('setupPanel').hidden = true; };
  $('saveSettingsBtn').onclick = saveSettings;
  await loadShifts();
}

function toggleSetup(){
  const panel = $('setupPanel');
  panel.hidden = !panel.hidden;
  if(!panel.hidden) loadSettings();
}

async function loadSettings(){
  $('settingsStatus').textContent = 'Loading settings...';
  try{
    const s = await api.get('/api/shifts/settings');
    $('smEnableShiftManagement').value = String(!!(s.enableShiftManagement ?? s.EnableShiftManagement));
    $('smUserWiseShift').value = String(s.userWiseShift ?? s.UserWiseShift ?? true);
    $('settingsStatus').textContent = '';
  }catch(e){
    $('settingsStatus').textContent = e.message || 'Unable to load settings.';
  }
}

async function saveSettings(){
  $('settingsStatus').textContent = 'Saving...';
  try{
    const enabled = boolFromSelect('smEnableShiftManagement');
    const r = await api.put('/api/shifts/settings', {
      enableShiftManagement: enabled,
      enableShift: enabled,
      userWiseShift: boolFromSelect('smUserWiseShift')
    });
    $('settingsStatus').textContent = r.message || 'Shift settings saved.';
    await loadShifts();
  }catch(e){
    $('settingsStatus').textContent = e.message || 'Unable to save settings. Configuration permission may be required.';
  }
}

async function loadShifts(){
  $('status').textContent = 'Loading shifts...';
  const from = $('fromDate').value;
  const to = $('toDate').value;
  try{
    const list = await api.get(`/api/shifts/list?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
    renderRows(Array.isArray(list) ? list : []);
    $('status').textContent = '';
  }catch(e){
    $('shiftBody').innerHTML = `<tr><td colspan="10" class="bc-empty-row">${e.message || 'Unable to load shifts.'}</td></tr>`;
    $('recordCount').textContent = '0 records';
    $('status').textContent = e.message || 'Unable to load shifts.';
  }
}

function renderRows(rows){
  $('recordCount').textContent = `${rows.length} record${rows.length === 1 ? '' : 's'}`;
  $('footerSummary').textContent = `Total sales: ${money(rows.reduce((s, r) => s + Number(val(r,'totalSales') || 0), 0))}`;
  if(!rows.length){
    $('shiftBody').innerHTML = '<tr><td colspan="10" class="bc-empty-row">No shifts found for the selected dates.</td></tr>';
    return;
  }
  $('shiftBody').innerHTML = rows.map(r => {
    const id = val(r, 'shiftId');
    const opened = val(r,'openedAt');
    const closed = val(r,'closedAt');
    return `<tr data-shift-id="${id}" onclick="openShiftCard(${id})">
      <td><b>${id}</b></td>
      <td>${fmtDateOnly(opened)}</td>
      <td>${fmtTimeOnly(opened)}</td>
      <td>${closed ? fmtDateOnly(closed) : ''}</td>
      <td>${closed ? fmtTimeOnly(closed) : ''}</td>
      <td class="number">${money(val(r,'openingCash'))}</td>
      <td class="number">${money(val(r,'closingCash'))}</td>
      <td>${val(r,'status') || ''}</td>
      <td class="number">${money(val(r,'totalSales'))}</td>
      <td class="number">${money(val(r,'totalReturns'))}</td>
    </tr>`;
  }).join('');
}

function openShiftCard(shiftId){
  location.href = '/shift-card.html?shiftId=' + encodeURIComponent(shiftId);
}

init();
