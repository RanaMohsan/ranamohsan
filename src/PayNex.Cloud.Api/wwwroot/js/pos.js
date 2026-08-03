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
    document.getElementById('who').textContent = `${me.companyName || me.CompanyName} | ${me.displayName || me.DisplayName} | ${me.roleName || me.RoleName}`;
    if(me.postingBlocked || me.PostingBlocked){
      window.__paynexPostingBlocked = true;
      window.__paynexLicenseMessage = me.licenseMessage || me.LicenseMessage || 'License expired. Posting is blocked.';
      msg('status', window.__paynexLicenseMessage, false);
    }
    const look = await api.get('/api/lookups');
    lockCustomerToWalkIn(look.customers || []);
    await PayNexPayment.load();
    PayNexPayment.fillBankSelect(document.getElementById('payBank'));
    PayNexPayment.toggleBankRow('payMethod','bankPickWrap');
    wireBarcodeScan();
    await loadProducts();
    focusScan();
  }catch{
    location.href = '/login.html';
  }
}

function focusScan(){
  const el = document.getElementById('cartBarcode');
  if(el){ el.focus(); el.select(); }
}

function wireBarcodeScan(){
  const el = document.getElementById('cartBarcode');
  if(!el) return;
  el.addEventListener('keydown', async ev => {
    if(ev.key !== 'Enter') return;
    ev.preventDefault();
    const code = String(el.value || '').trim();
    el.value = '';
    if(!code) return;
    await autoAddByBarcode(code);
    focusScan();
  });
}

function productMatchKey(p, code){
  const q = String(code || '').trim().toLowerCase();
  if(!q) return false;
  const barcode = String(val(p,'barcode','Barcode') || '').trim().toLowerCase();
  const productCode = String(val(p,'productCode','ProductCode') || '').trim().toLowerCase();
  return barcode === q || productCode === q;
}

async function findProductByCode(code){
  const q = String(code || '').trim();
  if(!q) return null;
  let p = (products || []).find(x => productMatchKey(x, q));
  if(p) return p;
  try{
    const list = await api.get('/api/products?term=' + encodeURIComponent(q));
    const rows = list || [];
    p = rows.find(x => productMatchKey(x, q)) || null;
    // If API returns a single strong hit with matching barcode/code prefix, still require exact.
    if(!p && rows.length === 1 && productMatchKey(rows[0], q)) p = rows[0];
    if(p && !(products || []).some(x => Number(val(x,'productId','ProductId')) === Number(val(p,'productId','ProductId')))){
      products = [p, ...(products || [])];
    }
    return p;
  }catch{
    return null;
  }
}

function addProductToCart(p, qty = 1){
  const pid = Number(val(p,'productId','ProductId'));
  const availableStock = Number(val(p,'stockOnHand','StockOnHand') || 0);
  const existingLine = cart.find(x => x.productId === pid);
  const finalQuantity = Math.max(1, Number(qty || 1)) + Number(existingLine?.quantity || 0);
  if(finalQuantity > availableStock){
    msg('status', `Stock not enough for ${val(p,'productName','ProductName')}. Available: ${availableStock}`, false);
    return false;
  }
  const price = Math.max(0, Number(val(p,'salePrice','SalePrice') || 0));
  const discountAllowed = Boolean(val(p,'discountAllowed','DiscountAllowed'));
  const discountPercent = discountAllowed ? Math.min(100, Math.max(0, Number(val(p,'productDiscountPercent','ProductDiscountPercent') || 0))) : 0;
  const taxPercent = Number(val(p,'taxPercent','TaxPercent') || 0);
  const taxInclusive = Boolean(val(p,'taxInclusive','TaxInclusive'));
  if(existingLine){
    existingLine.quantity = finalQuantity;
  }else{
    cart.push({
      productId: pid,
      productName: val(p,'productName','ProductName'),
      productCode: val(p,'productCode','ProductCode'),
      barcode: val(p,'barcode','Barcode') || '',
      quantity: Math.max(1, Number(qty || 1)),
      price,
      discountPercent,
      taxPercent,
      taxInclusive
    });
  }
  renderCart();
  return true;
}

