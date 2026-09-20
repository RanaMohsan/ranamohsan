let tenants = [];
let selectedTenantCode = '';

async function superAdminLogin(){
  try{
    const r=await api.post('/api/admin/auth/login',{companyCode:val('superCompany'),userName:val('superUser'),password:val('superPass')});
    api.setAdminToken(r.token);
    msg('loginStatus','Super Admin login successful.',true);
    await loadTenants();
  }catch(e){msg('loginStatus',e.message,false)}
}
function superAdminLogout(){
  api.clearAdmin();
  tenants=[]; selectedTenantCode='';
  renderTenantList();
  document.getElementById('tenantCard').innerHTML='<div class="empty-card-state"><h2>Company Card</h2><p class="muted">Login first, then select a company.</p></div>';
  msg('loginStatus','Super Admin logged out.',true);
}
function toggleCreatePanel(){ document.getElementById('createPanel')?.classList.toggle('collapsed'); }

async function createTenant(){
  try{
    const adminEmail=val('adminEmail') || val('ownerEmail');
    const ownerEmail=val('ownerEmail');
    const isGmail=v=>{ const x=String(v||'').trim().toLowerCase(); return x.includes('@') && (x.endsWith('@gmail.com') || x.endsWith('@googlemail.com')); };
    if(!isGmail(adminEmail)){ msg('status','First administrator must use a real Gmail address (@gmail.com).',false); return; }
    if(ownerEmail && !isGmail(ownerEmail)){ msg('status','Owner email must be a real Gmail address (@gmail.com).',false); return; }
    const body={
      companyName:val('companyName'),
      ownerName:val('ownerName'),
      ownerEmail,
      ownerMobile:val('ownerMobile'),
      subscriptionPlan:val('subscriptionPlan') || 'Standard',
      maxBranches:num('branches'),
      allowMultipleBranches:checked('allowMultipleBranches'),
      maxUsers:num('users'),
      maxCounters:num('counters'),
      adminUserName:adminEmail,
      adminPassword:val('adminPass'),
      adminEmail,
      companyStartDate:dateOrNull('companyStartDate'),
      licenseExpiryDate:dateOrNull('licenseExpiryDate'),
      allowSandbox:checked('allowSandbox'),
      createSandbox:checked('createSandboxNow')
    };
    const r=await api.post('/api/admin/tenants',body);
    msg('status',r.message || `Client activated. Company Code: ${r.companyCode}. The first administrator's login details were sent by email; OTP is required on first login.`,true);
    await loadTenants();
    selectedTenantCode = r.companyCode;
    openTenantCard(r.companyCode);
  }catch(e){msg('status',e.message,false)}
}

async function loadTenants(){
  try{
    tenants=await api.get('/api/admin/tenants');
    renderTenantList();
    if(tenants.length && !selectedTenantCode) selectedTenantCode = tenants[0].companyCode;
    if(selectedTenantCode) openTenantCard(selectedTenantCode, false);
  }catch(e){msg('status',e.message,false); msg('loginStatus',e.message,false)}
}

function renderTenantList(){
  const root=document.getElementById('tenantList');
  if(!root) return;
  const term=(document.getElementById('tenantSearch')?.value||'').toLowerCase();
  const rows=tenants.filter(x=>{
    const text=[x.companyCode,x.companyName,x.ownerName,x.ownerEmail,x.ownerMobile,x.databaseName,x.productionDatabaseName,x.sandboxDatabaseName,x.status,x.licenseStatus,x.allowMultipleBranches?'multi branch':'single branch'].join(' ').toLowerCase();
    return !term || text.includes(term);
  });
  if(!rows.length){
    root.innerHTML='<div class="empty-card-state"><p class="muted">No companies found.</p></div>';
    return;
  }
  root.innerHTML=`
    <div class="admin-client-table-wrap">
      <table class="admin-client-table">
        <thead>
          <tr>
            <th>Company</th>
            <th>Company Code</th>
            <th>Owner</th>
            <th>Status</th>
            <th>License</th>
            <th>Sandbox</th>
            <th>Branches</th>
            <th>Start Date</th>
            <th>Expiry Date</th>
          </tr>
        </thead>
        <tbody>
          ${rows.map(x=>{
            const sandboxReady = !!(x.allowSandbox && x.sandboxDatabaseName);
            const selected = x.companyCode===selectedTenantCode ? 'selected' : '';
            return `<tr class="${selected}" onclick="openTenantCard('${escAttr(x.companyCode)}')" title="Open company card">
              <td><b>${esc(x.companyName||'')}</b><small>${esc(x.ownerEmail||'')}</small></td>
              <td><span class="code-chip">${esc(x.companyCode||'')}</span></td>
              <td>${esc(x.ownerName||'-')}<small>${esc(x.ownerMobile||'')}</small></td>
              <td><span class="status-chip ${String(x.status||'Active').toLowerCase()==='active'?'ok':'off'}">${esc(x.status||'Active')}</span></td>
              <td><span class="status-chip ${String(x.licenseStatus||'Active').toLowerCase()==='active'?'ok':'warn'}">${esc(x.licenseStatus||'Active')}</span></td>
              <td><span class="status-chip ${sandboxReady?'ok':'off'}">${sandboxReady?'Ready':'Not Created'}</span></td>
              <td><span class="status-chip ${x.allowMultipleBranches?'ok':'off'}">${x.allowMultipleBranches?'Multi':'Single'}</span><small>${x.maxBranches||1} branch limit</small></td>
              <td>${fmtDate(x.companyStartDate||x.createdAt)||'-'}</td>
              <td>${fmtDate(x.licenseExpiryDate||x.expiryDate)||'-'}</td>
            </tr>`;
          }).join('')}
        </tbody>
      </table>
    </div>`;
}

