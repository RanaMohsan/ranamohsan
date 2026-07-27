(() => {
  const $ = id => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  let companyCode = (query.get('code') || '').trim();
  let details = null;
  let app = null;
  let users = [];
  let editingUserId = 0;

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
      throw new Error('PayNex Owner access is required.');
    }
    $('who').textContent = `${me.displayName || me.DisplayName || 'PayNex Owner'} | Platform Super Admin`;
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
    $('refreshBtn').addEventListener('click', () => { clearUserForm(); loadDetails(); });
    $('registerBtn').addEventListener('click', registerApp);
    $('saveBtn').addEventListener('click', saveApp);
    $('blockCompanyBtn').addEventListener('click', () => setCompanyBlocked(true));
    $('unblockCompanyBtn').addEventListener('click', () => setCompanyBlocked(false));
    $('addUserBtn').addEventListener('click', addUser);
    $('saveUserBtn').addEventListener('click', saveUserChanges);
    $('cancelUserEditBtn').addEventListener('click', clearUserForm);
    $('toggleUserPasswordBtn').addEventListener('click', () => {
      const input = $('fUserPassword');
      const show = input.type === 'password';
      input.type = show ? 'text' : 'password';
      $('toggleUserPasswordBtn').textContent = show ? 'Hide' : 'Show';
    });
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
      document.title = `PayNex - Mobile App ${companyCode}`;
      $('fCompanyCode').value = companyCode;
      $('fCompanyName').value = value(company, 'companyName', 'CompanyName') || '';
      $('fOwnerName').value = value(company, 'ownerName', 'OwnerName') || '';
      $('fOwnerEmail').value = value(company, 'ownerEmail', 'OwnerEmail') || '';
      $('companyCardLink').href = `/platform-company-card.html?code=${encodeURIComponent(companyCode)}`;

      if(registered && app){
        $('fAppName').value = value(app, 'appName', 'AppName') || 'PayNex Mobile';
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
        $('fAppName').value = 'PayNex Mobile';
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
      if(editingUserId){
        const stillThere = users.find(u => Number(value(u, 'mobileAppUserId', 'MobileAppUserId')) === Number(editingUserId));
        if(stillThere) loadUserIntoForm(stillThere);
        else clearUserForm();
      }
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
      body.innerHTML = '<tr><td colspan="9" class="bc-empty-row">No mobile users yet.</td></tr>';
      return;
    }
    body.innerHTML = users.map(user => {
      const id = value(user, 'mobileAppUserId', 'MobileAppUserId');
      const blocked = toBool(value(user, 'isBlocked', 'IsBlocked'));
      const selected = Number(id) === Number(editingUserId);
      const password = value(user, 'password', 'Password') || '';
      const status = blocked ? pill('Blocked', 'inactive') : pill('Active', 'active');
      const action = blocked
        ? `<button class="secondary compact-button" type="button" data-unblock-user="${esc(id)}">Unblock</button>`
        : `<button class="danger compact-button" type="button" data-block-user="${esc(id)}">Block</button>`;
      return `<tr data-user-row="${esc(id)}" class="${selected ? 'selected' : ''}" style="cursor:pointer">
        <td>${esc(id)}</td>
        <td>${esc(value(user, 'userName', 'UserName'))}</td>
        <td>${esc(value(user, 'displayName', 'DisplayName'))}</td>
        <td>${esc(value(user, 'email', 'Email'))}</td>
        <td>${esc(value(user, 'mobile', 'Mobile'))}</td>
        <td>${esc(value(user, 'roleName', 'RoleName'))}</td>
        <td style="font-family:Consolas,monospace;white-space:nowrap">${password ? esc(password) : '<span class="muted">Set password again to view</span>'}</td>
        <td>${status}</td>
        <td>${action}</td>
      </tr>`;
    }).join('');
  }

  function userPayload(){
    return {
      userName: $('fUserName').value.trim(),
      displayName: $('fDisplayName').value.trim(),
      email: $('fUserEmail').value.trim(),
      mobile: $('fUserMobile').value.trim(),
      roleName: $('fUserRole').value.trim() || 'Mobile User',
      password: $('fUserPassword').value
    };
  }

  function clearUserForm(){
    editingUserId = 0;
    $('fMobileAppUserId').value = '0';
    $('fUserName').value = '';
    $('fDisplayName').value = '';
    $('fUserEmail').value = '';
    $('fUserMobile').value = '';
    $('fUserRole').value = 'Mobile User';
    $('fUserPassword').value = '';
    $('fUserPassword').type = 'text';
    $('toggleUserPasswordBtn').textContent = 'Hide';
    $('addUserBtn').hidden = false;
    $('saveUserBtn').hidden = true;
    $('cancelUserEditBtn').hidden = true;
    $('userFormNote').textContent = 'User Name must be unique across all companies. Add a new mobile user, or click a user line below to load it here for change and save again.';
    renderUsers();
  }

  function loadUserIntoForm(user){
    editingUserId = Number(value(user, 'mobileAppUserId', 'MobileAppUserId')) || 0;
    $('fMobileAppUserId').value = String(editingUserId);
    $('fUserName').value = value(user, 'userName', 'UserName') || '';
    $('fDisplayName').value = value(user, 'displayName', 'DisplayName') || '';
    $('fUserEmail').value = value(user, 'email', 'Email') || '';
    $('fUserMobile').value = value(user, 'mobile', 'Mobile') || '';
    $('fUserRole').value = value(user, 'roleName', 'RoleName') || 'Mobile User';
    $('fUserPassword').value = value(user, 'password', 'Password') || '';
    $('fUserPassword').type = 'text';
    $('toggleUserPasswordBtn').textContent = 'Hide';
    $('addUserBtn').hidden = true;
    $('saveUserBtn').hidden = false;
    $('cancelUserEditBtn').hidden = false;
    $('userFormNote').textContent = `Editing user #${editingUserId}. Change fields above, then click Save User Changes.`;
    renderUsers();
  }

  function appPayload(){
    return {
      appName: $('fAppName').value.trim() || 'PayNex Mobile',
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
    const payload = userPayload();
    if(!payload.userName){ setMessage('User name is required.', false); return; }
    if(!isAcceptablePassword(payload.password)){
      setMessage('Password must be 8 to 128 characters and include upper-case, lower-case, and a number.', false);
      return;
    }
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app/users`, payload);
      clearUserForm();
      setMessage(result.message || 'Mobile app user added.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function saveUserChanges(){
    if(!editingUserId){ setMessage('Select a user line first.', false); return; }
    const payload = userPayload();
    if(!payload.userName){ setMessage('User name is required.', false); return; }
    if(!isAcceptablePassword(payload.password)){
      setMessage('Password must be 8 to 128 characters and include upper-case, lower-case, and a number.', false);
      return;
    }
    try{
      const result = await api.put(`/api/platform/companies/${encodeURIComponent(companyCode)}/mobile-app/users/${editingUserId}`, payload);
      setMessage(result.message || 'Mobile app user saved.', true);
      await loadDetails();
    }catch(error){
      setMessage(error.message, false);
    }
  }

  async function onUserAction(event){
    const blockBtn = event.target.closest('[data-block-user]');
    const unblockBtn = event.target.closest('[data-unblock-user]');
    if(blockBtn || unblockBtn){
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
      return;
    }

    const row = event.target.closest('tr[data-user-row]');
    if(!row) return;
    const id = Number(row.dataset.userRow);
    const user = users.find(u => Number(value(u, 'mobileAppUserId', 'MobileAppUserId')) === id);
    if(!user) return;
    loadUserIntoForm(user);
    setMessage(`User #${id} loaded for change. Update fields and click Save User Changes.`, true);
  }

  init();
})();