async function autoAddByBarcode(code){
  try{
    const p = await findProductByCode(code);
    if(!p){
      msg('status', `No item found for barcode/code: ${code}`, false);
      return;
    }
    if(addProductToCart(p, 1)){
      msg('status', `Added: ${val(p,'productName','ProductName')}`, true);
    }
  }catch(e){
    msg('status', e.message || 'Scan failed.', false);
  }
}

async function loadProducts(){
  try{
    products = await api.get('/api/products?term=' + encodeURIComponent(term.value || ''));
    document.getElementById('products').innerHTML = products.map(p => `<tr class="product"><td><b>${val(p,'productName','ProductName')}</b><br><span class="muted">${val(p,'productCode','ProductCode')} ${val(p,'barcode','Barcode') || ''}</span></td><td class="pos-num">${money(val(p,'salePrice','SalePrice'))}</td><td class="pos-num"><span class="stock-badge">${val(p,'stockOnHand','StockOnHand')}</span></td><td class="pos-action"><button class="accent" onclick="add(${val(p,'productId','ProductId')})">Add</button></td></tr>`).join('');
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
  focusScan();
}

function lineTotal(line){
  return calculateLine(line).total;
}

function renderCart(){
  const total = roundMoney(cart.reduce((sum, line) => sum + lineTotal(line), 0));
  document.getElementById('cart').innerHTML = cart.length ? cart.map((line, index) => {
    const calculation = calculateLine(line);
    return `<div class="cart-item"><div><b>${line.productName}</b><br><span class="muted">${line.productCode || ''}</span><div class="row" style="grid-template-columns:70px 80px 80px;margin-top:4px"><input type="number" value="${line.quantity}" min="1" onchange="cart[${index}].quantity=Math.max(1,Number(this.value||1));renderCart()"><input type="number" value="${line.price}" min="0" onchange="cart[${index}].price=Math.max(0,Number(this.value||0));renderCart()"><input type="number" value="${line.discountPercent || 0}" min="0" max="100" onchange="cart[${index}].discountPercent=Math.min(100,Math.max(0,Number(this.value||0)));renderCart()"></div><span class="mini">Qty • Price • Disc% | Tax ${money(calculation.taxAmount)}</span></div><div class="right"><b>${money(calculation.total)}</b><br><a href="#" onclick="event.preventDefault();cart.splice(${index},1);renderCart()">remove</a></div></div>`;
  }).join('') : '<p class="muted">Cart is empty. Add product to start billing.</p>';
  document.getElementById('total').textContent = money(total);
  document.getElementById('paid').value = total.toFixed(2);
}

async function postSale(){
  try{
    if(window.__paynexPostingBlocked){ msg('status', window.__paynexLicenseMessage || 'License expired. Posting is blocked.', false); return; }
    if(!cart.length){ msg('status','Cart is empty.',false); return; }
    const payment = PayNexPayment.buildPayment('payMethod', 'payBank', Number(paid.value || 0));
    const body = {
      customerId: 0,
      remarks: 'Web POS sale',
      lines: cart.map(line => ({
        productId: line.productId,
        quantity: line.quantity,
        unitPrice: line.price,
        discountPercent: line.discountPercent || 0
      })),
      payments: [payment]
    };
    const result = await api.post('/api/pos/sales', body);
    lastSaleId = result.saleId || result.SaleId;
    msg('status', `Posted ${result.invoiceNo || result.InvoiceNo} | Total ${money(result.grandTotal || result.GrandTotal)} | Change ${money(result.changeAmount || result.change || 0)}`, true);
    const postedId = lastSaleId;
    cart = [];
    renderCart();
    await loadProducts();
    focusScan();
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
