
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
  const head=document.querySelector('.table thead').innerHTML;
  const body=g('glBody').innerHTML;
  const w=window.open('about:blank','_blank');
  w.document.write(`<!doctype html><html><head><title>G/L Entries</title><style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#172033}.doc{max-width:1200px;margin:auto}.top{display:flex;justify-content:space-between;border-bottom:3px solid #004578;padding-bottom:12px;margin-bottom:16px}.company{color:#004578;font-weight:800;font-size:22px}.muted{color:#667085}button{float:right;background:#004578;color:white;border:0;border-radius:4px;padding:8px 14px}table{width:100%;border-collapse:collapse}th,td{border-bottom:1px solid #d8e0e8;padding:7px 8px;text-align:left;font-size:12px}th{background:#eef4fb;color:#344054;text-transform:uppercase;font-size:10.5px}tbody tr:nth-child(odd){background:#eaf4ff}tbody tr:nth-child(even){background:#f8fbff}@media print{button{display:none}}</style></head><body><button onclick="window.print()">Print</button><div class="doc"><div class="top"><div><div class="company">PayNex Cloud</div><div class="muted">Posted General Ledger Entries</div></div><div><h2>G/L Entries</h2><div class="muted">${g('glFrom').value} to ${g('glTo').value}</div></div></div><table><thead>${head}</thead><tbody>${body}</tbody></table></div></body></html>`);
  w.document.close();
}
init();