async function openTenantCard(code, mark=true){
  const tenant = tenants.find(x=>x.companyCode===code) || await api.get('/api/admin/tenants/'+encodeURIComponent(code));
  selectedTenantCode = tenant.companyCode;
  if(mark) renderTenantList();
  const sandboxReady = !!(tenant.allowSandbox && tenant.sandboxDatabaseName);
  document.getElementById('tenantCard').innerHTML=`
    <div class="admin-card-head">
      <div>
        <h2>${esc(tenant.companyName)}</h2>
        <p class="muted"><b>${esc(tenant.companyCode)}</b> • ${esc(tenant.subscriptionPlan||'Standard')} • ${esc(tenant.status||'Active')}</p>
      </div>
      <div class="admin-card-actions">
        <button class="secondary" onclick="copyLogin('${escAttr(tenant.companyCode)}')">Copy Login</button>
        <button class="secondary" onclick="openEnvironmentLogin('${escAttr(tenant.companyCode)}','Production')">Open Production</button>
        <button class="secondary" ${sandboxReady?'':'disabled'} onclick="openEnvironmentLogin('${escAttr(tenant.companyCode)}','Sandbox')">Open Sandbox</button>
      </div>
    </div>

    <div class="admin-card-summary">
      <div><span>Production DB</span><b>${esc(tenant.productionDatabaseName||tenant.databaseName)}</b></div>
      <div><span>Sandbox DB</span><b>${esc(tenant.sandboxDatabaseName || 'Not created')}</b></div>
      <div><span>Sandbox</span><b>${sandboxReady?'Ready':'Not Ready'}</b></div>
      <div><span>Branches</span><b>${tenant.allowMultipleBranches?'Multiple Enabled':'Single Branch'}</b></div>
      <div><span>Last Login</span><b>${fmtDate(tenant.lastLoginAt)}</b></div>
    </div>

    <div class="admin-card-form">
      <div class="form-field"><label>Company Name</label><input id="cardCompanyName" value="${escAttr(tenant.companyName)}"></div>
      <div class="form-field"><label>Owner Name</label><input id="cardOwnerName" value="${escAttr(tenant.ownerName||'')}"></div>
      <div class="form-field"><label>Owner Email</label><input id="cardOwnerEmail" value="${escAttr(tenant.ownerEmail||'')}"></div>
      <div class="form-field"><label>Owner Mobile</label><input id="cardOwnerMobile" value="${escAttr(tenant.ownerMobile||'')}"></div>

      <div class="form-field"><label>Company Start Date</label><input id="cardCompanyStartDate" type="date" value="${dateInput(tenant.companyStartDate||tenant.createdAt)}"></div>
      <div class="form-field"><label>License Expiry Date</label><input id="cardLicenseExpiryDate" type="date" value="${dateInput(tenant.licenseExpiryDate||tenant.expiryDate)}"></div>
      <div class="form-field"><label>Renewal Date</label><input id="cardRenewalDate" type="date" value="${dateInput(tenant.renewalDate)}"></div>
      <div class="form-field"><label>Plan</label><select id="cardSubscriptionPlan" data-no-lookup="1"><option ${sel(tenant.subscriptionPlan,'Standard')}>Standard</option><option ${sel(tenant.subscriptionPlan,'Premium')}>Premium</option><option ${sel(tenant.subscriptionPlan,'Enterprise')}>Enterprise</option></select></div>

      <div class="form-field"><label>Status</label><select id="cardStatus" data-no-lookup="1"><option ${sel(tenant.status,'Active')}>Active</option><option ${sel(tenant.status,'Inactive')}>Inactive</option></select></div>
      <div class="form-field"><label>License Status</label><select id="cardLicenseStatus" data-no-lookup="1"><option ${sel(tenant.licenseStatus,'Active')}>Active</option><option ${sel(tenant.licenseStatus,'Trial')}>Trial</option><option ${sel(tenant.licenseStatus,'Suspended')}>Suspended</option><option ${sel(tenant.licenseStatus,'Expired')}>Expired</option></select></div>
      <label class="check-field"><input id="cardAllowSandbox" type="checkbox" ${tenant.allowSandbox?'checked':''}> Allow Sandbox</label>
      <label class="check-field"><input id="cardAllowMultipleBranches" type="checkbox" ${tenant.allowMultipleBranches?'checked':''}> Allow Multiple Branches</label>
      <div class="form-field"><label>Max Branches</label><input id="cardMaxBranches" type="number" value="${tenant.maxBranches||1}"></div>
      <div class="form-field"><label>Sandbox Created</label><input readonly value="${fmtDate(tenant.sandboxCreatedAt)||'Not created'}"></div>
    </div>

    <div class="actions admin-card-actions-bottom">
      <button class="success" onclick="saveTenantCard('${escAttr(tenant.companyCode)}')">Save Company Card</button>
      <button class="secondary" onclick="createSandbox('${escAttr(tenant.companyCode)}')">Create / Repair Sandbox</button>
      <button class="danger" onclick="setTenantStatus('${escAttr(tenant.companyCode)}','Inactive','Suspended')">Deactivate</button>
      <button class="success" onclick="setTenantStatus('${escAttr(tenant.companyCode)}','Active','Active')">Activate</button>
    </div>
    <p id="cardStatusMsg"></p>`;
}

