function getCookie(name){
  const key=encodeURIComponent(name)+'=';
  return document.cookie.split(';').map(x=>x.trim()).find(x=>x.startsWith(key))?.slice(key.length) || '';
}

const api = {
  token: '',
  adminToken: sessionStorage.getItem('paynex_admin_token') || localStorage.getItem('paynex_admin_token') || '',
  refreshing: null,
  setToken(_t){
    // Browser authentication is cookie-only. Never expose or persist the tenant access token to JavaScript storage.
    this.token='';
    sessionStorage.removeItem('paynex_access_token');
    localStorage.removeItem('paynex_token');
  },
  setAdminToken(t){ this.adminToken=t||''; if(this.adminToken) sessionStorage.setItem('paynex_admin_token',this.adminToken); else sessionStorage.removeItem('paynex_admin_token'); },
  clear(){
    sessionStorage.removeItem('paynex_access_token');
    localStorage.removeItem('paynex_token');
    localStorage.removeItem('paynex_last_user');
    this.token='';
  },
  clearAdmin(){ sessionStorage.removeItem('paynex_admin_token'); localStorage.removeItem('paynex_admin_token'); this.adminToken=''; },
  async logout(){
    try{ await this.post('/api/auth/logout',{}); }catch{}
    this.clear();
  },
  async forgetDevice(){ return this.post('/api/auth/forget-device',{}); },
  async get(url){ return this.req(url,{method:'GET'}); },
  async post(url, body){ return this.req(url,{method:'POST',body:JSON.stringify(body)}); },
  async put(url, body){ return this.req(url,{method:'PUT',body:JSON.stringify(body)}); },
  async delete(url){ return this.req(url,{method:'DELETE'}); },
  authHeaders(url){
    const headers = {};
    // The separate legacy /api/admin surface still uses its own explicit token.
    if(url.startsWith('/api/admin') && this.adminToken) headers['Authorization']='Bearer '+this.adminToken;
    return headers;
  },
  securityHeaders(method){
    const headers={};
    if(!/^(GET|HEAD|OPTIONS)$/i.test(method||'GET')){
      const csrf=getCookie('paynex_csrf');
      if(csrf) headers['X-PayNex-CSRF']=decodeURIComponent(csrf);
    }
    return headers;
  },
  async refresh(){
    if(this.refreshing) return this.refreshing;
    const perform=async()=>{
      try{
        const headers={'Content-Type':'application/json',...this.securityHeaders('POST')};
        const r=await fetch('/api/auth/refresh',{method:'POST',headers,credentials:'same-origin',body:'{}'});
        if(!r.ok) return false;
        const data=normalizeKeys(await r.json());
        this.setToken(data.token||'');
        if(data.user) localStorage.setItem('paynex_last_user',JSON.stringify(data.user));
        return true;
      }catch{return false}
    };
    this.refreshing=(async()=>{
      try{
        // Web Locks coordinates refresh-token rotation across tabs in the same browser profile.
        if(navigator.locks?.request) return await navigator.locks.request('paynex-auth-refresh',perform);
        return await perform();
      }finally{this.refreshing=null;}
    })();
    return this.refreshing;
  },
  async req(url, options={}, retried=false){
    const requestOptions={...options};
    requestOptions.headers={...(options.headers||{})};
    requestOptions.credentials='same-origin';
    requestOptions.headers['Content-Type']='application/json';
    Object.assign(requestOptions.headers,this.authHeaders(url),this.securityHeaders(requestOptions.method));
    const r=await fetch(url,requestOptions);
    const txt=await r.text();
    let data=null;
    try{data=txt?JSON.parse(txt):null}catch{data=txt}
    if(r.status===401 && !url.startsWith('/api/admin') && !url.startsWith('/api/auth/refresh') && !url.startsWith('/api/auth/login') && !url.startsWith('/api/auth/verify-otp') && !url.startsWith('/api/auth/resend-otp') && !retried){
      if(await this.refresh()) return this.req(url,options,true);
      this.clear();
      const ret=encodeURIComponent(location.pathname+location.search);
      location.href='/login.html?returnUrl='+ret;
      throw new Error('Login required.');
    }
    if(!r.ok) throw new Error((data&&data.message)||(typeof data==='string'&&data)||('HTTP '+r.status));
    return normalizeKeys(data);
  },
  async fetchWithRefresh(url, options={}, retried=false){
    const requestOptions={...options,headers:{...(options.headers||{})},credentials:'same-origin'};
    Object.assign(requestOptions.headers,this.authHeaders(url),this.securityHeaders(requestOptions.method));
    let r=await fetch(url,requestOptions);
    if(r.status===401 && !retried && await this.refresh()) return this.fetchWithRefresh(url,options,true);
    return r;
  },
  async html(url){
    const r=await this.fetchWithRefresh(url,{method:'GET'});
    const txt=await r.text();
    if(!r.ok) throw new Error(txt||('HTTP '+r.status));
    return txt;
  },
  async download(url){
    const r=await this.fetchWithRefresh(url,{method:'GET'});
    if(!r.ok){
      const txt=await r.text(); let data=null; try{data=txt?JSON.parse(txt):null}catch{data=txt}
      throw new Error((data&&data.message)||(typeof data==='string'&&data)||('HTTP '+r.status));
    }
    const disposition=r.headers.get('content-disposition')||'';
    const encoded=/filename\*=UTF-8''([^;]+)/i.exec(disposition);
    const plain=/filename="?([^";]+)"?/i.exec(disposition);
    const fileName=encoded?decodeURIComponent(encoded[1]):(plain?plain[1]:'download.xlsx');
    const blob=await r.blob(); const objectUrl=URL.createObjectURL(blob);
    const a=document.createElement('a'); a.href=objectUrl; a.download=fileName; document.body.appendChild(a); a.click(); a.remove();
    setTimeout(()=>URL.revokeObjectURL(objectUrl),1000); return fileName;
  },
  async openReport(url, autoPrint=false){
    if(!url||/undefined|null|NaN/.test(url)) throw new Error('Please select a valid record first.');
    const html=await this.html(url); const w=window.open('about:blank','_blank');
    if(!w) throw new Error('Popup blocked. Please allow popups for this site.');
    w.document.open(); w.document.write(html); w.document.close();
    if(autoPrint) setTimeout(()=>{try{w.focus();w.print()}catch{}},450);
  },
  openA4Sheet({title, docNo='', companyName='InterNex Cloud', subtitle='Report', tableHtml='', autoPrint=false}={}){
    const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    const printed=new Date().toISOString().slice(0,16).replace('T',' ');
    const html=`<!doctype html><html><head><meta charset="utf-8"><title>${esc(title)} ${esc(docNo)}</title>
<link rel="stylesheet" href="/css/report-print.css?v=a4-fit-20260817"></head>
<body><div class="toolbar"><button class="print" onclick="window.print()">Print</button></div>
<div class="doc layout-bc layout-sheet"><div class="doc-head"><div class="brand-block no-logo"><div>
<div class="company">${esc(companyName)}</div><div class="muted">${esc(subtitle)}</div></div></div>
<div class="doc-title"><h1>${esc(title)}</h1>${docNo?`<div class="doc-no">${esc(docNo)}</div>`:''}
<div class="muted">Printed: ${esc(printed)}</div></div></div>
${tableHtml}
<div class="footer-note">This is a system generated document from InterNex Cloud. Verify posting accounts and tax setup before statutory submission.</div>
</div></body></html>`;
    const w=window.open('about:blank','_blank');
    if(!w) throw new Error('Popup blocked. Please allow popups for this site.');
    w.document.open(); w.document.write(html); w.document.close();
    if(autoPrint) setTimeout(()=>{try{w.focus();w.print()}catch{}},450);
  }
};

