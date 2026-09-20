(() => {
  const $ = id => document.getElementById(id);
  let records = [], filtered = [], selectedId = 0;
  const esc = v => String(v ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  const num = v => Number(v || 0);
  const url = (id, p = false) => `/bank-account-card.html${id ? `?id=${id}${p ? '&print=1' : ''}` : ''}`;
  const glLabel = x => [x.glAccountNo || x.GLAccountNo, x.glAccountName || x.GLAccountName].filter(Boolean).join(' — ');

  async function init() {
    try {
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName} | ${me.displayName} | ${me.roleName}`;
    } catch { return; }
    bind();
    await load();
  }

  function bind() {
    $('newBtn').onclick = () => location.href = url();
    $('openBtn').onclick = open;
    $('refreshBtn').onclick = load;
    $('deleteBtn').onclick = remove;
    $('applyFilterBtn').onclick = apply;
    $('clearFilterBtn').onclick = () => { $('searchTerm').value = ''; $('statusFilter').value = ''; apply(); };
    $('searchTerm').oninput = apply;
  }

  async function load() {
    try {
      records = await api.get('/api/bank-accounts?includeInactive=true') || [];
      if (selectedId && !records.some(x => num(x.bankAccountId) === selectedId)) selectedId = 0;
      apply();
      msg('status', `${records.length} bank account(s) loaded.`, true);
    } catch (e) {
      records = [];
      filtered = [];
      $('recordBody').innerHTML = `<tr><td colspan="8" class="bc-empty-row bad">${esc(e.message)}</td></tr>`;
      summary();
    }
  }

  function apply() {
    const t = $('searchTerm').value.toLowerCase().trim();
    const s = $('statusFilter').value;
    filtered = records.filter(x => {
      const a = x.isActive !== false;
      const hay = [x.bankCode, x.bankName, x.accountNumber, x.currency, x.glAccountNo, x.glAccountName].map(v => String(v ?? '').toLowerCase());
      return (!t || hay.some(v => v.includes(t))) && (!s || (s === 'active') === a);
    });
    render();
  }

  function render() {
    if (!filtered.length) {
      $('recordBody').innerHTML = '<tr><td colspan="8" class="bc-empty-row">No bank accounts match the current filter.</td></tr>';
      summary();
      return;
    }
    $('recordBody').innerHTML = filtered.map(x => {
      const id = num(x.bankAccountId);
      const a = x.isActive !== false;
      const sel = id === selectedId;
      return `<tr data-id="${id}" class="${sel ? 'selected' : ''}" tabindex="0">
        <td class="bc-select-col"><input type="radio" name="selectedRecord" ${sel ? 'checked' : ''}></td>
        <td><a class="bc-record-link" href="${url(id)}">${esc(x.bankCode)}</a></td>
        <td><a class="bc-record-link bc-name-link" href="${url(id)}">${esc(x.bankName)}</a></td>
        <td>${esc(x.accountNumber)}</td>
        <td>${esc(x.currency)}</td>
        <td>${esc(glLabel(x))}</td>
        <td class="number">${money(x.balance)}</td>
        <td><span class="bc-status-pill ${a ? 'active' : 'inactive'}">${a ? 'Active' : 'Inactive'}</span></td>
      </tr>`;
    }).join('');
    $('recordBody').querySelectorAll('tr[data-id]').forEach(r => {
      r.onclick = e => { if (!e.target.closest('a')) select(num(r.dataset.id)); };
      r.ondblclick = () => { select(num(r.dataset.id)); open(); };
      r.onkeydown = e => { if (e.key === 'Enter') { select(num(r.dataset.id)); open(); } };
    });
    summary();
  }

  function select(id) { selectedId = id; render(); }
  function selected() { return records.find(x => num(x.bankAccountId) === selectedId); }

  function summary() {
    const r = selected();
    $('recordCount').textContent = `${filtered.length} record${filtered.length === 1 ? '' : 's'}`;
    $('selectedCaption').textContent = r ? `Selected: ${r.bankCode} — ${r.bankName}` : 'No bank account selected';
    const total = filtered.reduce((s, x) => s + num(x.balance), 0);
    $('footerSummary').textContent = `${filtered.filter(x => x.isActive !== false).length} active bank accounts | Total balance: ${money(total)}`;
  }

  function open() {
    const r = selected();
    if (!r) return msg('status', 'Select a bank account first.', false);
    location.href = url(r.bankAccountId);
  }

  async function remove() {
    const r = selected();
    if (!r) return msg('status', 'Select a bank account first.', false);
    if (!confirm(`Delete bank account ${r.bankCode}?`)) return;
    try {
      const result = await api.delete(`/api/bank-accounts/${selectedId}`);
      selectedId = 0;
      msg('status', result.message || 'Bank account deleted.', true);
      await load();
    } catch (e) {
      msg('status', e.message, false);
    }
  }

  init();
})();
