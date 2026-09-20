let products = [], cart = [], lastSaleId = 0;
let selectedProduct = null;

const val = (o, a, b) => o?.[a] ?? o?.[b] ?? '';
const roundMoney = value => Math.round((Number(value || 0) + Number.EPSILON) * 100) / 100;
const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));

function lockCustomerToWalkIn(customers){
  const list = customers || [];
  const walkIn = list.find(c => String(val(c,'customerCode','CustomerCode')).toUpperCase() === 'WALKIN')
    || list.find(c => /walk[-\s]?in/i.test(String(val(c,'customerName','CustomerName'))))
    || { customerId: 0, customerCode: 'WALKIN', customerName: 'Walk-in Customer', currentBalance: 0 };
  const id = val(walkIn,'customerId','CustomerId') || 0;
  const text = `${val(walkIn,'customerCode','CustomerCode') || 'WALKIN'} - ${val(walkIn,'customerName','CustomerName') || 'Walk-in Customer'} | Balance ${money(val(walkIn,'currentBalance','CurrentBalance'))}`;
  customer.dataset.noLookup = '1';
  customer.innerHTML = `<option value="${esc(id)}">${esc(text)}</option>`;
  customer.value = String(id);
  customer.disabled = true;
  customer.title = 'POS uses Walk-in Customer only.';
}

async function init(){
  try{
    const me = await api.get('/api/me');
    const who = document.getElementById('who');
    if(who) who.textContent = `${me.companyName || me.CompanyName} | ${me.displayName || me.DisplayName} | ${me.roleName || me.RoleName}`;
    const look = await api.get('/api/lookups');
    lockCustomerToWalkIn(look.customers || []);
    await loadBankAccounts();
    await loadProducts();
    const payMethodEl = document.getElementById('payMethod');
    const bankRow = document.getElementById('bankRow');
    if(payMethodEl && bankRow){
      const syncBank = () => { bankRow.hidden = payMethodEl.value !== 'Bank'; };
      payMethodEl.addEventListener('change', syncBank);
      syncBank();
    }
    const cartBarcode = document.getElementById('cartBarcode');
    if(cartBarcode){
      cartBarcode.addEventListener('keydown', async event => {
        if(event.key !== 'Enter' || event.repeat) return;
        event.preventDefault();
        await autoAddByBarcode(cartBarcode.value.trim());
        cartBarcode.value = '';
        cartBarcode.focus();
      });
    }
  }catch{
    location.href = '/login.html';
  }
}

async function loadBankAccounts(){
  const select = document.getElementById('bankAccount');
  if(!select) return;
  try{
    const accounts = await api.get('/api/accounting/chart-of-accounts');
    const banks = (accounts || []).filter(a => {
      const type = String(val(a,'accountType','AccountType') || val(a,'accountCategory','AccountCategory') || '').toLowerCase();
      const name = String(val(a,'accountName','AccountName') || '');
      const no = String(val(a,'accountNo','AccountNo') || '');
      return type.includes('bank') || /bank/i.test(name) || /^11/.test(no);
    });
    if(banks.length){
      select.innerHTML = banks.map(b => {
        const no = val(b,'accountNo','AccountNo');
        const name = val(b,'accountName','AccountName');
        return `<option value="${esc(no)}">${esc(no)} - ${esc(name)}</option>`;
      }).join('');
    }else{
      select.innerHTML = '<option value="BANK">Default Bank</option>';
    }
  }catch{
    select.innerHTML = '<option value="BANK">Default Bank</option>';
  }
}

async function autoAddByBarcode(code){
  if(!code) return;
  try{
    let match = products.find(p => String(val(p,'barcode','Barcode')).trim() === code
      || String(val(p,'productCode','ProductCode')).trim().toLowerCase() === code.toLowerCase());
    if(!match){
      const found = await api.get('/api/products?term=' + encodeURIComponent(code));
      match = (found || []).find(p => String(val(p,'barcode','Barcode')).trim() === code
        || String(val(p,'productCode','ProductCode')).trim().toLowerCase() === code.toLowerCase())
        || (found || [])[0];
      if(found?.length) products = found;
    }
    if(!match){ msg('status', `No item matched barcode ${code}.`, false); return; }
    const pid = Number(val(match,'productId','ProductId'));
    const existing = cart.find(x => x.productId === pid);
    if(existing){
      existing.quantity = Number(existing.quantity || 0) + 1;
      renderCart();
      msg('status', `Added ${val(match,'productName','ProductName')} (qty +1).`, true);
      return;
    }
    cart.push({
      productId: pid,
      productName: val(match,'productName','ProductName'),
      productCode: val(match,'productCode','ProductCode'),
      quantity: 1,
      price: Number(val(match,'salePrice','SalePrice') || 0),
      discountPercent: Number(val(match,'productDiscountPercent','ProductDiscountPercent') || 0),
      taxPercent: Number(val(match,'taxPercent','TaxPercent') || 0),
      taxInclusive: Boolean(val(match,'taxInclusive','TaxInclusive'))
    });
    renderCart();
    msg('status', `Added ${val(match,'productName','ProductName')} to cart.`, true);
  }catch(e){
    msg('status', e.message, false);
  }
}

