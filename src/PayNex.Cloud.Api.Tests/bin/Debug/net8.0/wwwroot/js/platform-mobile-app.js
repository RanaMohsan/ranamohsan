(() => {
  const $ = id => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  let companyCode = (query.get('code') || '').trim();
  let details = null;
  let app = null;
  let users = [];
  const passwordReceipts = new Map();

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function toBool(v){ return v === true || v === 1 || String(v).toLowerCase() === 'true'; }
  function setMessage(text, ok = true){ msg('status', text || '', ok); }
  function pill(text, tone = ''){ return `<span class="bc-status-pill ${tone}">${esc(text)}</span>`; }
  function isAcceptablePassword(password){
    return typeof password === 'string' && password.length >= 8 && password.length <= 128 && /[A-Z]/.test(password) && /[a-z]/.test(password) && /\d/.test(password);
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
      setMessage('Open Mobile App Detail from a saved Company Card.', false);
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
    $('addUserBtn').addEventListener('click', addUser);
    $('toggleUserPasswordBtn').addEventListener('click', () => {
      const input = $('fUserPassword');
      const show = input.type === 'password';
      input.type = show ? 'text' : 'password';
      $('toggleUserPasswordBtn').textContent = show ? 'Hide' : 'Show';
    });
    $('toggleMobilePasswordBtn').addEventListener('click', () => {
      const input = $('mobileUserPassword');
      const show = input.type === 'password';
      input.type = show ? 'text' : 'password';
      $('toggleMobilePasswordBtn').textContent = show ? 'Hide' : 'Show';
    });
    $('saveMobileUserPasswordBtn').addEventListener('click', saveMobileUserPassword);
    $('cancelMobileUserPasswordBtn').addEventListener('click', closeMobilePasswordManager);
    $('userRows').addEventListener('click', onUserAction);
    await loadDetails();
  }

  async function loadDetails(){
    try{
      setMessage('Loading mobile app details...', true);
      details = await api.get(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app`);
      const company = details.company || details.Company || {};
      companyCode = value(company, 'companyCode', 'CompanyCode') || companyCode;
      app = details.app || details.App || null;
      users = details.users || details.Users || [];
      const registered = !!(details.registered ?? details.Registered ?? app);

      $('documentNo').textContent = companyCode;
      $('documentTitle').textContent = `${value(company, 'companyName', 'CompanyName') || companyCode} - Mobile App`;
      document.title = `InterNex - Mobile App ${companyCode}`;
      $('fCompanyCode').value = companyCode;
      $('fCompanyName').value = value(company, 'companyName', 'CompanyName') || '';
      $('fOwnerName').value = value(company, 'ownerName', 'OwnerName') || '';
      $('fOwnerEmail').value = value(company, 'ownerEmail', 'OwnerEmail') || '';
      $('companyCardLink').href = `/platform-company-card.html?code=${encodeURIComponent(companyCode)}`;

      if(registered && app){
        $('fAppName').value = value(app, 'appName', 'AppName') || 'InterNex Mobile';
        $('fPlatform').value = value(app, 'platform', 'Platform') || 'Both';
        $('fAppVersion').value = value(app, 'appVersion', 'AppVersion') || '';
        $('fPackageName').value = value(app, 'packageName', 'PackageName') || '';
        $('fBundleId').value = value(app, 'bundleId', 'BundleId') || '';
        $('fApiKey').value = value(app, 'apiKey', 'ApiKey') || '';
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
        $('fAppName').value = 'InterNex Mobile';
        $('fPlatform').value = 'Both';
        $('fAppVersion').value = '';
        $('fPackageName').value = '';
        $('fBundleId').value = '';
        $('fApiKey').value = '';
        $('fNotes').value = '';
        $('fStatus').value = 'Not Registered';
        $('fBlockReason').value = '';
        $('registerNote').hidden = false;
        $('registerBtn').hidden = false;
        $('saveBtn').hidden = true;
        $('blockCompanyBtn').hidden = true;
        $('unblockCompanyBtn').hidden = true;
        $('usersSection').hidden = true;
        $('documentStatus').textContent = 'Not Registered';
        $('documentStatus').className = 'bc-status-pill draft';
      }

      renderUsers();
      updateFacts();
      setMessage(registered ? `Mobile app detail loaded for ${companyCode}.` : `No mobile app registered yet for ${companyCode}.`, true);
    }catch(error){
      setMessage(error.message, false);
    }
  }

  function updateFacts(){
    const registered = !!app;
    const blocked = registered && toBool(value(app, 'isBlocked', 'IsBlocked'));
    const blockedUsers = users.filter(u => toBool(value(u, 'isBlocked', 'IsBlocked'))).length;
    $('factRegistered').textContent = registered ? 'Yes' : 'No';
    $('factPlatform').textContent = registered ? (value(app, 'platform', 'Platform') || '—') : '—';
    $('factStatus').textContent = registered ? (blocked ? 'Blocked' : (value(app, 'status', 'Status') || 'Active')) : 'Not Registered';
    $('factCompanyBlocked').textContent = blocked ? 'Yes' : 'No';
    $('factUsers').textContent = String(users.length);
    $('factBlockedUsers').textContent = String(blockedUsers);
    $('userCountLabel').textContent = `${users.length} record${users.length === 1 ? '' : 's'}`;
  }

  function renderUsers(){
    const body = $('userRows');
    if(!users.length){
      body.innerHTML = '<tr><td colspan="8" class="bc-empty-row">No mobile users yet.</td></tr>';
      return;
    }
    body.innerHTML = users.map(user => {
      const id = value(user, 'mobileAppUserId', 'MobileAppUserId');
      const blocked = toBool(value(user, 'isBlocked', 'IsBlocked'));
      const hasPassword = toBool(value(user, 'hasPassword', 'HasPassword'));
      const passwordPlain = String(value(user, 'passwordPlain', 'PasswordPlain') || passwordReceipts.get(String(id)) || '').trim();
      const passwordLabel = passwordPlain || (hasPassword ? 'Unknown — Set / Reset to show' : 'Not set');
      const emailLogin = value(user, 'email', 'Email') || value(user, 'userName', 'UserName');
      const verified = toBool(value(user, 'emailVerified', 'EmailVerified'));
      const verifiedPill = verified ? pill('Verified', 'active') : pill('Pending OTP', 'inactive');
      const status = blocked ? pill('Blocked', 'inactive') : pill('Active', 'active');
      const action = blocked
        ? `<button class="secondary compact-button" type="button" data-unblock-user="${esc(id)}">Unblock</button>`
        : `<button class="danger compact-button" type="button" data-block-user="${esc(id)}">Block</button>`;
      return `<tr>
        <td><div class="password-table-actions">
          <code class="password-clear-text">${esc(emailLogin)}</code>
          <button type="button" class="success compact-button" data-copy-username="${esc(emailLogin)}">Copy</button>
        </div></td>
        <td class="mobile-password-cell"><div class="password-table-actions">
          <code class="password-clear-text password-visible-full">${esc(passwordLabel)}</code>
          ${passwordPlain ? `<button type="button" class="success compact-button" data-copy-password-user="${esc(id)}">Copy</button>` : ''}
          <button type="button" class="secondary compact-button" data-reset-password="${esc(id)}">Set / Reset</button>
        </div></td>
        <td>${esc(value(user, 'displayName', 'DisplayName'))}</td>
        <td>${verifiedPill}</td>
        <td>${esc(value(user, 'mobile', 'Mobile'))}</td>
        <td>${esc(value(user, 'roleName', 'RoleName'))}</td>
        <td>${status}</td>
        <td>${action}</td>
      </tr>`;
    }).join('');
  }

  function appPayload(){
    return {
      appName: $('fAppName').value.trim() || 'InterNex Mobile',
      platform: $('fPlatform').value,
      packageName: $('fPackageName').value.trim(),
      bundleId: $('fBundleId').value.trim(),
      appVersion: $('fAppVersion').value.trim(),
      notes: $('fNotes').value.trim()
    };
  }

  async function registerApp(){
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app`, appPayload());
      setMessage(result.message || 'Mobile app registered.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function saveApp(){
    try{
      const result = await api.put(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app`, appPayload());
      setMessage(result.message || 'Mobile app details saved.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function setCompanyBlocked(isBlocked){
    const reason = isBlocked
      ? (window.prompt('Reason for blocking company mobile app access (optional):', value(app, 'blockReason', 'BlockReason') || '') || '')
      : '';
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app/block`, { isBlocked, reason });
      setMessage(result.message || (isBlocked ? 'Company app blocked.' : 'Company app unblocked.'), true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function addUser(){
    const payload = {
      email: $('fUserEmail').value.trim(),
      displayName: $('fDisplayName').value.trim(),
      mobile: $('fUserMobile').value.trim(),
      roleName: $('fUserRole').value.trim() || 'Mobile User',
      password: $('fUserPassword').value
    };
    if(!payload.email || !payload.email.includes('@')){ setMessage('A valid email is required (email is the login id).', false); return; }
    if(!isAcceptablePassword(payload.password)){
      setMessage('Password must be 8 to 128 characters and include upper-case, lower-case, and a number.', false);
      return;
    }
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app/users`, payload);
      $('fUserEmail').value = '';
      $('fDisplayName').value = '';
      $('fUserEmail').value = '';
      $('fUserMobile').value = '';
      $('fUserRole').value = 'Mobile User';
      $('fUserPassword').value = '';
      $('fUserPassword').type = 'password';
      $('toggleUserPasswordBtn').textContent = 'Show';
      setMessage(result.message || 'Mobile app user added.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  function findMobileUser(id){
    return users.find(user => String(value(user, 'mobileAppUserId', 'MobileAppUserId')) === String(id));
  }

  function openMobilePasswordManager(id){
    const selected = findMobileUser(id);
    if(!selected){ setMessage('Select a mobile user first.', false); return; }
    $('mobilePasswordManagerUserId').value = String(id);
    $('mobilePasswordManagerUser').textContent = value(selected, 'userName', 'UserName') || `User ${id}`;
    $('mobileUserPassword').value = '';
    $('mobileUserPasswordConfirm').value = '';
    $('mobileUserPassword').type = 'password';
    $('toggleMobilePasswordBtn').textContent = 'Show';
    $('mobilePasswordStatus').textContent = '';
    $('mobilePasswordManagerSection').hidden = false;
    setTimeout(() => $('mobileUserPassword').focus(), 0);
  }

  function closeMobilePasswordManager(){
    $('mobilePasswordManagerUserId').value = '0';
    $('mobileUserPassword').value = '';
    $('mobileUserPasswordConfirm').value = '';
    $('mobilePasswordStatus').textContent = '';
    $('mobilePasswordManagerSection').hidden = true;
  }

  async function saveMobileUserPassword(){
    const id = Number($('mobilePasswordManagerUserId').value || 0);
    const newPassword = $('mobileUserPassword').value;
    const confirmation = $('mobileUserPasswordConfirm').value;
    if(!id){ msg('mobilePasswordStatus', 'Select a mobile user first.', false); return; }
    if(!isAcceptablePassword(newPassword)){
      msg('mobilePasswordStatus', 'Password must be 8 to 128 characters and include upper-case, lower-case, and a number.', false);
      return;
    }
    if(newPassword !== confirmation){ msg('mobilePasswordStatus', 'Password confirmation does not match.', false); return; }
    $('saveMobileUserPasswordBtn').disabled = true;
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app/users/${id}/reset-password`, { newPassword });
      passwordReceipts.set(String(id), newPassword);
      const selected = findMobileUser(id);
      if(selected){
        selected.hasPassword = true;
        selected.HasPassword = true;
        selected.passwordPlain = newPassword;
        selected.PasswordPlain = newPassword;
      }
      renderUsers();
      closeMobilePasswordManager();
      setMessage(result.message || 'Password saved. It is now shown clearly in the Password column.', true);
    }catch(error){
      msg('mobilePasswordStatus', error.message, false);
    }finally{
      $('saveMobileUserPasswordBtn').disabled = false;
    }
  }

  async function onUserAction(event){
    const copyPassword = event.target.closest('[data-copy-password-user]');
    if(copyPassword){
      const id = copyPassword.dataset.copyPasswordUser;
      const selected = findMobileUser(id);
      const password = String(value(selected || {}, 'passwordPlain', 'PasswordPlain') || passwordReceipts.get(String(id)) || '').trim();
      if(!password){ setMessage('No password available to copy.', false); return; }
      try{
        await navigator.clipboard.writeText(password);
        setMessage('Password copied.', true);
      }catch{
        setMessage('Unable to copy password.', false);
      }
      return;
    }
    const resetBtn = event.target.closest('[data-reset-password]');
    if(resetBtn){
      openMobilePasswordManager(Number(resetBtn.dataset.resetPassword));
      return;
    }
    const copyUser = event.target.closest('[data-copy-username]');
    if(copyUser){
      const loginName = String(copyUser.dataset.copyUsername || '').trim();
      if(!loginName){ setMessage('No user name available to copy.', false); return; }
      try{
        await navigator.clipboard.writeText(loginName);
        setMessage('User name copied. Use this to sign in to the Mobile App.', true);
      }catch{
        setMessage('Unable to copy user name.', false);
      }
      return;
    }
    const blockBtn = event.target.closest('[data-block-user]');
    const unblockBtn = event.target.closest('[data-unblock-user]');
    if(!blockBtn && !unblockBtn) return;
    const id = Number((blockBtn || unblockBtn).dataset.blockUser || (blockBtn || unblockBtn).dataset.unblockUser);
    const isBlocked = !!blockBtn;
    const reason = isBlocked ? (window.prompt('Reason for blocking this mobile user (optional):', '') || '') : '';
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app/users/${id}/block`, { isBlocked, reason });
      setMessage(result.message || (isBlocked ? 'User blocked.' : 'User unblocked.'), true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  init();
})();
