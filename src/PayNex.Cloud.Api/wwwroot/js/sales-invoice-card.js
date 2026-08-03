(() => {
  const $ = id => document.getElementById(id);
  let invoiceId = Number(new URLSearchParams(location.search).get('id') || 0);
  let products = [];
  let customers = [];
  let lines = [];
  let postedHeader = null;
  let isReadOnly = false;
  let isPosting = false;
  let dirtyVersion = 0;
  let savedVersion = 0;
  let saveTimer = 0;
  let saveQueue = Promise.resolve(true);

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function num(value){ return Number(value || 0); }
  function roundMoney(value){ return Math.round((num(value) + Number.EPSILON) * 100) / 100; }
  function dateOnly(value){ return value ? String(value).slice(0, 10) : ''; }
  function layout(){ return encodeURIComponent($('printLayout')?.value || 'bc'); }
  function selectedCustomer(){ return customers.find(x => Number(x.customerId) === Number($('customer').value)); }

  function calculateLine(line){
    const gross = roundMoney(num(line.quantity) * num(line.unitPrice));
    const discountAmount = roundMoney(gross * num(line.discountPercent) / 100);
    const taxable = roundMoney(gross - discountAmount);
    const taxPercent = num(line.taxPercent);
    const taxInclusive = Boolean(line.taxInclusive);
    const taxAmount = taxInclusive
      ? (taxPercent <= 0 ? 0 : roundMoney(taxable - (taxable / (1 + taxPercent / 100))))
      : roundMoney(taxable * taxPercent / 100);
    return { gross, discountAmount, taxAmount, lineTotal: taxInclusive ? taxable : roundMoney(taxable + taxAmount) };
  }

  function calculateTotals(){
    if(postedHeader){
      return {
        subTotal: num(postedHeader.subTotal),
        discountAmount: num(postedHeader.discountAmount),
        taxAmount: num(postedHeader.taxAmount),
        grandTotal: num(postedHeader.grandTotal),
        paidAmount: num(postedHeader.paidAmount),
        balanceAmount: num(postedHeader.balanceAmount)
      };
    }
    const totals = lines.reduce((acc, line) => {
      const calc = calculateLine(line);
      acc.subTotal += calc.gross;
      acc.discountAmount += calc.discountAmount;
      acc.taxAmount += calc.taxAmount;
      acc.grandTotal += calc.lineTotal;
      return acc;
    }, {subTotal:0, discountAmount:0, taxAmount:0, grandTotal:0});
    totals.paidAmount = num($('paidAmount').value);
    totals.balanceAmount = Math.max(0, totals.grandTotal - totals.paidAmount);
    return totals;
  }

  async function init(){
    try{
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName} | ${me.displayName} | ${me.roleName}`;
      $('factPreparedBy').textContent = me.displayName || '—';
      $('factStore').textContent = me.branchName || me.storeName || 'Current branch';
      if(me.postingBlocked || me.PostingBlocked){
        window.__paynexPostingBlocked = true;
        $('postBtn').disabled = true;
        $('postBtn').title = me.licenseMessage || me.LicenseMessage || 'License expired. Draft only.';
      }
    }catch{
      location.href = '/login.html';
      return;
    }
    bindEvents();
    if(invoiceId) await loadInvoice();
    else await prepareNewInvoice();
  }

  function bindEvents(){
    $('backBtn').addEventListener('click', () => navigateWithSave('/sales.html'));
    $('newBtn').addEventListener('click', () => navigateWithSave('/sales-invoice-card.html'));
    $('postBtn').addEventListener('click', postInvoice);
    $('printBtn').addEventListener('click', printInvoice);
    $('addLineBtn').addEventListener('click', addLine);
    $('product').addEventListener('change', fillProduct);
    $('customer').addEventListener('change', () => { fillCustomer(); markDirty(); });
    $('invoiceDate').addEventListener('change', markDirty);
    $('paidAmount').addEventListener('input', () => { renderSummary(); markDirty(); });
    $('remarks').addEventListener('input', markDirty);
    window.addEventListener('beforeunload', event => {
      if(!isReadOnly && dirtyVersion !== savedVersion && (invoiceId > 0 || lines.length > 0)){
        event.preventDefault();
        event.returnValue = '';
      }
    });
  }

  async function loadLookups(){
    const [lookups, productList] = await Promise.all([api.get('/api/lookups'), api.get('/api/products?term=')]);
    customers = lookups.customers || [];
    products = productList || [];
  }

  function populateLookups(){
    $('customer').innerHTML = customers.length ? customers.map(x => `<option value="${x.customerId}">${esc(x.customerCode)} - ${esc(x.customerName)}</option>`).join('') : '<option value="">No customers available</option>';
    $('product').innerHTML = products.length ? products.map(x => `<option value="${x.productId}">${esc(x.productCode)} - ${esc(x.productName)}</option>`).join('') : '<option value="">No items available</option>';
  }

  async function prepareNewInvoice(){
    isReadOnly = false;
    postedHeader = null;
    $('invoiceDate').value = today();
    $('documentNo').textContent = 'NEW';
    $('documentTitle').textContent = 'New Sales Invoice';
    setOpenStatus('New');
    $('printBtn').disabled = true;
    try{
      await loadLookups();
      populateLookups();
      fillCustomer();
      fillProduct();
      renderLines();
      dirtyVersion = 0;
      savedVersion = 0;
      msg('status', 'New sales invoice is ready. It will save automatically after a line is added.', true);
    }catch(error){ msg('status', error.message, false); }
  }

  async function loadInvoice(){
    try{
      const [header, savedLines] = await Promise.all([
        api.get(`/api/sales-invoices/${invoiceId}`),
        api.get(`/api/sales-invoices/${invoiceId}/lines`),
        loadLookups()
      ]);
      const isPosted = String(header.status || '').toLowerCase() === 'posted';
      postedHeader = isPosted ? header : null;
      isReadOnly = isPosted;
      if(!customers.some(x => Number(x.customerId) === Number(header.customerId))){
        customers.unshift({customerId:header.customerId,customerCode:header.customerCode,customerName:header.customerName,mobile:header.mobile,email:header.email,addressLine:header.addressLine});
      }
      populateLookups();
      lines = (savedLines || []).map(line => {
        const product = products.find(x => Number(x.productId) === Number(line.productId));
        return {...line,productCode:line.productCode || product?.productCode || '',productName:line.productName || product?.productName || '',stockOnHand:line.stockOnHand ?? product?.stockOnHand,taxInclusive:line.taxInclusive ?? product?.taxInclusive ?? false};
      });
      $('customer').value = String(header.customerId);
      $('invoiceDate').value = dateOnly(header.invoiceDate);
      $('paidAmount').value = num(header.paidAmount).toFixed(2);
      $('remarks').value = header.remarks || '';
      $('documentNo').textContent = header.invoiceNo || `#${invoiceId}`;
      $('documentTitle').textContent = `${isPosted ? 'Sales Invoice' : 'Open Sales Invoice'} ${header.invoiceNo || ''}`.trim();
      $('factCustomerNo').textContent = header.customerCode || '—';
      $('factStore').textContent = [header.storeCode, header.storeName].filter(Boolean).join(' - ') || header.branchCode || '—';
      $('factPreparedBy').textContent = header.preparedBy || '—';
      $('factPostedAt').textContent = isPosted && header.postedAt ? new Date(header.postedAt).toLocaleString() : '—';
      fillCustomer();
      fillProduct();
      if(isPosted){
        $('documentStatus').textContent = 'Posted';
        $('documentStatus').className = 'bc-status-pill posted';
        $('factStatus').textContent = 'Posted';
        setReadOnlyMode();
      }else{
        setOpenStatus('Open');
        $('printBtn').disabled = true;
      }
      dirtyVersion = 0;
      savedVersion = 0;
      renderLines();
      msg('status', `Sales invoice ${header.invoiceNo} opened.`, true);
    }catch(error){
      msg('status', error.message, false);
      $('documentTitle').textContent = 'Sales Invoice Not Found';
      setReadOnlyMode();
    }
  }

  function setOpenStatus(label = 'Open'){
    $('documentStatus').textContent = label;
    $('documentStatus').className = 'bc-status-pill draft';
    $('factStatus').textContent = label === 'New' ? 'New' : 'Open';
  }

  function setReadOnlyMode(){
    isReadOnly = true;
    ['customer','invoiceDate','paidAmount','remarks'].forEach(id => $(id).disabled = true);
    $('lineEntrySection').classList.add('read-only');
    $('lineEntrySection').querySelectorAll('input,select,button').forEach(el => el.disabled = true);
    $('postBtn').hidden = true;
    $('printBtn').disabled = false;
  }

  function fillCustomer(){
    const customer = selectedCustomer();
    if(!customer){ $('customerInformation').value = ''; $('factCustomerNo').textContent = '—'; return; }
    $('customerInformation').value = [customer.mobile, customer.email, customer.addressLine].filter(Boolean).join(' | ');
    $('factCustomerNo').textContent = customer.customerCode || '—';
  }

  function fillProduct(){
    const product = products.find(x => Number(x.productId) === Number($('product').value));
    if(!product) return;
    $('unitPrice').value = num(product.salePrice).toFixed(2);
    $('discountPercent').value = product.discountAllowed ? num(product.productDiscountPercent).toFixed(2) : '0.00';
    $('taxPercent').value = num(product.taxPercent).toFixed(2);
  }

  function addLine(){
    if(isReadOnly) return;
    const product = products.find(x => Number(x.productId) === Number($('product').value));
    if(!product){ msg('status', 'Select an item first.', false); return; }
    const quantity = num($('qty').value);
    if(quantity <= 0){ msg('status', 'Quantity must be greater than zero.', false); return; }
    lines.push({productId:product.productId,productCode:product.productCode,productName:product.productName,stockOnHand:num(product.stockOnHand),quantity,unitPrice:num($('unitPrice').value),discountPercent:product.discountAllowed ? num($('discountPercent').value) : 0,taxPercent:num($('taxPercent').value),taxInclusive:Boolean(product.taxInclusive)});
    $('qty').value = '1';
    renderLines();
    markDirty();
    msg('status', `${product.productName} added. Draft auto-save is running.`, true);
  }

  function renderLines(){
    const body = $('linesBody');
    if(!lines.length){ body.innerHTML = '<tr><td colspan="10" class="bc-empty-row">No invoice lines.</td></tr>'; renderSummary(); return; }
    body.innerHTML = lines.map((line, index) => {
      const calc = postedHeader ? {lineTotal:num(line.lineTotal)} : calculateLine(line);
      const available = line.stockOnHand == null ? '—' : num(line.stockOnHand).toLocaleString();
      return `<tr><td>Item</td><td>${esc(line.productCode || '')}</td><td>${esc(line.productName)}</td><td class="number">${available}</td><td class="number">${num(line.quantity).toLocaleString()}</td><td class="number">${money(line.unitPrice)}</td><td class="number">${num(line.discountPercent).toFixed(2)}</td><td class="number">${num(line.taxPercent).toFixed(2)}</td><td class="number">${money(calc.lineTotal)}</td><td class="bc-row-action">${isReadOnly ? '' : `<button type="button" class="bc-icon-button danger" data-remove="${index}" title="Remove line">×</button>`}</td></tr>`;
    }).join('');
    body.querySelectorAll('[data-remove]').forEach(button => button.addEventListener('click', () => { lines.splice(Number(button.dataset.remove), 1); renderLines(); markDirty(); }));
    renderSummary();
  }

  function renderSummary(){
    const totals = calculateTotals();
    $('summarySubtotal').textContent = money(totals.subTotal);
    $('summaryDiscount').textContent = money(totals.discountAmount);
    $('summaryTax').textContent = money(totals.taxAmount);
    $('summaryTotal').textContent = money(totals.grandTotal);
    $('summaryPaid').textContent = money(totals.paidAmount);
    $('summaryBalance').textContent = money(totals.balanceAmount);
  }

  function markDirty(){
    if(isReadOnly || isPosting) return;
    dirtyVersion += 1;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => saveDraft(false), 800);
  }

  function draftPayload(){
    return {salesInvoiceId:invoiceId,customerId:Number($('customer').value || 0),invoiceDate:$('invoiceDate').value,paidAmount:num($('paidAmount').value),remarks:$('remarks').value,lines:lines.map(line => ({productId:line.productId,quantity:num(line.quantity),unitPrice:num(line.unitPrice),discountPercent:num(line.discountPercent),taxPercent:num(line.taxPercent)}))};
  }

  function saveDraft(showMessage){
    clearTimeout(saveTimer);
    if(isReadOnly || isPosting) return Promise.resolve(true);
    if(!invoiceId && !lines.length) return Promise.resolve(true);
    if(!$('customer').value) return Promise.resolve(false);
    if(dirtyVersion === savedVersion && invoiceId > 0) return Promise.resolve(true);
    saveQueue = saveQueue.catch(() => true).then(async () => {
      const versionBeingSaved = dirtyVersion;
      setOpenStatus('Saving…');
      try{
        const result = await api.post('/api/sales-invoices/draft', draftPayload());
        invoiceId = Number(result.salesInvoiceId || invoiceId || 0);
        if(invoiceId){ history.replaceState(null, '', `/sales-invoice-card.html?id=${encodeURIComponent(invoiceId)}`); $('documentNo').textContent = result.invoiceNo || $('documentNo').textContent; $('documentTitle').textContent = `Open Sales Invoice ${result.invoiceNo || ''}`.trim(); }
        if(dirtyVersion === versionBeingSaved) savedVersion = versionBeingSaved;
        setOpenStatus('Open');
        if(showMessage) msg('status', `Sales invoice ${result.invoiceNo || ''} saved as Open.`, true);
        return true;
      }catch(error){ setOpenStatus(invoiceId ? 'Open' : 'New'); msg('status', `Auto-save failed: ${error.message}`, false); return false; }
    });
    return saveQueue;
  }

  async function navigateWithSave(url){ const saved = await saveDraft(true); if(saved) location.href = url; }

  async function postInvoice(){
    if(isReadOnly || isPosting) return;
    if(!$('customer').value){ msg('status', 'Customer is required.', false); return; }
    if(!lines.length){ msg('status', 'Add at least one sales invoice line.', false); return; }
    clearTimeout(saveTimer);
    await saveQueue.catch(() => false);
    isPosting = true;
    $('postBtn').disabled = true;
    try{
      const result = await api.post('/api/sales-invoices/post', draftPayload());
      const postedId = Number(result.salesInvoiceId || 0);
      if(!postedId) throw new Error('Invoice was posted but its record ID was not returned.');
      savedVersion = dirtyVersion;
      location.href = `/sales-invoice-card.html?id=${postedId}&posted=1`;
    }catch(error){ isPosting = false; $('postBtn').disabled = false; msg('status', error.message, false); }
  }

  async function printInvoice(){
    if(!invoiceId || !isReadOnly){ msg('status', 'Post the sales invoice before printing.', false); return; }
    try{ await api.openReport(`/api/reports/formal-sales-invoice/${invoiceId}/html?layout=${layout()}`); }catch(error){ msg('status', error.message, false); }
  }

  init();
})();
