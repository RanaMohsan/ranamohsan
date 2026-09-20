
let selectedGl=null;
function g(id){return document.getElementById(id)}
async function init(){
  try{
    const me=await api.get('/api/me');
    g('who').textContent=`${me.companyName || me.CompanyName || 'Company'} | ${me.displayName || me.DisplayName || me.userName || me.UserName || 'User'} | ${me.roleName || me.RoleName || ''}`;
  }catch{location.href='/login.html';return;}
  g('glFrom').value=days(-30);
  g('glTo').value=today();
  await loadGl();
}
async function loadGl(){
  try{
    const rows=await api.get(`/api/accounting/gl-entries?from=${g('glFrom').value}&to=${g('glTo').value}&term=${encodeURIComponent(g('glTerm').value||'')}`);
    window.__glRows=rows||[];
    g('glBody').innerHTML=(rows||[]).map((r,i)=>`<tr><td><input type="radio" name="glsel" onchange="selectGl(${i})"></td><td>${String(r.postingDate || r.PostingDate || '').slice(0,10)}</td><td>${r.accountNo || r.AccountNo} - ${r.accountName || r.AccountName}</td><td>${r.documentType || r.DocumentType}</td><td>${r.documentNo || r.DocumentNo}</td><td>${money(r.debitAmount || r.DebitAmount)}</td><td>${money(r.creditAmount || r.CreditAmount)}</td><td>${r.description || r.Description || ''}</td></tr>`).join('') || '<tr><td colspan="8" class="muted">No G/L entries found.</td></tr>';
    selectedGl=null;
    msg('glStatus','',true);
  }catch(e){msg('glStatus',e.message,false)}
}
function selectGl(i){
  const r=window.__glRows[i];
  selectedGl={documentType:r.documentType || r.DocumentType, documentNo:r.documentNo || r.DocumentNo};
}
async function reverseSelected(){
  if(!selectedGl){msg('glStatus','Select one G/L row first.',false);return}
  try{
    const r=await api.post('/api/accounting/gl-entries/reverse',{...selectedGl,reason:g('reverseReason').value});
    msg('glStatus',(r.message || 'Document reversed.')+' '+(r.reverseDocumentNo || ''),true);
    await loadGl();
  }catch(e){msg('glStatus',e.message,false)}
}
function printGlEntries(){
  const stripFirst=html=>html.replace(/<(th|td)(?:\s[^>]*)?>[\s\S]*?<\/\1>/i,'');
  const head=stripFirst(document.querySelector('.table thead tr').innerHTML);
  const body=[...g('glBody').querySelectorAll('tr')].map(tr=>{
    const cells=[...tr.children].slice(1).map(td=>td.outerHTML).join('');
    return `<tr>${cells}</tr>`;
  }).join('') || '<tr><td colspan="7" class="muted">No G/L entries found.</td></tr>';
  const who=(g('who')&&g('who').textContent)||'';
  const companyName=who.split('|')[0].trim()||'InterNex Cloud';
  try{
    api.openA4Sheet({
      title:'G/L Entries',
      docNo:`${g('glFrom').value} to ${g('glTo').value}`,
      companyName,
      subtitle:'Posted General Ledger Entries',
      tableHtml:`<table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`
    });
  }catch(e){msg('glStatus',e.message,false)}
}
init();