window.api = api;

function normalizeKeys(value){
  if(Array.isArray(value)) return value.map(normalizeKeys);
  if(value&&typeof value==='object'){
    const out={};
    for(const [k,v] of Object.entries(value)){const normalized=normalizeKeys(v);const nk=k.charAt(0).toLowerCase()+k.slice(1);out[k]=normalized;out[nk]=normalized;}
    return out;
  }
  return value;
}
function money(n){return 'Rs. '+Number(n||0).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2});}
function msg(id,text,ok=true){const el=document.getElementById(id);if(el){el.className=ok?'ok':'bad';el.textContent=text;}}
function today(){return new Date().toISOString().slice(0,10);}
function days(n){const d=new Date();d.setDate(d.getDate()+n);return d.toISOString().slice(0,10);}
function fmtDate(x){if(!x)return '';const d=new Date(x);return isNaN(d)?x:d.toLocaleDateString();}
function readPermissions(user){try{return JSON.parse(user.permissionsJson||user.PermissionsJson||'{}')||{};}catch{return {};}}
function hasPermission(user,key){
  const role=(user.roleName||user.RoleName||'').toLowerCase();
  if(user.isCompanySuperAdmin||user.IsCompanySuperAdmin||user.isPlatformOwner||user.IsPlatformOwner||role==='admin'||role==='system admin'||role==='company super admin') return true;
  const map=readPermissions(user); return !!(map[key]||map[key?.toLowerCase?.()]);
}
