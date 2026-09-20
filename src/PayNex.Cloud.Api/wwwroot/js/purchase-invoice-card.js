(() => {
  const $ = id => document.getElementById(id);
  let invoiceId = Number(new URLSearchParams(location.search).get('id') || 0);
  let products = [];
  let vendors = [];
  let lines = [];
  let postedHeader = null;
  let isReadOnly = false;
  let isPosting = false;
  let dirtyVersion = 0;
  let savedVersion = 0;
  let saveTimer = 0;
  let saveQueue = Promise.resolve(true);

  function esc(value){ return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }
  function num(value){ return Number(value || 0); }
  function roundMoney(value){ return Math.round((num(value) + Number.EPSILON) * 100) / 100; }
  function dateOnly(value){ return value ? String(value).slice(0, 10) : ''; }
  function layout(){ return encodeURIComponent($('printLayout')?.value || 'bc'); }
  function selectedVendor(){ return vendors.find(x => Number(x.vendorId) === Number($('vendor').value)); }

  function calculateLine(line){
    const subTotal = roundMoney(num(line.quantity) * num(line.unitCost));
    const taxAmount = roundMoney(subTotal * num(line.taxPercent) / 100);
    return { subTotal, taxAmount, lineTotal: roundMoney(subTotal + taxAmount) };
  }

  function calculateTotals(){
    if(postedHeader){ return {subTotal:num(postedHeader.subTotal),taxAmount:num(postedHeader.taxAmount),grandTotal:num(postedHeader.grandTotal),paidAmount:num(postedHeader.paidAmount),balanceAmount:num(postedHeader.balanceAmount)}; }
    const totals = lines.reduce((acc, line) => { const calc=calculateLine(line); acc.subTotal+=calc.subTotal; acc.taxAmount+=calc.taxAmount; acc.grandTotal+=calc.lineTotal; return acc; }, {subTotal:0,taxAmount:0,grandTotal:0});
    totals.paidAmount = num($('paidAmount').value);
    totals.balanceAmount = Math.max(0, totals.grandTotal - totals.paidAmount);
    return totals;
  }

  async function init(){
    try{ const me=await api.get('/api/me'); $('who').textContent=`${me.companyName} | ${me.displayName} | ${me.roleName}`; $('factPreparedBy').textContent=me.displayName||'—'; $('factStore').textContent=me.branchName||me.storeName||'Current branch'; }
    catch{ location.href='/login.html'; return; }
    bindEvents();
    if(invoiceId) await loadInvoice(); else await prepareNewInvoice();
  }

  function bindEvents(){
    $('backBtn').addEventListener('click', () => navigateWithSave('/purchases.html'));
    $('newBtn').addEventListener('click', () => navigateWithSave('/purchase-invoice-card.html'));
    $('saveDraftBtn').addEventListener('click', () => saveDraft(true));
    $('postBtn').addEventListener('click', postInvoice);
    $('deleteBtn').addEventListener('click', deleteInvoice);
    $('printBtn').addEventListener('click', printInvoice);
    $('addLineBtn').addEventListener('click', addLine);
    $('product').addEventListener('change', fillProduct);
    $('vendor').addEventListener('change', () => { fillVendor(); markDirty(); });
    $('vendorInvoiceNo').addEventListener('input', markDirty);
    $('invoiceDate').addEventListener('change', markDirty);
    $('paidAmount').addEventListener('input', () => { renderSummary(); markDirty(); });
    $('remarks').addEventListener('input', markDirty);
    window.addEventListener('beforeunload', event => { if(!isReadOnly && dirtyVersion!==savedVersion && (invoiceId>0 || lines.length>0)){ event.preventDefault(); event.returnValue=''; } });
  }

  async function loadLookups(){ const [vendorList,productList]=await Promise.all([api.get('/api/vendors?term='),api.get('/api/products?term=')]); vendors=vendorList||[]; products=productList||[]; }
  function populateLookups(){
    $('vendor').innerHTML='<option value="">Select vendor</option>'+(vendors.length?vendors.map(x=>`<option value="${x.vendorId}">${esc(x.vendorCode)} - ${esc(x.vendorName)}</option>`).join(''):'');
    $('product').innerHTML='<option value="">Select item</option>'+(products.length?products.map(x=>`<option value="${x.productId}">${esc(x.productCode)} - ${esc(x.productName)}</option>`).join(''):'');
  }

  async function prepareNewInvoice(){
    isReadOnly=false; postedHeader=null; $('invoiceDate').value=today(); $('documentNo').textContent='NEW'; $('documentTitle').textContent='New Purchase Invoice'; setOpenStatus('New'); $('printBtn').disabled=true;
    try{ await loadLookups(); populateLookups(); $('vendor').value=''; $('product').value=''; fillVendor(); fillProduct(); renderLines(); dirtyVersion=0; savedVersion=0; msg('status','New purchase invoice is ready. It will save automatically after a line is added.',true); }
    catch(error){ msg('status',error.message,false); }
  }

  async function loadInvoice(){
    try{
      const [header,savedLines]=await Promise.all([api.get(`/api/purchase-invoices/${invoiceId}`),api.get(`/api/purchase-invoices/${invoiceId}/lines`),loadLookups()]);
      const isPosted=String(header.status||'').toLowerCase()==='posted'; postedHeader=isPosted?header:null; isReadOnly=isPosted;
      if(Number(header.vendorId)>0&&!vendors.some(x=>Number(x.vendorId)===Number(header.vendorId))) vendors.unshift({vendorId:header.vendorId,vendorCode:header.vendorCode,vendorName:header.vendorName,mobile:header.mobile,email:header.email,addressLine:header.addressLine});
      populateLookups();
      lines=(savedLines||[]).map(line=>{const product=products.find(x=>Number(x.productId)===Number(line.productId));return {...line,productCode:line.productCode||product?.productCode||'',productName:line.productName||product?.productName||''};});
      $('vendor').value=Number(header.vendorId)>0?String(header.vendorId):''; $('product').value=''; $('vendorInvoiceNo').value=header.vendorInvoiceNo||''; $('invoiceDate').value=dateOnly(header.invoiceDate); $('paidAmount').value=num(header.paidAmount).toFixed(2); $('remarks').value=header.remarks||''; $('documentNo').textContent=header.invoiceNo||`#${invoiceId}`; $('documentTitle').textContent=`${isPosted?'Purchase Invoice':'Open Purchase Invoice'} ${header.invoiceNo||''}`.trim();
      $('factVendorNo').textContent=header.vendorCode||'—'; $('factStore').textContent=[header.storeCode,header.storeName].filter(Boolean).join(' - ')||header.branchCode||'—'; $('factPreparedBy').textContent=header.preparedBy||'—'; $('factPostedAt').textContent=isPosted&&header.postedAt?new Date(header.postedAt).toLocaleString():'—';
      fillVendor(); fillProduct();
      if(isPosted){ $('documentStatus').textContent='Posted'; $('documentStatus').className='bc-status-pill posted'; $('factStatus').textContent='Posted'; setReadOnlyMode(); } else { setOpenStatus('Open'); $('printBtn').disabled=true; }
      dirtyVersion=0; savedVersion=0; renderLines(); msg('status',`Purchase invoice ${header.invoiceNo} opened.`,true);
    }catch(error){ msg('status',error.message,false); $('documentTitle').textContent='Purchase Invoice Not Found'; setReadOnlyMode(); }
  }

  function setOpenStatus(label='Open'){ $('documentStatus').textContent=label; $('documentStatus').className='bc-status-pill draft'; $('factStatus').textContent=label==='New'?'New':'Open'; if($('deleteBtn')) $('deleteBtn').hidden=!invoiceId||isReadOnly; }
  function setReadOnlyMode(){ isReadOnly=true; ['vendor','vendorInvoiceNo','invoiceDate','paidAmount','remarks'].forEach(id=>$(id).disabled=true); $('lineEntrySection').classList.add('read-only'); $('lineEntrySection').querySelectorAll('input,select,button').forEach(el=>el.disabled=true); $('saveDraftBtn').hidden=true; $('postBtn').hidden=true; if($('deleteBtn')) $('deleteBtn').hidden=true; $('printBtn').disabled=false; }
  function fillVendor(){ const vendor=selectedVendor(); if(!vendor){$('vendorInformation').value='';$('factVendorNo').textContent='—';return;} $('vendorInformation').value=[vendor.mobile,vendor.email,vendor.addressLine].filter(Boolean).join(' | '); $('factVendorNo').textContent=vendor.vendorCode||'—'; }
  function fillProduct(){ const product=products.find(x=>Number(x.productId)===Number($('product').value)); if(!product){ $('unitCost').value=''; $('taxPercent').value=''; return; } $('unitCost').value=num(product.purchasePrice).toFixed(2); $('taxPercent').value=num(product.taxPercent).toFixed(2); }

  function addLine(){
    if(isReadOnly)return; const product=products.find(x=>Number(x.productId)===Number($('product').value)); if(!product){msg('status','Select an item first.',false);return;} const quantity=num($('qty').value); if(quantity<=0){msg('status','Quantity must be greater than zero.',false);return;}
    lines.push({productId:product.productId,productCode:product.productCode,productName:product.productName,quantity,unitCost:num($('unitCost').value),taxPercent:num($('taxPercent').value)}); $('qty').value='1'; renderLines(); markDirty(); msg('status',`${product.productName} added. Draft auto-save is running.`,true);
  }

  function renderLines(){
    const body=$('linesBody');
    if(!lines.length){body.innerHTML='<tr><td colspan="9" class="bc-empty-row">No invoice lines.</td></tr>';renderSummary();return;}
    body.innerHTML=lines.map((line,index)=>{
      const calc=postedHeader?{taxAmount:num(line.taxAmount),lineTotal:num(line.lineTotal)}:calculateLine(line);
      const qtyCell=isReadOnly
        ? num(line.quantity).toLocaleString()
        : `<input class="bc-inline-edit" data-qty="${index}" type="number" min="0.001" step="any" value="${num(line.quantity)}" title="Edit quantity">`;
      const costCell=isReadOnly
        ? money(line.unitCost)
        : `<input class="bc-inline-edit" data-cost="${index}" type="number" min="0" step="0.01" value="${num(line.unitCost).toFixed(2)}" title="Edit unit cost">`;
      return `<tr><td>Item</td><td>${esc(line.productCode||'')}</td><td>${esc(line.productName)}</td><td class="number">${qtyCell}</td><td class="number">${costCell}</td><td class="number">${num(line.taxPercent).toFixed(2)}</td><td class="number" data-line-tax="${index}">${money(calc.taxAmount)}</td><td class="number" data-line-total="${index}">${money(calc.lineTotal)}</td><td class="bc-row-action">${isReadOnly?'':`<button type="button" class="bc-icon-button danger" data-remove="${index}" title="Remove line">×</button>`}</td></tr>`;
    }).join('');
    body.querySelectorAll('[data-remove]').forEach(button=>button.addEventListener('click',()=>{lines.splice(Number(button.dataset.remove),1);renderLines();markDirty();}));
    body.querySelectorAll('[data-qty]').forEach(input=>{
      input.addEventListener('input',()=>applyLineEdit(Number(input.dataset.qty),'quantity',input.value));
      input.addEventListener('change',()=>{
        const i=Number(input.dataset.qty);
        if(num(input.value)<=0){ input.value=String(lines[i].quantity||1); applyLineEdit(i,'quantity',input.value); }
      });
    });
    body.querySelectorAll('[data-cost]').forEach(input=>{
      input.addEventListener('input',()=>applyLineEdit(Number(input.dataset.cost),'unitCost',input.value));
      input.addEventListener('change',()=>{
        const i=Number(input.dataset.cost);
        input.value=num(input.value).toFixed(2);
        applyLineEdit(i,'unitCost',input.value);
      });
    });
    renderSummary();
  }

  function applyLineEdit(index, fieldName, rawValue){
    if(isReadOnly || !lines[index]) return;
    const value = num(rawValue);
    if(fieldName === 'quantity' && value <= 0) return;
    if(fieldName === 'unitCost' && value < 0) return;
    lines[index][fieldName] = value;
    const calc = calculateLine(lines[index]);
    const taxCell = $('linesBody').querySelector(`[data-line-tax="${index}"]`);
    const totalCell = $('linesBody').querySelector(`[data-line-total="${index}"]`);
    if(taxCell) taxCell.textContent = money(calc.taxAmount);
    if(totalCell) totalCell.textContent = money(calc.lineTotal);
    renderSummary();
    markDirty();
  }

  function renderSummary(){const totals=calculateTotals();$('summarySubtotal').textContent=money(totals.subTotal);$('summaryTax').textContent=money(totals.taxAmount);$('summaryTotal').textContent=money(totals.grandTotal);$('summaryPaid').textContent=money(totals.paidAmount);$('summaryBalance').textContent=money(totals.balanceAmount);}
  function markDirty(){if(isReadOnly||isPosting)return;dirtyVersion+=1;clearTimeout(saveTimer);saveTimer=setTimeout(()=>saveDraft(false),800);}
  function draftPayload(){return {purchaseInvoiceId:invoiceId,vendorId:Number($('vendor').value||0),vendorInvoiceNo:$('vendorInvoiceNo').value,invoiceDate:$('invoiceDate').value,paidAmount:num($('paidAmount').value),remarks:$('remarks').value,lines:lines.map(line=>({productId:line.productId,quantity:num(line.quantity),unitCost:num(line.unitCost),taxPercent:num(line.taxPercent)}))};}

  function saveDraft(showMessage){
    clearTimeout(saveTimer);
    if(isReadOnly||isPosting) return Promise.resolve(true);
    if(!$('vendor').value && !invoiceId) return Promise.resolve(true);
    if(!$('vendor').value) return Promise.resolve(true);
    if(dirtyVersion===savedVersion && invoiceId>0){
      if(showMessage) msg('status',`Purchase invoice ${$('documentNo').textContent||''} is already saved as Open.`,true);
      return Promise.resolve(true);
    }
    saveQueue=saveQueue.catch(()=>true).then(async()=>{
      const versionBeingSaved=dirtyVersion;
      setOpenStatus('Saving…');
      try{
        const result=await api.post('/api/purchase-invoices/draft',draftPayload());
        invoiceId=Number(result.purchaseInvoiceId||invoiceId||0);
        if(invoiceId){
          history.replaceState(null,'',`/purchase-invoice-card.html?id=${encodeURIComponent(invoiceId)}`);
          $('documentNo').textContent=result.invoiceNo||$('documentNo').textContent;
          $('documentTitle').textContent=`Open Purchase Invoice ${result.invoiceNo||''}`.trim();
        }
        if(dirtyVersion===versionBeingSaved) savedVersion=versionBeingSaved;
        setOpenStatus('Open');
        if(showMessage) msg('status',`Purchase invoice ${result.invoiceNo||''} saved as Open.`,true);
        return true;
      }catch(error){
        setOpenStatus(invoiceId?'Open':'New');
        msg('status',`Auto-save failed: ${error.message}`,false);
        return false;
      }
    });
    return saveQueue;
  }

  async function navigateWithSave(url){
    if(!$('vendor').value && !invoiceId){ location.href=url; return; }
    const saved=await saveDraft(false);
    if(saved) location.href=url;
  }
  async function deleteInvoice(){
    if(isReadOnly){ msg('status','Only Open purchase invoices can be deleted.',false); return; }
    if(!invoiceId){ msg('status','Save the invoice as Open before deleting, or leave without saving.',false); return; }
    const invoiceNo=$('documentNo').textContent||invoiceId;
    if(!confirm(`Delete open purchase invoice ${invoiceNo}? This cannot be undone.`)) return;
    try{
      $('deleteBtn').disabled=true;
      clearTimeout(saveTimer);
      const result=await api.delete('/api/purchase-invoices/'+invoiceId);
      savedVersion=dirtyVersion;
      msg('status',result.message||'Purchase invoice deleted.',true);
      location.href='/purchases.html';
    }catch(error){
      $('deleteBtn').disabled=false;
      msg('status',error.message,false);
    }
  }
  async function postInvoice(){if(isReadOnly||isPosting)return;if(!lines.length){msg('status','Add at least one purchase invoice line.',false);return;}if(!$('vendor').value){const saved=await saveDraft(false);msg('status',saved?'Vendor is required for posting. The invoice has been saved as an Open draft.':'Unable to save the draft. Add at least one invoice line and try again.',saved);return;}clearTimeout(saveTimer);await saveQueue.catch(()=>false);isPosting=true;$('postBtn').disabled=true;try{const result=await api.post('/api/purchases',draftPayload());const postedId=Number(result.purchaseInvoiceId||0);if(!postedId)throw new Error('Invoice was posted but its record ID was not returned.');savedVersion=dirtyVersion;location.href=`/purchase-invoice-card.html?id=${postedId}&posted=1`;}catch(error){isPosting=false;$('postBtn').disabled=false;msg('status',error.message,false);}}
  async function printInvoice(){if(!invoiceId||!isReadOnly){msg('status','Post the purchase invoice before printing.',false);return;}try{await api.openReport(`/api/reports/purchase-invoice/${invoiceId}/html?layout=${layout()}`);}catch(error){msg('status',error.message,false);}}

  init();
})();
