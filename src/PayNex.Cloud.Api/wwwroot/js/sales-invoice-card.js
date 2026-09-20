(() => {
  const $ = id => document.getElementById(id);
  const params = new URLSearchParams(location.search);
  const posSaleId = Number(params.get('posSaleId') || 0);
  const typeHint = String(params.get('type') || params.get('documentType') || '').toLowerCase();
  const isPosDocument = posSaleId > 0 || typeHint === 'pos' || typeHint === 'possale';
  let invoiceId = posSaleId > 0 ? posSaleId : Number(params.get('id') || 0);
  let products = [];
  let customers = [];
  let banks = [];
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
  function field(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function roundMoney(value){ return Math.round((num(value) + Number.EPSILON) * 100) / 100; }
  function dateOnly(value){
    if(!value) return '';
    const raw = String(value);
    if(/^\d{4}-\d{2}-\d{2}/.test(raw)) return raw.slice(0, 10);
    const parsed = new Date(raw);
    if(Number.isNaN(parsed.getTime())) return '';
    const month = String(parsed.getMonth() + 1).padStart(2, '0');
    const day = String(parsed.getDate()).padStart(2, '0');
    return `${parsed.getFullYear()}-${month}-${day}`;
  }
  function layout(){ return encodeURIComponent($('printLayout')?.value || 'bc'); }
  function documentQuery(){ return isPosDocument ? '?documentType=PosSale' : ''; }
  function selectedCustomer(){
    return customers.find(x => Number(field(x, 'customerId', 'CustomerId')) === Number($('customer').value));
  }
  function listUrl(){ return isPosDocument ? '/sales.html?status=Posted' : '/sales.html'; }

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
    }catch{
      location.href = '/login.html';
      return;
    }
    bindEvents();
    if(invoiceId) await loadInvoice();
    else if(isPosDocument){
      $('documentTitle').textContent = 'POS Sale Not Found';
      msg('status', 'This POS sale could not be opened because its ID is missing.', false);
      setReadOnlyMode();
    }
    else await prepareNewInvoice();
  }

  function bindEvents(){
    $('backBtn').addEventListener('click', () => navigateWithSave(listUrl()));
    $('newBtn').addEventListener('click', () => navigateWithSave('/sales-invoice-card.html'));
    $('postBtn').addEventListener('click', postInvoice);
    $('deleteBtn').addEventListener('click', deleteInvoice);
    $('printBtn').addEventListener('click', printInvoice);
    $('addLineBtn').addEventListener('click', addLine);
    $('product').addEventListener('change', fillProduct);
    $('customer').addEventListener('change', () => { fillCustomer(); markDirty(); });
    $('invoiceDate').addEventListener('change', markDirty);
    $('paidAmount').addEventListener('input', () => { renderSummary(); toggleReceiveNow(); markDirty(); });
    $('receivePaymentMethod').addEventListener('change', toggleReceiveNow);
    $('remarks').addEventListener('input', markDirty);
    window.addEventListener('beforeunload', event => {
      if(!isReadOnly && dirtyVersion !== savedVersion && (invoiceId > 0 || lines.length > 0)){
        event.preventDefault();
        event.returnValue = '';
      }
    });
  }

  async function loadLookups(){
    const [lookups, productList, bankList] = await Promise.all([
      api.get('/api/lookups').catch(() => ({})),
      api.get('/api/products?term=').catch(() => []),
      api.get('/api/bank-accounts').catch(() => [])
    ]);
    customers = lookups.customers || lookups.Customers || [];
    products = productList || [];
    banks = bankList || [];
  }

  function populateLookups(){
    $('customer').innerHTML = '<option value="">Select customer</option>' + (customers.length ? customers.map(x => `<option value="${field(x, 'customerId', 'CustomerId')}">${esc(field(x, 'customerCode', 'CustomerCode'))} - ${esc(field(x, 'customerName', 'CustomerName'))}</option>`).join('') : '');
    $('product').innerHTML = '<option value="">Select item</option>' + (products.length ? products.map(x => `<option value="${field(x, 'productId', 'ProductId')}">${esc(field(x, 'productCode', 'ProductCode'))} - ${esc(field(x, 'productName', 'ProductName'))}</option>`).join('') : '');
    $('receiveBankAccount').innerHTML = (banks.length ? banks.map(x => `<option value="${field(x, 'bankAccountId', 'BankAccountId')}">${esc(field(x, 'bankCode', 'BankCode'))} - ${esc(field(x, 'bankName', 'BankName'))}</option>`).join('') : '<option value="">No active bank accounts</option>');
    toggleReceiveNow();
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
      $('customer').value = '';
      $('product').value = '';
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
      const query = documentQuery();
      const [header, savedLines] = await Promise.all([
        api.get(`/api/sales-invoices/${invoiceId}${query}`),
        api.get(`/api/sales-invoices/${invoiceId}/lines${query}`)
      ]);
      try{ await loadLookups(); }catch{ customers = []; products = []; banks = []; }
      const customerId = Number(field(header, 'customerId', 'CustomerId') || 0);
      const invoiceNo = field(header, 'invoiceNo', 'InvoiceNo');
      const status = String(field(header, 'status', 'Status') || '').toLowerCase();
      const isPosted = isPosDocument || status === 'posted';
      postedHeader = isPosted ? {
        subTotal: num(field(header, 'subTotal', 'SubTotal')),
        discountAmount: num(field(header, 'discountAmount', 'DiscountAmount')),
        taxAmount: num(field(header, 'taxAmount', 'TaxAmount')),
        grandTotal: num(field(header, 'grandTotal', 'GrandTotal')),
        paidAmount: num(field(header, 'paidAmount', 'PaidAmount')),
        balanceAmount: num(field(header, 'balanceAmount', 'BalanceAmount'))
      } : null;
      isReadOnly = isPosted;
      if(!customers.some(x => Number(field(x, 'customerId', 'CustomerId')) === customerId)){
        customers.unshift({
          customerId,
          customerCode: field(header, 'customerCode', 'CustomerCode'),
          customerName: field(header, 'customerName', 'CustomerName'),
          mobile: field(header, 'mobile', 'Mobile'),
          email: field(header, 'email', 'Email'),
          addressLine: field(header, 'addressLine', 'AddressLine')
        });
      }
      populateLookups();
      lines = (savedLines || []).map(line => {
        const productId = Number(field(line, 'productId', 'ProductId') || 0);
        const product = products.find(x => Number(field(x, 'productId', 'ProductId')) === productId);
        return {
          productId,
          productCode: field(line, 'productCode', 'ProductCode') || field(product, 'productCode', 'ProductCode') || '',
          productName: field(line, 'productName', 'ProductName') || field(product, 'productName', 'ProductName') || '',
          stockOnHand: line.stockOnHand ?? line.StockOnHand ?? product?.stockOnHand ?? product?.StockOnHand,
          quantity: num(field(line, 'quantity', 'Quantity')),
          unitPrice: num(field(line, 'unitPrice', 'UnitPrice')),
          discountPercent: num(field(line, 'discountPercent', 'DiscountPercent')),
          taxPercent: num(field(line, 'taxPercent', 'TaxPercent')),
          lineTotal: num(field(line, 'lineTotal', 'LineTotal')),
          taxInclusive: Boolean(line.taxInclusive ?? line.TaxInclusive ?? product?.taxInclusive ?? product?.TaxInclusive)
        };
      });
      $('customer').value = customerId ? String(customerId) : '';
      $('product').value = '';
      $('invoiceDate').value = dateOnly(field(header, 'invoiceDate', 'InvoiceDate'));
      $('paidAmount').value = num(field(header, 'paidAmount', 'PaidAmount')).toFixed(2);
      $('remarks').value = field(header, 'remarks', 'Remarks') || '';
      $('documentNo').textContent = invoiceNo || `#${invoiceId}`;
      const kind = isPosDocument ? 'POS Sale' : (isPosted ? 'Sales Invoice' : 'Open Sales Invoice');
      $('documentTitle').textContent = `${kind} ${invoiceNo || ''}`.trim();
      $('factCustomerNo').textContent = field(header, 'customerCode', 'CustomerCode') || '—';
      $('factStore').textContent = [field(header, 'storeCode', 'StoreCode'), field(header, 'storeName', 'StoreName')].filter(Boolean).join(' - ') || field(header, 'branchCode', 'BranchCode') || '—';
      $('factPreparedBy').textContent = field(header, 'preparedBy', 'PreparedBy') || '—';
      const postedAt = field(header, 'postedAt', 'PostedAt');
      $('factPostedAt').textContent = isPosted && postedAt ? new Date(postedAt).toLocaleString() : '—';
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
      msg('status', `${isPosDocument ? 'POS sale' : 'Sales invoice'} ${invoiceNo} opened.`, true);
    }catch(error){
      msg('status', error.message, false);
      const missing = /not found/i.test(error.message || '');
      $('documentTitle').textContent = missing
        ? (isPosDocument ? 'POS Sale Not Found' : 'Sales Invoice Not Found')
        : (isPosDocument ? 'POS Sale' : 'Sales Invoice');
      setReadOnlyMode();
    }
  }

  function setOpenStatus(label = 'Open'){
    $('documentStatus').textContent = label;
    $('documentStatus').className = 'bc-status-pill draft';
    $('factStatus').textContent = label === 'New' ? 'New' : 'Open';
    if($('deleteBtn')) $('deleteBtn').hidden = !invoiceId || isPosDocument || isReadOnly;
  }

  function setReadOnlyMode(){
    isReadOnly = true;
    ['customer','invoiceDate','paidAmount','remarks','receivePaymentMethod','receiveBankAccount'].forEach(id => $(id).disabled = true);
    $('lineEntrySection').classList.add('read-only');
    $('lineEntrySection').querySelectorAll('input,select,button').forEach(el => el.disabled = true);
    $('postBtn').hidden = true;
    if($('deleteBtn')) $('deleteBtn').hidden = true;
    $('printBtn').disabled = false;
  }

  function fillCustomer(){
    const customer = selectedCustomer();
    if(!customer){ $('customerInformation').value = ''; $('factCustomerNo').textContent = '—'; return; }
    $('customerInformation').value = [field(customer, 'mobile', 'Mobile'), field(customer, 'email', 'Email'), field(customer, 'addressLine', 'AddressLine')].filter(Boolean).join(' | ');
    $('factCustomerNo').textContent = field(customer, 'customerCode', 'CustomerCode') || '—';
  }

  function fillProduct(){
    const product = products.find(x => Number(field(x, 'productId', 'ProductId')) === Number($('product').value));
    if(!product){
      $('unitPrice').value = '';
      $('discountPercent').value = '';
      $('taxPercent').value = '';
      return;
    }
    $('unitPrice').value = num(field(product, 'salePrice', 'SalePrice')).toFixed(2);
    $('discountPercent').value = (product.discountAllowed ?? product.DiscountAllowed) ? num(field(product, 'productDiscountPercent', 'ProductDiscountPercent')).toFixed(2) : '0.00';
    $('taxPercent').value = num(field(product, 'taxPercent', 'TaxPercent')).toFixed(2);
  }

  function addLine(){
    if(isReadOnly) return;
    const product = products.find(x => Number(field(x, 'productId', 'ProductId')) === Number($('product').value));
    if(!product){ msg('status', 'Select an item first.', false); return; }
    const quantity = num($('qty').value);
    if(quantity <= 0){ msg('status', 'Quantity must be greater than zero.', false); return; }
    lines.push({
      productId: Number(field(product, 'productId', 'ProductId')),
      productCode: field(product, 'productCode', 'ProductCode'),
      productName: field(product, 'productName', 'ProductName'),
      stockOnHand: num(field(product, 'stockOnHand', 'StockOnHand')),
      quantity,
      unitPrice: num($('unitPrice').value),
      discountPercent: (product.discountAllowed ?? product.DiscountAllowed) ? num($('discountPercent').value) : 0,
      taxPercent: num($('taxPercent').value),
      taxInclusive: Boolean(product.taxInclusive ?? product.TaxInclusive)
    });
    $('qty').value = '1';
    renderLines();
    markDirty();
    msg('status', `${field(product, 'productName', 'ProductName')} added. Draft auto-save is running.`, true);
  }

  function renderLines(){
    const body = $('linesBody');
    if(!lines.length){ body.innerHTML = '<tr><td colspan="10" class="bc-empty-row">No invoice lines.</td></tr>'; renderSummary(); return; }
    body.innerHTML = lines.map((line, index) => {
      const calc = postedHeader ? {lineTotal:num(line.lineTotal)} : calculateLine(line);
      const available = line.stockOnHand == null ? '—' : num(line.stockOnHand).toLocaleString();
      const qtyCell = isReadOnly
        ? num(line.quantity).toLocaleString()
        : `<input class="bc-inline-edit" data-qty="${index}" type="number" min="0.001" step="any" value="${num(line.quantity)}" title="Edit quantity">`;
      const priceCell = isReadOnly
        ? money(line.unitPrice)
        : `<input class="bc-inline-edit" data-price="${index}" type="number" min="0" step="0.01" value="${num(line.unitPrice).toFixed(2)}" title="Edit unit price">`;
      return `<tr><td>Item</td><td>${esc(line.productCode || '')}</td><td>${esc(line.productName)}</td><td class="number">${available}</td><td class="number">${qtyCell}</td><td class="number">${priceCell}</td><td class="number">${num(line.discountPercent).toFixed(2)}</td><td class="number">${num(line.taxPercent).toFixed(2)}</td><td class="number" data-line-total="${index}">${money(calc.lineTotal)}</td><td class="bc-row-action">${isReadOnly ? '' : `<button type="button" class="bc-icon-button danger" data-remove="${index}" title="Remove line">×</button>`}</td></tr>`;
    }).join('');
    body.querySelectorAll('[data-remove]').forEach(button => button.addEventListener('click', () => { lines.splice(Number(button.dataset.remove), 1); renderLines(); markDirty(); }));
    body.querySelectorAll('[data-qty]').forEach(input => {
      input.addEventListener('input', () => applyLineEdit(Number(input.dataset.qty), 'quantity', input.value));
      input.addEventListener('change', () => {
        const i = Number(input.dataset.qty);
        if(num(input.value) <= 0){ input.value = String(lines[i].quantity || 1); applyLineEdit(i, 'quantity', input.value); }
      });
    });
    body.querySelectorAll('[data-price]').forEach(input => {
      input.addEventListener('input', () => applyLineEdit(Number(input.dataset.price), 'unitPrice', input.value));
      input.addEventListener('change', () => {
        const i = Number(input.dataset.price);
        input.value = num(input.value).toFixed(2);
        applyLineEdit(i, 'unitPrice', input.value);
      });
    });
    renderSummary();
  }

  function applyLineEdit(index, fieldName, rawValue){
    if(isReadOnly || !lines[index]) return;
    const value = num(rawValue);
    if(fieldName === 'quantity' && value <= 0) return;
    if(fieldName === 'unitPrice' && value < 0) return;
    lines[index][fieldName] = value;
    const calc = calculateLine(lines[index]);
    const totalCell = $('linesBody').querySelector(`[data-line-total="${index}"]`);
    if(totalCell) totalCell.textContent = money(calc.lineTotal);
    renderSummary();
    markDirty();
  }

  function renderSummary(){
    const totals = calculateTotals();
    $('summarySubtotal').textContent = money(totals.subTotal);
    $('summaryDiscount').textContent = money(totals.discountAmount);
    $('summaryTax').textContent = money(totals.taxAmount);
    $('summaryTotal').textContent = money(totals.grandTotal);
    $('summaryPaid').textContent = money(totals.paidAmount);
    $('summaryBalance').textContent = money(totals.balanceAmount);
    toggleReceiveNow();
  }

  function toggleReceiveNow(){
    const paid = num($('paidAmount').value);
    const show = !isReadOnly && paid > 0;
    $('receiveNowMethodWrap').hidden = !show;
    const bank = show && $('receivePaymentMethod').value === 'Bank';
    $('receiveNowBankWrap').hidden = !bank;
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

  function postPayload(){
    const body = draftPayload();
    if(num($('paidAmount').value) > 0){
      body.receivePaymentMethod = $('receivePaymentMethod').value;
      body.bankAccountId = $('receivePaymentMethod').value === 'Bank' ? Number($('receiveBankAccount').value || 0) || null : null;
    }
    return body;
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
    if(num($('paidAmount').value) > 0 && $('receivePaymentMethod').value === 'Bank' && !Number($('receiveBankAccount').value || 0)){
      msg('status', 'Select a bank account for Bank receive-now.', false); return;
    }
    clearTimeout(saveTimer);
    await saveQueue.catch(() => false);
    isPosting = true;
    $('postBtn').disabled = true;
    try{
      const result = await api.post('/api/sales-invoices/post', postPayload());
      const postedId = Number(result.salesInvoiceId || 0);
      if(!postedId) throw new Error('Invoice was posted but its record ID was not returned.');
      savedVersion = dirtyVersion;
      location.href = `/sales-invoice-card.html?id=${postedId}&posted=1`;
    }catch(error){ isPosting = false; $('postBtn').disabled = false; msg('status', error.message, false); }
  }

  async function deleteInvoice(){
    if(isReadOnly || isPosDocument){ msg('status', 'Only Open sales invoices can be deleted.', false); return; }
    if(!invoiceId){ msg('status', 'Save the invoice as Open before deleting, or leave without saving.', false); return; }
    const invoiceNo = $('documentNo').textContent || invoiceId;
    if(!confirm(`Delete open sales invoice ${invoiceNo}? This cannot be undone.`)) return;
    try{
      $('deleteBtn').disabled = true;
      clearTimeout(saveTimer);
      const result = await api.delete('/api/sales-invoices/' + invoiceId);
      savedVersion = dirtyVersion;
      msg('status', result.message || 'Sales invoice deleted.', true);
      location.href = listUrl();
    }catch(error){
      $('deleteBtn').disabled = false;
      msg('status', error.message, false);
    }
  }

  async function printInvoice(){
    if(!invoiceId || !isReadOnly){ msg('status', isPosDocument ? 'This POS sale cannot be printed yet.' : 'Post the sales invoice before printing.', false); return; }
    const reportUrl = isPosDocument
      ? `/api/reports/sales-invoice/${invoiceId}/html?layout=${layout()}`
      : `/api/reports/formal-sales-invoice/${invoiceId}/html?layout=${layout()}`;
    try{ await api.openReport(reportUrl); }catch(error){ msg('status', error.message, false); }
  }

  init();
})();
