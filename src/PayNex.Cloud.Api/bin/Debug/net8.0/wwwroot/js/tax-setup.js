function today(){return new Date().toISOString().slice(0,10)}
async function init(){try{const me=await api.get('/api/me');who.textContent=`${me.companyName} | ${me.displayName} | ${me.roleName}`;}catch{location.href='/login.html'} await loadTax();}
async function loadTax(){const list=await api.get('/api/tax-groups'); taxBody.innerHTML=list.map(t=>`<tr onclick='editTax(${JSON.stringify(t).replaceAll("'","&#39;")})'><td>${t.taxGroupName}</td><td>${Number(t.taxPercent||0).toFixed(2)}%</td><td>${t.isInclusive?'Inclusive':'Exclusive'}</td><td>${t.isActive?'Active':'Inactive'}</td></tr>`).join('')}
function editTax(t){taxId.value=t.taxGroupId; taxName.value=t.taxGroupName; taxPercent.value=t.taxPercent; taxInclusive.value=String(!!t.isInclusive); taxActive.value=String(!!t.isActive); msg('taxStatus','Tax group loaded.',true)}
function newTax(){taxId.value=0; taxName.value=''; taxPercent.value=0; taxInclusive.value='false'; taxActive.value='true'; msg('taxStatus','New tax group.',true)}
async function saveTax(){try{const r=await api.post('/api/tax-groups',{taxGroupId:Number(taxId.value||0),taxGroupName:taxName.value,taxPercent:Number(taxPercent.value||0),isInclusive:taxInclusive.value==='true',isActive:taxActive.value==='true'}); msg('taxStatus',r.message,true); await loadTax();}catch(e){msg('taxStatus',e.message,false)}}
function openTaxReport(){const d=today(); const f=d.slice(0,8)+'01'; window.open(`/accounting-reports.html`,'_self')}
init();
