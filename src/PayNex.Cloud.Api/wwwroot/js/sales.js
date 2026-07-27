(() => {
  const $ = id => document.getElementById(id);
  let invoices = [];
  let filteredInvoices = [];
  let selectedId = 0;

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function dateOnly(value){ return value ? String(value).slice(0, 10) : ''; }
  function selectedInvoice(){ return invoices.find(x => Number(x.salesInvoiceId) === Number(selectedId)); }
  function layout(){ return encodeURIComponent($('printLayout')?.value || 'bc'); }
  function cardUrl(id){ return id ? `/sales-invoice-card.html?id=${encodeURIComponent(id)}` : '/sales-invoice-card.html'; }
  function requestedStatus(){
    const params = new URLSearchParams(location.search);
    const value = String(params.get('status') || params.get('view') || '').toLowerCase();
    if(value === 'posted') return 'Posted';
    if(value === 'draft') return 'Draft';
    if(value === 'open') return 'Open';
    if(value === 'opendraft' || value === 'draftopen' || value === 'open/draft') return 'OpenDraft';
    return 'OpenDraft';
  }
  function updateView(){
    const status = $('documentStatus').value;
    const title = status === 'Posted' ? 'Posted Sales Invoices' : status === 'Draft' ? 'Draft Sales Invoices' : status === 'Open' ? 'Open Sales Invoices' : 'Sales Invoices';
    document.title = title;
    const heading = document.querySelector('.bc-page-heading h1');
    const caption = document.querySelector('.bc-list-caption strong');
    if(heading) heading.textContent = title;
    if(caption) caption.textContent = `${title} List`;
    const url = new URL(location.href);
    url.searchParams.delete('view');
    url.searchParams.set('status', status || 'OpenDraft');
    history.replaceState(null, '', url.pathname + url.search + url.hash);
  }

  async function init(){
    try{
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName} | ${me.displayName} | ${me.roleName}`;
    }catch{
      location.href = '/login.html';
      return;
    }

    $('documentStatus').value = requestedStatus();
    updateView();
    $('fromDate').value = '';
    $('toDate').value = today();
    bindEvents();
    await loadInvoices();
  }

  function bindEvents(){
    $('newInvoiceBtn').addEventListener('click', () => location.href = cardUrl());
    $('openInvoiceBtn').addEventListener('click', openSelected);
    $('printInvoiceBtn').addEventListener('click', printSelected);
    $('refreshBtn').addEventListener('click', loadInvoices);
    $('applyFilterBtn').addEventListener('click', loadInvoices);
    $('documentStatus').addEventListener('change', () => { updateView(); loadInvoices(); });
    $('clearFilterBtn').addEventListener('click', async () => {
      $('searchTerm').value = '';
      $('fromDate').value = '';
      $('toDate').value = today();
      $('documentStatus').value = 'OpenDraft';
      updateView();
      await loadInvoices();
    });
    $('searchTerm').addEventListener('input', applyClientFilter);
    document.addEventListener('keydown', event => {
      if(event.altKey && event.key.toLowerCase() === 'n') location.href = cardUrl();
      if(event.key === 'Enter' && document.activeElement?.closest?.('#invoiceBody')) openSelected();
    });
  }

  async function loadInvoices(){
    try{
      $('invoiceBody').innerHTML = '<tr><td colspan="10" class="bc-empty-row">Loading sales invoices...</td></tr>';
      const query = new URLSearchParams();
      if($('documentStatus').value) query.set('status', $('documentStatus').value);
      if($('fromDate').value) query.set('from', $('fromDate').value);
      if($('toDate').value) query.set('to', $('toDate').value);
      invoices = await api.get('/api/sales-invoices' + (query.toString() ? `?${query}` : '')) || [];
      if(selectedId && !invoices.some(x => Number(x.salesInvoiceId) === Number(selectedId))) selectedId = 0;
      applyClientFilter();
      msg('status', `${invoices.length} sales invoice(s) loaded.`, true);
    }catch(error){
      invoices = [];
      filteredInvoices = [];
      $('invoiceBody').innerHTML = `<tr><td colspan="10" class="bc-empty-row bad">${esc(error.message)}</td></tr>`;
      updateSummary();
      msg('status', error.message, false);
    }
  }

  function applyClientFilter(){
    const term = $('searchTerm').value.trim().toLowerCase();
    filteredInvoices = !term ? [...invoices] : invoices.filter(x => [
      x.invoiceNo, x.customerCode, x.customerName, x.status, x.preparedBy, x.storeName
    ].some(v => String(v ?? '').toLowerCase().includes(term)));
    renderInvoices();
  }

  function renderInvoices(){
    const body = $('invoiceBody');
    if(!filteredInvoices.length){
      body.innerHTML = '<tr><td colspan="10" class="bc-empty-row">No sales invoices match the current filter.</td></tr>';
      updateSummary();
      return;
    }

    body.innerHTML = filteredInvoices.map(x => {
      const id = Number(x.salesInvoiceId);
      const selected = id === Number(selectedId);
      return `<tr class="${selected ? 'selected' : ''}" data-id="${id}" tabindex="0">
        <td class="bc-select-col"><input type="radio" name="selectedInvoice" aria-label="Select ${esc(x.invoiceNo)}" ${selected ? 'checked' : ''}></td>
        <td><a class="bc-record-link" href="${cardUrl(id)}">${esc(x.invoiceNo)}</a></td>
        <td>${esc(x.customerCode)}</td>
        <td>${esc(x.customerName)}</td>
        <td>${esc(dateOnly(x.invoiceDate))}</td>
        <td><span class="bc-status-pill ${String(x.status).toLowerCase() === 'posted' ? 'posted' : ''}">${esc(x.status || 'Posted')}</span></td>
        <td class="number">${money(x.grandTotal)}</td>
        <td class="number">${money(x.paidAmount)}</td>
        <td class="number">${money(x.balanceAmount)}</td>
        <td>${esc(x.preparedBy)}</td>
      </tr>`;
    }).join('');

    body.querySelectorAll('tr[data-id]').forEach(row => {
      row.addEventListener('click', event => {
        if(event.target.closest('a')) return;
        selectRow(Number(row.dataset.id));
      });
      row.addEventListener('dblclick', () => {
        selectRow(Number(row.dataset.id));
        openSelected();
      });
      row.addEventListener('keydown', event => {
        if(event.key === 'Enter'){
          selectRow(Number(row.dataset.id));
          openSelected();
        }
      });
    });
    updateSummary();
  }

  function selectRow(id){
    selectedId = Number(id || 0);
    renderInvoices();
  }

  function updateSummary(){
    const selected = selectedInvoice();
    $('recordCount').textContent = `${filteredInvoices.length} record${filteredInvoices.length === 1 ? '' : 's'}`;
    $('selectedCaption').textContent = selected ? `Selected: ${selected.invoiceNo} — ${selected.customerName}` : 'No invoice selected';
    const total = filteredInvoices.reduce((sum, x) => sum + Number(x.grandTotal || 0), 0);
    $('footerSummary').textContent = `Total amount: ${money(total)}`;
  }

  function openSelected(){
    const selected = selectedInvoice();
    if(!selected){ msg('status', 'Select a sales invoice first.', false); return; }
    location.href = cardUrl(selected.salesInvoiceId);
  }

  async function printSelected(){
    const selected = selectedInvoice();
    if(!selected){ msg('status', 'Select a sales invoice first.', false); return; }
    try{
      await api.openReport(`/api/reports/formal-sales-invoice/${selected.salesInvoiceId}/html?layout=${layout()}`);
    }catch(error){ msg('status', error.message, false); }
  }

  init();
})();
