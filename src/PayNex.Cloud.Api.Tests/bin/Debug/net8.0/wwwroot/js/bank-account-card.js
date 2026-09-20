(() => {
  const $ = id => document.getElementById(id);
  const q = new URLSearchParams(location.search);
  let id = Number(q.get('id') || 0);
  let record = null;
  let glAccounts = [];
  const num = v => Number(v || 0);
  const v = id => $(id).value;
  const s = (id, x) => { $(id).value = x ?? ''; };
  const esc = x => String(x ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));

  async function init() {
    try {
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName} | ${me.displayName} | ${me.roleName}`;
    } catch { return; }
    bind();
    await loadGl();
    id ? await load() : reset();
    if (q.get('print') === '1') setTimeout(() => window.print(), 400);
  }

  function bind() {
    $('backBtn').onclick = () => location.href = '/bank-accounts.html';
    $('newBtn').onclick = () => location.href = '/bank-account-card.html';
    $('saveBtn').onclick = save;
    $('printBtn').onclick = () => window.print();
    $('deleteBtn').onclick = remove;
    ['bankCode','bankName','currency','bankType','isActive','glAccountId','accountNumber'].forEach(x => $(x).addEventListener('input', facts));
  }

  async function loadGl() {
    glAccounts = await api.get('/api/accounting/chart-of-accounts') || [];
    fillGl();
  }

  function fillGl(selectedId) {
    const current = num(selectedId || v('glAccountId'));
    const options = glAccounts.filter(x => x.accountType === 'Asset' && (x.isActive !== false || num(x.accountId) === current));
    $('glAccountId').innerHTML = '<option value="">Select linked G/L account</option>' + options.map(x =>
      `<option value="${num(x.accountId)}">${esc(x.accountNo)} — ${esc(x.accountName)}</option>`
    ).join('');
    if (current) $('glAccountId').value = String(current);
  }

  function reset() {
    id = 0;
    record = null;
    s('bankAccountId', 0);
    s('bankCode', '');
    s('bankName', '');
    s('accountNumber', '');
    s('iban', '');
    s('branchName', '');
    s('currency', 'PKR');
    s('bankType', 'Bank');
    s('isActive', 'true');
    s('glAccountId', '');
    $('ledgerSection').hidden = true;
    $('deleteBtn').disabled = true;
    facts();
    $('bankCode').focus();
  }

  async function load() {
    try {
      record = await api.get(`/api/bank-accounts/${id}`);
      s('bankAccountId', record.bankAccountId);
      s('bankCode', record.bankCode);
      s('bankName', record.bankName);
      s('accountNumber', record.accountNumber);
      s('iban', record.iban);
      s('branchName', record.branchName);
      s('currency', record.currency || 'PKR');
      s('bankType', record.bankType || 'Bank');
      s('isActive', String(record.isActive !== false));
      fillGl(record.glAccountId);
      s('glAccountId', record.glAccountId);
      $('deleteBtn').disabled = false;
      await loadLedger();
      facts();
      msg('status', 'Bank account card loaded.', true);
    } catch (e) {
      msg('status', e.message, false);
    }
  }

  async function loadLedger() {
    try {
      const rows = await api.get(`/api/bank-accounts/${id}/ledger`) || [];
      $('ledgerSection').hidden = false;
      if (!rows.length) {
        $('ledgerBody').innerHTML = '<tr><td colspan="7" class="bc-empty-row">No bank ledger entries.</td></tr>';
        return;
      }
      $('ledgerBody').innerHTML = rows.map(x => `<tr>
        <td>${esc(String(x.postingDate || '').slice(0, 10))}</td>
        <td>${esc(x.documentType)}</td>
        <td>${esc(x.documentNo)}</td>
        <td class="number">${money(x.debitAmount)}</td>
        <td class="number">${money(x.creditAmount)}</td>
        <td class="number">${money(x.balanceAfter)}</td>
        <td>${esc(x.description)}</td>
      </tr>`).join('');
    } catch {
      $('ledgerSection').hidden = true;
    }
  }

  function facts() {
    const a = v('isActive') === 'true';
    const code = v('bankCode') || 'NEW';
    const name = v('bankName') || 'New Bank Account';
    const gl = $('glAccountId').selectedOptions[0]?.textContent || '—';
    $('cardTitle').textContent = id ? name : 'New Bank Account';
    $('recordNo').textContent = code;
    $('recordStatus').textContent = id ? (a ? 'Active' : 'Inactive') : 'New';
    $('recordStatus').className = `bc-status-pill ${id ? (a ? 'active' : 'inactive') : 'draft'}`;
    $('factCode').textContent = code;
    $('factCurrency').textContent = v('currency') || 'PKR';
    $('factGl').textContent = v('glAccountId') ? gl : '—';
    $('factBalance').textContent = money(record?.balance ?? 0);
    $('factStatus').textContent = id ? (a ? 'Active' : 'Inactive') : 'New';
    $('factType').textContent = v('bankType') || 'Bank';
    $('factAccount').textContent = v('accountNumber') || '—';
    $('factCreated').textContent = record?.createdAt ? new Date(record.createdAt).toLocaleString() : '—';
  }

  function body() {
    return {
      bankAccountId: num(v('bankAccountId')),
      bankCode: v('bankCode').trim(),
      bankName: v('bankName').trim(),
      accountNumber: v('accountNumber').trim(),
      iban: v('iban').trim(),
      branchName: v('branchName').trim(),
      currency: v('currency').trim() || 'PKR',
      bankType: v('bankType'),
      glAccountId: num(v('glAccountId')),
      isActive: v('isActive') === 'true'
    };
  }

  async function save() {
    try {
      const payload = body();
      if (!payload.bankCode) throw new Error('Bank Code is required.');
      if (!payload.bankName) throw new Error('Bank Name is required.');
      if (!payload.glAccountId) throw new Error('Linked G/L Account is required.');
      const r = id
        ? await api.put(`/api/bank-accounts/${id}`, payload)
        : await api.post('/api/bank-accounts', payload);
      id = num(r.bankAccountId || id);
      history.replaceState(null, '', `/bank-account-card.html?id=${id}`);
      await load();
      msg('status', r.message || 'Bank account saved.', true);
    } catch (e) {
      msg('status', e.message, false);
    }
  }

  async function remove() {
    if (!id) return;
    if (!confirm(`Delete bank account ${v('bankCode')}?`)) return;
    try {
      const r = await api.delete(`/api/bank-accounts/${id}`);
      alert(r.message || 'Bank account deleted.');
      location.href = '/bank-accounts.html';
    } catch (e) {
      msg('status', e.message, false);
    }
  }

  init();
})();
