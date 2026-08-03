let catalog = [];

const esc = s => String(s ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
const v = (o, a, b) => o?.[a] ?? o?.[b] ?? '';

function methodClass(method){
  const m = String(method || '').toUpperCase();
  if(m === 'GET') return 'get';
  if(m === 'POST') return 'post';
  if(m === 'PUT') return 'put';
  if(m === 'DELETE') return 'delete';
  return 'other';
}

function filtered(){
  const q = String(apiSearch.value || '').trim().toLowerCase();
  const group = groupFilter.value || '';
  const method = methodFilter.value || '';
  return catalog.filter(row => {
    const m = String(v(row,'method','Method') || '');
    const path = String(v(row,'path','Path') || '');
    const g = String(v(row,'group','Group') || '');
    if(group && g !== group) return false;
    if(method && m.toUpperCase() !== method) return false;
    if(!q) return true;
    return (`${m} ${path} ${g}`).toLowerCase().includes(q);
  });
}

function render(){
  const rows = filtered();
  apiCountChip.textContent = `${rows.length} / ${catalog.length} endpoints`;
  if(!rows.length){
    apiGroups.innerHTML = '<p class="muted">No APIs match the current filter.</p>';
    return;
  }
  const byGroup = new Map();
  rows.forEach(row => {
    const g = String(v(row,'group','Group') || 'Other');
    if(!byGroup.has(g)) byGroup.set(g, []);
    byGroup.get(g).push(row);
  });
  apiGroups.innerHTML = [...byGroup.entries()].map(([group, list]) => `
    <section class="api-group-card">
      <div class="api-group-head">
        <h3>${esc(group)}</h3>
        <span class="lookup-chip">${list.length}</span>
      </div>
      <table class="table striped-table list-table api-catalog-table">
        <thead><tr><th style="width:90px">Method</th><th>Path</th><th style="width:120px">Auth</th></tr></thead>
        <tbody>${list.map(row => {
          const method = String(v(row,'method','Method') || 'ANY').toUpperCase();
          const path = String(v(row,'path','Path') || '');
          const auth = v(row,'authRequired','AuthRequired') !== false;
          return `<tr>
            <td><span class="api-method ${methodClass(method)}">${esc(method)}</span></td>
            <td><code class="api-path">${esc(path)}</code></td>
            <td>${auth ? '<span class="api-auth yes">Required</span>' : '<span class="api-auth no">Public / Admin</span>'}</td>
          </tr>`;
        }).join('')}</tbody>
      </table>
    </section>`).join('');
}

function fillGroupFilter(){
  const groups = [...new Set(catalog.map(r => String(v(r,'group','Group') || 'Other')))].sort((a,b)=>a.localeCompare(b));
  const current = groupFilter.value;
  groupFilter.innerHTML = '<option value="">All groups</option>' + groups.map(g => `<option value="${esc(g)}">${esc(g)}</option>`).join('');
  if(groups.includes(current)) groupFilter.value = current;
}

async function loadCatalog(){
  try{
    const data = await api.get('/api/platform/api-catalog');
    catalog = data.endpoints || data.Endpoints || [];
    fillGroupFilter();
    render();
    msg('status', `Loaded ${catalog.length} PayNex APIs.`, true);
  }catch(e){
    catalog = [];
    apiGroups.innerHTML = '';
    apiCountChip.textContent = '0 endpoints';
    msg('status', e.message || 'Unable to load API catalog. Platform Owner login is required.', false);
  }
}

async function copyAll(){
  const rows = filtered();
  const text = rows.map(r => `${String(v(r,'method','Method')).toUpperCase()} ${v(r,'path','Path')}`).join('\n');
  try{
    if(navigator.clipboard?.writeText) await navigator.clipboard.writeText(text);
    else{
      const ta = document.createElement('textarea');
      ta.value = text; document.body.appendChild(ta); ta.select(); document.execCommand('copy'); ta.remove();
    }
    msg('status', `Copied ${rows.length} endpoints.`, true);
  }catch(e){ msg('status', e.message || 'Copy failed.', false); }
}

async function init(){
  try{
    const me = await api.get('/api/me');
    const isOwner = !!(me.isPlatformOwner || me.IsPlatformOwner);
    who.textContent = `${me.companyName || me.CompanyName || 'PayNex'} | ${me.displayName || me.DisplayName} | ${me.roleName || me.RoleName}`;
    if(!isOwner){
      msg('status', 'This API page is available only for Platform Owner.', false);
      apiGroups.innerHTML = '<p class="muted">Access denied. Sign in as PayNex Platform Owner to view the full API catalog.</p>';
      return;
    }
    apiSearch.addEventListener('input', render);
    groupFilter.addEventListener('change', render);
    methodFilter.addEventListener('change', render);
    await loadCatalog();
  }catch{
    location.href = '/login.html?returnUrl=' + encodeURIComponent('/api.html');
  }
}

init();
