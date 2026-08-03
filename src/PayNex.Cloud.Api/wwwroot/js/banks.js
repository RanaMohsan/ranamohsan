(() => {
  let banks = [];
  const $ = id => document.getElementById(id);

  async function init(){
    try{ await api.get('/api/me'); }catch{ location.href='/login.html'; return; }
    await loadCoa();
    await loadBanks();
  }

  async function loadCoa(){
    try{
      const rows = await api.get('/api/accounting/chart-of-accounts');
      const assets = (rows || []).filter(a => String(a.accountType || a.AccountType || '').toLowerCase() === 'asset');
      $('coaList').innerHTML = assets.map(a => `<option value="${esc(a.accountNo || a.AccountNo)}">${esc(a.accountName || a.AccountName || '')}</option>`).join('');
    }catch{ /* optional */ }
  }

  async function loadBanks(){
    try{
      const r = await api.get('/api/banks');
      banks = r.banks || r.Banks || [];
      render();
      msg('bankMsg', `${banks.length} bank(s).`, true);
    }catch(e){ msg('bankMsg', e.message, false); }
  }

  function render(){
    const body = $('bankRows');
    if(!banks.length){ body.innerHTML = '<tr><td colspan="4" class="muted">No banks yet. Add HBL / Meezan etc. linked to G/L accounts.</td></tr>'; return; }
    body.innerHTML = banks.map(b => {
      const id = b.bankAccountId || b.BankAccountId;
      const active = (b.isActive ?? b.IsActive) !== false;
      return `<tr onclick="openBank(${id})" style="cursor:pointer">
        <td>${esc(b.bankCode || b.BankCode)}</td>
        <td>${esc(b.bankName || b.BankName)}</td>
        <td>${esc(b.accountNo || b.AccountNo)} ${esc(b.accountName || b.AccountName || '')}</td>
        <td>${active ? 'Active' : 'Inactive'}</td>
      </tr>`;
    }).join('');
  }

  function newBank(){
    $('bankAccountId').value = '0';
    $('bankCode').value = '';
    $('bankName').value = '';
    $('accountNo').value = '';
    $('bankActive').checked = true;
  }

  function openBank(id){
    const b = banks.find(x => Number(x.bankAccountId || x.BankAccountId) === Number(id));
    if(!b) return;
    $('bankAccountId').value = String(b.bankAccountId || b.BankAccountId);
    $('bankCode').value = b.bankCode || b.BankCode || '';
    $('bankName').value = b.bankName || b.BankName || '';
    $('accountNo').value = b.accountNo || b.AccountNo || '';
    $('bankActive').checked = (b.isActive ?? b.IsActive) !== false;
  }

  async function saveBank(){
    try{
      const body = {
        bankAccountId: Number($('bankAccountId').value || 0),
        bankCode: $('bankCode').value.trim(),
        bankName: $('bankName').value.trim(),
        accountNo: $('accountNo').value.trim(),
        isActive: $('bankActive').checked
      };
      const r = await api.post('/api/banks', body);
      msg('bankMsg', r.message || 'Saved.', true);
      banks = r.banks || r.Banks || banks;
      render();
      newBank();
    }catch(e){ msg('bankMsg', e.message, false); }
  }

  function esc(s){ return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

  window.loadBanks = loadBanks;
  window.newBank = newBank;
  window.openBank = openBank;
  window.saveBank = saveBank;
  init();
})();
