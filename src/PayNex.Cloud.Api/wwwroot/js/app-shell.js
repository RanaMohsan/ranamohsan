(function(){
  if(document.body.classList.contains('login-page') || location.pathname.endsWith('/admin.html')) return;

  // Keep the left Actions rail deliberately focused. The full ERP menu remains
  // available as action tiles on Home, while these five shortcuts stay visible.
  const menuGroups = [
    { title:'Navigation', items:[
      ['/workspace.html','HOME','Home'],
      ['/dashboard.html','DASH','Dashboard'],
      ['/pos.html','POS','POS'],
      ['/company.html','COM','Company Information'],
      ['/branches.html','BR','Branches']
    ]}
  ];

  function isActive(path){
    const cleanPath = String(path || '').split('#')[0];
    if(cleanPath === '/swagger') return location.pathname.startsWith('/swagger');
    if(cleanPath === '/sales.html' && location.pathname.endsWith('/sales-invoice-card.html')) return true;
    if(cleanPath === '/purchases.html' && location.pathname.endsWith('/purchase-invoice-card.html')) return true;
    if(cleanPath === '/items.html' && location.pathname.endsWith('/item-card.html')) return true;
    if(cleanPath === '/customers.html' && location.pathname.endsWith('/customer-card.html')) return true;
    if(cleanPath === '/vendors.html' && location.pathname.endsWith('/vendor-card.html')) return true;
    if(cleanPath === '/finance.html' && location.pathname.endsWith('/account-card.html')) return true;
    if(cleanPath === '/platform-portal.html' && (location.pathname.endsWith('/platform-company-card.html') || location.pathname.endsWith('/platform-mobile-app.html'))) return true;
    if(cleanPath === '/configuration-packages.html' && location.pathname.endsWith('/configuration-package-card.html')) return true;
    if(cleanPath === '/expenses.html' && location.pathname.endsWith('/expense-card.html')) return true;
    return location.pathname === cleanPath || location.pathname.endsWith(cleanPath);
  }
  function currentEnvironment(){
    const fromSession = (()=>{ try{ return JSON.parse(localStorage.getItem('paynex_last_user')||'{}').environment; }catch{return '';} })();
    const v = (fromSession || localStorage.getItem('paynex_environment') || 'Production');
    return /^sandbox$/i.test(v) ? 'Sandbox' : 'Production';
  }

  const title = (document.title || 'PayNex Cloud').replace(/^PayNex\s*/i,'').trim() || 'PayNex Cloud';
  const sidebar = document.createElement('aside');
  sidebar.className = 'app-sidebar';
  sidebar.innerHTML = `
    <div class="brand">
      <div class="brand-logo" aria-hidden="true"></div>
      <div><h2>PayNex</h2><p>SaaS ERP</p></div>
    </div>
    <div class="side-user">
      <b id="sideUser">Signed in user</b>
      <span id="sideRole">Company workspace</span>
      <span><i class="shift-dot"></i><span id="sideShift">Operational</span></span>
    </div>
    <nav class="side-menu">
      ${menuGroups.map(g=>`<div class="side-section">${g.title}</div>${g.items.map(m=>`<a href="${m[0]}" class="${isActive(m[0])?'active':''}"><span class="micon">${m[1]}</span><span>${m[2]}</span></a>`).join('')}`).join('')}
    </nav>
    <div class="side-logout"><button class="danger" onclick="api.logout().then(()=>location.href='/login.html')">Logout</button></div>`;

  const header = document.createElement('header');
  header.className = 'app-header';
  header.innerHTML = `
    <div class="header-left">
      <button class="toggle" id="sideToggle" title="Expand / collapse actions">☰</button>
      <div class="page-title">${title}</div>
    </div>
    <div class="command-area">
      <select id="paynexBranchSelector" class="branch-selector" hidden></select>
    </div>
    <div class="header-right">
      <div class="environment-switcher" id="paynexEnvironmentSwitcher">
        <button type="button" class="environment-trigger app-launcher" id="paynexEnvLauncher" aria-haspopup="dialog" aria-expanded="false" aria-controls="paynexEnvMenu" title="Change environment">
          <span class="environment-status-dot" aria-hidden="true"></span>
          <span class="env-badge" id="currentEnvironmentBadge">${currentEnvironment()} SaaS</span>
          <span class="environment-chevron" aria-hidden="true">⌄</span>
        </button>
        <section class="env-menu environment-panel" id="paynexEnvMenu" role="dialog" aria-modal="false" aria-label="Choose environment" aria-hidden="true">
          <div class="environment-panel-head">
            <div>
              <strong>Choose environment</strong>
              <span>Open the live company or the safe testing workspace.</span>
            </div>
            <button type="button" class="environment-close" id="paynexEnvClose" aria-label="Close environment panel">×</button>
          </div>
          <div class="environment-options">
            <button type="button" class="environment-option" data-env="Production">
              <span class="environment-option-icon production" aria-hidden="true">P</span>
              <span class="environment-option-copy"><b>Production</b><small>Live company database for normal business transactions.</small></span>
              <span class="environment-current" aria-hidden="true">✓</span>
            </button>
            <button type="button" class="environment-option" data-env="Sandbox">
              <span class="environment-option-icon sandbox" aria-hidden="true">S</span>
              <span class="environment-option-copy"><b>Sandbox</b><small>Safe environment for testing, setup and staff training.</small></span>
              <span class="environment-current" aria-hidden="true">✓</span>
            </button>
          </div>
          <div class="environment-panel-note">Switching refreshes the ERP workspace and applies the selected environment to your session.</div>
        </section>
      </div>
      <div class="app-user-menu" id="paynexUserMenu">
        <button type="button" class="app-user-trigger" id="paynexUserTrigger" aria-haspopup="dialog" aria-expanded="false" aria-controls="paynexUserPanel" title="Signed-in user">
          <span class="app-user-avatar">
            <img id="paynexUserAvatarImage" alt="" hidden>
            <span id="paynexUserAvatarFallback">U</span>
          </span>
          <span class="app-user-copy"><b id="paynexHeaderUserName">User</b><small id="paynexHeaderUserId">ID —</small></span>
          <span class="app-user-chevron" aria-hidden="true">⌄</span>
        </button>
        <section class="app-user-panel" id="paynexUserPanel" role="dialog" aria-modal="false" aria-label="User profile" aria-hidden="true">
          <div class="app-user-panel-head">
            <span class="app-user-avatar large"><img id="paynexUserPanelImage" alt="" hidden><span id="paynexUserPanelFallback">U</span></span>
            <div><strong id="paynexPanelUserName">Signed-in user</strong><span id="paynexPanelUserMeta">User ID</span></div>
          </div>
          <div class="app-user-panel-details">
            <div><span>Company</span><b id="paynexPanelCompany">—</b></div>
            <div><span>Role</span><b id="paynexPanelRole">—</b></div>
            <div><span>Email</span><b id="paynexPanelEmail">—</b></div>
          </div>
          <div class="app-user-panel-actions">
            <a id="paynexMyUserCardLink" href="/users.html">Open user card</a>
            <button type="button" id="paynexHeaderLogout">Sign out</button>
          </div>
        </section>
      </div>
    </div>`;

  document.body.prepend(header);
  document.body.prepend(sidebar);
  document.body.classList.add('shell-ready');
  document.getElementById('sideToggle')?.addEventListener('click',()=>document.body.classList.toggle('sidebar-collapsed'));

  const launcher = document.getElementById('paynexEnvLauncher');
  const envMenu = document.getElementById('paynexEnvMenu');
  const envClose = document.getElementById('paynexEnvClose');
  const environmentSwitcher = document.getElementById('paynexEnvironmentSwitcher');
  const userMenu = document.getElementById('paynexUserMenu');
  const userTrigger = document.getElementById('paynexUserTrigger');
  const userPanel = document.getElementById('paynexUserPanel');

  function setUserPanelOpen(open){
    if(!userPanel || !userTrigger) return;
    userPanel.classList.toggle('open',!!open);
    userPanel.setAttribute('aria-hidden',open?'false':'true');
    userTrigger.setAttribute('aria-expanded',open?'true':'false');
  }

  let currentAvatarObjectUrl='';
  let avatarRefreshSeq=0;
  async function refreshCurrentUserAvatar(event){
    const seq=++avatarRefreshSeq;
    const pairs=[
      [document.getElementById('paynexUserAvatarImage'),document.getElementById('paynexUserAvatarFallback')],
      [document.getElementById('paynexUserPanelImage'),document.getElementById('paynexUserPanelFallback')]
    ];
    const apiClient=typeof api!=='undefined'?api:window.api;
    if(!apiClient?.fetchWithRefresh) return;

    const showFallback=()=>{
      pairs.forEach(([img,fallback])=>{
        if(!img||!fallback) return;
        img.hidden=true;
        fallback.hidden=false;
      });
    };
    const showPhoto=(objectUrl)=>{
      pairs.forEach(([img,fallback])=>{
        if(!img||!fallback) return;
        img.onload=()=>{ img.hidden=false; fallback.hidden=true; };
        img.onerror=()=>{ img.hidden=true; fallback.hidden=false; };
        img.src=objectUrl;
      });
    };

    showFallback();
    try{
      const me=(()=>{try{return JSON.parse(localStorage.getItem('paynex_last_user')||'{}');}catch{return {};}})();
      const detail=event?.detail||{};
      let userId=Number(detail.userId||me.userId||me.UserId||0);
      if(!userId){
        try{
          const live=await apiClient.get('/api/me');
          localStorage.setItem('paynex_last_user', JSON.stringify(live||{}));
          userId=Number(live.userId||live.UserId||0);
        }catch{}
      }
      const stamp=Date.now();
      const candidates=[];
      if(detail.photoUrl) candidates.push(String(detail.photoUrl));
      candidates.push('/api/me/photo?v='+stamp);
      if(userId>0) candidates.push(`/api/users/${userId}/photo?v=${stamp}`);

      for(const candidate of candidates){
        if(seq!==avatarRefreshSeq) return;
        const response=await apiClient.fetchWithRefresh(candidate,{method:'GET',cache:'no-store'});
        if(!response.ok) continue;
        const blob=await response.blob();
        if(!blob.size || blob.size<32) continue;
        const type=String(blob.type||'');
        if(type && !type.startsWith('image/') && type!=='application/octet-stream') continue;
        if(seq!==avatarRefreshSeq) return;
        const objectUrl=URL.createObjectURL(blob);
        const previous=currentAvatarObjectUrl;
        currentAvatarObjectUrl=objectUrl;
        showPhoto(objectUrl);
        if(previous) URL.revokeObjectURL(previous);
        return;
      }
    }catch{}
  }

  function applyCachedUserIdentity(){
    try{
      const me=JSON.parse(localStorage.getItem('paynex_last_user')||'{}');
      const user=me.displayName||me.DisplayName||me.userName||me.UserName;
      if(!user)return;
      const userId=me.userId||me.UserId||0;
      const userName=me.userName||me.UserName||'';
      const initials=String(user).trim().split(/\s+/).slice(0,2).map(x=>x.charAt(0)).join('').toUpperCase()||'U';
      const setText=(id,value)=>{const el=document.getElementById(id);if(el)el.textContent=value||'—'};
      setText('paynexHeaderUserName',user);
      setText('paynexHeaderUserId',`ID ${userId||'—'}${userName?` • ${userName}`:''}`);
      setText('paynexPanelUserName',user);
      setText('paynexPanelUserMeta',`User ID ${userId||'—'}${userName?` • ${userName}`:''}`);
      setText('paynexPanelCompany',me.companyName||me.CompanyName||'');
      setText('paynexPanelRole',me.roleName||me.RoleName||'');
      setText('paynexPanelEmail',me.email||me.Email||'');
      ['paynexUserAvatarFallback','paynexUserPanelFallback'].forEach(id=>setText(id,initials));
      const cardLink=document.getElementById('paynexMyUserCardLink');
      if(cardLink)cardLink.href=userId?`/user-card.html?id=${userId}`:'/users.html';
    }catch{}
  }
  applyCachedUserIdentity();
  refreshCurrentUserAvatar();

  function syncEnvironmentPanel(environment){
    const selected = /^sandbox$/i.test(environment || '') ? 'Sandbox' : 'Production';
    envMenu?.querySelectorAll('.environment-option[data-env]').forEach(option=>{
      const active = option.dataset.env === selected;
      option.classList.toggle('selected', active);
      option.setAttribute('aria-pressed', active ? 'true' : 'false');
    });
    environmentSwitcher?.classList.toggle('sandbox-active', selected === 'Sandbox');
  }

  function setEnvironmentPanelOpen(open){
    if(!envMenu || !launcher || launcher.disabled) return;
    envMenu.classList.toggle('open', !!open);
    envMenu.setAttribute('aria-hidden', open ? 'false' : 'true');
    launcher.setAttribute('aria-expanded', open ? 'true' : 'false');
    if(open) envMenu.querySelector('.environment-option.selected')?.focus();
  }

  syncEnvironmentPanel(currentEnvironment());
  launcher?.addEventListener('click',(event)=>{
    event.stopPropagation();
    setEnvironmentPanelOpen(!envMenu?.classList.contains('open'));
  });
  envClose?.addEventListener('click',()=>setEnvironmentPanelOpen(false));
  userTrigger?.addEventListener('click',(event)=>{
    event.stopPropagation();
    setEnvironmentPanelOpen(false);
    setUserPanelOpen(!userPanel?.classList.contains('open'));
  });
  document.getElementById('paynexHeaderLogout')?.addEventListener('click',()=>api.logout().then(()=>location.href='/login.html'));
  document.addEventListener('click',(event)=>{
    if(!environmentSwitcher?.contains(event.target)) setEnvironmentPanelOpen(false);
    if(!userMenu?.contains(event.target)) setUserPanelOpen(false);
  });
  document.addEventListener('keydown',(event)=>{
    if(event.key === 'Escape'){ setEnvironmentPanelOpen(false); setUserPanelOpen(false); }
  });
  window.addEventListener('paynex-profile-updated',refreshCurrentUserAvatar);

  envMenu?.querySelectorAll('button[data-env]').forEach(btn=>{
    btn.addEventListener('click', async ()=>{
      const env = btn.dataset.env || 'Production';
      const previousEnvironment = currentEnvironment();
      if(env === previousEnvironment){
        setEnvironmentPanelOpen(false);
        return;
      }

      const optionButtons = [...envMenu.querySelectorAll('button[data-env]')];
      optionButtons.forEach(option=>option.disabled=true);
      launcher?.classList.add('is-switching');

      try{
        localStorage.setItem('paynex_environment', env);
        const badge=document.getElementById('currentEnvironmentBadge');
        if(badge) badge.textContent = env + ' SaaS';
        syncEnvironmentPanel(env);
        setEnvironmentPanelOpen(false);

        if(window.api){
          const r = await api.post('/api/auth/environment',{environment:env});
          api.setToken(r.token);
          localStorage.setItem('paynex_last_user', JSON.stringify(r.user || {}));
          location.href = '/workspace.html';
        }else if(!location.pathname.endsWith('/admin.html')){
          location.href = '/login.html?environment=' + encodeURIComponent(env);
        }
      }catch(ex){
        localStorage.setItem('paynex_environment', previousEnvironment);
        const restored = previousEnvironment;
        const badge=document.getElementById('currentEnvironmentBadge');
        if(badge) badge.textContent = restored + ' SaaS';
        syncEnvironmentPanel(restored);
        alert(ex.message || ex);
      }finally{
        optionButtons.forEach(option=>option.disabled=false);
        launcher?.classList.remove('is-switching');
      }
    });
  });


  function ensureBranchMenuVisible(){
    const menu=document.querySelector('.side-menu');
    if(!menu || menu.querySelector('a[href="/branches.html"]')) return;
    const link=document.createElement('a');
    link.href='/branches.html';
    link.id='branchMenuLink';
    link.className=isActive('/branches.html')?'active':'';
    link.innerHTML='<span class="micon">BR</span><span>Branches</span>';
    menu.appendChild(link);
  }

  function applyPermissionVisibility(me){
    try{
      const userLink=[...document.querySelectorAll('.side-menu a')].find(a=>a.getAttribute('href')==='/users.html');
      if(userLink && !(hasPermission(me,'users.createUser')||hasPermission(me,'users.editUser')||hasPermission(me,'users.assignPermissions'))) userLink.remove();
      const companyLink=[...document.querySelectorAll('.side-menu a')].find(a=>a.getAttribute('href')==='/company.html');
      if(companyLink && !hasPermission(me,'system.companySettings')) companyLink.remove();
      const taxLink=[...document.querySelectorAll('.side-menu a')].find(a=>a.getAttribute('href')==='/tax-discount.html');
      if(taxLink && !(hasPermission(me,'pricing.changeProductDiscount')||hasPermission(me,'pricing.changeProductPrice'))) taxLink.remove();
      const packageLink=[...document.querySelectorAll('.side-menu a')].find(a=>a.getAttribute('href')==='/configuration-packages.html');
      if(packageLink && !(hasPermission(me,'configurationPackages.view')||hasPermission(me,'configurationPackages.manage')||hasPermission(me,'configurationPackages.export')||hasPermission(me,'configurationPackages.import')||hasPermission(me,'configurationPackages.apply'))) packageLink.remove();
      const expenseLink=[...document.querySelectorAll('.side-menu a')].find(a=>a.getAttribute('href')==='/expenses.html');
      if(expenseLink && !(hasPermission(me,'expenses.view')||hasPermission(me,'expenses.create')||hasPermission(me,'finance.createExpense')||hasPermission(me,'expenses.edit')||hasPermission(me,'expenses.delete'))) expenseLink.remove();
      const expenseReportLink=[...document.querySelectorAll('.side-menu a')].find(a=>a.getAttribute('href')==='/expense-report.html');
      if(expenseReportLink && !(hasPermission(me,'expenses.viewReport')||hasPermission(me,'expenses.printReport'))) expenseReportLink.remove();
    }catch{}
  }

  async function loadBranchContext(me){
    if(!window.api) return;
    try{
      const ctx=await api.get('/api/branches/context');
      const allow=!!(ctx.allowMultipleBranches || ctx.AllowMultipleBranches);
      const branches=ctx.branches || ctx.Branches || [];
      const selector=document.getElementById('paynexBranchSelector');
      const sideRole=document.getElementById('sideRole');
      const currentId=String(ctx.currentBranchId || ctx.CurrentBranchId || me.branchId || me.BranchId || me.storeId || me.StoreId || '');
      const currentName=ctx.currentBranchName || ctx.CurrentBranchName || me.branchName || me.BranchName || me.storeName || me.StoreName || '';
      if(allow) ensureBranchMenuVisible();
      if(sideRole && currentName && allow){
        const text=sideRole.textContent || '';
        if(!text.includes('Branch:')) sideRole.textContent = text ? `${text} • Branch: ${currentName}` : `Branch: ${currentName}`;
      }
      if(!selector) return;
      if(allow && branches.length>1){
        selector.hidden=false;
        selector.innerHTML=branches.map(b=>{
          const id=b.branchId || b.BranchId || b.storeId || b.StoreId;
          const code=b.branchCode || b.BranchCode || b.storeCode || b.StoreCode || '';
          const name=b.branchName || b.BranchName || b.storeName || b.StoreName || '';
          return `<option value="${id}">${code} - ${name}</option>`;
        }).join('');
        selector.value=currentId;
        selector.onchange=async ()=>{
          try{
            const r=await api.post('/api/branches/switch',{branchId:Number(selector.value)});
            api.setToken(r.token);
            localStorage.setItem('paynex_last_user', JSON.stringify(r.user || {}));
            location.reload();
          }catch(ex){ alert(ex.message || ex); }
        };
      }else{
        selector.hidden=true;
      }
    }catch{}
  }

  try{
    if(window.api){
      api.get('/api/me').then(me=>{
        localStorage.setItem('paynex_last_user', JSON.stringify(me || {}));
        if(me.environment) localStorage.setItem('paynex_environment', me.environment);
        const su=document.getElementById('sideUser'), sr=document.getElementById('sideRole'), badge=document.getElementById('currentEnvironmentBadge');
        const company = me.companyName || me.CompanyName || '';
        const user = me.displayName || me.DisplayName || me.userName || me.UserName || 'User';
        const role = me.roleName || me.RoleName || '';
        const env = me.environment || me.Environment || currentEnvironment();
        const isOwner=!!(me.isPlatformOwner||me.IsPlatformOwner);
        const userId=me.userId||me.UserId||0;
        const userName=me.userName||me.UserName||'';
        const email=me.email||me.Email||'';
        const initials=String(user||userName||'U').trim().split(/\s+/).slice(0,2).map(x=>x.charAt(0)).join('').toUpperCase()||'U';
        const setText=(id,value)=>{const el=document.getElementById(id);if(el)el.textContent=value||'—'};
        setText('paynexHeaderUserName',user);
        setText('paynexHeaderUserId',`ID ${userId}${userName?` • ${userName}`:''}`);
        setText('paynexPanelUserName',user);
        setText('paynexPanelUserMeta',`User ID ${userId}${userName?` • ${userName}`:''}`);
        setText('paynexPanelCompany',company);
        setText('paynexPanelRole',role);
        setText('paynexPanelEmail',email);
        ['paynexUserAvatarFallback','paynexUserPanelFallback'].forEach(id=>setText(id,initials));
        const cardLink=document.getElementById('paynexMyUserCardLink');
        if(cardLink) cardLink.href=userId?`/user-card.html?id=${userId}`:'/users.html';
        refreshCurrentUserAvatar();
        if(su) su.textContent = user;
        if(sr) sr.textContent = isOwner ? 'Platform Super Admin • PayNex Cloud ERP' : ([role,company].filter(Boolean).join(' • ') || 'Company workspace');
        if(badge) badge.textContent = isOwner ? 'PayNex Owner' : (env + ' SaaS');
        if(!isOwner) syncEnvironmentPanel(env);
        if(isOwner){
          const menu=document.querySelector('.side-menu');
          const platformOnly = (me.companyCode || me.CompanyCode || '') === 'PAYNEX' || !(me.databaseName || me.DatabaseName);
          if(menu && !document.getElementById('ownerPortalMenuLink')){
            const active = isActive('/platform-portal.html') ? 'active' : '';
            const emailActive = isActive('/owner-email-security.html') ? 'active' : '';
            menu.insertAdjacentHTML('afterbegin', `<div class="side-section owner-section">Owner</div><a id="ownerPortalMenuLink" href="/platform-portal.html" class="${active}"><span class="micon">PN</span><span>Owner Portal</span></a><a id="ownerEmailSecurityMenuLink" href="/owner-email-security.html" class="${emailActive}"><span class="micon">OTP</span><span>Email & OTP Setup</span></a>`);
          }
          const envLauncher=document.getElementById('paynexEnvLauncher');
          const envMenu=document.getElementById('paynexEnvMenu');
          const environmentSwitcher=document.getElementById('paynexEnvironmentSwitcher');
          const branch=document.getElementById('paynexBranchSelector');
          if(envLauncher) envLauncher.title='PayNex Owner / Environment';
          if(platformOnly){
            setEnvironmentPanelOpen(false);
            if(envLauncher){ envLauncher.disabled=true; envLauncher.setAttribute('aria-disabled','true'); }
            if(environmentSwitcher) environmentSwitcher.classList.add('owner-environment-locked');
            if(envMenu) envMenu.setAttribute('aria-hidden','true');
            if(branch){ branch.hidden=true; const wrap=branch.nextElementSibling; if(wrap && wrap.classList.contains('lookup-wrap')) wrap.style.display='none'; }
            const ownerPortalPath = location.pathname.endsWith('/platform-portal.html')
              || location.pathname.endsWith('/platform-company-card.html')
              || location.pathname.endsWith('/platform-mobile-app.html')
              || location.pathname.endsWith('/owner-email-security.html');
            if(!ownerPortalPath && !location.pathname.endsWith('/login.html') && !location.pathname.endsWith('/admin.html')){
              location.href='/platform-portal.html';
              return;
            }
          }
        }
        applyPermissionVisibility(me);
        const ownerPlatformOnly = !!(me.isPlatformOwner||me.IsPlatformOwner) && (((me.companyCode || me.CompanyCode || '') === 'PAYNEX') || !(me.databaseName || me.DatabaseName));
        if(!ownerPlatformOnly) loadBranchContext(me);
      }).catch(()=>{});
    }
  }catch{}

  function ensureLookupModal(){
    let root=document.getElementById('bcLookupBackdrop');
    if(root) return root;
    root=document.createElement('div');
    root.id='bcLookupBackdrop'; root.className='bc-lookup-backdrop';
    root.innerHTML=`<div class="bc-lookup-modal"><div class="bc-lookup-head"><h2 id="bcLookupTitle">Lookup</h2><button class="bc-lookup-close" id="bcLookupClose">×</button></div><div class="bc-lookup-search"><input id="bcLookupSearch" placeholder="Type to search"><button class="secondary" id="bcLookupRefresh">Search</button></div><div class="bc-lookup-list"><table class="bc-lookup-table"><thead id="bcLookupHead"></thead><tbody id="bcLookupBody"></tbody></table></div></div>`;
    document.body.appendChild(root);
    root.querySelector('#bcLookupClose').onclick=()=>root.style.display='none';
    root.addEventListener('click',e=>{ if(e.target===root) root.style.display='none'; });
    return root;
  }

  window.openBcLookup = async function(options){
    const root=ensureLookupModal();
    const title=root.querySelector('#bcLookupTitle'), search=root.querySelector('#bcLookupSearch'), refresh=root.querySelector('#bcLookupRefresh'), head=root.querySelector('#bcLookupHead'), body=root.querySelector('#bcLookupBody');
    title.textContent=options.title||'Lookup'; search.value=''; root.style.display='flex'; search.focus();
    let rows=[];
    const cols=options.columns||[];
    head.innerHTML='<tr>'+cols.map(c=>`<th>${c.label||c.key}</th>`).join('')+'</tr>';
    async function load(){
      body.innerHTML='<tr><td colspan="99" class="muted">Loading...</td></tr>';
      rows=await options.provider(search.value||'');
      if(!rows || !rows.length){ body.innerHTML='<tr><td colspan="99" class="muted">No records found</td></tr>'; return; }
      body.innerHTML=rows.map((r,i)=>`<tr data-i="${i}">${cols.map(c=>`<td>${(r[c.key]??r[c.key?.charAt(0).toLowerCase()+c.key?.slice(1)]??'')}</td>`).join('')}</tr>`).join('');
      body.querySelectorAll('tr[data-i]').forEach(tr=>tr.onclick=()=>{ const r=rows[Number(tr.dataset.i)]; options.onPick && options.onPick(r); root.style.display='none'; });
    }
    refresh.onclick=load;
    search.onkeydown=e=>{ if(e.key==='Enter') load(); };
    await load();
  };

  function selectText(select){
    const opt=select.options[select.selectedIndex];
    return opt ? opt.textContent.trim() : '';
  }
  function enhanceSelect(select){
    if(select.id==='paynexBranchSelector' || select.dataset.noLookup==='1' || select.dataset.lookupReady==='1' || select.multiple) return;
    if(select.closest('.login-card')) return;
    if(select.closest('.admin-card-form')) return;
    select.dataset.lookupReady='1';
    const wrap=document.createElement('div'); wrap.className='lookup-wrap';
    const display=document.createElement('input'); display.className='lookup-display'; display.readOnly=true; display.placeholder=select.getAttribute('placeholder') || 'Select...';
    const btn=document.createElement('button'); btn.type='button'; btn.className='lookup-button'; btn.title='Open lookup'; btn.textContent='';
    wrap.appendChild(display); wrap.appendChild(btn);
    select.style.display='none'; select.parentNode.insertBefore(wrap, select.nextSibling);
    const refresh=()=>{ display.value=selectText(select); display.title=display.value; };
    select.addEventListener('change',refresh);
    new MutationObserver(refresh).observe(select,{childList:true,subtree:true,attributes:true});
    btn.onclick=()=>{
      const rows=[...select.options].map((o,idx)=>({idx, value:o.value, text:o.textContent.trim()}));
      openBcLookup({title:select.dataset.lookupTitle||'Lookup', columns:[{key:'text',label:'Description'},{key:'value',label:'Code/ID'}], provider:async term=>rows.filter(r=>!term || r.text.toLowerCase().includes(term.toLowerCase()) || String(r.value).toLowerCase().includes(term.toLowerCase())), onPick:r=>{select.value=r.value; select.dispatchEvent(new Event('change',{bubbles:true})); refresh();}});
    };
    display.onclick=btn.onclick;
    refresh();
  }
  function enhanceAllSelects(){ document.querySelectorAll('select').forEach(enhanceSelect); }
  setTimeout(enhanceAllSelects,50); setTimeout(enhanceAllSelects,500); setInterval(enhanceAllSelects,1800);

  setTimeout(()=>{
    document.querySelectorAll('button').forEach(btn=>{
      if(btn.classList.contains('toggle')||btn.classList.contains('lookup-button')||btn.classList.contains('bc-lookup-close')||btn.classList.contains('app-launcher')||btn.classList.contains('environment-close')||btn.closest('.env-menu')) return;
      const t=(btn.textContent||'').toLowerCase().trim();
      if(btn.classList.contains('secondary')||btn.classList.contains('success')||btn.classList.contains('danger')||btn.classList.contains('accent')||btn.classList.contains('warning')) return;
      if(/save|post|pay|confirm|open shift|register|create|add|entry|login/.test(t)) btn.classList.add('success');
      else if(/remove|delete|reverse|return|cash out|close shift|restore|clear/.test(t)) btn.classList.add('danger');
      else if(/search|load|refresh|reload|cancel|new|print|recall|hold|open/.test(t)) btn.classList.add('secondary');
      else btn.classList.add('accent');
    });
  },100);

  // Load the shared Business Central-style list filtering engine once. It scans
  // existing tables and observes dynamically rendered list rows/tables, so every
  // list page receives the same column-filter experience without page-specific code.
  if(!window.PayNexListFilters && !document.querySelector('script[data-paynex-list-filters]')){
    const listFilters=document.createElement('script');
    listFilters.src='/js/bc-list-filter.js?v=bc-column-filter-20260721';
    listFilters.async=false;
    listFilters.dataset.paynexListFilters='1';
    document.head.appendChild(listFilters);
  }
})();
