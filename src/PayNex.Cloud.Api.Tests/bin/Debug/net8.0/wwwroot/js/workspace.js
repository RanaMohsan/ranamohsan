const foModules = [
  ['/shift-management.html','cash','Shift'],
  ['/pos.html','cash','Counter Sales / POS Billing'],
  ['/pos-cashier.html','cash','Cashier POS'],
  ['/picture-sales.html','product','Counter Sale'],
  ['/items.html','product','Item Master'],
  ['/customers.html','self','Customer Master'],
  ['/sales.html','invoice','Sales Invoices'],
  ['/posted-sales-invoices.html','invoice','Posted Sales Invoices'],
  ['/sales-return-orders.html','release','Sales Return Orders'],
  ['/customer-ledger.html','ledger','Customer Ledger Entry'],
  ['/customer-payment.html','cash','Customer Payment'],
  ['/vendors.html','people','Vendor Master'],
  ['/purchases.html','expense','Purchase Invoices'],
  ['/posted-purchase-invoices.html','expense','Posted Purchase Invoices'],
  ['/vendor-ledger.html','ledger','Vendor Ledger Entry'],
  ['/vendor-payment.html','cash','Vendor Payment'],
  ['/gl-entries.html','ledger','G/L Entries'],
  ['/gl-setup.html','ledger','G/L Account Setup'],
  ['/tax-setup.html','period','Tax Setup'],
  ['/tax-discount.html','price','Tax & Discount'],
  ['/currency-setup.html','bank','Currency Setup'],
  ['/finance.html#coa','ledger','Chart of Accounts'],
  ['/inventory.html','asset','Inventory Assets'],
  ['/accounting-reports.html','analysis','Accounting Reports'],
  ['/expense-report.html','analysis','Expense Management Report'],
  ['/reports.html','journal','Document Print Center'],
  ['/bank-accounts.html','bank','Bank Accounts'],
  ['/finance.html#budget','budget','Budget Planning'],
  ['/dashboard.html','cash','Cash Overview'],
  ['/retail-products.html','retail','Retail Products'],
  ['/product-variants.html','variant','Product Variants'],
  ['/released-products.html','release','Released Products'],
  ['/expenses.html','expense','Expense Management'],
  ['/configuration-packages.html','process','Package Configuration'],
  ['/company.html','self','Company Information'],
  ['/users.html','recruit','Users']
];
let workspaceUser = null;

