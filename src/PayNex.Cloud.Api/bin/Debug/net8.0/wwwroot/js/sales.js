(() => {
  const $ = id => document.getElementById(id);
  let invoices = [];
  let filteredInvoices = [];
  let selectedKey = '';

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function dateOnly(value){ return value ? String(value).slice(0, 10) : ''; }
  function field(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function isPos(row){
    return String(field(row, 'documentType', 'DocumentType')).toLowerCase() === 'possale';
  }
  function rowKey(row){
    if(!row) return '';
    if(isPos(row)) return `POS-${Number(field(row, 'posSaleId', 'PosSaleId') || 0)}`;
    return `SI-${Number(field(row, 'salesInvoiceId', 'SalesInvoiceId') || 0)}`;
  }
  function sourceLabel(row){
    const raw = String(field(row, 'applicationSource', 'ApplicationSource') || (isPos(row) ? 'Mobile' : 'Cloud')).trim();
    const key = raw.toLowerCase();
    if(key === 'mobile') return 'Mobile App';
    if(key === 'desktop') return 'Desktop';
    if(key === 'cloud') return 'Cloud';
    return raw || (isPos(row) ? 'Mobile App' : 'Cloud');
  }
  function selectedInvoice(){ return invoices.find(x => rowKey(x) === selectedKey); }
  function layout(){ return encodeURIComponent($('printLayout')?.value || 'bc'); }
  function cardUrl(rowOrId){
    if(rowOrId && typeof rowOrId === 'object'){
      if(isPos(rowOrId)){
        const saleId = Number(field(rowOrId, 'posSaleId', 'PosSaleId') || 0);
        return saleId ? `/sales-invoice-card.html?posSaleId=${encodeURIComponent(saleId)}` : '/sales-invoice-card.html';
      }
      const id = Number(field(rowOrId, 'salesInvoiceId', 'SalesInvoiceId') || 0);
      return id ? `/sales-invoice-card.html?id=${encodeURIComponent(id)}` : '/sales-invoice-card.html';
    }
    return rowOrId ? `/sales-invoice-card.html?id=${encodeURIComponent(rowOrId)}` : '/sales-invoice-card.html';
  }
  function posReportUrl(saleId){
    return `/api/reports/sales-invoice/${encodeURIComponent(saleId)}/html?layout=${layout()}`;
  }
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
    $('deleteInvoiceBtn').addEventListener('click', deleteSelected);
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
      $('invoiceBody').innerHTML = '<tr><td colspan="11" class="bc-empty-row">Loading sales invoices...</td></tr>';
      const query = new URLSearchParams();
      if($('documentStatus').value) query.set('status', $('documentStatus').value);
      if($('fromDate').value) query.set('from', $('fromDate').value);
      if($('toDate').value) query.set('to', $('toDate').value);
      invoices = await api.get('/api/sales-invoices' + (query.toString() ? `?${query}` : '')) || [];
      if(selectedKey && !invoices.some(x => rowKey(x) === selectedKey)) selectedKey = '';
      applyClientFilter();
      msg('status', `${invoices.length} sales invoice(s) loaded.`, true);
    }catch(error){
      invoices = [];
      filteredInvoices = [];
      $('invoiceBody').innerHTML = `<tr><td colspan="11" class="bc-empty-row bad">${esc(error.message)}</td></tr>`;
      updateSummary();
      msg('status', error.message, false);
    }
  }

  function applyClientFilter(){
    const term = $('searchTerm').value.trim().toLowerCase();
    filteredInvoices = !term ? [...invoices] : invoices.filter(x => [
      field(x, 'invoiceNo', 'InvoiceNo'),
      field(x, 'customerCode', 'CustomerCode'),
      field(x, 'customerName', 'CustomerName'),
      field(x, 'status', 'Status'),
      field(x, 'preparedBy', 'PreparedBy'),
      field(x, 'storeName', 'StoreName'),
      sourceLabel(x)
    ].some(v => String(v ?? '').toLowerCase().includes(term)));
    renderInvoices();
  }

  function renderInvoices(){
    const body = $('invoiceBody');
    if(!filteredInvoices.length){
      body.innerHTML = '<tr><td colspan="11" class="bc-empty-row">No sales invoices match the current filter.</td></tr>';
      updateSummary();
      return;
    }

    body.innerHTML = filteredInvoices.map(x => {
      const key = rowKey(x);
      const selected = key === selectedKey;
      const invoiceNo = field(x, 'invoiceNo', 'InvoiceNo');
      const href = cardUrl(x);
      return `<tr class="${selected ? 'selected' : ''}" data-key="${esc(key)}" tabindex="0">
        <td class="bc-select-col"><input type="radio" name="selectedInvoice" aria-label="Select ${esc(invoiceNo)}" ${selected ? 'checked' : ''}></td>
        <td><a class="bc-record-link" href="${esc(href)}" data-row-key="${esc(key)}">${esc(invoiceNo)}</a></td>
        <td>${esc(field(x, 'customerCode', 'CustomerCode'))}</td>
        <td>${esc(field(x, 'customerName', 'CustomerName'))}</td>
        <td>${esc(dateOnly(field(x, 'invoiceDate', 'InvoiceDate')))}</td>
        <td><span class="bc-status-pill ${String(field(x, 'status', 'Status')).toLowerCase() === 'posted' ? 'posted' : ''}">${esc(field(x, 'status', 'Status') || 'Posted')}</span></td>
        <td>${esc(sourceLabel(x))}</td>
        <td class="number">${money(field(x, 'grandTotal', 'GrandTotal'))}</td>
        <td class="number">${money(field(x, 'paidAmount', 'PaidAmount'))}</td>
        <td class="number">${money(field(x, 'balanceAmount', 'BalanceAmount'))}</td>
        <td>${esc(field(x, 'preparedBy', 'PreparedBy'))}</td>
      </tr>`;
    }).join('');

    body.querySelectorAll('tr[data-key]').forEach(row => {
      row.addEventListener('click', event => {
        if(event.target.closest('a')) return;
        selectRow(row.dataset.key);
      });
      row.addEventListener('dblclick', () => {
        selectRow(row.dataset.key);
        openSelected();
      });
      row.addEventListener('keydown', event => {
        if(event.key === 'Enter'){
          selectRow(row.dataset.key);
          openSelected();
        }
      });
    });
    body.querySelectorAll('a[data-row-key]').forEach(link => {
      link.addEventListener('click', () => selectRow(link.dataset.rowKey));
    });
    updateSummary();
  }

  function selectRow(key){
    selectedKey = String(key || '');
    renderInvoices();
  }

  function updateSummary(){
    const selected = selectedInvoice();
    $('recordCount').textContent = `${filteredInvoices.length} record${filteredInvoices.length === 1 ? '' : 's'}`;
    $('selectedCaption').textContent = selected ? `Selected: ${field(selected, 'invoiceNo', 'InvoiceNo')} — ${field(selected, 'customerName', 'CustomerName')}` : 'No invoice selected';
    const total = filteredInvoices.reduce((sum, x) => sum + Number(field(x, 'grandTotal', 'GrandTotal') || 0), 0);
    $('footerSummary').textContent = `Total amount: ${money(total)}`;
    updateDeleteButton();
  }

  function isOpenDraft(row){
    const status = String(field(row, 'status', 'Status') || '').toLowerCase();
    return status === 'open' || status === 'draft';
  }

  function updateDeleteButton(){
    const btn = $('deleteInvoiceBtn');
    if(!btn) return;
    const selected = selectedInvoice();
    const canDelete = !!selected && !isPos(selected) && isOpenDraft(selected);
    btn.disabled = !canDelete;
    btn.hidden = $('documentStatus').value === 'Posted';
  }

  function openSelected(){
    const selected = selectedInvoice();
    if(!selected){ msg('status', 'Select a sales invoice first.', false); return; }
    location.href = cardUrl(selected);
  }

  async function deleteSelected(){
    const selected = selectedInvoice();
    if(!selected){ msg('status', 'Select an Open sales invoice first.', false); return; }
    if(isPos(selected)){ msg('status', 'POS sales cannot be deleted from this list.', false); return; }
    if(!isOpenDraft(selected)){ msg('status', 'Only Open or Draft sales invoices can be deleted.', false); return; }
    const id = Number(field(selected, 'salesInvoiceId', 'SalesInvoiceId') || 0);
    const invoiceNo = field(selected, 'invoiceNo', 'InvoiceNo') || id;
    if(!id){ msg('status', 'Sales invoice id was not found.', false); return; }
    if(!confirm(`Delete open sales invoice ${invoiceNo}? This cannot be undone.`)) return;
    try{
      $('deleteInvoiceBtn').disabled = true;
      const result = await api.delete('/api/sales-invoices/' + id);
      selectedKey = '';
      msg('status', result.message || `Sales invoice ${invoiceNo} deleted.`, true);
      await loadInvoices();
    }catch(error){
      msg('status', error.message, false);
      updateDeleteButton();
    }
  }

  async function printSelected(){
    const selected = selectedInvoice();
    if(!selected){ msg('status', 'Select a sales invoice first.', false); return; }
    try{
      if(isPos(selected)){
        await api.openReport(posReportUrl(field(selected, 'posSaleId', 'PosSaleId')));
        return;
      }
      await api.openReport(`/api/reports/formal-sales-invoice/${field(selected, 'salesInvoiceId', 'SalesInvoiceId')}/html?layout=${layout()}`);
    }catch(error){ msg('status', error.message, false); }
  }

  init();
})();