async function loadProducts(){
  try{
    products = await api.get('/api/products?term=' + encodeURIComponent(term.value || ''));
    document.getElementById('products').innerHTML = products.map(p => `<tr class="product"><td><b>${val(p,'productName','ProductName')}</b><br><span class="muted">${val(p,'productCode','ProductCode')} ${val(p,'barcode','Barcode') || ''}</span></td><td>${money(val(p,'salePrice','SalePrice'))}</td><td><span class="stock-badge">${val(p,'stockOnHand','StockOnHand')}</span></td><td><button class="accent" onclick="add(${val(p,'productId','ProductId')})">Add</button></td></tr>`).join('');
  }catch(e){
    msg('status', e.message, false);
  }
}

function add(id){
  const p = products.find(x => Number(val(x,'productId','ProductId')) === Number(id));
  if(!p) return;
  selectedProduct = p;
  modalName.textContent = val(p,'productName','ProductName');
  modalCode.textContent = (val(p,'productCode','ProductCode') || '') + ' ' + (val(p,'barcode','Barcode') || '');
  modalStock.textContent = 'Stock: ' + val(p,'stockOnHand','StockOnHand');
  modalQty.value = 1;
  modalPrice.value = Number(val(p,'salePrice','SalePrice') || 0).toFixed(2);
  modalDiscount.value = Number(val(p,'productDiscountPercent','ProductDiscountPercent') || 0);
  modalError.textContent = '';
  productModal.style.display = 'flex';
  calcModal();
  setTimeout(() => { modalQty.focus(); modalQty.select(); }, 0);
}

function closeProductModal(){
  productModal.style.display = 'none';
  selectedProduct = null;
}

function calculateLine(values){
  const quantity = Math.max(1, Number(values.quantity || 1));
  const price = Math.max(0, Number(values.price || 0));
  const discountPercent = Math.min(100, Math.max(0, Number(values.discountPercent || 0)));
  const taxPercent = Math.max(0, Number(values.taxPercent || 0));
  const gross = roundMoney(quantity * price);
  const discountAmount = roundMoney(gross * discountPercent / 100);
  const taxableAmount = roundMoney(gross - discountAmount);
  const taxAmount = values.taxInclusive
    ? (taxPercent <= 0 ? 0 : roundMoney(taxableAmount - (taxableAmount / (1 + taxPercent / 100))))
    : roundMoney(taxableAmount * taxPercent / 100);
  const total = values.taxInclusive ? taxableAmount : roundMoney(taxableAmount + taxAmount);
  return { gross, discountAmount, taxableAmount, taxAmount, total };
}

function calcModal(){
  if(!selectedProduct) return;
  const taxPercent = Number(val(selectedProduct,'taxPercent','TaxPercent') || 0);
  const taxInclusive = Boolean(val(selectedProduct,'taxInclusive','TaxInclusive'));
  const calculation = calculateLine({
    quantity: modalQty.value,
    price: modalPrice.value,
    discountPercent: modalDiscount.value,
    taxPercent,
    taxInclusive
  });
  modalTotal.textContent = money(calculation.total);
  modalTax.textContent = `Tax ${taxPercent}% ${taxInclusive ? 'inclusive' : 'exclusive'} | Tax amount ${money(calculation.taxAmount)}`;
}

function confirmProductModal(){
  if(!selectedProduct) return;
  const qty = Math.max(1, Number(modalQty.value || 1));
  const availableStock = Number(val(selectedProduct,'stockOnHand','StockOnHand') || 0);
  const pid = Number(val(selectedProduct,'productId','ProductId'));
  const existingLine = cart.find(x => x.productId === pid);
  const finalQuantity = qty + Number(existingLine?.quantity || 0);
  if(finalQuantity > availableStock){
    modalError.textContent = `Quantity is greater than stock. Available: ${availableStock}`;
    return;
  }

  const price = Math.max(0, Number(modalPrice.value || 0));
  const discountAllowed = Boolean(val(selectedProduct,'discountAllowed','DiscountAllowed'));
  const discountPercent = discountAllowed ? Math.min(100, Math.max(0, Number(modalDiscount.value || 0))) : 0;
  const taxPercent = Number(val(selectedProduct,'taxPercent','TaxPercent') || 0);
  const taxInclusive = Boolean(val(selectedProduct,'taxInclusive','TaxInclusive'));

  if(existingLine){
    existingLine.quantity = finalQuantity;
    existingLine.price = price;
    existingLine.discountPercent = discountPercent;
    existingLine.taxPercent = taxPercent;
    existingLine.taxInclusive = taxInclusive;
  }else{
    cart.push({
      productId: pid,
      productName: val(selectedProduct,'productName','ProductName'),
      productCode: val(selectedProduct,'productCode','ProductCode'),
      quantity: qty,
      price,
      discountPercent,
      taxPercent,
      taxInclusive
    });
  }
  closeProductModal();
  renderCart();
}

