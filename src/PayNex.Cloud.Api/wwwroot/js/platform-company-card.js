(() => {
  const $ = id => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  let companyCode = query.get('code') || '';
  let isNew = !companyCode || query.get('mode') === 'new';
  let company = null;
  let currentDetails = null;
  const passwordReceipts = new Map();

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function toBool(v){ return v === true || v === 1 || String(v).toLowerCase() === 'true'; }
  function inputDate(v){
    if(!v) return '';
    const d = new Date(v);
    if(Number.isNaN(d.getTime())) return String(v).slice(0, 10);
    return d.toISOString().slice(0, 10);
  }
  function displayDate(v){ return inputDate(v) || '—'; }
  function daysFromNow(days){
    const d = new Date();
    d.setDate(d.getDate() + days);
    return d.toISOString().slice(0, 10);
  }
  function pill(text, tone = ''){ return `<span class="bc-status-pill ${tone}">${esc(text)}</span>`; }
  function boolPill(v){ return toBool(v) ? pill('Yes', 'active') : pill('No'); }
  function statusClass(text){
    const v = String(text || '').toLowerCase();
    if(v === 'active') return 'active';
    if(v === 'trial') return 'trial';
    if(v === 'expired' || v === 'suspended' || v === 'inactive') return 'inactive';
    return '';
  }
  function setMessage(text, ok = true){ msg('status', text || '', ok); }
  function isAcceptablePassword(password){
    return typeof password === 'string' && password.length >= 8 && password.length <= 128 && /[A-Z]/.test(password) && /[a-z]/.test(password) && /\d/.test(password);
  }
  function togglePasswordVisibility(input, button){
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    button.textContent = show ? 'Hide' : 'Show';
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
    try{ await ensureOwner(); }
    catch(error){ setMessage(error.message, false); return; }
    bindEvents();
    if(isNew) prepareNewCompany();
    else{
      await loadCompany();
      if(query.get('created') === '1'){
        const creationMessage = sessionStorage.getItem('paynex_company_creation_message');
        sessionStorage.removeItem('paynex_company_creation_message');
        setMessage(creationMessage || 'Company created successfully. The first administrator will receive login details by email and OTP on first login.', true);
      }
    }
  }

  function bindEvents(){
    $('backBtn').addEventListener('click', () => location.href = '/platform-portal.html');
    $('newBtn').addEventListener('click', () => location.href = '/platform-company-card.html?mode=new');
    $('saveBtn').addEventListener('click', saveCompany);
    $('printBtn').addEventListener('click', () => window.print());
    $('openProductionBtn').addEventListener('click', openProduction);
    $('createSandboxBtn').addEventListener('click', createSandbox);
    $('mobileAppBtn').addEventListener('click', () => {
      if(!companyCode || isNew){
        setMessage('Save the company first, then open Mobile App Detail.', false);
        return;
      }
      location.href = `/platform-mobile-app.html?code=${encodeURIComponent(companyCode)}`;
    });
    $('refreshBtn').addEventListener('click', () => {
      forgetAllPasswordReceipts();
      isNew ? prepareNewCompany() : loadCompany();
    });
    $('toggleAdminPasswordBtn').addEventListener('click', () => togglePasswordVisibility($('fAdminPassword'), $('toggleAdminPasswordBtn')));
    $('toggleCompanyPasswordBtn').addEventListener('click', () => togglePasswordVisibility($('companyUserPassword'), $('toggleCompanyPasswordBtn')));
    $('toggleCompanyPasswordReceiptBtn').addEventListener('click', () => togglePasswordVisibility($('companyPasswordReceiptValue'), $('toggleCompanyPasswordReceiptBtn')));
    $('saveCompanyUserPasswordBtn').addEventListener('click', saveCompanyUserPassword);
    $('cancelCompanyUserPasswordBtn').addEventListener('click', closePasswordManager);
    $('copyCompanyPasswordReceiptBtn').addEventListener('click', copyCompanyPasswordReceipt);
    $('forgetCompanyPasswordReceiptBtn').addEventListener('click', forgetSelectedPasswordReceipt);
    $('userRows').addEventListener('click', event => {
      const resetButton = event.target.closest('[data-reset-company-user]');
      if(resetButton){ openPasswordManager(Number(resetButton.dataset.resetCompanyUser), false); return; }
      const receiptButton = event.target.closest('[data-view-password-receipt]');
      if(receiptButton) openPasswordManager(Number(receiptButton.dataset.viewPasswordReceipt), true);
    });

    $('fAllowMultiBranch').addEventListener('change', () => {
      if($('fAllowMultiBranch').checked && Number($('fMaxBranches').value || 1) < 2) $('fMaxBranches').value = '2';
      if(!$('fAllowMultiBranch').checked) $('fMaxBranches').value = '1';
      updateFactBoxes();
    });
    $('fAllowSandbox').addEventListener('change', () => {
      if(!$('fAllowSandbox').checked) $('fCreateSandbox').checked = false;
      updateFactBoxes();
    });
    $('fCreateSandbox').addEventListener('change', () => {
      if($('fCreateSandbox').checked) $('fAllowSandbox').checked = true;
      updateFactBoxes();
    });
    ['fPlan','fStatus','fLicenseStatus','fLicenseExpiryDate','fMaxBranches','fCompanyName'].forEach(id => {
      $(id).addEventListener('input', updateFactBoxes);
      $(id).addEventListener('change', updateFactBoxes);
    });
  }

  function prepareNewCompany(){
    isNew = true;
    companyCode = '';
    company = null;
    currentDetails = null;
    forgetAllPasswordReceipts();
    closePasswordManager();
    $('documentTitle').textContent = 'New Client Company';
    $('documentNo').textContent = 'NEW';
    $('documentStatus').textContent = 'New';
    $('documentStatus').className = 'bc-status-pill draft';
    document.title = 'PayNex - New Client Company';
    $('saveBtn').innerHTML = '<span>✓</span> Create Company';
    $('openProductionBtn').disabled = true;
    $('createSandboxBtn').disabled = false;
    $('mobileAppBtn').disabled = true;
    $('firstAdminSection').hidden = false;
    $('createSandboxNowField').hidden = false;
    document.querySelectorAll('.owner-related-section').forEach(section => section.hidden = true);

    setFields({
      companyCode:'', companyName:'', ownerName:'', ownerEmail:'', ownerMobile:'', subscriptionPlan:'Standard',
      status:'Active', licenseStatus:'Active', companyStartDate:new Date().toISOString(),
      licenseExpiryDate:daysFromNow(30), renewalDate:daysFromNow(30), allowSandbox:false,
      allowMultipleBranches:false, maxBranches:1, productionDatabaseName:'', sandboxDatabaseName:''
    });
    $('fCreateSandbox').checked = false;
    $('fAdminEmail').value = '';
    $('fAdminUserName').value = 'admin';
    $('fAdminPassword').value = 'Admin@123';
    $('fAdminPassword').type = 'password';
    $('toggleAdminPasswordBtn').textContent = 'Show';
    renderRelated([], [], [], 'PayNex owner access is portal-only and is not created inside client company databases.');
    updateFactBoxes();
    setMessage('Enter company and first administrator details, then select Create Company.', true);
  }

  async function loadCompany(){
    if(!companyCode){ prepareNewCompany(); return; }
    try{
      setMessage('Loading company card...', true);
      $('documentTitle').textContent = 'Loading Company Card...';
      const details = await api.get(`/api/platform/companies/${encodeURIComponent(companyCode)}/details`);
      currentDetails = details;
      company = details.company || details.Company || {};
      companyCode = value(company, 'companyCode', 'CompanyCode') || companyCode;
      isNew = false;

      setFields(company);
      $('documentTitle').textContent = value(company, 'companyName', 'CompanyName') || 'Company Card';
      $('documentNo').textContent = companyCode;
      const companyStatus = value(company, 'status', 'Status') || 'Active';
      const licenseStatus = value(company, 'licenseStatus', 'LicenseStatus') || 'Active';
      $('documentStatus').textContent = `${companyStatus} / ${licenseStatus}`;
      $('documentStatus').className = `bc-status-pill ${statusClass(companyStatus)}`;
      document.title = `PayNex - ${value(company, 'companyName', 'CompanyName') || companyCode}`;
      $('saveBtn').innerHTML = '<span>✓</span> Save';
      $('openProductionBtn').disabled = false;
      $('createSandboxBtn').disabled = false;
      $('mobileAppBtn').disabled = false;
      $('firstAdminSection').hidden = true;
      $('createSandboxNowField').hidden = true;
      document.querySelectorAll('.owner-related-section').forEach(section => section.hidden = false);

      const users = details.users || details.Users || [];
      const branches = details.branches || details.Branches || [];
      const directory = details.centralDirectory || details.CentralDirectory || [];
      const policy = details.passwordPolicy || details.PasswordPolicy || 'Passwords are securely hashed and cannot be displayed.';
      renderRelated(users, branches, directory, `${policy} PayNex owner email is portal-only and is not created inside client databases.`);
      updateFactBoxes(users, branches, directory);
      setMessage(`Company Card ${companyCode} loaded.`, true);

      if(query.get('print') === '1') setTimeout(() => window.print(), 350);
    }catch(error){
      $('documentTitle').textContent = 'Company Card Not Found';
      setMessage(error.message, false);
    }
  }

  function setFields(c){
    $('fCompanyCode').value = value(c, 'companyCode', 'CompanyCode') || '';
    $('fCompanyName').value = value(c, 'companyName', 'CompanyName') || '';
    $('fOwnerName').value = value(c, 'ownerName', 'OwnerName') || '';
    $('fOwnerEmail').value = value(c, 'ownerEmail', 'OwnerEmail') || '';
    $('fOwnerMobile').value = value(c, 'ownerMobile', 'OwnerMobile') || '';
    $('fCompanyStartDate').value = inputDate(value(c, 'companyStartDate', 'CompanyStartDate') || value(c, 'createdAt', 'CreatedAt'));
    $('fLicenseExpiryDate').value = inputDate(value(c, 'licenseExpiryDate', 'LicenseExpiryDate') || value(c, 'expiryDate', 'ExpiryDate'));
    $('fRenewalDate').value = inputDate(value(c, 'renewalDate', 'RenewalDate'));
    $('fPlan').value = value(c, 'subscriptionPlan', 'SubscriptionPlan') || 'Standard';
    $('fStatus').value = value(c, 'status', 'Status') || 'Active';
    $('fLicenseStatus').value = value(c, 'licenseStatus', 'LicenseStatus') || 'Active';
    $('fMaxBranches').value = Number(value(c, 'maxBranches', 'MaxBranches') || 1);
    $('fAllowSandbox').checked = toBool(value(c, 'allowSandbox', 'AllowSandbox'));
    $('fAllowMultiBranch').checked = toBool(value(c, 'allowMultipleBranches', 'AllowMultipleBranches'));
    $('fProductionDb').value = value(c, 'productionDatabaseName', 'ProductionDatabaseName') || value(c, 'databaseName', 'DatabaseName') || '';
    $('fSandboxDb').value = value(c, 'sandboxDatabaseName', 'SandboxDatabaseName') || '';
  }

  function getPayload(){
    return {
      companyName:$('fCompanyName').value.trim(),
      ownerName:$('fOwnerName').value.trim(),
      ownerEmail:$('fOwnerEmail').value.trim(),
      ownerMobile:$('fOwnerMobile').value.trim(),
      subscriptionPlan:$('fPlan').value,
      status:$('fStatus').value,
      licenseStatus:$('fLicenseStatus').value,
      companyStartDate:$('fCompanyStartDate').value || null,
      licenseExpiryDate:$('fLicenseExpiryDate').value || null,
      renewalDate:$('fRenewalDate').value || null,
      allowSandbox:$('fAllowSandbox').checked || $('fCreateSandbox').checked,
      createSandbox:$('fCreateSandbox').checked,
      allowMultipleBranches:$('fAllowMultiBranch').checked,
      maxBranches:Number($('fMaxBranches').value || 1),
      adminEmail:$('fAdminEmail').value.trim(),
      adminUserName:$('fAdminUserName').value.trim() || 'admin',
      adminPassword:$('fAdminPassword').value || 'Admin@123'
    };
  }

  async function saveCompany(){
    const payload = getPayload();
    if(!payload.companyName){ setMessage('Company Name is required.', false); $('fCompanyName').focus(); return; }
    if(payload.allowMultipleBranches && payload.maxBranches < 2){
      payload.maxBranches = 2;
      $('fMaxBranches').value = '2';
    }
    if(isNew && !payload.adminEmail){ setMessage('First Admin Email is required for a new client.', false); $('fAdminEmail').focus(); return; }
    if(isNew && !isAcceptablePassword(payload.adminPassword)){
      setMessage('Admin Password must be 8 to 128 characters and include upper-case, lower-case, and a number.', false);
      $('fAdminPassword').focus();
      return;
    }

    $('saveBtn').disabled = true;
    try{
      setMessage(isNew ? 'Creating client company and database...' : 'Saving company card...', true);
      if(isNew){
        const result = await api.post('/api/platform/companies', payload);
        const newCode = result.companyCode || result.CompanyCode;
        if(!newCode) throw new Error('Company was created but Company Code was not returned.');
        sessionStorage.setItem('paynex_company_creation_message', result.message || result.Message || 'Company created successfully. First administrator login details were emailed.');
        location.href = `/platform-company-card.html?code=${encodeURIComponent(newCode)}&created=1`;
        return;
      }
      await api.put(`/api/platform/companies/${encodeURIComponent(companyCode)}`, payload);
      await loadCompany();
      setMessage('Company Card saved successfully.', true);
    }catch(error){
      setMessage(error.message, false);
    }finally{
      $('saveBtn').disabled = false;
    }
  }

  async function createSandbox(){
    if(isNew){
      $('fAllowSandbox').checked = true;
      $('fCreateSandbox').checked = true;
      updateFactBoxes();
      setMessage('Sandbox will be created when the new Company Card is saved.', true);
      return;
    }
    try{
      $('createSandboxBtn').disabled = true;
      setMessage('Creating or repairing sandbox database...', true);
      await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/sandbox`, {setAsDefaultEnvironment:false});
      await loadCompany();
      setMessage('Sandbox database is ready.', true);
    }catch(error){ setMessage(error.message, false); }
    finally{ $('createSandboxBtn').disabled = false; }
  }

  async function openProduction(){
    if(isNew || !companyCode){ setMessage('Save the company before opening Production.', false); return; }
    try{
      $('openProductionBtn').disabled = true;
      setMessage('Opening selected company Production ERP...', true);
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/open`, {companyCode, environment:'Production'});
      api.setToken(result.token);
      localStorage.setItem('paynex_last_user', JSON.stringify(result.user || {}));
      localStorage.setItem('paynex_environment', 'Production');
      location.href = result.redirectUrl || '/workspace.html';
    }catch(error){
      $('openProductionBtn').disabled = false;
      setMessage(error.message, false);
    }
  }

  function companyUsers(){
    return currentDetails?.users || currentDetails?.Users || [];
  }

  function findCompanyUser(id){
    return companyUsers().find(user => Number(value(user, 'userId', 'UserId')) === Number(id));
  }

  function renderCurrentRelated(){
    if(!currentDetails) return;
    const policy = currentDetails.passwordPolicy || currentDetails.PasswordPolicy || 'Saved passwords cannot be retrieved.';
    renderRelated(companyUsers(), currentDetails.branches || currentDetails.Branches || [], currentDetails.centralDirectory || currentDetails.CentralDirectory || [], `${policy} PayNex owner email is portal-only and is not created inside client databases.`);
  }

  function openPasswordManager(id, showReceipt){
    const selectedUser = findCompanyUser(id);
    if(!selectedUser){ setMessage('Company user was not found.', false); return; }
    const displayName = value(selectedUser, 'displayName', 'DisplayName') || value(selectedUser, 'userName', 'UserName') || `User ${id}`;
    $('passwordManagerUserId').value = String(id);
    $('passwordManagerUser').textContent = displayName;
    $('passwordManagerSection').hidden = false;
    $('companyPasswordStatus').textContent = '';

    const receipt = passwordReceipts.get(Number(id));
    if(showReceipt && receipt){
      $('companyPasswordEntry').hidden = true;
      $('companyPasswordReceipt').hidden = false;
      $('companyPasswordReceiptValue').value = receipt;
      $('companyPasswordReceiptValue').type = 'password';
      $('toggleCompanyPasswordReceiptBtn').textContent = 'Show';
    }else{
      $('companyPasswordEntry').hidden = false;
      $('companyPasswordReceipt').hidden = true;
      $('companyUserPassword').value = '';
      $('companyUserPasswordConfirm').value = '';
      $('companyUserPassword').type = 'password';
      $('toggleCompanyPasswordBtn').textContent = 'Show';
      setTimeout(() => $('companyUserPassword').focus(), 0);
    }
    $('passwordManagerSection').scrollIntoView({ behavior:'smooth', block:'center' });
  }

  function closePasswordManager(){
    $('companyUserPassword').value = '';
    $('companyUserPasswordConfirm').value = '';
    $('companyPasswordReceiptValue').value = '';
    $('companyPasswordStatus').textContent = '';
    $('passwordManagerUserId').value = '0';
    $('passwordManagerSection').hidden = true;
  }

  function forgetAllPasswordReceipts(){
    passwordReceipts.clear();
    if($('passwordManagerSection')) closePasswordManager();
  }

  async function saveCompanyUserPassword(){
    const id = Number($('passwordManagerUserId').value || 0);
    const newPassword = $('companyUserPassword').value;
    const confirmation = $('companyUserPasswordConfirm').value;
    if(!id){ msg('companyPasswordStatus', 'Select a company user first.', false); return; }
    if(!isAcceptablePassword(newPassword)){
      msg('companyPasswordStatus', 'Password must be 8 to 128 characters and include upper-case, lower-case, and a number.', false);
      return;
    }
    if(newPassword !== confirmation){ msg('companyPasswordStatus', 'Password confirmation does not match.', false); return; }

    $('saveCompanyUserPasswordBtn').disabled = true;
    try{
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/users/${id}/reset-password`, { newPassword });
      passwordReceipts.set(id, newPassword);
      const selectedUser = findCompanyUser(id);
      if(selectedUser){
        selectedUser.hasPassword = true;
        selectedUser.HasPassword = true;
        selectedUser.passwordInfo = 'Password set securely';
        selectedUser.PasswordInfo = 'Password set securely';
      }
      renderCurrentRelated();
      openPasswordManager(id, true);
      msg('companyPasswordStatus', result.message || 'Password saved securely. Copy it before leaving this page.', true);
      setMessage('Company user password saved. A one-time View / Copy receipt is available in the Password column.', true);
    }catch(error){
      msg('companyPasswordStatus', error.message, false);
    }finally{
      $('saveCompanyUserPasswordBtn').disabled = false;
    }
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
    if(!copied) throw new Error('Clipboard copy was blocked. Use Show and copy the password manually.');
  }

  async function copyCompanyPasswordReceipt(){
    const id = Number($('passwordManagerUserId').value || 0);
    const receipt = passwordReceipts.get(id);
    if(!receipt){ msg('companyPasswordStatus', 'This one-time password receipt is no longer available.', false); return; }
    try{
      await copyText(receipt);
      msg('companyPasswordStatus', 'Password copied. Use “Forget now” after sharing it safely.', true);
    }catch(error){ msg('companyPasswordStatus', error.message, false); }
  }

  function forgetSelectedPasswordReceipt(){
    const id = Number($('passwordManagerUserId').value || 0);
    if(id) passwordReceipts.delete(id);
    closePasswordManager();
    renderCurrentRelated();
    setMessage('The one-time password receipt was removed from this page.', true);
  }

  function renderRelated(users, branches, directory, securityText){
    $('userCountLabel').textContent = `${users.length} record${users.length === 1 ? '' : 's'}`;
    $('branchCountLabel').textContent = `${branches.length} record${branches.length === 1 ? '' : 's'}`;
    $('directoryCountLabel').textContent = `${directory.length} record${directory.length === 1 ? '' : 's'}`;

    $('userRows').innerHTML = users.length ? users.map(user => {
      if(user.error || user.Error) return `<tr><td colspan="8" class="bc-empty-row bad">${esc(user.error || user.Error)}</td></tr>`;
      const active = toBool(value(user, 'isActive', 'IsActive'));
      const userId = Number(value(user, 'userId', 'UserId'));
      const hasPassword = toBool(value(user, 'hasPassword', 'HasPassword'));
      const hasReceipt = passwordReceipts.has(userId);
      return `<tr>
        <td>${esc(userId)}</td>
        <td><b>${esc(value(user, 'displayName', 'DisplayName') || value(user, 'userName', 'UserName'))}</b><br><span class="muted small-text">${esc(value(user, 'userName', 'UserName'))}</span></td>
        <td>${esc(value(user, 'email', 'Email'))}</td>
        <td>${esc(value(user, 'roleName', 'RoleName'))}</td>
        <td>${esc([value(user, 'branchCode', 'BranchCode'), value(user, 'branchName', 'BranchName')].filter(Boolean).join(' - '))}</td>
        <td>${pill(active ? 'Active' : 'Inactive', active ? 'active' : 'inactive')}</td>
        <td>${toBool(value(user, 'emailVerified', 'EmailVerified')) ? pill('Verified', 'active') : pill('Pending OTP')}</td>
        <td><div class="password-table-actions">
          ${pill(hasPassword ? 'Set securely' : 'Not set', hasPassword ? 'active' : 'inactive')}
          <button type="button" class="secondary compact-button" data-reset-company-user="${userId}">Set / Reset</button>
          ${hasReceipt ? `<button type="button" class="success compact-button" data-view-password-receipt="${userId}">View / Copy once</button>` : ''}
        </div></td>
      </tr>`;
    }).join('') : '<tr><td colspan="8" class="bc-empty-row">No users found.</td></tr>';

    $('branchRows').innerHTML = branches.length ? branches.map(branch => {
      const active = toBool(value(branch, 'isActive', 'IsActive'));
      return `<tr>
        <td>${esc(value(branch, 'branchId', 'BranchId'))}</td>
        <td>${esc(value(branch, 'branchCode', 'BranchCode'))}</td>
        <td><b>${esc(value(branch, 'branchName', 'BranchName'))}</b></td>
        <td>${esc(value(branch, 'addressLine', 'AddressLine'))}</td>
        <td>${boolPill(value(branch, 'isMainBranch', 'IsMainBranch'))}</td>
        <td>${pill(active ? 'Active' : 'Inactive', active ? 'active' : 'inactive')}</td>
      </tr>`;
    }).join('') : '<tr><td colspan="6" class="bc-empty-row">No branches found.</td></tr>';

    $('directoryRows').innerHTML = directory.length ? directory.map(item => `<tr>
      <td>${esc(value(item, 'directoryUserId', 'DirectoryUserId'))}</td>
      <td>${esc(value(item, 'userId', 'UserId'))}</td>
      <td>${esc(value(item, 'email', 'Email'))}</td>
      <td>${esc(value(item, 'userName', 'UserName'))}</td>
      <td>${esc(value(item, 'roleName', 'RoleName'))}</td>
      <td>${boolPill(value(item, 'isCompanySuperAdmin', 'IsCompanySuperAdmin'))}</td>
      <td>${boolPill(value(item, 'isActive', 'IsActive'))}</td>
      <td>${esc(value(item, 'lastLoginAt', 'LastLoginAt') ? new Date(value(item, 'lastLoginAt', 'LastLoginAt')).toLocaleString() : '—')}</td>
    </tr>`).join('') : '<tr><td colspan="8" class="bc-empty-row">No central directory records found.</td></tr>';

    $('securityNote').textContent = securityText;
  }

  function updateFactBoxes(users, branches, directory){
    const productionDb = $('fProductionDb').value || (isNew ? 'Created on save' : '—');
    const sandboxDb = $('fSandboxDb').value || ($('fAllowSandbox').checked ? 'Allowed / not created' : 'Not allowed');
    $('factCompanyCode').textContent = $('fCompanyCode').value || 'NEW';
    $('factPlan').textContent = $('fPlan').value || 'Standard';
    $('factStatus').textContent = isNew ? 'New' : ($('fStatus').value || 'Active');
    $('factLicense').textContent = $('fLicenseStatus').value || 'Active';
    $('factExpiry').textContent = displayDate($('fLicenseExpiryDate').value);
    $('factProductionDb').textContent = productionDb;
    $('factSandboxDb').textContent = sandboxDb;
    $('factMultiBranch').textContent = $('fAllowMultiBranch').checked ? 'Yes' : 'No';
    $('factMaxBranches').textContent = String(Number($('fMaxBranches').value || 1));
    $('factUsers').textContent = String((users || currentDetails?.users || currentDetails?.Users || []).length);
    $('factBranches').textContent = String((branches || currentDetails?.branches || currentDetails?.Branches || []).length);
    $('factDirectory').textContent = String((directory || currentDetails?.centralDirectory || currentDetails?.CentralDirectory || []).length);
  }

  window.addEventListener('pagehide', forgetAllPasswordReceipts);
  init();
})();
