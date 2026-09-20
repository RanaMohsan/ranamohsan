let products = [], cart = [], selectedProduct = null, canOverrideSellingPrice = false;
const v = (o, a, b) => o?.[a] ?? o?.[b] ?? '';

function productIdOf(p){ return Number(v(p,'productId','ProductId') || 0); }
function productNameOf(p){ return v(p,'productName','ProductName') || 'Item'; }
function productCodeOf(p){ return v(p,'productCode','ProductCode') || ''; }
function productHasImage(p){ return Boolean(v(p,'hasImage','HasImage') || v(p,'imagePath','ImagePath')); }
function productPriceOf(p){ return Number(v(p,'salePrice','SalePrice') || 0); }
function productStockOf(p){ return Number(v(p,'stockOnHand','StockOnHand') || 0); }
function productTaxPercentOf(p){ return Number(v(p,'taxPercent','TaxPercent') || 0); }
function productTaxInclusiveOf(p){ return Boolean(v(p,'taxInclusive','TaxInclusive')); }
function round2(n){ return Math.round((Number(n || 0) + Number.EPSILON) * 100) / 100; }
function productImageUrl(p){
  const id = productIdOf(p);
  const path = v(p,'imagePath','ImagePath');
  if(path && /^https?:\/\//i.test(path)) return path;
  if(productHasImage(p) && id) return `/api/products/${id}/image?v=${Date.now()}`;
  return '';
}
function productInitials(name){
  return String(name || 'ITEM').split(/\s+/).filter(Boolean).slice(0,2).map(x=>x[0]).join('').toUpperCase() || 'IT';
}
function esc(s){ return String(s ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }
function lockCustomerToWalkIn(customers){
  const list = customers || [];
  const walkIn = list.find(c => String(v(c,'customerCode','CustomerCode')).toUpperCase() === 'WALKIN')
    || list.find(c => /walk[-\s]?in/i.test(String(v(c,'customerName','CustomerName'))))
    || { customerId: 0, customerCode: 'WALKIN', customerName: 'Walk-in Customer' };
  const id = v(walkIn,'customerId','CustomerId') || 0;
  const text = `${v(walkIn,'customerCode','CustomerCode') || 'WALKIN'} - ${v(walkIn,'customerName','CustomerName') || 'Walk-in Customer'}`;
  customer.dataset.noLookup = '1';
  customer.innerHTML = `<option value="${esc(id)}">${esc(text)}</option>`;
  customer.value = String(id);
  customer.disabled = true;
  customer.title = 'Picture Sales uses Walk-in Customer only.';
}

async function init(){
  try{
    const me = await api.get('/api/me');
    canOverrideSellingPrice = hasPermission(me,'pricing.overrideSellingPrice')
      || hasPermission(me,'pricing.changeProductPrice')
      || !!(me.isPlatformOwner || me.IsPlatformOwner)
      || !!(me.isCompanySuperAdmin || me.IsCompanySuperAdmin);
    const who = document.getElementById('who');
    if(who) who.textContent = `${me.companyName || me.CompanyName} | ${me.displayName || me.DisplayName} | ${me.roleName || me.RoleName}`;
  }catch{ location.href='/login.html'; return; }
  const look = await api.get('/api/lookups');
  lockCustomerToWalkIn(look.customers || []);
  wireLineModalKeys();
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
  renderCart();
  await loadProducts();
}

async function autoAddByBarcode(code){
  if(!code) return;
  try{
    let match = products.find(p => String(v(p,'barcode','Barcode')).trim() === code
      || String(v(p,'productCode','ProductCode')).trim().toLowerCase() === code.toLowerCase());
    if(!match){
      const found = await api.get('/api/products?term=' + encodeURIComponent(code));
      match = (found || []).find(p => String(v(p,'barcode','Barcode')).trim() === code
        || String(v(p,'productCode','ProductCode')).trim().toLowerCase() === code.toLowerCase())
        || (found || [])[0];
      if(found?.length) products = found;
    }
    if(!match){ msg('status', `No item matched barcode ${code}.`, false); return; }
    openLineModal(productIdOf(match));
  }catch(e){ msg('status', e.message, false); }
}

async function loadProducts(){
  products = await api.get('/api/products?term=' + encodeURIComponent(search.value || ''));
  const visibleProducts = products || [];
  cards.classList.toggle('picture-grid-compact', visibleProducts.length > 10);
  cards.innerHTML = visibleProducts.map(p=>{
    const id = productIdOf(p), name = productNameOf(p), img = productImageUrl(p), stock = productStockOf(p), price = productPriceOf(p);
    return `<button type="button" class="product-tile picture-cube" onclick="openLineModal(${id})">
      <div class="product-image-area ${img ? 'has-img' : 'no-img'}">
        ${img ? `<img src="${img}" alt="${esc(name)}" onerror="this.remove();this.parentElement.classList.remove('has-img');this.parentElement.classList.add('no-img');this.parentElement.innerHTML='<span>${productInitials(name)}</span>';">` : `<span>${productInitials(name)}</span>`}
      </div>
      <h2>${esc(name)}</h2>
      <div class="tile-bottom"><span class="tile-price">${money(price)}</span><span class="stock-badge">Stock ${stock}</span></div>
    </button>`;
  }).join('') || '<div class="panel muted">No item found. Add item pictures from Item Master first.</div>';
}

function wireLineModalKeys(){
  [lineQty, linePrice].forEach(el => {
    el.addEventListener('input', calcLineModal);
    el.addEventListener('keydown', ev => {
      if(ev.key === 'Enter'){
        ev.preventDefault();
        confirmLineModal();
      }
      if(ev.key === 'Escape') closeLineModal();
    });
  });
  pictureLineModal.addEventListener('click', ev => { if(ev.target === pictureLineModal) closeLineModal(); });
}

function openLineModal(id){
  const p = products.find(x => productIdOf(x) === Number(id));
  if(!p) return;
  selectedProduct = p;
  lineItemName.textContent = productNameOf(p);
  lineItemCode.textContent = productCodeOf(p);
  const taxText = productTaxPercentOf(p) > 0 ? ` | Tax ${productTaxPercentOf(p)}%${productTaxInclusiveOf(p) ? ' inclusive' : ''}` : ' | Tax 0%';
  lineItemStock.textContent = 'Stock: ' + productStockOf(p) + taxText;
  lineQty.value = 1;
  linePrice.value = productPriceOf(p).toFixed(2);
  linePrice.readOnly = !canOverrideSellingPrice;
  linePrice.title = canOverrideSellingPrice ? 'You can override selling price.' : 'Override Selling Price permission is required to change price.';
  lineError.textContent = '';
  pictureLineModal.style.display = 'flex';
  calcLineModal();
  setTimeout(() => { lineQty.focus(); lineQty.select(); }, 30);
}

function closeLineModal(){
  pictureLineModal.style.display = 'none';
  selectedProduct = null;
}

function calcTax(amountAfterDiscount, taxPercent, taxInclusive){
  const taxable = Math.max(0, Number(amountAfterDiscount || 0));
  const pct = Math.max(0, Number(taxPercent || 0));
  if(pct <= 0) return { tax: 0, total: round2(taxable), taxable: round2(taxable) };
  if(taxInclusive){
    const tax = round2(taxable - (taxable / (1 + pct / 100)));
    return { tax, total: round2(taxable), taxable: round2(taxable - tax) };
  }
  const tax = round2(taxable * pct / 100);
  return { tax, total: round2(taxable + tax), taxable: round2(taxable) };
}

function calcLine(l){
  const qty = Math.max(0, Number(l.quantity || 0));
  const price = Math.max(0, Number(l.price || 0));
  const gross = round2(qty * price);
  const discountPercent = Math.max(0, Number(l.discountPercent || 0));
  const discount = round2(gross * discountPercent / 100);
  const afterDiscount = round2(gross - discount);
  const taxCalc = calcTax(afterDiscount, l.taxPercent, l.taxInclusive);
  return {
    gross,
    discount,
    tax: taxCalc.tax,
    taxable: taxCalc.taxable,
    total: taxCalc.total,
    taxPercent: Number(l.taxPercent || 0),
    taxInclusive: Boolean(l.taxInclusive)
  };
}

function cartSummary(){
  const lines = cart.map(calcLine);
  return {
    subTotal: round2(lines.reduce((s,l)=>s+l.gross,0)),
    discount: round2(lines.reduce((s,l)=>s+l.discount,0)),
    tax: round2(lines.reduce((s,l)=>s+l.tax,0)),
    grandTotal: round2(lines.reduce((s,l)=>s+l.total,0))
  };
}

function calcLineModal(){
  const qty = Math.max(0, Number(lineQty.value || 0));
  const price = canOverrideSellingPrice ? Math.max(0, Number(linePrice.value || 0)) : productPriceOf(selectedProduct);
  const taxPercent = selectedProduct ? productTaxPercentOf(selectedProduct) : 0;
  const taxInclusive = selectedProduct ? productTaxInclusiveOf(selectedProduct) : false;
  const discountPercent = selectedProduct ? Number(v(selectedProduct,'productDiscountPercent','ProductDiscountPercent') || 0) : 0;
  const gross = round2(qty * price);
  const discount = round2(gross * discountPercent / 100);
  const calc = calcTax(gross - discount, taxPercent, taxInclusive);
  lineTotal.textContent = `${money(calc.total)}${taxPercent > 0 ? ` including tax ${money(calc.tax)}` : ''}`;
}

function confirmLineModal(){
  if(!selectedProduct) return;
  const qty = Math.max(0, Number(lineQty.value || 0));
  const price = canOverrideSellingPrice ? Math.max(0, Number(linePrice.value || 0)) : productPriceOf(selectedProduct);
  const stock = productStockOf(selectedProduct);
  if(qty <= 0){ lineError.textContent = 'Quantity must be greater than zero.'; lineQty.focus(); return; }
  if(qty > stock){ lineError.textContent = `Quantity cannot be greater than stock (${stock}).`; lineQty.focus(); return; }
  if(price < 0){ lineError.textContent = 'Price cannot be negative.'; linePrice.focus(); return; }
  const pid = productIdOf(selectedProduct);
  const taxPercent = productTaxPercentOf(selectedProduct);
  const taxInclusive = productTaxInclusiveOf(selectedProduct);
  const existing = cart.find(x => x.productId === pid && x.price === price && x.taxPercent === taxPercent && x.taxInclusive === taxInclusive);
  if(existing) existing.quantity += qty;
  else cart.push({
    productId: pid,
    productCode: productCodeOf(selectedProduct),
    productName: productNameOf(selectedProduct),
    quantity: qty,
    price,
    taxPercent,
    taxInclusive,
    discountPercent: Number(v(selectedProduct,'productDiscountPercent','ProductDiscountPercent') || 0)
  });
  closeLineModal();
  renderCart();
}

function updateCartQty(index, value){
  if(!cart[index]) return;
  cart[index].quantity = Math.max(1, Math.round(Number(value || 1)));
  renderCart();
}

function updateCartPrice(index, value){
  if(!cart[index] || !canOverrideSellingPrice) return;
  cart[index].price = Math.max(0, Number(value || 0));
  renderCart();
}

function renderCart(){
  if(!cart.length){
    cartBox.innerHTML = '<p class="muted empty-cart">Cart is empty.</p>';
    total.textContent = money(0);
    paid.value = '';
    return;
  }
  cartBox.innerHTML = `<table class="table picture-cart-table"><thead><tr><th class="cart-col-name">Item Name</th><th class="cart-col-price">Price</th><th class="cart-col-qty">Qty</th><th>Tax</th><th>Total</th><th></th></tr></thead><tbody>${cart.map((l,i)=>{
    const line = calcLine(l);
    const taxLabel = l.taxPercent > 0
      ? `${money(line.tax)} <span class="mini">${Number(l.taxPercent).toFixed(0)}%</span>`
      : 'Rs. 0.00';
    const priceReadonly = canOverrideSellingPrice ? '' : 'readonly';
    return `<tr class="picture-cart-row">
      <td class="cart-col-name" title="${esc(l.productCode || '')}"><b>${esc(l.productName)}</b></td>
      <td class="cart-col-price"><input class="cart-cell-input" type="number" step="0.01" min="0" ${priceReadonly} value="${Number(l.price || 0).toFixed(2)}" onchange="updateCartPrice(${i}, this.value)" title="${canOverrideSellingPrice ? 'Change price' : 'Price override permission required'}"></td>
      <td class="cart-col-qty"><input class="cart-cell-input small" type="number" step="1" min="1" value="${l.quantity}" onchange="updateCartQty(${i}, this.value)" title="Change quantity"></td>
      <td class="amount-cell tax-cell">${taxLabel}</td>
      <td class="amount-cell"><b>${money(line.total)}</b></td>
      <td class="cart-remove-cell"><button type="button" class="cart-remove-icon" onclick="cart.splice(${i},1);renderCart()" title="Remove" aria-label="Remove">×</button></td>
    </tr>`;
  }).join('')}</tbody></table>`;
  const s = cartSummary();
  total.textContent = money(s.grandTotal);
  paid.value = s.grandTotal.toFixed(2);
  const st = document.getElementById('status');
  if(st) st.textContent = '';
}

async function postSale(){
  try{
    if(!cart.length){ msg('status','Add at least one item.',false); return; }
    const s = cartSummary();
    let paidAmount = Number(paid.value || 0);
    if(paidAmount <= 0) paidAmount = s.grandTotal;
    if(round2(paidAmount) + 0.009 < s.grandTotal){
      paid.value = s.grandTotal.toFixed(2);
      msg('status',`Paid amount was less than grand total. It has been corrected to ${money(s.grandTotal)}.`,false);
      return;
    }
    const method = document.getElementById('payMethod')?.value || 'Cash';
    const bankCode = method === 'Bank' ? (document.getElementById('bankAccount')?.value || '') : '';
    if(method === 'Bank' && !bankCode){ msg('status','Select a bank account for Bank payment.',false); return; }
    const r = await api.post('/api/pos/sales',{
      customerId: 0,
      remarks: method === 'Bank' ? `Picture sale | Bank ${bankCode}` : 'Picture sale | Cash',
      lines: cart.map(l=>({productId:l.productId, quantity:l.quantity, unitPrice:l.price, discountPercent:l.discountPercent || 0})),
      payments: [{
        paymentMethodId: method === 'Bank' ? 4 : 1,
        paymentMethodName: method === 'Bank' ? 'Bank Transfer' : 'Cash',
        amount:round2(paidAmount),
        referenceNo: bankCode
      }]
    });
    msg('status',`Posted ${r.invoiceNo || r.InvoiceNo}`,true);
    cart = [];
    renderCart();
    await loadProducts();
    if(r.saleId || r.SaleId) await api.openReport('/api/reports/pos-receipt/' + (r.saleId || r.SaleId) + '/html?layout=receipt');
  }catch(e){ msg('status',e.message,false); }
}

init();
