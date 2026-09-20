function selectedLayout(){ return encodeURIComponent(document.getElementById('defaultLayout')?.value || 'bc'); }
function withLayout(url){ return url + (url.includes('?') ? '&' : '?') + 'layout=' + selectedLayout(); }
function fillSelect(el, rows, html, emptyText){ el.innerHTML = (emptyText ? `<option value="">${emptyText}</option>` : '') + (rows || []).map(html).join(''); }

async function init(){
  try{
    const me = await api.get('/api/me');
    who.textContent = `${me.companyName || me.CompanyName} | ${me.displayName || me.DisplayName} | ${me.roleName || me.RoleName}`;
  }catch{ location.href='/login.html'; return; }
  const from = today().slice(0,8) + '01', to = today();
  const [sales,purchases,cpay,vpay,customers,vendors] = await Promise.all([
    api.get(`/api/sales-invoices?from=${from}&to=${to}`),
    api.get(`/api/purchase-invoices?from=${from}&to=${to}`),
    api.get('/api/customer-payments'),
    api.get('/api/vendor-payments?term='),
    api.get('/api/customers?term='),
    api.get('/api/vendors?term=')
  ]);
  fillSelect(formalSalesId, sales, x=>`<option value="${x.salesInvoiceId}">${x.invoiceNo} - ${x.customerName} - ${money(x.grandTotal)}</option>`, 'Select posted sales invoice');
  fillSelect(purchaseId, purchases, x=>`<option value="${x.purchaseInvoiceId}">${x.invoiceNo} - ${x.vendorName} - ${money(x.grandTotal)}</option>`, 'Select posted purchase invoice');
  fillSelect(custPayId, cpay, x=>`<option value="${x.paymentId}">${x.paymentNo} - ${x.customerName} - ${money(x.amount)}</option>`, 'Select customer payment');
  fillSelect(vendPayId, vpay, x=>`<option value="${x.paymentId}">${x.paymentNo} - ${x.vendorName} - ${money(x.amount)}</option>`, 'Select vendor payment');
  customerId.innerHTML = '<option value="0">All Customers</option>' + (customers || []).map(x=>`<option value="${x.customerId}">${x.customerCode} - ${x.customerName}</option>`).join('');
  vendorId.innerHTML = '<option value="0">All Vendors</option>' + (vendors || []).map(x=>`<option value="${x.vendorId}">${x.vendorCode} - ${x.vendorName}</option>`).join('');
}
async function printFormalSales(){ try{ if(!formalSalesId.value) throw new Error('Select posted sales invoice.'); await api.openReport(withLayout('/api/reports/formal-sales-invoice/' + formalSalesId.value + '/html')); }catch(e){ alert(e.message); } }
async function printPurchase(){ try{ if(!purchaseId.value) throw new Error('Select posted purchase invoice.'); await api.openReport(withLayout('/api/reports/purchase-invoice/' + purchaseId.value + '/html')); }catch(e){ alert(e.message); } }
async function printCustomerPayment(){ try{ if(!custPayId.value) throw new Error('Select customer payment.'); await api.openReport(withLayout('/api/reports/customer-payment/' + custPayId.value + '/html')); }catch(e){ alert(e.message); } }
async function printVendorPayment(){ try{ if(!vendPayId.value) throw new Error('Select vendor payment.'); await api.openReport(withLayout('/api/reports/vendor-payment/' + vendPayId.value + '/html')); }catch(e){ alert(e.message); } }
async function printCustomerLedger(){ try{ await api.openReport(withLayout('/api/reports/customer-ledger/html' + (customerId.value && customerId.value !== '0' ? '?customerId=' + customerId.value : ''))); }catch(e){ alert(e.message); } }
async function printVendorLedger(){ try{ await api.openReport(withLayout('/api/reports/vendor-ledger/html' + (vendorId.value && vendorId.value !== '0' ? '?vendorId=' + vendorId.value : ''))); }catch(e){ alert(e.message); } }
async function printPosA4(){ try{ if(!saleId.value) throw new Error('Enter POS Sale ID.'); await api.openReport(withLayout('/api/reports/sales-invoice/' + saleId.value + '/html')); }catch(e){ alert(e.message); } }
async function printPosReceipt(){ try{ if(!receiptSaleId.value) throw new Error('Enter POS Sale ID.'); await api.openReport('/api/reports/pos-receipt/' + receiptSaleId.value + '/html?layout=receipt'); }catch(e){ alert(e.message); } }
async function loadTemplates(){ const list = await api.get('/api/reports/templates'); templates.innerHTML = (list || []).map(x=>`<div><a target="_blank" href="/api/reports/templates/${encodeURIComponent(x)}">${x}</a></div>`).join('') || '<span class="muted">No RDLC templates found.</span>'; }
init();