const iconSvg = {
  bank:'<svg viewBox="0 0 24 24"><path d="M3 10h18L12 4 3 10Zm3 2v6M10 12v6M14 12v6M18 12v6M4 20h16"/></svg>',
  benefits:'<svg viewBox="0 0 24 24"><path d="M8 7h8v12H8zM6 9h12M9 7V5h6v2M9 13l2 2 4-5"/></svg>',
  budget:'<svg viewBox="0 0 24 24"><path d="M7 5h10v14H7zM9 9h6M9 13h2M13 13h2M9 16h2M13 16h2"/></svg>',
  process:'<svg viewBox="0 0 24 24"><path d="M12 7a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm-7 13a7 7 0 0 1 14 0M17 11l3 3-3 3M4 14h16"/></svg>',
  cash:'<svg viewBox="0 0 24 24"><path d="M4 7h16v10H4zM7 12h.01M17 12h.01M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z"/></svg>',
  ledger:'<svg viewBox="0 0 24 24"><path d="M6 4h12v16H6zM9 8h6M9 12h6M9 16h4"/></svg>',
  cost:'<svg viewBox="0 0 24 24"><path d="M5 17 17 5M7 7h.01M17 17h.01M8 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0Zm12 10a2 2 0 1 1-4 0 2 2 0 0 1 4 0Z"/></svg>',
  analysis:'<svg viewBox="0 0 24 24"><path d="M4 18h16M6 15l4-4 3 3 5-7M6 15v3M10 11v7M13 14v4M18 7v11"/></svg>',
  control:'<svg viewBox="0 0 24 24"><path d="M5 12h14M8 7h8M8 17h8M12 4v16"/></svg>',
  credit:'<svg viewBox="0 0 24 24"><path d="M3 7h18v10H3zM3 10h18M7 15h4M16 15h2"/></svg>',
  self:'<svg viewBox="0 0 24 24"><path d="M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm-7 8a7 7 0 0 1 14 0"/></svg>',
  expense:'<svg viewBox="0 0 24 24"><path d="M6 4h12v16H6zM9 8h6M9 12h6M9 16h2M15 16h.01"/></svg>',
  period:'<svg viewBox="0 0 24 24"><path d="M5 5h14v14H5zM5 9h14M9 3v4M15 3v4M9 13h2M13 13h2M9 16h2"/></svg>',
  asset:'<svg viewBox="0 0 24 24"><path d="m12 3 8 4-8 4-8-4 8-4Zm-8 8 8 4 8-4M4 15l8 4 8-4"/></svg>',
  journal:'<svg viewBox="0 0 24 24"><path d="M7 4h10v16H7zM10 8h4M10 12h4M10 16h2"/></svg>',
  invoice:'<svg viewBox="0 0 24 24"><path d="M7 3h10v18l-2-1-2 1-2-1-2 1-2-1V3Zm3 6h4M10 13h4M10 16h2"/></svg>',
  payroll:'<svg viewBox="0 0 24 24"><path d="M4 7h16v10H4zM7 12h3M14 12h3M12 17v3M8 21h8"/></svg>',
  people:'<svg viewBox="0 0 24 24"><path d="M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm-5 9a5 5 0 0 1 10 0M17 11a3 3 0 1 0 0-6M15 15a5 5 0 0 1 5 5"/></svg>',
  price:'<svg viewBox="0 0 24 24"><path d="M4 5h8l8 8-7 7-8-8V5Zm4 4h.01"/></svg>',
  product:'<svg viewBox="0 0 24 24"><path d="m12 3 8 4v10l-8 4-8-4V7l8-4Zm0 8 8-4M12 11 4 7M12 11v10"/></svg>',
  variant:'<svg viewBox="0 0 24 24"><path d="M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z"/></svg>',
  recruit:'<svg viewBox="0 0 24 24"><path d="M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm-7 8a7 7 0 0 1 14 0M19 8h3M20.5 6.5v3"/></svg>',
  release:'<svg viewBox="0 0 24 24"><path d="M6 4h12v16H6zM9 8h6M9 12h6M9 16h3M16 16l3 3M19 16l-3 3"/></svg>',
  reserve:'<svg viewBox="0 0 24 24"><path d="M4 7h16v12H4zM8 7V5h8v2M8 12h8M8 16h5"/></svg>',
  resource:'<svg viewBox="0 0 24 24"><path d="M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0-5v3M12 18v3M3 12h3M18 12h3M5 5l2 2M17 17l2 2M19 5l-2 2M7 17l-2 2"/></svg>',
  retail:'<svg viewBox="0 0 24 24"><path d="M5 7h14l-1 13H6L5 7Zm3 0a4 4 0 0 1 8 0M9 12h6"/></svg>',
  store:'<svg viewBox="0 0 24 24"><path d="M4 9h16l-1-4H5L4 9Zm1 0v11h14V9M8 13h3v7M14 13h3"/></svg>'
};

function iconFor(key){
  return iconSvg[key] || iconSvg.product;
}

function displayCompanyName(name){
  const raw = String(name || '').trim();
  if(!raw) return 'InterNex Cloud ERP';
  if(/^paynex$/i.test(raw) || /^paynex[_-\s]/i.test(raw)) return 'InterNex';
  return raw.replace(/\bPayNex\b/gi, 'InterNex');
}

function buildCalendar(){
  const grid = document.getElementById('foCalendarGrid');
  if(!grid) return;
  const now = new Date();
  const monthName = now.toLocaleString(undefined,{month:'long'});
  const year = now.getFullYear();
  const monthEl = document.getElementById('foCalendarMonth');
  if(monthEl) monthEl.textContent = `${monthName}  ${year}`;
  const first = new Date(year, now.getMonth(), 1);
  const startDay = first.getDay();
  const daysInMonth = new Date(year, now.getMonth()+1, 0).getDate();
  const prevDays = new Date(year, now.getMonth(), 0).getDate();
  const cells = [];
  for(let i=startDay-1;i>=0;i--) cells.push({day:prevDays-i, muted:true});
  for(let d=1; d<=daysInMonth; d++) cells.push({day:d, today:d===now.getDate()});
  let next=1; while(cells.length<42) cells.push({day:next++, muted:true});
  grid.innerHTML = cells.map(c=>`<span class="${c.muted?'muted-date':''} ${c.today?'today':''}">${c.day}</span>`).join('');
}

