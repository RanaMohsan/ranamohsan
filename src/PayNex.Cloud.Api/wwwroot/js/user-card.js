let roles=[], stores=[], permissionGroups=[], loadedPermissions={}, passwordReceipt='', profileImageBase64=null, removeProfileImage=false, currentUserId=0;
const val=(o,a,b)=>o?.[a] ?? o?.[b] ?? '';
const qs=new URLSearchParams(location.search);
const editingId=Number(qs.get('id')||0);
function isEmail(x){ return /^\S+@\S+\.\S+$/.test((x||'').trim()); }
function isAcceptablePassword(value){ return typeof value==='string' && value.length>=8 && value.length<=128 && /[A-Z]/.test(value) && /[a-z]/.test(value) && /\d/.test(value); }
function userInitials(value){ return String(value||'U').trim().split(/\s+/).slice(0,2).map(x=>x.charAt(0)).join('').toUpperCase()||'U'; }
function showProfileFallback(name){
  profilePreviewImage.hidden=true;
  profilePreviewImage.removeAttribute('src');
  profilePreviewFallback.hidden=false;
  profilePreviewFallback.textContent=userInitials(name||displayName.value||userName.value);
}
function showProfilePreview(src){
  if(!src){showProfileFallback();return;}
  profilePreviewImage.onload=()=>{profilePreviewImage.hidden=false;profilePreviewFallback.hidden=true};
  profilePreviewImage.onerror=()=>showProfileFallback();
  profilePreviewImage.src=src;
}
function resetProfileEditor(name,hasPhoto=false,id=0){
  profileImageBase64=null; removeProfileImage=false; profileImageInput.value='';
  profileImageHelp.textContent='PNG, JPG, WEBP or GIF. Maximum file size: 2 MB.';
  if(hasPhoto&&id) showProfilePreview(`/api/users/${id}/photo?v=${Date.now()}`); else showProfileFallback(name);
}
async function selectProfileImage(file){
  if(!file)return;
  const allowed=['image/png','image/jpeg','image/webp','image/gif'];
  if(!allowed.includes(file.type)){msg('userStatus','Profile photo must be PNG, JPG, WEBP or GIF.',false);profileImageInput.value='';return;}
  if(file.size>2*1024*1024){msg('userStatus','Profile photo cannot exceed 2 MB.',false);profileImageInput.value='';return;}
  const data=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result||''));reader.onerror=()=>reject(new Error('Unable to read the selected photo.'));reader.readAsDataURL(file);});
  profileImageBase64=data; removeProfileImage=false; showProfilePreview(data);
  profileImageHelp.textContent=`Selected: ${file.name} • ${(file.size/1024).toFixed(0)} KB`;
}
function removeProfilePhoto(){
  profileImageBase64=null; removeProfileImage=true; profileImageInput.value=''; showProfileFallback(displayName.value||userName.value);
  profileImageHelp.textContent='Profile photo will be removed when the user card is saved.';
}
function togglePasswordVisibility(input,button){
  const show=input.type==='password';
  input.type=show?'text':'password';
  button.textContent=show?'Hide':'Show';
}
function showPasswordReceipt(value,message){
  passwordReceipt=value;
  userPasswordReceiptValue.value=value;
  userPasswordReceiptValue.type='password';
  toggleUserPasswordReceiptBtn.textContent='Show';
  userPasswordReceipt.hidden=false;
  passwordHelp.textContent='Password saved securely. Copy it from the one-time receipt before leaving this page.';
  if(message) msg('userStatus',message,true);
}
function forgetPasswordReceipt(){
  passwordReceipt='';
  userPasswordReceiptValue.value='';
  userPasswordReceipt.hidden=true;
  passwordHelp.textContent=Number(userId.value||0)>0?'A password is stored securely. Enter a new password only when you want to change it.':'Enter a password to create this user.';
}
async function copyText(text){
  if(navigator.clipboard && window.isSecureContext){ await navigator.clipboard.writeText(text); return; }
  const helper=document.createElement('textarea');
  helper.value=text; helper.setAttribute('readonly',''); helper.style.position='fixed'; helper.style.opacity='0';
  document.body.appendChild(helper); helper.select();
  const copied=document.execCommand('copy'); helper.remove();
  if(!copied) throw new Error('Clipboard copy was blocked. Use Show and copy the password manually.');
}
async function copyPasswordReceipt(){
  if(!passwordReceipt){msg('userStatus','This one-time password receipt is no longer available.',false);return;}
  try{await copyText(passwordReceipt);msg('userStatus','Password copied. Use “Forget now” after sharing it safely.',true)}
  catch(e){msg('userStatus',e.message,false)}
}
async function init(){
  try{
    const me=await api.get('/api/me');
    currentUserId=Number(me.userId||me.UserId||0);
    who.textContent=`${me.companyName||me.CompanyName} | ${me.displayName||me.DisplayName} | ${me.roleName||me.RoleName}`;
    const look=await api.get('/api/users/lookups');
    roles=look.roles||[]; stores=look.stores||[]; permissionGroups=look.permissionGroups||look.groups||[];
    roleId.innerHTML=roles.map(r=>`<option value="${val(r,'roleId','RoleId')}">${val(r,'roleName','RoleName')}</option>`).join('');
    storeId.innerHTML=stores.map(s=>`<option value="${val(s,'storeId','StoreId')}">${val(s,'storeCode','StoreCode')} - ${val(s,'storeName','StoreName')}</option>`).join('');
    renderBranchAssignments(!!(look.allowMultipleBranches||look.AllowMultipleBranches), []);
    renderPermissions({});
    if(editingId) await loadUser(editingId); else newUser();
  }catch(e){ location.href='/login.html?returnUrl='+encodeURIComponent(location.pathname+location.search); }
}
function renderBranchAssignments(allow, selected){
  branchAssignmentPanel.hidden=!allow;
  if(!allow) return;
  const selectedSet=new Set((selected||[]).map(Number));
  branchChecks.innerHTML=stores.map(s=>{
    const id=Number(val(s,'storeId','StoreId'));
    return `<label class="perm-toggle"><input type="checkbox" class="branch-check" value="${id}" ${selectedSet.has(id)?'checked':''}> <span>${val(s,'storeCode','StoreCode')} - ${val(s,'storeName','StoreName')}</span></label>`;
  }).join('');
}
function renderPermissions(values){
  loadedPermissions=values||{};
  permissionsGrid.innerHTML=(permissionGroups||[]).map(g=>{
    const perms=g.permissions||g.Permissions||[];
    return `<div class="perm-group"><h3>${g.category||g.Category}</h3><div class="permission-grid">${perms.map(p=>{
      const key=p.key||p.Key, label=p.label||p.Label;
      return `<label class="perm-toggle"><input type="checkbox" data-perm="${key}" ${loadedPermissions[key]?'checked':''}> <span>${label}</span></label>`;
    }).join('')}</div></div>`;
  }).join('');
}
function newUser(){
  forgetPasswordReceipt();
  userId.value=0; cardMode.textContent='New User'; userName.value=''; displayName.value=''; email.value=''; phoneNumber.value=''; password.value=''; password.type='password'; togglePasswordBtn.textContent='Show'; passwordHelp.textContent='Enter a password to create this user.'; isActive.value='true'; isCompanySuperAdmin.checked=false;
  resetProfileEditor('New User',false,0);
  if(roleId.options.length) roleId.selectedIndex=0; if(storeId.options.length) storeId.selectedIndex=0;
  document.querySelectorAll('[data-perm]').forEach(x=>x.checked=false);
  document.querySelectorAll('.branch-check').forEach(x=>x.checked=false);
}
async function loadUser(id){
  forgetPasswordReceipt();
  const r=await api.get('/api/users/'+id);
  const u=r.user||r.User||{};
  userId.value=val(u,'userId','UserId'); cardMode.textContent='Edit User';
  userName.value=val(u,'userName','UserName'); displayName.value=val(u,'displayName','DisplayName'); email.value=val(u,'email','Email'); phoneNumber.value=val(u,'phoneNumber','PhoneNumber');
  password.value=''; password.type='password'; togglePasswordBtn.textContent='Show'; passwordHelp.textContent='A password is stored securely. Enter a new password only when you want to change it.'; roleId.value=val(u,'roleId','RoleId'); storeId.value=val(u,'storeId','StoreId'); isActive.value=String(!!val(u,'isActive','IsActive')); isCompanySuperAdmin.checked=!!val(u,'isCompanySuperAdmin','IsCompanySuperAdmin');
  resetProfileEditor(val(u,'displayName','DisplayName'),!!val(u,'hasProfileImage','HasProfileImage'),Number(val(u,'userId','UserId')));
  const branches=(r.branches||[]).map(b=>val(b,'storeId','StoreId'));
  renderBranchAssignments(!branchAssignmentPanel.hidden, branches.length?branches:[Number(storeId.value)]);
  renderPermissions(r.permissions||{});
}
email?.addEventListener('input',()=>{ if(!userName.value.trim() && email.value.includes('@')) userName.value=email.value.trim().split('@')[0]; });
displayName?.addEventListener('input',()=>{if(profilePreviewImage.hidden)profilePreviewFallback.textContent=userInitials(displayName.value||userName.value)});
async function saveUser(){
  try{
    if(!isEmail(email.value)){msg('userStatus','A valid email address is mandatory.',false);return;}
    if(!displayName.value.trim()){msg('userStatus','Full name is required.',false);return;}
    if(Number(userId.value||0)===0 && !password.value){msg('userStatus','Password is required for new user.',false);return;}
    if(password.value && !isAcceptablePassword(password.value)){msg('userStatus','Password must be 8 to 128 characters and include upper-case, lower-case, and a number.',false);return;}
    const permissions={}; document.querySelectorAll('[data-perm]').forEach(x=>permissions[x.dataset.perm]=!!x.checked);
    const branchIds=[...document.querySelectorAll('.branch-check:checked')].map(x=>Number(x.value));
    const submittedPassword=password.value;
    const body={userId:Number(userId.value||0),userName:(userName.value.trim()||email.value.trim()),displayName:displayName.value.trim(),fullName:displayName.value.trim(),email:email.value.trim(),phoneNumber:phoneNumber.value.trim(),password:submittedPassword,roleId:Number(roleId.value),storeId:Number(storeId.value),branchIds,permissions,isCompanySuperAdmin:isCompanySuperAdmin.checked,isActive:isActive.value==='true',profileImageBase64,removeProfileImage};
    const profileWasChanged=!!profileImageBase64||removeProfileImage;
    const r=await api.post('/api/users',body);
    if(r.userId){ userId.value=r.userId; cardMode.textContent='Edit User'; history.replaceState(null,'','/user-card.html?id='+r.userId); }
    password.value=''; password.type='password'; togglePasswordBtn.textContent='Show';
    const savedId=Number(userId.value||0);
    if(removeProfileImage) showProfileFallback(displayName.value); else if(profileImageBase64 || !!document.getElementById('profilePreviewImage')?.getAttribute('src')) showProfilePreview(`/api/users/${savedId}/photo?v=${Date.now()}`);
    profileImageBase64=null; removeProfileImage=false; profileImageInput.value='';
    profileImageHelp.textContent='PNG, JPG, WEBP or GIF. Maximum file size: 2 MB.';
    const meCached=(()=>{try{return JSON.parse(localStorage.getItem('paynex_last_user')||'{}');}catch{return {};}})();
    const meId=Number(meCached.userId||meCached.UserId||currentUserId||0);
    const meEmail=String(meCached.email||meCached.Email||'').trim().toLowerCase();
    const savedEmail=String(email.value||'').trim().toLowerCase();
    const isSelf=savedId>0 && (savedId===meId || (meEmail && savedEmail && meEmail===savedEmail));
    if(profileWasChanged && isSelf){
      window.dispatchEvent(new CustomEvent('paynex-profile-updated',{detail:{userId:savedId,photoUrl:`/api/users/${savedId}/photo?v=${Date.now()}`}}));
    }
    if(submittedPassword && (r.passwordChanged!==false)) showPasswordReceipt(submittedPassword,(r.message||'User saved.')+' Password is ready in the one-time receipt.');
    else msg('userStatus',r.message||'User saved.',true);
  }catch(e){msg('userStatus',e.message,false)}
}
async function resetPassword(){
  try{
    const id=Number(userId.value||0); if(!id){msg('userStatus','Save the user before reset password.',false);return;}
    const p=password.value;
    if(!p){msg('userStatus','Enter the new password in the Password field first.',false);password.focus();return;}
    if(!isAcceptablePassword(p)){msg('userStatus','Password must be 8 to 128 characters and include upper-case, lower-case, and a number.',false);return;}
    resetPasswordBtn.disabled=true;
    const r=await api.post('/api/users/'+id+'/reset-password',{newPassword:p});
    password.value=''; password.type='password'; togglePasswordBtn.textContent='Show';
    showPasswordReceipt(p,r.message||'Password reset. Copy it from the one-time receipt.');
  }catch(e){msg('userStatus',e.message,false)}
  finally{resetPasswordBtn.disabled=false}
}
storeId?.addEventListener('change',()=>{
  const current=Number(storeId.value);
  const cb=[...document.querySelectorAll('.branch-check')].find(x=>Number(x.value)===current);
  if(cb) cb.checked=true;
});
togglePasswordBtn?.addEventListener('click',()=>togglePasswordVisibility(password,togglePasswordBtn));
toggleUserPasswordReceiptBtn?.addEventListener('click',()=>togglePasswordVisibility(userPasswordReceiptValue,toggleUserPasswordReceiptBtn));
copyUserPasswordReceiptBtn?.addEventListener('click',copyPasswordReceipt);
uploadProfileImageBtn?.addEventListener('click',()=>profileImageInput.click());
profileImageInput?.addEventListener('change',async()=>{try{await selectProfileImage(profileImageInput.files?.[0])}catch(e){msg('userStatus',e.message,false)}});
removeProfileImageBtn?.addEventListener('click',removeProfilePhoto);
forgetUserPasswordReceiptBtn?.addEventListener('click',()=>{forgetPasswordReceipt();msg('userStatus','The one-time password receipt was removed from this page.',true)});
window.addEventListener('pagehide',()=>{passwordReceipt='';userPasswordReceiptValue.value='';password.value=''});
init();
