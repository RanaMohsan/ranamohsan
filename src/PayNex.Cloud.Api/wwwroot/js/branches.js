let branches=[];
let allowMultipleBranches=false;
let selectedBranchId=0;
let counters=[];
let maxCounters=0;
let sessionStoreId=0;

async function loadBranches(){
  try{
    const me=await api.get('/api/me');
    sessionStoreId=Number(me.storeId||me.StoreId||me.branchId||me.BranchId||0);
    const r=await api.get('/api/branches');
    allowMultipleBranches=!!(r.allowMultipleBranches || r.AllowMultipleBranches);
    branches=r.branches || r.Branches || [];
    document.getElementById('branchDisabled').style.display=allowMultipleBranches?'none':'block';
    document.getElementById('branchWorkspace').style.display=allowMultipleBranches?'grid':'none';
    renderBranches();
    if(allowMultipleBranches && branches.length){
      if(!selectedBranchId) openBranch(branches[0]);
      else {
        const b=branches.find(x=>Number(x.branchId||x.BranchId)===Number(selectedBranchId));
        if(b) openBranch(b); else openBranch(branches[0]);
      }
    }else{
      selectedBranchId=sessionStoreId;
      await loadCounters();
    }
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
async function openBranch(b){
  selectedBranchId=Number(b.branchId || b.BranchId || 0);
  document.getElementById('branchId').value=selectedBranchId||'';
  document.getElementById('branchCode').value=b.branchCode || b.BranchCode || '';
  document.getElementById('branchName').value=b.branchName || b.BranchName || '';
  document.getElementById('branchAddress').value=b.addressLine || b.AddressLine || '';
  document.getElementById('branchActive').checked=(b.isActive ?? b.IsActive) !== false;
  renderBranches();
  await loadCounters();
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
    await loadCounters();
  }catch(e){ msg('branchMsg',e.message,false); }
}

async function loadCounters(){
  const storeId=selectedBranchId || sessionStoreId;
  if(!storeId){ document.getElementById('counterRows').innerHTML='<tr><td colspan="3" class="muted">Select a branch first.</td></tr>'; return; }
  try{
    const r=await api.get(`/api/counters?storeId=${storeId}`);
    counters=r.counters || r.Counters || [];
    maxCounters=Number(r.maxCounters || r.MaxCounters || 0);
    document.getElementById('counterHint').textContent=`POS counters for branch #${storeId}. ${maxCounters?`License max: ${maxCounters}. `:''}Each counter is a POS terminal.`;
    renderCounters();
  }catch(e){ msg('counterMsg',e.message,false); }
}
function renderCounters(){
  const body=document.getElementById('counterRows');
  if(!counters.length){ body.innerHTML='<tr><td colspan="3" class="muted">No counters yet. Add Counter 01.</td></tr>'; return; }
  const selected=Number(document.getElementById('terminalId').value||0);
  body.innerHTML=counters.map(c=>{
    const id=c.terminalId||c.TerminalId;
    const active=(c.isActive??c.IsActive)!==false;
    return `<tr class="${Number(id)===selected?'selected':''}" onclick="editCounter(${Number(id)})">
      <td><b>${esc(c.terminalCode||c.TerminalCode)}</b></td>
      <td>${esc(c.terminalName||c.TerminalName)}</td>
      <td><span class="branch-status ${active?'':'off'}">${active?'Active':'Inactive'}</span></td>
    </tr>`;
  }).join('');
}
function editCounter(id){
  const c=counters.find(x=>Number(x.terminalId||x.TerminalId)===Number(id));
  if(!c) return;
  document.getElementById('terminalId').value=id;
  document.getElementById('terminalCode').value=c.terminalCode||c.TerminalCode||'';
  document.getElementById('terminalName').value=c.terminalName||c.TerminalName||'';
  document.getElementById('terminalActive').checked=(c.isActive??c.IsActive)!==false;
  renderCounters();
}
function newCounter(){
  document.getElementById('terminalId').value='0';
  const next=counters.length+1;
  document.getElementById('terminalCode').value=`COUNTER-${String(next).padStart(2,'0')}`;
  document.getElementById('terminalName').value=`Counter ${String(next).padStart(2,'0')}`;
  document.getElementById('terminalActive').checked=true;
  renderCounters();
  msg('counterMsg','',true);
}
async function saveCounter(){
  try{
    const storeId=selectedBranchId || sessionStoreId;
    const body={
      terminalId:Number(document.getElementById('terminalId').value||0),
      storeId,
      terminalCode:document.getElementById('terminalCode').value.trim(),
      terminalName:document.getElementById('terminalName').value.trim(),
      isActive:document.getElementById('terminalActive').checked
    };
    const r=await api.post('/api/counters', body);
    counters=r.counters || r.Counters || [];
    msg('counterMsg', r.message || 'Counter saved.', true);
    newCounter();
    renderCounters();
    await loadCounters();
  }catch(e){ msg('counterMsg', e.message, false); }
}

function esc(s){ return String(s??'').replace(/[&<>\"]/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[m])); }
window.addEventListener('load',loadBranches);