async function init(){
  buildCalendar();
  try{
    const me = await api.get('/api/me');
    workspaceUser = me;
    let companyName = displayCompanyName(me.companyName || me.CompanyName || 'InterNex Cloud ERP');
    const userName = me.displayName || me.DisplayName || me.userName || me.UserName || '';
    const roleName = me.roleName || me.RoleName || '';
    const homeCompanyName = document.getElementById('homeCompanyName');
    if(homeCompanyName) homeCompanyName.textContent = companyName;
    const shellCompany = document.getElementById('foCompanyText');
    if(shellCompany) shellCompany.textContent = companyName;
    const avatarFallback = document.getElementById('foCompanyAvatarFallback');
    const avatarImg = document.getElementById('foCompanyAvatarImg');
    const setCompanyInitials = (name)=>{
      if(!avatarFallback) return;
      const initials = String(name||'IN').trim().split(/\s+/).filter(Boolean).slice(0,2).map(w=>w[0]).join('').toUpperCase() || 'IN';
      avatarFallback.textContent = initials;
      avatarFallback.hidden = false;
    };
    setCompanyInitials(companyName);
    if(avatarImg){
      avatarImg.hidden = true;
      avatarImg.removeAttribute('src');
    }
    // Company Information logo (same image saved on company.html)
    try{
      const company = await api.get('/api/company');
      const rawInfoName = company.companyName || company.CompanyName || '';
      if(rawInfoName){
        companyName = displayCompanyName(rawInfoName);
        if(homeCompanyName) homeCompanyName.textContent = companyName;
        if(shellCompany) shellCompany.textContent = companyName;
        setCompanyInitials(companyName);
      }
      const logoB64 = company.logoBase64 || company.LogoBase64 || '';
      if(avatarImg && logoB64){
        avatarImg.onload = ()=>{ avatarImg.hidden=false; if(avatarFallback) avatarFallback.hidden=true; };
        avatarImg.onerror = ()=>{ avatarImg.hidden=true; if(avatarFallback) avatarFallback.hidden=false; };
        avatarImg.src = 'data:image/png;base64,' + logoB64;
      } else if(avatarImg && window.api?.fetchWithRefresh){
        const response = await api.fetchWithRefresh('/api/company/logo?v='+Date.now(),{method:'GET',cache:'no-store'});
        if(response.ok){
          const blob = await response.blob();
          if(blob.size){
            avatarImg.onload = ()=>{ avatarImg.hidden=false; if(avatarFallback) avatarFallback.hidden=true; };
            avatarImg.onerror = ()=>{ avatarImg.hidden=true; if(avatarFallback) avatarFallback.hidden=false; };
            avatarImg.src = URL.createObjectURL(blob);
          }
        }
      }
    }catch{}
    const sideUser = document.getElementById('sideUser');
    const sideRole = document.getElementById('sideRole');
    if(sideUser && userName) sideUser.textContent = userName;
    if(sideRole) sideRole.textContent = [roleName, companyName].filter(Boolean).join(' • ') || 'Company workspace';
  }catch{
    const homeCompanyName = document.getElementById('homeCompanyName');
    if(homeCompanyName) homeCompanyName.textContent = 'InterNex Cloud ERP';
  }

  await showSubscriptionRenewBanner();

  try{
    const ctx = await api.get('/api/branches/context');
    if((ctx.allowMultipleBranches || ctx.AllowMultipleBranches) && !foModules.some(x=>x[0]==='/branches.html')){
      foModules.push(['/branches.html','store','Branches']);
    }
  }catch{}

  const tiles = document.getElementById('tiles');
  if(!tiles) return;
  const visibleModules = foModules.filter(m=>{
    if(!workspaceUser) return true;
    if(m[0]==='/configuration-packages.html') return hasPermission(workspaceUser,'configurationPackages.view') || hasPermission(workspaceUser,'configurationPackages.manage') || hasPermission(workspaceUser,'configurationPackages.export') || hasPermission(workspaceUser,'configurationPackages.import') || hasPermission(workspaceUser,'configurationPackages.apply');
    if(m[0]==='/expenses.html') return hasPermission(workspaceUser,'expenses.view') || hasPermission(workspaceUser,'expenses.create') || hasPermission(workspaceUser,'finance.createExpense') || hasPermission(workspaceUser,'expenses.edit') || hasPermission(workspaceUser,'expenses.delete');
    if(m[0]==='/expense-report.html') return hasPermission(workspaceUser,'expenses.viewReport') || hasPermission(workspaceUser,'expenses.printReport');
    return true;
  });
  tiles.innerHTML = visibleModules.map((m, i)=>{
    const href = m[0].startsWith('/') ? m[0] : '/' + m[0];
    const tone = ['navy','blue','cyan','teal'][i % 4];
    return `<a class="fo-module-tile" href="${href}">
      <span class="fo-module-icon ${tone}" aria-hidden="true">${iconFor(m[1])}</span>
      <span class="fo-module-label">${m[2]}</span>
    </a>`;
  }).join('');
}

async function showSubscriptionRenewBanner(){
  const banner = document.getElementById('subscriptionRenewBanner');
  if(!banner) return;
  banner.hidden = true;
  banner.textContent = '';
  try{
    const sub = await api.get('/api/me/subscription');
    const show = !!(sub.showRenewBanner || sub.ShowRenewBanner);
    if(!show) return;
    const msg = sub.renewMessage || sub.RenewMessage ||
      (sub.expiryDate || sub.ExpiryDate
        ? `Your subscription expired on ${sub.expiryDate || sub.ExpiryDate}. Please renew.`
        : 'Your subscription has expired. Please renew.');
    banner.textContent = msg;
    banner.hidden = false;
  }catch{}
}

init();
