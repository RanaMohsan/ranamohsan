let users=[];
const val=(o,a,b)=>o?.[a] ?? o?.[b] ?? '';
async function init(){
  try{
    const me=await api.get('/api/me');
    who.textContent=`${me.companyName||me.CompanyName} | ${me.displayName||me.DisplayName} | ${me.roleName||me.RoleName}`;
    await loadUsers();
  }catch{ location.href='/login.html?returnUrl='+encodeURIComponent(location.pathname); }
}
async function loadUsers(){
  try{
    users=await api.get('/api/users?term='+encodeURIComponent(userSearch.value||''));
    usersBody.innerHTML=users.length?users.map(u=>{
      const id=val(u,'userId','UserId');
      const verified=!!val(u,'emailVerified','EmailVerified');
      return `<tr onclick="openUser(${id})" class="click-row">
        <td><b>${val(u,'displayName','DisplayName')||'-'}</b>${val(u,'isCompanySuperAdmin','IsCompanySuperAdmin')?' <span class="status-chip ok">Company Admin</span>':''}</td>
        <td>${val(u,'email','Email')||'<span class="bad">Missing</span>'}</td>
        <td>${val(u,'phoneNumber','PhoneNumber')||''}</td>
        <td>${val(u,'roleName','RoleName')}</td>
        <td>${val(u,'storeCode','StoreCode')} - ${val(u,'storeName','StoreName')}</td>
        <td>${verified?'<span class="status-chip ok">Verified</span>':'<span class="status-chip off">Pending OTP</span>'}</td>
        <td>${val(u,'isActive','IsActive')?'<span class="status-chip ok">Active</span>':'<span class="status-chip off">Inactive</span>'}</td>
        <td><button class="secondary" onclick="event.stopPropagation();openUser(${id})">Show Details</button></td>
      </tr>`;
    }).join(''):'<tr><td colspan="8" class="muted">No users found.</td></tr>';
  }catch(e){ usersBody.innerHTML=`<tr><td colspan="8" class="bad">${e.message}</td></tr>`; }
}
function openUser(id){ location.href='/user-card.html?id='+encodeURIComponent(id); }
function newUser(){ location.href='/user-card.html'; }
userSearch?.addEventListener('keydown',e=>{if(e.key==='Enter')loadUsers();});
init();
