(() => {
  const $ = id => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  let companyCode = (query.get('code') || '').trim();
  let details = null;
  let app = null;
  let users = [];
  const shownPasswords = new Set();

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function toBool(v){ return v === true || v === 1 || String(v).toLowerCase() === 'true'; }
  function setMessage(text, ok = true){ msg('status', text || '', ok); }
  function pill(text, tone = ''){ return `<span class="bc-status-pill ${tone}">${esc(text)}</span>`; }
  function userIdOf(user){ return Number(value(user, 'userId', 'UserId') || 0); }
  function userNameOf(user){
    return String(value(user, 'email', 'Email') || value(user, 'userName', 'UserName') || '').trim();
  }

  async function ensureOwner(){
    const me = await api.get('/api/me');
    if(!(me.isPlatformOwner || me.IsPlatformOwner)){
      location.href = '/workspace.html';
      throw new Error('InterNex Owner access is required.');
    }
    $('who').textContent = `${me.displayName || me.DisplayName || 'InterNex Owner'} | Platform Super Admin`;
    return me;
  }

  async function init(){
    if(!companyCode){
      setMessage('Open Desktop Detail from a saved Company Card.', false);
      return;
    }
    try{ await ensureOwner(); }
    catch(error){ setMessage(error.message, false); return; }
    $('companyCardLink').href = `/platform-company-card.html?code=${encodeURIComponent(companyCode)}`;
    $('backBtn').addEventListener('click', () => location.href = `/platform-company-card.html?code=${encodeURIComponent(companyCode)}`);
    $('refreshBtn').addEventListener('click', loadDetails);
    $('registerBtn').addEventListener('click', registerApp);
    $('saveBtn').addEventListener('click', saveApp);
    $('blockCompanyBtn').addEventListener('click', () => setCompanyBlocked(true));
    $('unblockCompanyBtn').addEventListener('click', () => setCompanyBlocked(false));
    $('userRows').addEventListener('click', onUserAction);
    await loadDetails();
  }

  async function loadDetails(){
    try{
      setMessage('Loading desktop app details...', true);
      details = await api.get(`/api/platform/companies/${encodeURIComponent(companyCode)}/desktop-app`);
      const company = details.company || details.Company || {};
      companyCode = value(company, 'companyCode', 'CompanyCode') || companyCode;
      app = details.app || details.App || null;
      users = details.users || details.Users || [];
      const registered = !!(details.registered ?? details.Registered ?? app);

      $('documentNo').textContent = companyCode;
      $('documentTitle').textContent = `${value(company, 'companyName', 'CompanyName') || companyCode} - Desktop`;
      document.title = `InterNex - Desktop Detail ${companyCode}`;
      $('fCompanyCode').value = companyCode;
      $('fCompanyName').value = value(company, 'companyName', 'CompanyName') || '';
      $('fOwnerName').value = value(company, 'ownerName', 'OwnerName') || '';
      $('fOwnerEmail').value = value(company, 'ownerEmail', 'OwnerEmail') || '';
      $('companyCardLink').href = `/platform-company-card.html?code=${encodeURIComponent(companyCode)}`;

      if(registered && app){
        $('fAppName').value = value(app, 'appName', 'AppName') || 'InterNex Desktop';
        $('fAppVersion').value = value(app, 'appVersion', 'AppVersion') || '';
        $('fNotes').value = value(app, 'notes', 'Notes') || '';
        $('fStatus').value = value(app, 'status', 'Status') || 'Active';
        $('fBlockReason').value = value(app, 'blockReason', 'BlockReason') || '';
        $('registerNote').hidden = true;
        $('registerBtn').hidden = true;
        $('saveBtn').hidden = false;
        $('usersSection').hidden = false;
        const blocked = toBool(value(app, 'isBlocked', 'IsBlocked'));
        $('blockCompanyBtn').hidden = blocked;
        $('unblockCompanyBtn').hidden = !blocked;
        $('documentStatus').textContent = blocked ? 'Blocked' : (value(app, 'status', 'Status') || 'Active');
        $('documentStatus').className = `bc-status-pill ${blocked ? 'inactive' : 'active'}`;
      }else{
        $('fAppName').value = 'InterNex Desktop';
        $('fAppVersion').value = '1.0.0';
        $('fNotes').value = '';
        $('fStatus').value = 'Not Registered';
        $('fBlockReason').value = '';
        $('registerNote').hidden = false;
        $('registerBtn').hidden = false;
        $('saveBtn').hidden = true;
        $('blockCompanyBtn').hidden = true;
        $('unblockCompanyBtn').hidden = true;
        $('usersSection').hidden = false;
        $('documentStatus').textContent = 'Not Registered';
        $('documentStatus').className = 'bc-status-pill draft';
      }

      renderUsers();
      updateFacts();
      setMessage(registered ? `Desktop detail loaded for ${companyCode}.` : `No desktop app registered yet for ${companyCode}. Users are listed so you can grant access.`, true);
    }catch(error){
      setMessage(error.message, false);
    }
  }

  function updateFacts(){
    const registered = !!app;
    const blocked = registered && toBool(value(app, 'isBlocked', 'IsBlocked'));
    const allowedUsers = users.filter(u => toBool(value(u, 'desktopAccessAllowed', 'DesktopAccessAllowed'))).length;
    const blockedUsers = users.filter(u => toBool(value(u, 'desktopUserBlocked', 'DesktopUserBlocked'))).length;
    $('factRegistered').textContent = registered ? 'Yes' : 'No';
    $('factStatus').textContent = registered ? (blocked ? 'Blocked' : (value(app, 'status', 'Status') || 'Active')) : 'Not Registered';
    $('factCompanyBlocked').textContent = blocked ? 'Yes' : 'No';
    $('factUsers').textContent = String(users.length);
    $('factAllowedUsers').textContent = String(allowedUsers);
    $('factBlockedUsers').textContent = String(blockedUsers);
    $('userCountLabel').textContent = `${users.length} record${users.length === 1 ? '' : 's'}`;
  }

  function passwordCell(user){
    const id = userIdOf(user);
    const plain = String(value(user, 'passwordPlain', 'PasswordPlain') || '').trim();
    const info = String(value(user, 'passwordInfo', 'PasswordInfo') || '').trim();
    if(!plain){
      return `<span class="muted">${esc(info || 'No password set')}</span>`;
    }
    const shown = shownPasswords.has(id);
    const display = shown ? esc(plain) : '••••••••';
    return `<span class="password-input-row">
      <code data-password-value="${esc(plain)}">${display}</code>
      <button class="secondary compact-button" type="button" data-toggle-password="${id}">${shown ? 'Hide' : 'Show'}</button>
      <button class="secondary compact-button" type="button" data-copy-password="${id}">Copy</button>
    </span>`;
  }

  function renderUsers(){
    const body = $('userRows');
    if(!users.length){
      body.innerHTML = '<tr><td colspan="8" class="bc-empty-row">No company users found.</td></tr>';
      return;
    }
    body.innerHTML = users.map(user => {
      const id = userIdOf(user);
      const allowed = toBool(value(user, 'desktopAccessAllowed', 'DesktopAccessAllowed'));
      const blocked = toBool(value(user, 'desktopUserBlocked', 'DesktopUserBlocked'));
      const access = allowed ? pill('Yes', 'active') : pill('No', 'inactive');
      const blockStatus = blocked ? pill('Blocked', 'inactive') : pill('Open', 'active');
      const grant = allowed
        ? `<button class="secondary compact-button" type="button" data-revoke-user="${id}">Revoke</button>`
        : `<button class="success compact-button" type="button" data-grant-user="${id}">Grant</button>`;
      const block = blocked
        ? `<button class="secondary compact-button" type="button" data-unblock-user="${id}">Unblock</button>`
        : `<button class="danger compact-button" type="button" data-block-user="${id}">Block</button>`;
      return `<tr>
        <td>${esc(id)}</td>
        <td>${esc(userNameOf(user))}</td>
        <td>${esc(value(user, 'displayName', 'DisplayName'))}</td>
        <td>${esc(value(user, 'roleName', 'RoleName'))}</td>
        <td>${passwordCell(user)}</td>
        <td>${access}</td>
        <td>${blockStatus}</td>
        <td>${grant} ${block}</td>
      </tr>`;
    }).join('');
  }

  function appPayload(){
    return {
      appName: $('fAppName').value.trim() || 'InterNex Desktop',
      appVersion: $('fAppVersion').value.trim(),
      notes: $('fNotes').value.trim()
    };
  }

  function findUser(id){
    return users.find(u => userIdOf(u) === Number(id));
  }

  async function copyText(text){
    if(navigator.clipboard && window.isSecureContext){
      await navigator.clipboard.writeText(text);
      return;
    }
    const helper = document.createElement('textarea');
    helper.value = text;
    helper.setAttribute('readonly', '');
    helper.style.position = 'fixed';
    helper.style.opacity = '0';
    document.body.appendChild(helper);
    helper.select();
    const copied = document.execCommand('copy');
    helper.remove();
    if(!copied) throw new Error('Clipboard copy was blocked.');
  }

  async function registerApp(){
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/desktop-app`, appPayload());
      setMessage(result.message || 'Desktop app registered.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function saveApp(){
    try{
      const result = await api.put(`/api/platform/companies/${encodeURIComponent(companyCode)}/desktop-app`, appPayload());
      setMessage(result.message || 'Desktop app details saved.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function setCompanyBlocked(isBlocked){
    const reason = isBlocked
      ? (window.prompt('Reason for blocking company desktop app access (optional):', value(app, 'blockReason', 'BlockReason') || '') || '')
      : '';
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/desktop-app/block`, { isBlocked, reason });
      setMessage(result.message || (isBlocked ? 'Company desktop app blocked.' : 'Company desktop app unblocked.'), true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function setUserAccess(id, isAllowed){
    const user = findUser(id);
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/desktop-app/users/${id}/access`, {
        isAllowed,
        userName: userNameOf(user || {})
      });
      setMessage(result.message || (isAllowed ? 'Desktop access granted.' : 'Desktop access revoked.'), true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function setUserBlocked(id, isBlocked){
    const user = findUser(id);
    const reason = isBlocked ? (window.prompt('Reason for blocking this desktop user (optional):', '') || '') : '';
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/desktop-app/users/${id}/block`, {
        isBlocked,
        reason,
        userName: userNameOf(user || {})
      });
      setMessage(result.message || (isBlocked ? 'Desktop user blocked.' : 'Desktop user unblocked.'), true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function onUserAction(event){
    const toggle = event.target.closest('[data-toggle-password]');
    if(toggle){
      const id = Number(toggle.dataset.togglePassword);
      if(shownPasswords.has(id)) shownPasswords.delete(id);
      else shownPasswords.add(id);
      renderUsers();
      return;
    }
    const copy = event.target.closest('[data-copy-password]');
    if(copy){
      const id = Number(copy.dataset.copyPassword);
      const user = findUser(id);
      const password = String(value(user || {}, 'passwordPlain', 'PasswordPlain') || '').trim();
      if(!password){ setMessage('No password available to copy.', false); return; }
      try{
        await copyText(password);
        setMessage('Password copied.', true);
      }catch{
        setMessage('Unable to copy password.', false);
      }
      return;
    }
    const grant = event.target.closest('[data-grant-user]');
    if(grant){ await setUserAccess(Number(grant.dataset.grantUser), true); return; }
    const revoke = event.target.closest('[data-revoke-user]');
    if(revoke){ await setUserAccess(Number(revoke.dataset.revokeUser), false); return; }
    const block = event.target.closest('[data-block-user]');
    if(block){ await setUserBlocked(Number(block.dataset.blockUser), true); return; }
    const unblock = event.target.closest('[data-unblock-user]');
    if(unblock){ await setUserBlocked(Number(unblock.dataset.unblockUser), false); }
  }

  init();
})();