async function saveTenantCard(code){
  try{
    const body={
      companyName:val('cardCompanyName'),
      ownerName:val('cardOwnerName'),
      ownerEmail:val('cardOwnerEmail'),
      ownerMobile:val('cardOwnerMobile'),
      subscriptionPlan:val('cardSubscriptionPlan'),
      status:val('cardStatus'),
      licenseStatus:val('cardLicenseStatus'),
      companyStartDate:dateOrNull('cardCompanyStartDate'),
      licenseExpiryDate:dateOrNull('cardLicenseExpiryDate'),
      renewalDate:dateOrNull('cardRenewalDate'),
      allowSandbox:checked('cardAllowSandbox'),
      allowMultipleBranches:checked('cardAllowMultipleBranches'),
      maxBranches:num('cardMaxBranches'),
      notes:'Updated from Super Admin company card'
    };
    const updated=await api.put('/api/admin/tenants/'+encodeURIComponent(code), body);
    msg('cardStatusMsg','Company card saved.',true);
    const i=tenants.findIndex(x=>x.companyCode===code); if(i>=0) tenants[i]=updated;
    renderTenantList();
  }catch(e){msg('cardStatusMsg',e.message,false)}
}

async function createSandbox(code){
  try{
    const r=await api.post('/api/admin/tenants/'+encodeURIComponent(code)+'/sandbox',{setAsDefaultEnvironment:false});
    const updated=r.tenant || await api.get('/api/admin/tenants/'+encodeURIComponent(code));
    const i=tenants.findIndex(x=>x.companyCode===code); if(i>=0) tenants[i]=updated;
    msg('cardStatusMsg','Sandbox database created/verified successfully.',true);
    renderTenantList();
    openTenantCard(code,false);
  }catch(e){msg('cardStatusMsg',e.message,false)}
}

async function setTenantStatus(code,status,licenseStatus){
  try{
    await api.post(`/api/admin/tenants/${encodeURIComponent(code)}/status`,{status,licenseStatus,notes:'Updated from Super Admin company card'});
    msg('cardStatusMsg',`${code} updated: ${status} / ${licenseStatus}`,true);
    await loadTenants();
    openTenantCard(code,false);
  }catch(e){msg('cardStatusMsg',e.message,false)}
}
function copyLogin(code){
  const text=`Company Code: ${code}\nProduction Username: admin\nProduction Password: Admin@123\nSandbox Username: admin\nSandbox Password: Admin@123\nURL: http://localhost:5000/login.html`;
  navigator.clipboard?.writeText(text);
  msg('cardStatusMsg','Login details copied for '+code,true);
}
function openEnvironmentLogin(code, environment){
  localStorage.setItem('paynex_environment', environment);
  window.open(`/login.html?company=${encodeURIComponent(code)}&environment=${encodeURIComponent(environment)}`,'_blank');
}
function fmtDate(v){ if(!v) return ''; const d=new Date(v); return isNaN(d)?v:d.toLocaleDateString(); }
function dateInput(v){ if(!v) return ''; const d=new Date(v); if(isNaN(d)) return String(v).slice(0,10); return d.toISOString().slice(0,10); }
function val(id){const el=document.getElementById(id); return el ? String(el.value||'').trim() : '';}
function num(id){return Number(val(id)||0)}
function checked(id){return !!document.getElementById(id)?.checked}
function dateOrNull(id){const v=val(id); return v || null;}
function sel(current,value){ return String(current||'').toLowerCase()===String(value).toLowerCase() ? 'selected' : ''; }
function esc(s){ return String(s??'').replace(/[&<>"]/g,m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[m])); }
function escAttr(s){ return esc(s).replace(/'/g,'&#39;'); }
window.addEventListener('load',()=>{
  const start=document.getElementById('companyStartDate'); if(start && !start.value) start.value=today();
  const exp=document.getElementById('licenseExpiryDate'); if(exp && !exp.value) exp.value=days(30);
  if(api.adminToken) loadTenants(); else msg('status','Login as Super Admin first.',false);
});
