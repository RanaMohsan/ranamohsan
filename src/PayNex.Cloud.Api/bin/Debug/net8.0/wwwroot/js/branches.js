let branches=[];
let allowMultipleBranches=false;
let selectedBranchId=0;

async function loadBranches(){
  try{
    const r=await api.get('/api/branches');
    allowMultipleBranches=!!(r.allowMultipleBranches || r.AllowMultipleBranches);
    branches=r.branches || r.Branches || [];
    document.getElementById('branchDisabled').style.display=allowMultipleBranches?'none':'block';
    document.getElementById('branchWorkspace').style.display=allowMultipleBranches?'grid':'none';
    renderBranches();
    if(allowMultipleBranches && branches.length && !selectedBranchId){ openBranch(branches[0]); }
  }catch(e){ msg('branchMsg',e.message,false); }
}

function renderBranches(){
  const body=document.getElementById('branchRows'); if(!body) return;
  if(!branches.length){ body.innerHTML='<tr><td colspan="5" class="muted">No branches found.</td></tr>'; return; }
  body.innerHTML=branches.map(b=>{
    const id=b.branchId || b.BranchId;
    const code=b.branchCode || b.BranchCode || '';
    const name=b.branchName || b.BranchName || '';
    const addr=b.addressLine || b.AddressLine || '';
    const active=(b.isActive ?? b.IsActive) !== false;
    const main=!!(b.isMainBranch || b.IsMainBranch);
    return `<tr class="${Number(id)===Number(selectedBranchId)?'selected':''}" onclick='openBranchById(${Number(id)})'>
      <td><b>${esc(code)}</b></td><td>${esc(name)}</td><td>${esc(addr)}</td>
      <td><span class="branch-status ${active?'':'off'}">${active?'Active':'Inactive'}</span></td>
      <td>${main?'Yes':'-'}</td>
    </tr>`;
  }).join('');
}
function openBranchById(id){ const b=branches.find(x=>Number(x.branchId||x.BranchId)===Number(id)); if(b) openBranch(b); }
function openBranch(b){
  selectedBranchId=Number(b.branchId || b.BranchId || 0);
  document.getElementById('branchId').value=selectedBranchId||'';
  document.getElementById('branchCode').value=b.branchCode || b.BranchCode || '';
  document.getElementById('branchName').value=b.branchName || b.BranchName || '';
  document.getElementById('branchAddress').value=b.addressLine || b.AddressLine || '';
  document.getElementById('branchActive').checked=(b.isActive ?? b.IsActive) !== false;
  renderBranches();
}
function newBranch(){
  selectedBranchId=0;
  document.getElementById('branchId').value='';
  document.getElementById('branchCode').value='';
  document.getElementById('branchName').value='';
  document.getElementById('branchAddress').value='';
  document.getElementById('branchActive').checked=true;
  renderBranches();
  msg('branchMsg','',true);
}
async function saveBranch(){
  try{
    const body={
      branchId:Number(document.getElementById('branchId').value||0),
      branchCode:document.getElementById('branchCode').value.trim(),
      branchName:document.getElementById('branchName').value.trim(),
      addressLine:document.getElementById('branchAddress').value.trim(),
      isActive:document.getElementById('branchActive').checked
    };
    const r=await api.post('/api/branches',body);
    branches=r.branches || r.Branches || [];
    selectedBranchId=body.branchId || selectedBranchId;
    msg('branchMsg','Branch saved successfully.',true);
    renderBranches();
  }catch(e){ msg('branchMsg',e.message,false); }
}
function esc(s){ return String(s??'').replace(/[&<>\"]/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[m])); }
window.addEventListener('load',loadBranches);
