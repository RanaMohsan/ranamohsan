let returnLines = [];
function kvTable(id, data){
  const el=document.getElementById(id); if(!el) return;
  const obj=Array.isArray(data)?(data[0]||{}):(data||{});
  const rows=Object.entries(obj).filter(([k])=>!String(k).match(/logoImage|password|token/i)).slice(0,20);
  el.innerHTML=rows.map(([k,v])=>`<tr><th>${k}</th><td>${v??''}</td></tr>`).join('') || '<tr><td class="muted">No data.</td></tr>';
}
function dataTable(headId, bodyId, rows){
  const h=document.getElementById(headId), b=document.getElementById(bodyId); rows=rows||[];
  if(!rows.length){ h.innerHTML=''; b.innerHTML='<tr><td class="muted">No records found.</td></tr>'; return; }
  const keys=Object.keys(rows[0]).filter(k=>!String(k).match(/logoImage|password|token/i)).slice(0,8);
  h.innerHTML='<tr>'+keys.map(k=>`<th>${k}</th>`).join('')+'</tr>';
  b.innerHTML=rows.map(r=>'<tr>'+keys.map(k=>`<td>${r[k]??''}</td>`).join('')+'</tr>').join('');
}
async function init(){ try{ const me=await api.get('/api/me'); who.textContent = `${me.companyName} - ${me.userName}`; const c=await api.get('/api/company'); cname.value=c.companyName||''; caddr.value=c.addressLine||''; cphone.value=c.phoneNo||''; cemail.value=c.email||''; ctax.value=c.taxRegistrationNo||''; kvTable('companyBody', c); await currentShift(); }catch(e){ msg('companyStatus', e.message, false); } }
async function saveCompany(){ try{ const r=await api.put('/api/company',{companyName:cname.value,addressLine:caddr.value,phoneNo:cphone.value,email:cemail.value,taxRegistrationNo:ctax.value}); msg('companyStatus','Company information saved.',true); kvTable('companyBody', r);}catch(e){msg('companyStatus', e.message, false)} }
async function openShift(){ try{ const r=await api.post('/api/shifts/open',{openingCash:Number(openingCash.value||0),terminalId:1}); msg('shiftStatus','Shift opened.',true); kvTable('shiftBody', r); }catch(e){msg('shiftStatus', e.message, false)} }
async function currentShift(){ try{ const r=await api.get('/api/shifts/current'); kvTable('shiftBody', r); }catch(e){msg('shiftStatus', e.message, false)} }
async function closeShift(){ try{ const r=await api.post('/api/shifts/close',{closingCash:Number(closingCash.value||0),remarks:'Closed from cloud modules page'}); msg('shiftStatus','Shift closed.',true); kvTable('shiftBody', r); }catch(e){msg('shiftStatus', e.message, false)} }
async function loadReturnLines(){ try{ returnLines=await api.get('/api/returns/sale-lines?invoiceNo='+encodeURIComponent(invoiceNo.value)); returnLinesBody.innerHTML=(returnLines||[]).map(x=>`<tr><td><input style="width:auto" type="checkbox" data-id="${x.saleLineId}"></td><td>${x.productName}</td><td>${x.soldQuantity}</td><td>${x.availableToReturn}</td><td><input style="max-width:120px" type="number" min="0" max="${x.availableToReturn}" value="${x.availableToReturn}" data-qty="${x.saleLineId}"></td></tr>`).join('') || '<tr><td colspan="5" class="muted">No returnable lines found.</td></tr>'; }catch(e){msg('returnStatus', e.message, false)} }
async function postReturn(){ try{ const lines=[...document.querySelectorAll('[data-id]')].filter(x=>x.checked).map(x=>({saleLineId:Number(x.dataset.id),returnQuantity:Number(document.querySelector(`[data-qty="${x.dataset.id}"]`).value||0)})); const r=await api.post('/api/returns',{originalInvoiceNo:invoiceNo.value,reason:returnReason.value,lines}); msg('returnStatus','Return posted: '+(r.returnNo||''),true);}catch(e){msg('returnStatus', e.message, false)} }
async function loadReport(path){ try{ const r=await api.get(path); dataTable('reportHead','reportBody',Array.isArray(r)?r:[r]); }catch(e){ reportHead.innerHTML=''; reportBody.innerHTML=`<tr><td class="bad">${e.message}</td></tr>`; } }
init();
