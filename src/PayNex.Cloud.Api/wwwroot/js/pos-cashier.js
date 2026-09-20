(() => {
  const val = (o, a, b, c) => o?.[a] ?? o?.[b] ?? o?.[c] ?? '';
  const num = (v) => Number(v || 0);
  const roundMoney = (v) => Math.round((num(v) + Number.EPSILON) * 100) / 100;
  const money = (v) => roundMoney(v).toFixed(2);
  const esc = (v) => String(v ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));

  const state = {
    me: null,
    permissions: {},
    cashierDiscountLimit: 100,
    blockNegativeStock: true,
    lines: [],
    selectedIndex: -1,
    customer: { customerId: 0, customerCode: 'WALKIN', customerName: 'Walk-in Customer', currentBalance: 0, discountPercent: 0 },
    invoiceDiscountPercent: 0,
    invoiceDiscountAmount: 0,
    invoiceDiscountMode: 'percent', // percent | amount
    paidAmount: 0,
    paidManual: false,
    paymentMethod: 'Cash',
    bankAccount: '',
    bankAccountId: 0,
    bankAccounts: [],
    pendingQty: 1,
    posting: false,
    lastSaleId: 0,
    pendingProduct: null,
    modalMode: null,
    modalRows: [],
    modalSel: 0
  };

  const $ = (id) => document.getElementById(id);

  function hasPerm(key) {
    const map = state.permissions || {};
    return !!map[key] || !!state.me?.isCompanySuperAdmin || !!state.me?.IsCompanySuperAdmin;
  }

  function canOverridePrice() { return hasPerm('pricing.overrideSellingPrice'); }
  function canChangeDiscount() { return hasPerm('pricing.changeProductDiscount'); }

  function lineCalc(line) {
    const qty = Math.max(0, num(line.quantity));
    const price = Math.max(0, num(line.unitPrice));
    const discPct = Math.min(100, Math.max(0, num(line.discountPercent)));
    const taxPct = Math.max(0, num(line.taxPercent));
    const taxInclusive = !!(line.taxInclusive);
    const gross = roundMoney(qty * price);
    const disc = roundMoney(gross * discPct / 100);
    const taxable = Math.max(0, roundMoney(gross - disc));
    let tax = 0;
    let total = taxable;
    if (taxPct > 0) {
      if (taxInclusive) {
        tax = roundMoney(taxable - (taxable / (1 + taxPct / 100)));
        total = taxable;
      } else {
        tax = roundMoney(taxable * taxPct / 100);
        total = roundMoney(taxable + tax);
      }
    }
    return { gross, disc, tax, total };
  }

  function totals() {
    const active = state.lines.filter(l => !l.voided);
    const itemsQty = active.reduce((s, l) => s + num(l.quantity), 0);
    const subtotal = roundMoney(active.reduce((s, l) => s + lineCalc(l).total, 0));
    let discAmt = 0;
    if (state.invoiceDiscountMode === 'amount') {
      discAmt = Math.min(subtotal, Math.max(0, roundMoney(state.invoiceDiscountAmount)));
      state.invoiceDiscountPercent = subtotal > 0 ? roundMoney(discAmt * 100 / subtotal) : 0;
    } else {
      const pct = Math.min(state.cashierDiscountLimit, Math.max(0, num(state.invoiceDiscountPercent)));
      state.invoiceDiscountPercent = pct;
      discAmt = roundMoney(subtotal * pct / 100);
      state.invoiceDiscountAmount = discAmt;
    }
    const after = Math.max(0, roundMoney(subtotal - discAmt));
    const paid = roundMoney(state.paidAmount);
    const change = Math.max(0, roundMoney(paid - after));
    const due = Math.max(0, roundMoney(after - paid));
    return { itemsQty, itemCount: active.length, subtotal, discAmt, after, paid, change, due };
  }

  function focusBarcode() {
    const el = $('barcodeInput');
    if (!el) return;
    el.focus();
    el.select();
  }

  function showStatus(msg, ok) {
    const el = $('posStatus');
    if (!el) return;
    el.textContent = msg;
    el.className = 'pos-status show ' + (ok ? 'ok' : 'err');
    clearTimeout(showStatus._t);
    showStatus._t = setTimeout(() => { el.className = 'pos-status'; }, 4500);
  }

  function renderHeader() {
    const me = state.me || {};
    $('storeName').textContent = val(me, 'companyName', 'CompanyName') || 'InterNex';
    $('cashierName').textContent = val(me, 'displayName', 'DisplayName') || val(me, 'userName', 'UserName') || '—';
    $('registerLabel').textContent = val(me, 'branchCode', 'BranchCode') || 'MAIN';
    $('saleStatus').textContent = state.lines.length ? 'OPEN' : 'READY';
  }

  function renderClock() {
    const now = new Date();
    $('liveClock').textContent = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  function renderGrid() {
    const body = $('cartBody');
    if (!state.lines.length) {
      body.innerHTML = '<tr><td colspan="7" style="text-align:center;color:#5a6f8a;padding:16px">Scan barcode or press F2 / F4 to add items</td></tr>';
      return;
    }
    body.innerHTML = state.lines.map((line, i) => {
      const c = lineCalc(line);
      const sel = i === state.selectedIndex ? 'selected' : '';
      const voided = line.voided ? 'voided' : '';
      return `<tr class="${sel} ${voided}" data-idx="${i}">
        <td title="${esc(line.productCode)}">${esc(line.productCode)}</td>
        <td title="${esc(line.productName)}">${esc(line.productName)}</td>
        <td class="num">${num(line.quantity).toFixed(2)}</td>
        <td class="num">${money(line.unitPrice)}</td>
        <td class="num">${money(c.disc)}</td>
        <td class="num">${money(c.total)}</td>
        <td class="status">${line.voided ? 'VOID' : 'OK'}</td>
      </tr>`;
    }).join('');
    const row = body.querySelector(`tr[data-idx="${state.selectedIndex}"]`);
    if (row) row.scrollIntoView({ block: 'nearest' });
  }

  function billAfter() {
    return totals().after;
  }

  function syncPaidDefault() {
    if (state.paidManual) return;
    state.paidAmount = billAfter();
  }

  function setPaidAmount(value, manual) {
    state.paidAmount = Math.max(0, roundMoney(value));
    if (manual) state.paidManual = true;
    renderTotals();
  }

  function paymentMethodMeta(method) {
    const m = String(method || 'Cash');
    if (m === 'Bank') return { id: 4, name: 'Bank Transfer' };
    if (m === 'Card') return { id: 2, name: 'Card' };
    if (m === 'Credit') return { id: 5, name: 'Credit' };
    if (m === 'Wallet') return { id: 3, name: 'Wallet' };
    return { id: 1, name: 'Cash' };
  }

  function fillBankSelect(selectEl, selectedId) {
    if (!selectEl) return;
    const rows = state.bankAccounts || [];
    const opts = ['<option value="">— Select bank —</option>'].concat(
      rows.map(b => {
        const id = num(val(b, 'bankAccountId', 'BankAccountId'));
        const code = val(b, 'bankCode', 'BankCode') || '';
        const name = val(b, 'bankName', 'BankName') || '';
        const sel = id === num(selectedId) ? ' selected' : '';
        return `<option value="${id}"${sel}>${esc(code)}${code && name ? ' - ' : ''}${esc(name)}</option>`;
      })
    );
    selectEl.innerHTML = opts.join('');
  }

  function syncPayMethodUi() {
    const method = state.paymentMethod || 'Cash';
    const methodEl = $('custPayMethod');
    if (methodEl && methodEl.value !== method) methodEl.value = method;
    const bankEl = $('custBankCode');
    const isBank = method === 'Bank';
    if (bankEl) {
      bankEl.disabled = !isBank;
      if (!isBank) {
        bankEl.value = '';
        state.bankAccountId = 0;
        state.bankAccount = '';
      } else if (state.bankAccountId) {
        bankEl.value = String(state.bankAccountId);
      }
    }
  }

  function renderTotals() {
    syncPaidDefault();
    const t = totals();
    $('totItems').textContent = String(Math.round(t.itemsQty * 100) / 100);
    $('totSub').textContent = money(t.subtotal);
    if ($('totPayInput') && document.activeElement !== $('totPayInput')) $('totPayInput').value = money(t.paid);
    if ($('custPayInput') && document.activeElement !== $('custPayInput')) $('custPayInput').value = money(t.paid);
    if ($('totChangeLbl')) $('totChangeLbl').textContent = t.due > 0 ? 'CHANGE / DUE' : 'CHANGE';
    $('totChange').textContent = t.due > 0 ? ('DUE ' + money(t.due)) : money(t.change);
    $('invDiscPct').textContent = String(state.invoiceDiscountPercent);
    $('invDiscAmt').textContent = money(t.discAmt);
    $('totAfter').textContent = money(t.after);
    $('custCurTrn').textContent = money(t.after);
    const oldBal = num(state.customer.currentBalance);
    $('custOldBal').textContent = money(oldBal);
    $('custCurBal').textContent = t.due > 0 ? ('DUE ' + money(t.due)) : money(t.change);
    $('custCode').textContent = state.customer.customerCode || 'WALKIN';
    $('custName').textContent = state.customer.customerName || 'Walk-in Customer';
    $('custDisc').textContent = String(state.customer.discountPercent || 0);
    $('billNo').textContent = state.lastSaleId ? String(state.lastSaleId) : 'NEW';
    syncPayMethodUi();
  }

  function renderQuickItems() {
    const key = 'posCashierQuick_' + (val(state.me, 'companyCode', 'CompanyCode') || 'default');
    let list = [];
    try { list = JSON.parse(localStorage.getItem(key) || '[]'); } catch { list = []; }
    const box = $('quickItems');
    if (!list.length) {
      box.innerHTML = '<button type="button" data-quick-pin="1" title="Pin from product search">+ PIN</button>';
      return;
    }
    box.innerHTML = list.slice(0, 8).map(p =>
      `<button type="button" data-quick-id="${esc(p.productId)}" title="${esc(p.productCode)}">${esc((p.productName || '').slice(0, 18))}</button>`
    ).join('') + '<button type="button" data-quick-pin="1">+ PIN</button>';
  }

  function refreshUi() {
    renderHeader();
    renderGrid();
    renderTotals();
    renderQuickItems();
  }

  function clearSale() {
    state.lines = [];
    state.selectedIndex = -1;
    state.invoiceDiscountPercent = 0;
    state.invoiceDiscountAmount = 0;
    state.invoiceDiscountMode = 'percent';
    state.paidAmount = 0;
    state.paidManual = false;
    state.paymentMethod = 'Cash';
    state.bankAccount = '';
    state.bankAccountId = 0;
    state.pendingQty = 1;
    if ($('custPayMethod')) $('custPayMethod').value = 'Cash';
    if ($('custBankCode')) { $('custBankCode').value = ''; $('custBankCode').disabled = true; }
    $('qtyInput').value = '1';
    $('priceInput').value = '';
    $('lineDiscInput').value = '0';
    refreshUi();
    focusBarcode();
  }

  function addOrMergeProduct(product, qtyOverride) {
    if (!product) return;
    const productId = num(val(product, 'productId', 'ProductId'));
    const code = String(val(product, 'productCode', 'ProductCode', 'barcode') || val(product, 'Barcode') || '');
    const name = String(val(product, 'productName', 'ProductName') || code);
    const stock = num(val(product, 'stockOnHand', 'StockOnHand', 'availableStock'));
    const priceDefault = num(val(product, 'salePrice', 'SalePrice', 'retailPrice', 'RetailPrice'));
    let qty = qtyOverride != null ? num(qtyOverride) : num($('qtyInput').value || state.pendingQty || 1);
    if (qty <= 0) qty = 1;
    let unitPrice = $('priceInput').value !== '' ? num($('priceInput').value) : priceDefault;
    if (!canOverridePrice()) unitPrice = priceDefault;
    let discPct = num($('lineDiscInput').value || 0);
    if (!canChangeDiscount()) discPct = 0;

    if (state.blockNegativeStock && stock < qty) {
      const existing = state.lines.find(l => !l.voided && l.productId === productId);
      const already = existing ? num(existing.quantity) : 0;
      if (already + qty > stock) {
        showStatus(`Stock not available for ${name}. Available: ${stock}`, false);
        return;
      }
    }

    const existingIdx = state.lines.findIndex(l => !l.voided && l.productId === productId && roundMoney(l.unitPrice) === roundMoney(unitPrice) && roundMoney(l.discountPercent) === roundMoney(discPct));
    if (existingIdx >= 0) {
      state.lines[existingIdx].quantity = num(state.lines[existingIdx].quantity) + qty;
      state.selectedIndex = existingIdx;
    } else {
      state.lines.push({
        productId, productCode: code, productName: name,
        quantity: qty, unitPrice, discountPercent: discPct,
        taxPercent: num(val(product, 'taxPercent', 'TaxPercent')),
        taxInclusive: !!(val(product, 'taxInclusive', 'TaxInclusive', 'isInclusive', 'IsInclusive')),
        voided: false
      });
      state.selectedIndex = state.lines.length - 1;
    }
    state.pendingQty = 1;
    $('qtyInput').value = '1';
    $('priceInput').value = '';
    $('lineDiscInput').value = '0';
    $('barcodeInput').value = '';
    $('descInput').value = '';
    refreshUi();
    focusBarcode();
  }

  async function findProductByCode(term) {
    const q = String(term || '').trim();
    if (!q) return null;
    const rows = await api.get('/api/products?term=' + encodeURIComponent(q) + '&take=25');
    const list = Array.isArray(rows) ? rows : (rows.items || rows.products || []);
    const exact = list.find(p => {
      const code = String(val(p, 'productCode', 'ProductCode') || '').toLowerCase();
      const bar = String(val(p, 'barcode', 'Barcode') || '').toLowerCase();
      return code === q.toLowerCase() || bar === q.toLowerCase();
    });
    return exact || (list.length === 1 ? list[0] : null) || { _ambiguous: true, list };
  }

  async function onBarcodeEnter() {
    const term = $('barcodeInput').value.trim() || $('descInput').value.trim();
    if (!term) return;
    try {
      const found = await findProductByCode(term);
      if (!found) { showStatus('Item not found: ' + term, false); focusBarcode(); return; }
      if (found._ambiguous) {
        openProductPicker(found.list, term);
        return;
      }
      addOrMergeProduct(found);
    } catch (e) {
      showStatus(e.message || 'Lookup failed', false);
    }
  }

  function closeModal() {
    $('modalBackdrop').classList.remove('open');
    state.modalMode = null;
    state.modalRows = [];
    focusBarcode();
  }

  function openModal(title, bodyHtml, footHtml, mode, sm) {
    $('modalTitle').textContent = title;
    $('modalBody').innerHTML = bodyHtml;
    $('modalFoot').innerHTML = footHtml || '';
    $('modalBox').className = 'pos-modal' + (sm ? ' sm' : '');
    $('modalBackdrop').classList.add('open');
    state.modalMode = mode;
    const first = $('modalBody').querySelector('input,select,button,textarea');
    if (first) setTimeout(() => first.focus(), 30);
  }

  function openProductPicker(rows, term) {
    state.modalRows = rows || [];
    state.modalSel = 0;
    const body = `<input id="modalSearch" value="${esc(term || '')}" placeholder="Search item code / name / barcode">
      <div style="max-height:360px;overflow:auto"><table id="modalTable"><thead><tr><th>Code</th><th>Name</th><th>Price</th><th>Stock</th></tr></thead>
      <tbody>${renderProductRows(state.modalRows, 0)}</tbody></table></div>`;
    openModal('Product Search', body, '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Select</button>', 'product');
    wireProductModal();
  }

  function renderProductRows(rows, sel) {
    if (!rows.length) return '<tr><td colspan="4">No products</td></tr>';
    return rows.map((p, i) => `<tr class="${i === sel ? 'sel' : ''}" data-i="${i}">
      <td>${esc(val(p, 'productCode', 'ProductCode'))}</td>
      <td>${esc(val(p, 'productName', 'ProductName'))}</td>
      <td class="num">${money(val(p, 'salePrice', 'SalePrice'))}</td>
      <td class="num">${esc(val(p, 'stockOnHand', 'StockOnHand'))}</td>
    </tr>`).join('');
  }

  function wireProductModal() {
    const search = $('modalSearch');
    const table = $('modalTable');
    let timer;
    const reload = async () => {
      const term = search.value.trim();
      try {
        const rows = await api.get('/api/products?term=' + encodeURIComponent(term) + '&take=80');
        state.modalRows = Array.isArray(rows) ? rows : (rows.items || []);
        state.modalSel = 0;
        table.querySelector('tbody').innerHTML = renderProductRows(state.modalRows, 0);
      } catch (e) { showStatus(e.message, false); }
    };
    search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(reload, 220); });
    table.addEventListener('click', (e) => {
      const tr = e.target.closest('tr[data-i]');
      if (!tr) return;
      state.modalSel = Number(tr.dataset.i);
      [...table.querySelectorAll('tr')].forEach((r, i) => r.classList.toggle('sel', i === state.modalSel));
      if (e.detail === 2) selectModalProduct();
    });
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = selectModalProduct;
    if (!search.value) reload();
  }

  function selectModalProduct() {
    const p = state.modalRows[state.modalSel];
    if (!p) return;
    if (state.modalMode === 'pin') {
      pinQuick(p);
      closeModal();
      return;
    }
    closeModal();
    addOrMergeProduct(p);
  }

  function pinQuick(p) {
    const key = 'posCashierQuick_' + (val(state.me, 'companyCode', 'CompanyCode') || 'default');
    let list = [];
    try { list = JSON.parse(localStorage.getItem(key) || '[]'); } catch { list = []; }
    const id = num(val(p, 'productId', 'ProductId'));
    list = list.filter(x => x.productId !== id);
    list.unshift({
      productId: id,
      productCode: val(p, 'productCode', 'ProductCode'),
      productName: val(p, 'productName', 'ProductName')
    });
    localStorage.setItem(key, JSON.stringify(list.slice(0, 12)));
    renderQuickItems();
    showStatus('Quick item pinned', true);
  }

  async function openCustomerPicker() {
    openModal('Customer Search',
      `<input id="modalSearch" placeholder="Code / name / phone / email">
       <div style="max-height:360px;overflow:auto"><table id="modalTable"><thead><tr><th>Code</th><th>Name</th><th>Balance</th></tr></thead><tbody></tbody></table></div>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Select</button>',
      'customer');
    const search = $('modalSearch');
    const tbody = $('modalTable').querySelector('tbody');
    let timer;
    const reload = async () => {
      try {
        const rows = await api.get('/api/customers?term=' + encodeURIComponent(search.value.trim()) + '&take=80');
        state.modalRows = Array.isArray(rows) ? rows : (rows.items || []);
        state.modalSel = 0;
        tbody.innerHTML = state.modalRows.map((c, i) => `<tr class="${i === 0 ? 'sel' : ''}" data-i="${i}">
          <td>${esc(val(c, 'customerCode', 'CustomerCode'))}</td>
          <td>${esc(val(c, 'customerName', 'CustomerName'))}</td>
          <td>${money(val(c, 'currentBalance', 'CurrentBalance'))}</td>
        </tr>`).join('') || '<tr><td colspan="3">No customers</td></tr>';
      } catch (e) { showStatus(e.message, false); }
    };
    search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(reload, 220); });
    $('modalTable').addEventListener('click', (e) => {
      const tr = e.target.closest('tr[data-i]');
      if (!tr) return;
      state.modalSel = Number(tr.dataset.i);
      [...tbody.querySelectorAll('tr')].forEach((r, i) => r.classList.toggle('sel', i === state.modalSel));
      if (e.detail === 2) pickCustomer();
    });
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = pickCustomer;
    reload();
  }

  function pickCustomer() {
    const c = state.modalRows[state.modalSel];
    if (!c) return;
    state.customer = {
      customerId: num(val(c, 'customerId', 'CustomerId')),
      customerCode: val(c, 'customerCode', 'CustomerCode'),
      customerName: val(c, 'customerName', 'CustomerName'),
      currentBalance: num(val(c, 'currentBalance', 'CurrentBalance')),
      discountPercent: 0
    };
    closeModal();
    renderTotals();
    showStatus('Customer set: ' + state.customer.customerName, true);
  }

  function openCategoryFilter() {
    openModal('Category Filter',
      `<input id="modalSearch" placeholder="Type category name to filter products">
       <p class="mini">Enter a category keyword, then Select to open matching products.</p>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Search</button>',
      'category', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = async () => {
      const term = $('modalSearch').value.trim();
      closeModal();
      try {
        const rows = await api.get('/api/products?term=' + encodeURIComponent(term) + '&take=100');
        const list = Array.isArray(rows) ? rows : [];
        const filtered = term
          ? list.filter(p => String(val(p, 'categoryName', 'CategoryName')).toLowerCase().includes(term.toLowerCase()) || String(val(p, 'productName', 'ProductName')).toLowerCase().includes(term.toLowerCase()))
          : list;
        openProductPicker(filtered.length ? filtered : list, term);
      } catch (e) { showStatus(e.message, false); }
    };
  }

  function openDiscountPct() {
    if (!canChangeDiscount()) { showStatus('Discount permission required', false); return; }
    openModal('Invoice Discount %',
      `<label>Discount % (max ${state.cashierDiscountLimit})<input id="modalVal" type="number" min="0" max="${state.cashierDiscountLimit}" step="0.01" value="${state.invoiceDiscountPercent}"></label>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Apply</button>',
      'discPct', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = () => {
      let v = num($('modalVal').value);
      v = Math.min(state.cashierDiscountLimit, Math.max(0, v));
      state.invoiceDiscountMode = 'percent';
      state.invoiceDiscountPercent = v;
      closeModal();
      renderTotals();
    };
  }

  function openDiscountAmt() {
    if (!canChangeDiscount()) { showStatus('Discount permission required', false); return; }
    const t = totals();
    openModal('Manual Discount Amount',
      `<label>Discount amount<input id="modalVal" type="number" min="0" max="${t.subtotal}" step="0.01" value="${state.invoiceDiscountAmount}"></label>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Apply</button>',
      'discAmt', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = () => {
      state.invoiceDiscountMode = 'amount';
      state.invoiceDiscountAmount = Math.min(t.subtotal, Math.max(0, num($('modalVal').value)));
      closeModal();
      renderTotals();
    };
  }

  function openPriceOverride() {
    if (state.selectedIndex < 0) { showStatus('Select a line first', false); return; }
    if (!canOverridePrice()) { showStatus('Price override permission required', false); return; }
    const line = state.lines[state.selectedIndex];
    openModal('Override Price',
      `<label>${esc(line.productName)}<input id="modalVal" type="number" min="0" step="0.01" value="${line.unitPrice}"></label>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Apply</button>',
      'price', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = () => {
      line.unitPrice = Math.max(0, num($('modalVal').value));
      closeModal();
      refreshUi();
    };
  }

  function openQtyDialog(multi) {
    openModal(multi ? 'Multi Quantity (next scan)' : 'Change Quantity',
      `<label>Quantity<input id="modalVal" type="number" min="0.001" step="any" value="${multi ? state.pendingQty : (state.lines[state.selectedIndex]?.quantity || 1)}"></label>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">OK</button>',
      multi ? 'multiQty' : 'chgQty', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = () => {
      const q = Math.max(0.001, num($('modalVal').value));
      if (multi) {
        state.pendingQty = q;
        $('qtyInput').value = String(q);
      } else if (state.selectedIndex >= 0) {
        state.lines[state.selectedIndex].quantity = q;
      }
      closeModal();
      refreshUi();
      focusBarcode();
    };
  }

  function openPayment() {
    const t = totals();
    const bankOpts = (state.bankAccounts || []).map(b => {
      const id = num(val(b, 'bankAccountId', 'BankAccountId'));
      const code = val(b, 'bankCode', 'BankCode') || '';
      const name = val(b, 'bankName', 'BankName') || '';
      const sel = id === state.bankAccountId ? ' selected' : '';
      return `<option value="${id}"${sel}>${esc(code)}${code && name ? ' - ' : ''}${esc(name)}</option>`;
    }).join('');
    openModal('Mode of Payment',
      `<label>Method
        <select id="payMethod">
          <option value="Cash">Cash</option>
          <option value="Bank">Bank</option>
          <option value="Card">Card</option>
          <option value="Credit">Credit</option>
        </select>
      </label>
      <label>Amount due<input id="dueAmt" value="${money(t.after)}" readonly></label>
      <label>Amount received<input id="modalVal" type="number" min="0" step="0.01" value="${money(state.paidAmount || t.after)}"></label>
      <label id="bankRow" hidden>Bank Code
        <select id="bankRef"><option value="">— Select bank —</option>${bankOpts}</select>
      </label>
      <p>Change / Due: <b id="payChangePreview">${money(0)}</b></p>
      <button type="button" class="primary" id="postNowBtn" style="width:100%;margin-top:8px;height:36px">COMPLETE / POST SALE</button>`,
      '<button type="button" id="modalCancel">Close</button><button type="button" class="primary" id="modalOk">Apply Payment</button>',
      'payment');
    const syncChange = () => {
      const paid = num($('modalVal').value);
      const due = Math.max(0, roundMoney(t.after - paid));
      const change = Math.max(0, roundMoney(paid - t.after));
      $('payChangePreview').textContent = due > 0 ? ('DUE ' + money(due)) : money(change);
      $('bankRow').hidden = $('payMethod').value !== 'Bank';
    };
    $('payMethod').onchange = syncChange;
    $('modalVal').oninput = syncChange;
    $('payMethod').value = state.paymentMethod || 'Cash';
    syncChange();
    $('modalCancel').onclick = closeModal;
    const applyPay = () => {
      state.paymentMethod = $('payMethod').value;
      state.paidAmount = num($('modalVal').value);
      state.paidManual = true;
      if (state.paymentMethod === 'Bank') {
        state.bankAccountId = num($('bankRef')?.value || 0);
        const b = (state.bankAccounts || []).find(x => num(val(x, 'bankAccountId', 'BankAccountId')) === state.bankAccountId);
        state.bankAccount = b ? String(val(b, 'bankCode', 'BankCode') || '') : '';
      } else {
        state.bankAccountId = 0;
        state.bankAccount = '';
      }
    };
    $('modalOk').onclick = () => {
      applyPay();
      closeModal();
      renderTotals();
    };
    $('postNowBtn').onclick = async () => {
      applyPay();
      closeModal();
      await postSale();
    };
  }

  async function holdSale() {
    const active = state.lines.filter(l => !l.voided);
    if (!active.length) { showStatus('Nothing to hold', false); return; }
    const t = totals();
    try {
      const payload = {
        customerId: state.customer.customerId,
        invoiceDiscountPercent: state.invoiceDiscountPercent,
        invoiceDiscountAmount: state.invoiceDiscountAmount,
        invoiceDiscountMode: state.invoiceDiscountMode,
        lines: active.map(l => ({
          productId: l.productId,
          productCode: l.productCode,
          productName: l.productName,
          quantity: l.quantity,
          unitPrice: l.unitPrice,
          discountPercent: l.discountPercent
        }))
      };
      const r = await api.post('/api/hold-sales', {
        customerId: state.customer.customerId,
        subTotal: t.subtotal,
        discountAmount: t.discAmt,
        taxAmount: 0,
        grandTotal: t.after,
        lines: active.map(l => ({ productId: l.productId, quantity: l.quantity, unitPrice: l.unitPrice, discountPercent: l.discountPercent })),
        payload,
        remarks: 'Cashier POS hold'
      });
      showStatus(r.message || ('Held ' + (r.holdNo || '')), true);
      clearSale();
    } catch (e) { showStatus(e.message, false); }
  }

  async function retrieveSale() {
    try {
      const rows = await api.get('/api/hold-sales');
      const list = Array.isArray(rows) ? rows : [];
      state.modalRows = list;
      state.modalSel = 0;
      openModal('Retrieve Hold',
        `<div style="max-height:360px;overflow:auto"><table id="modalTable"><thead><tr><th>Hold No</th><th>Date</th><th>Customer</th><th>Total</th></tr></thead>
        <tbody>${list.map((h, i) => `<tr class="${i === 0 ? 'sel' : ''}" data-i="${i}">
          <td>${esc(val(h, 'holdNo', 'HoldNo'))}</td>
          <td>${esc(val(h, 'holdDate', 'HoldDate'))}</td>
          <td>${esc(val(h, 'customerId', 'CustomerId'))}</td>
          <td>${money(val(h, 'grandTotal', 'GrandTotal'))}</td>
        </tr>`).join('') || '<tr><td colspan="4">No holds</td></tr>'}</tbody></table></div>`,
        '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Retrieve</button>',
        'retrieve');
      const tbody = $('modalTable').querySelector('tbody');
      $('modalTable').addEventListener('click', (e) => {
        const tr = e.target.closest('tr[data-i]');
        if (!tr) return;
        state.modalSel = Number(tr.dataset.i);
        [...tbody.querySelectorAll('tr')].forEach((r, i) => r.classList.toggle('sel', i === state.modalSel));
      });
      $('modalCancel').onclick = closeModal;
      $('modalOk').onclick = async () => {
        const h = state.modalRows[state.modalSel];
        if (!h) return;
        const id = num(val(h, 'holdId', 'HoldId'));
        try {
          const r = await api.post('/api/hold-sales/' + id + '/retrieve', {});
          const payload = r.payload || {};
          const lines = payload.lines || payload.Lines || [];
          clearSale();
          state.customer.customerId = num(r.customerId || payload.customerId || 0);
          state.invoiceDiscountPercent = num(payload.invoiceDiscountPercent || 0);
          state.invoiceDiscountAmount = num(payload.invoiceDiscountAmount || 0);
          state.invoiceDiscountMode = payload.invoiceDiscountMode || 'percent';
          state.lines = lines.map(l => ({
            productId: num(val(l, 'productId', 'ProductId')),
            productCode: val(l, 'productCode', 'ProductCode'),
            productName: val(l, 'productName', 'ProductName'),
            quantity: num(val(l, 'quantity', 'Quantity')),
            unitPrice: num(val(l, 'unitPrice', 'UnitPrice')),
            discountPercent: num(val(l, 'discountPercent', 'DiscountPercent')),
            voided: false
          }));
          state.selectedIndex = state.lines.length ? 0 : -1;
          if (state.customer.customerId > 0) {
            try {
              const c = await api.get('/api/customers/' + state.customer.customerId);
              state.customer.customerCode = val(c, 'customerCode', 'CustomerCode');
              state.customer.customerName = val(c, 'customerName', 'CustomerName');
              state.customer.currentBalance = num(val(c, 'currentBalance', 'CurrentBalance'));
            } catch { /* keep ids */ }
          }
          closeModal();
          refreshUi();
          showStatus('Hold ' + (r.holdNo || id) + ' retrieved', true);
        } catch (e) { showStatus(e.message, false); }
      };
    } catch (e) { showStatus(e.message, false); }
  }

  async function duplicateBill() {
    openModal('Duplicate Previous Bill',
      `<input id="modalSearch" placeholder="Invoice / sale number">
       <p>Loads lines into a <b>new</b> transaction (does not edit the original).</p>`,
      '<button type="button" id="modalCancel">Cancel</button><button type="button" class="primary" id="modalOk">Load</button>',
      'dup', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = async () => {
      const no = $('modalSearch').value.trim();
      if (!no) return;
      try {
        // Prefer POS receipt by searching sales invoices / returns source list is heavier; try formal lines then products by code.
        let lines = [];
        try {
          const inv = await api.get('/api/sales-invoices?status=Posted&take=50');
          const list = Array.isArray(inv) ? inv : (inv.items || []);
          const hit = list.find(x => String(val(x, 'invoiceNo', 'InvoiceNo')).toLowerCase() === no.toLowerCase());
          if (hit) {
            const id = num(val(hit, 'salesInvoiceId', 'SalesInvoiceId', 'saleId', 'SaleId', 'invoiceId', 'InvoiceId'));
            const detail = await api.get('/api/sales-invoices/' + id + '/lines');
            lines = Array.isArray(detail) ? detail : (detail.lines || []);
          }
        } catch { /* fall through */ }
        if (!lines.length) {
          showStatus('Bill not found or has no lines: ' + no, false);
          return;
        }
        clearSale();
        for (const l of lines) {
          state.lines.push({
            productId: num(val(l, 'productId', 'ProductId')),
            productCode: val(l, 'productCode', 'ProductCode'),
            productName: val(l, 'productName', 'ProductName'),
            quantity: num(val(l, 'quantity', 'Quantity')),
            unitPrice: num(val(l, 'unitPrice', 'UnitPrice', 'salePrice')),
            discountPercent: num(val(l, 'discountPercent', 'DiscountPercent')),
            voided: false
          });
        }
        state.selectedIndex = 0;
        closeModal();
        refreshUi();
        showStatus('Duplicated into new sale', true);
      } catch (e) { showStatus(e.message, false); }
    };
  }

  function cancelBill() {
    if (!state.lines.length) { clearSale(); return; }
    openModal('Cancel Bill',
      '<p>Are you sure you want to cancel the current sale?</p>',
      '<button type="button" id="modalCancel">No</button><button type="button" class="primary" id="modalOk">Yes, Cancel</button>',
      'cancel', true);
    $('modalCancel').onclick = closeModal;
    $('modalOk').onclick = () => { closeModal(); clearSale(); showStatus('Sale cancelled', true); };
  }

  function voidLine() {
    if (state.selectedIndex < 0) { showStatus('Select a line to void', false); return; }
    state.lines[state.selectedIndex].voided = true;
    refreshUi();
    focusBarcode();
  }

  async function postSale() {
    if (state.posting) return;
    const active = state.lines.filter(l => !l.voided);
    if (!active.length) { showStatus('Cart is empty', false); return; }
    syncPaidDefault();
    let t = totals();
    const method = state.paymentMethod || 'Cash';
    if (method === 'Bank' && !state.bankAccountId) {
      showStatus('Select Bank Code for bank payment', false);
      return;
    }
    // API requires paid >= grand total. Credit posts full bill as credit tender.
    // Cash/Bank/Card: if short, top up to bill total; overpay keeps change.
    let tender = roundMoney(state.paidAmount);
    if (method === 'Credit') {
      tender = t.after;
    } else if (tender < t.after) {
      state.paidAmount = t.after;
      tender = t.after;
      t = totals();
      renderTotals();
    }
    const meta = paymentMethodMeta(method);
    const bankRef = method === 'Bank'
      ? (state.bankAccount || String(state.bankAccountId || ''))
      : (state.bankAccount || '');
    state.posting = true;
    try {
      const body = {
        customerId: state.customer.customerId || 0,
        remarks: 'Cashier POS | ' + method + (state.invoiceDiscountPercent ? ` | Disc ${state.invoiceDiscountPercent}%` : ''),
        lines: active.map(l => ({
          productId: l.productId,
          quantity: l.quantity,
          unitPrice: l.unitPrice,
          discountPercent: l.discountPercent || 0
        })),
        payments: [{
          paymentMethodId: meta.id,
          paymentMethodName: meta.name,
          amount: tender,
          referenceNo: bankRef
        }]
      };
      const result = await api.post('/api/pos/sales', body);
      state.lastSaleId = result.saleId || result.SaleId;
      showStatus(`Posted ${result.invoiceNo || result.InvoiceNo} | Change ${money(result.change || result.changeAmount || 0)}`, true);
      const postedId = state.lastSaleId;
      clearSale();
      if (postedId) {
        try { await api.openReport('/api/reports/cashier-invoice/' + postedId + '/html'); } catch { /* optional */ }
      }
    } catch (e) {
      showStatus(e.message || 'Post failed', false);
    } finally {
      state.posting = false;
    }
  }

  function handleAction(act) {
    switch (act) {
      case 'category': openCategoryFilter(); break;
      case 'customer': openCustomerPicker(); break;
      case 'description':
      case 'itemcode': openProductPicker([], ''); break;
      case 'hold': holdSale(); break;
      case 'retrieve': retrieveSale(); break;
      case 'duplicate': duplicateBill(); break;
      case 'discPct': openDiscountPct(); break;
      case 'discAmt': openDiscountAmt(); break;
      case 'payment': openPayment(); break;
      case 'post': postSale(); break;
      case 'cancel': cancelBill(); break;
      case 'price': openPriceOverride(); break;
      default: break;
    }
  }

  function handleFkey(key) {
    if (key === 'F2') openProductPicker([], $('barcodeInput').value);
    else if (key === 'F4') openProductPicker([], $('descInput').value);
    else if (key === 'F5') voidLine();
    else if (key === 'F6') location.href = '/sales-return-orders.html';
    else if (key === 'F7') openQtyDialog(true);
    else if (key === 'F9') openQtyDialog(false);
    else if (key === 'EXIT') location.href = '/workspace.html';
  }

  function isTypingTarget(el) {
    if (!el) return false;
    const tag = (el.tagName || '').toLowerCase();
    return tag === 'input' || tag === 'textarea' || tag === 'select' || el.isContentEditable;
  }

  async function init() {
    try {
      state.me = await api.get('/api/me');
      state.permissions = state.me.permissions || state.me.Permissions || {};
      if (typeof state.permissions === 'string') {
        try { state.permissions = JSON.parse(state.permissions); } catch { state.permissions = {}; }
      }
      try {
        const setup = await api.get('/api/posting-setup');
        state.cashierDiscountLimit = num(val(setup, 'cashierDiscountLimit', 'CashierDiscountLimit') || 100);
        state.blockNegativeStock = String(val(setup, 'blockNegativeStock', 'BlockNegativeStock') ?? 'true') !== 'false';
      } catch { /* defaults */ }
      try {
        const banks = await api.get('/api/bank-accounts');
        state.bankAccounts = Array.isArray(banks) ? banks : [];
      } catch { state.bankAccounts = []; }
      const look = await api.get('/api/lookups');
      const customers = look.customers || look.Customers || [];
      const walkIn = customers.find(c => String(val(c, 'customerCode', 'CustomerCode')).toUpperCase() === 'WALKIN')
        || customers.find(c => /walk[-\s]?in/i.test(String(val(c, 'customerName', 'CustomerName'))))
        || { customerId: 0, customerCode: 'WALKIN', customerName: 'Walk-in Customer', currentBalance: 0 };
      state.customer = {
        customerId: num(val(walkIn, 'customerId', 'CustomerId')),
        customerCode: val(walkIn, 'customerCode', 'CustomerCode') || 'WALKIN',
        customerName: val(walkIn, 'customerName', 'CustomerName') || 'Walk-in Customer',
        currentBalance: num(val(walkIn, 'currentBalance', 'CurrentBalance')),
        discountPercent: 0
      };
    } catch (e) {
      showStatus(e.message || 'Failed to load session', false);
    }

    fillBankSelect($('custBankCode'), state.bankAccountId);
    refreshUi();
    renderClock();
    setInterval(renderClock, 1000);
    focusBarcode();

    const onPayInput = (e) => setPaidAmount(e.target.value, true);
    $('totPayInput')?.addEventListener('input', onPayInput);
    $('totPayInput')?.addEventListener('change', onPayInput);
    $('custPayInput')?.addEventListener('input', onPayInput);
    $('custPayInput')?.addEventListener('change', onPayInput);
    $('custPayMethod')?.addEventListener('change', () => {
      state.paymentMethod = $('custPayMethod').value || 'Cash';
      if (state.paymentMethod !== 'Bank') {
        state.bankAccountId = 0;
        state.bankAccount = '';
      }
      syncPayMethodUi();
    });
    $('custBankCode')?.addEventListener('change', () => {
      state.bankAccountId = num($('custBankCode').value || 0);
      const b = (state.bankAccounts || []).find(x => num(val(x, 'bankAccountId', 'BankAccountId')) === state.bankAccountId);
      state.bankAccount = b ? String(val(b, 'bankCode', 'BankCode') || '') : '';
    });

    $('barcodeInput').addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); onBarcodeEnter(); }
    });
    $('descInput').addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); openProductPicker([], $('descInput').value); }
    });
    $('addItemBtn').onclick = onBarcodeEnter;

    $('cartBody').addEventListener('click', (e) => {
      const tr = e.target.closest('tr[data-idx]');
      if (!tr) return;
      state.selectedIndex = Number(tr.dataset.idx);
      renderGrid();
    });

    document.querySelectorAll('[data-act]').forEach(btn => {
      btn.addEventListener('click', () => handleAction(btn.getAttribute('data-act')));
    });
    document.querySelectorAll('[data-fkey]').forEach(btn => {
      btn.addEventListener('click', () => handleFkey(btn.getAttribute('data-fkey')));
    });
    $('quickItems').addEventListener('click', async (e) => {
      const pin = e.target.closest('[data-quick-pin]');
      if (pin) { state.modalMode = 'pin'; openProductPicker([], ''); return; }
      const q = e.target.closest('[data-quick-id]');
      if (!q) return;
      try {
        const p = await api.get('/api/products/' + q.getAttribute('data-quick-id'));
        addOrMergeProduct(p);
      } catch (err) { showStatus(err.message, false); }
    });

    $('modalCloseBtn').onclick = closeModal;
    $('modalBackdrop').addEventListener('click', (e) => {
      if (e.target === $('modalBackdrop')) closeModal();
    });

    document.addEventListener('keydown', (e) => {
      const modalOpen = $('modalBackdrop').classList.contains('open');
      if (modalOpen) {
        if (e.key === 'Escape') { e.preventDefault(); closeModal(); return; }
        if (e.key === 'ArrowDown') {
          e.preventDefault();
          state.modalSel = Math.min(state.modalRows.length - 1, state.modalSel + 1);
          const rows = document.querySelectorAll('#modalTable tbody tr');
          rows.forEach((r, i) => r.classList.toggle('sel', i === state.modalSel));
          rows[state.modalSel]?.scrollIntoView({ block: 'nearest' });
          return;
        }
        if (e.key === 'ArrowUp') {
          e.preventDefault();
          state.modalSel = Math.max(0, state.modalSel - 1);
          const rows = document.querySelectorAll('#modalTable tbody tr');
          rows.forEach((r, i) => r.classList.toggle('sel', i === state.modalSel));
          return;
        }
        if (e.key === 'Enter' && (state.modalMode === 'product' || state.modalMode === 'pin')) {
          e.preventDefault();
          selectModalProduct();
        }
        return;
      }

      if (e.key === 'Escape') { e.preventDefault(); handleFkey('EXIT'); return; }
      if (e.key === 'F2') { e.preventDefault(); handleFkey('F2'); return; }
      if (e.key === 'F4') { e.preventDefault(); handleFkey('F4'); return; }
      if (e.key === 'F5') { e.preventDefault(); handleFkey('F5'); return; }
      if (e.key === 'F6') { e.preventDefault(); handleFkey('F6'); return; }
      if (e.key === 'F7') { e.preventDefault(); handleFkey('F7'); return; }
      if (e.key === 'F9') { e.preventDefault(); handleFkey('F9'); return; }

      if (isTypingTarget(e.target)) return;
      if (e.ctrlKey || e.altKey || e.metaKey) return;

      const letterActs = {
        c: 'category', j: 'customer', o: 'description', i: 'itemcode',
        t: 'hold', r: 'retrieve', a: 'duplicate', d: 'discPct',
        m: 'discAmt', g: 'payment', z: 'cancel', l: 'price', p: 'post'
      };
      const letterAct = letterActs[String(e.key).toLowerCase()];
      if (letterAct) {
        e.preventDefault();
        handleAction(letterAct);
        return;
      }

      if (e.key === 'ArrowDown') {
        e.preventDefault();
        if (!state.lines.length) return;
        state.selectedIndex = Math.min(state.lines.length - 1, state.selectedIndex + 1);
        if (state.selectedIndex < 0) state.selectedIndex = 0;
        renderGrid();
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        if (!state.lines.length) return;
        state.selectedIndex = Math.max(0, state.selectedIndex - 1);
        renderGrid();
      } else if (e.key === 'Delete' && state.selectedIndex >= 0) {
        e.preventDefault();
        voidLine();
      }
    });
  }

  init();
})();