function lineTotal(line){
  return calculateLine(line).total;
}

function renderCart(){
  const total = roundMoney(cart.reduce((sum, line) => sum + lineTotal(line), 0));
  document.getElementById('cart').innerHTML = cart.length ? cart.map((line, index) => {
    const calculation = calculateLine(line);
    const name = esc(line.productName || '');
    const code = esc(line.productCode || '');
    return `<div class="cart-item">
      <div class="cart-item-main">
        <div class="cart-item-head">
          <b title="${code}">${name}</b>
          <span class="cart-item-total">${money(calculation.total)}</span>
          <button type="button" class="cart-remove-icon" onclick="cart.splice(${index},1);renderCart()" title="Remove" aria-label="Remove">×</button>
        </div>
        <div class="cart-item-fields">
          <input class="cart-cell-input small" type="number" value="${line.quantity}" min="1" title="Qty" onchange="cart[${index}].quantity=Math.max(1,Number(this.value||1));renderCart()">
          <input class="cart-cell-input" type="number" value="${line.price}" min="0" step="0.01" title="Price" onchange="cart[${index}].price=Math.max(0,Number(this.value||0));renderCart()">
          <input class="cart-cell-input small" type="number" value="${line.discountPercent || 0}" min="0" max="100" title="Disc %" onchange="cart[${index}].discountPercent=Math.min(100,Math.max(0,Number(this.value||0)));renderCart()">
          <span class="mini cart-item-tax">Tax ${money(calculation.taxAmount)}</span>
        </div>
      </div>
    </div>`;
  }).join('') : '<p class="muted empty-cart">Cart is empty. Add product to start billing.</p>';
  document.getElementById('total').textContent = money(total);
  document.getElementById('paid').value = total.toFixed(2);
}

async function postSale(){
  try{
    if(!cart.length){ msg('status','Cart is empty.',false); return; }
    const method = payMethod.value || 'Cash';
    if(method === 'Bank'){
      const bank = document.getElementById('bankAccount')?.value || '';
      if(!bank){ msg('status','Select a bank account for Bank payment.',false); return; }
    }
    const bankCode = method === 'Bank' ? (document.getElementById('bankAccount')?.value || '') : '';
    const body = {
      customerId: 0,
      remarks: method === 'Bank' ? `Web POS sale | Bank ${bankCode}` : 'Web POS sale | Cash',
      lines: cart.map(line => ({
        productId: line.productId,
        quantity: line.quantity,
        unitPrice: line.price,
        discountPercent: line.discountPercent || 0
      })),
      payments: [{
        paymentMethodId: method === 'Bank' ? 4 : 1,
        paymentMethodName: method === 'Bank' ? 'Bank Transfer' : 'Cash',
        amount: Number(paid.value || 0),
        referenceNo: bankCode
      }]
    };
    const result = await api.post('/api/pos/sales', body);
    lastSaleId = result.saleId || result.SaleId;
    msg('status', `Posted ${result.invoiceNo || result.InvoiceNo} | Total ${money(result.grandTotal || result.GrandTotal)} | Change ${money(result.changeAmount || result.change || 0)}`, true);
    const postedId = lastSaleId;
    cart = [];
    renderCart();
    await loadProducts();
    if(postedId) await api.openReport('/api/reports/pos-receipt/' + postedId + '/html');
  }catch(e){
    msg('status', e.message, false);
  }
}

async function printLastReceipt(){
  try{
    if(!lastSaleId){ msg('status','No posted receipt available in this session.',false); return; }
    await api.openReport('/api/reports/pos-receipt/' + lastSaleId + '/html');
  }catch(e){
    msg('status', e.message, false);
  }
}

document.addEventListener('keydown', event => {
  if(productModal.style.display !== 'flex') return;
  if(event.key === 'Enter' && !event.repeat){
    event.preventDefault();
    confirmProductModal();
  }else if(event.key === 'Escape'){
    event.preventDefault();
    closeProductModal();
  }
});

init();
