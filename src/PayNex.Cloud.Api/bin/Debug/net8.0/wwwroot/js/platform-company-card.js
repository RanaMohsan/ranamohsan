(() => {
  const $ = id => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  let companyCode = query.get('code') || '';
  let isNew = !companyCode || query.get('mode') === 'new';
  let company = null;
  let currentDetails = null;
  let deleteChallengeId = '';
  let deleteToken = '';
  let deleteModalStep = '';

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
  function statusClass(text){
    const v = String(text || '').toLowerCase();
    if(v === 'active') return 'active';
    if(v === 'trial') return 'trial';
    if(v === 'expired' || v === 'suspended' || v === 'inactive') return 'inactive';
    return '';
  }
  function setMessage(text, ok = true){ msg('status', text || '', ok); }
  function isGmail(email){
    const v = String(email || '').trim().toLowerCase();
    return v.includes('@') && !v.includes(' ') && (v.endsWith('@gmail.com') || v.endsWith('@googlemail.com'));
  }
  function isAcceptablePassword(password){
    return typeof password === 'string' && password.length >= 8 && password.length <= 128 && /[A-Z]/.test(password) && /[a-z]/.test(password) && /\d/.test(password);
  }
  function togglePasswordVisibility(input, button){
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    button.textContent = show ? 'Hide' : 'Show';
  }
  function relatedCount(list){
    return (list || []).filter(item => !(item?.error || item?.Error)).length;
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
    $('desktopAppBtn').addEventListener('click', () => {
      if(!companyCode || isNew){
        setMessage('Save the company first, then open Desktop Detail.', false);
        return;
      }
      location.href = `/platform-desktop-app.html?code=${encodeURIComponent(companyCode)}`;
    });
    $('subscriptionBtn').addEventListener('click', () => {
      if(!companyCode || isNew){
        setMessage('Save the company first, then open Subscriptions.', false);
        return;
      }
      const link = $('openSubscriptionLink');
      if(link?.href && !link.href.endsWith('/platform-subscriptions.html')) location.href = link.href;
      else location.href = `/platform-subscription-card.html?mode=new`;
    });
    $('refreshBtn').addEventListener('click', () => { isNew ? prepareNewCompany() : loadCompany(); });
    $('deleteClientBtn').addEventListener('click', startDeleteClient);
    $('deleteClientCancelBtn').addEventListener('click', closeDeleteClientModal);
    $('deleteClientContinueBtn').addEventListener('click', continueDeleteClient);
    $('deleteClientCode').addEventListener('input', () => { $('deleteClientCode').value = $('deleteClientCode').value.replace(/\D/g,'').slice(0,6); });
    $('deleteClientCode').addEventListener('keydown', event => { if(event.key === 'Enter') continueDeleteClient(); });
    $('deleteClientConfirmCode').addEventListener('keydown', event => { if(event.key === 'Enter') continueDeleteClient(); });
    $('toggleAdminPasswordBtn').addEventListener('click', () => togglePasswordVisibility($('fAdminPassword'), $('toggleAdminPasswordBtn')));

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
    $('documentTitle').textContent = 'New Client Company';
    $('documentNo').textContent = 'NEW';
    $('documentStatus').textContent = 'New';
    $('documentStatus').className = 'bc-status-pill draft';
    document.title = 'InterNex - New Client Company';
    $('saveBtn').innerHTML = '<span>✓</span> Create Company';
    $('openProductionBtn').disabled = true;
    $('createSandboxBtn').disabled = false;
    $('mobileAppBtn').disabled = true;
    $('desktopAppBtn').disabled = true;
    $('subscriptionBtn').disabled = true;
    $('deleteClientBtn').hidden = true;
    $('firstAdminSection').hidden = false;
    $('createSandboxNowField').hidden = false;

    setFields({
      companyCode:'', companyName:'', ownerName:'', ownerEmail:'', ownerMobile:'', subscriptionPlan:'Standard',
      status:'Active', licenseStatus:'Active', companyStartDate:new Date().toISOString(),
      licenseExpiryDate:daysFromNow(30), renewalDate:daysFromNow(30), allowSandbox:false,
      allowMultipleBranches:false, maxBranches:1, productionDatabaseName:'', sandboxDatabaseName:''
    });
    $('fCreateSandbox').checked = false;
    $('fAdminEmail').value = '';
    $('fAdminPassword').value = '';
    $('fAdminPassword').type = 'password';
    $('toggleAdminPasswordBtn').textContent = 'Show';
    updateFactBoxes([], []);
    loadSubscriptionFact('');
    setMessage('Enter company details and a real Gmail for the first administrator, then select Create Company.', true);
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
      document.title = `InterNex - ${value(company, 'companyName', 'CompanyName') || companyCode}`;
      $('saveBtn').innerHTML = '<span>✓</span> Save';
      $('openProductionBtn').disabled = false;
      $('createSandboxBtn').disabled = false;
      $('mobileAppBtn').disabled = false;
      $('desktopAppBtn').disabled = false;
      $('subscriptionBtn').disabled = false;
      $('deleteClientBtn').hidden = false;
      $('firstAdminSection').hidden = true;
      $('createSandboxNowField').hidden = true;

      const users = details.users || details.Users || [];
      const branches = details.branches || details.Branches || [];
      updateFactBoxes(users, branches);
      await loadSubscriptionFact(companyCode);
      setMessage(`Company Card ${companyCode} loaded.`, true);

      if(query.get('print') === '1') setTimeout(() => window.print(), 350);
    }catch(error){
      $('documentTitle').textContent = 'Company Card Not Found';
      setMessage(error.message, false);
    }
  }

  function openDeleteClientModal(step, text, hint){
    deleteModalStep = step;
    $('deleteClientModalText').textContent = text || '';
    $('deleteClientModalHint').textContent = hint || '';
    $('deleteClientCodeLabel').hidden = step !== 'otp';
    $('deleteClientConfirmLabel').hidden = step !== 'confirm';
    if(step === 'otp') $('deleteClientCode').value = '';
    if(step === 'confirm'){
      $('deleteClientConfirmCode').value = '';
      $('deleteClientConfirmCode').placeholder = companyCode;
    }
    $('deleteClientContinueBtn').textContent = step === 'otp' ? 'Verify' : 'Delete permanently';
    $('deleteClientModalTitle').textContent = step === 'otp' ? 'Verify deletion' : 'Confirm deletion';
    const modal = $('deleteClientModal');
    modal.hidden = false;
    modal.style.display = 'flex';
    setTimeout(() => (step === 'otp' ? $('deleteClientCode') : $('deleteClientConfirmCode')).focus(), 40);
  }

  function closeDeleteClientModal(){
    const modal = $('deleteClientModal');
    modal.hidden = true;
    modal.style.display = 'none';
    deleteModalStep = '';
    $('deleteClientContinueBtn').disabled = false;
  }

  async function startDeleteClient(){
    if(!companyCode || isNew){
      setMessage('Save the company first, then delete the client.', false);
      return;
    }
    if(!window.confirm('Are you sure you want to delete this client?')) return;
    try{
      setMessage('Sending a verification code to the InterNex owner email...', true);
      $('deleteClientBtn').disabled = true;
      const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/delete-challenge`, {});
      deleteChallengeId = result.challengeId || result.ChallengeId || '';
      const masked = result.maskedEmail || result.MaskedEmail || 'the owner email';
      openDeleteClientModal('otp', `Enter the 6-digit code sent to ${masked}.`, result.message || '');
      setMessage(result.message || 'Verification code sent.', true);
    }catch(error){
      setMessage(error.message, false);
    }finally{
      $('deleteClientBtn').disabled = false;
    }
  }

  async function continueDeleteClient(){
    if(deleteModalStep === 'otp'){
      const code = ($('deleteClientCode').value || '').replace(/\D/g,'');
      if(code.length !== 6){ setMessage('Enter the 6-digit verification code.', false); return; }
      try{
        $('deleteClientContinueBtn').disabled = true;
        const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/delete-challenge/verify`, {
          challengeId: deleteChallengeId,
          code
        });
        deleteToken = result.deleteToken || result.DeleteToken || '';
        openDeleteClientModal('confirm', 'This permanently drops the SQL database(s) and the client. Continue?', `Type ${companyCode} to confirm.`);
      }catch(error){
        setMessage(error.message, false);
      }finally{
        $('deleteClientContinueBtn').disabled = false;
      }
      return;
    }
    if(deleteModalStep === 'confirm'){
      const typed = ($('deleteClientConfirmCode').value || '').trim();
      if(typed.toUpperCase() !== String(companyCode).toUpperCase()){
        setMessage('Type the company code exactly to confirm deletion.', false);
        return;
      }
      try{
        $('deleteClientContinueBtn').disabled = true;
        setMessage('Deleting client and dropping SQL databases...', true);
        const result = await api.post(`/api/platform/companies/${encodeURIComponent(companyCode)}/delete`, {
          deleteToken,
          confirmCompanyCode: typed
        });
        closeDeleteClientModal();
        setMessage(result.message || 'Client deleted.', true);
        setTimeout(() => { location.href = '/platform-portal.html'; }, 900);
      }catch(error){
        setMessage(error.message, false);
        $('deleteClientContinueBtn').disabled = false;
      }
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
    const adminEmail = $('fAdminEmail').value.trim();
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
      adminEmail,
      adminUserName:adminEmail,
      adminPassword:$('fAdminPassword').value.trim()
    };
  }

  async function saveCompany(){
    const payload = getPayload();
    if(!payload.companyName){ setMessage('Company Name is required.', false); $('fCompanyName').focus(); return; }
    if(payload.ownerEmail && !isGmail(payload.ownerEmail)){
      setMessage('Owner Email must be a real Gmail address (@gmail.com).', false);
      $('fOwnerEmail').focus();
      return;
    }
    if(payload.allowMultipleBranches && payload.maxBranches < 2){
      payload.maxBranches = 2;
      $('fMaxBranches').value = '2';
    }
    if(isNew && !isGmail(payload.adminEmail)){
      setMessage('First administrator must use a real Gmail address (@gmail.com). Login OTP is sent to that inbox.', false);
      $('fAdminEmail').focus();
      return;
    }
    if(isNew && payload.adminPassword && !isAcceptablePassword(payload.adminPassword)){
      setMessage('Admin Password must be 8 to 128 characters and include upper-case, lower-case, and a number. Leave it blank to auto-create a unique password for this company.', false);
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

  function updateFactBoxes(users, branches){
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
    $('factUsers').textContent = String(relatedCount(users || currentDetails?.users || currentDetails?.Users));
    $('factBranches').textContent = String(relatedCount(branches || currentDetails?.branches || currentDetails?.Branches));
  }

  async function loadSubscriptionFact(code){
    const setDash = () => {
      ['factSubPlan','factSubStatus','factSubStart','factSubExpiry','factSubDays','factSubAmount','factSubRenewal'].forEach(id => {
        if($(id)) $(id).textContent = '—';
      });
      if($('openSubscriptionLink')) $('openSubscriptionLink').href = '/platform-subscription-card.html?mode=new';
    };
    if(!code || isNew){ setDash(); return; }
    try{
      const detail = await api.get(`/api/platform/subscriptions/by-company/${encodeURIComponent(code)}`);
      const sub = detail.subscription || detail.Subscription;
      const tenant = detail.tenant || detail.Tenant;
      if(!sub){
        $('factSubPlan').textContent = value(tenant || company || {}, 'subscriptionPlan', 'SubscriptionPlan') || $('fPlan').value || '—';
        $('factSubStatus').textContent = value(tenant || company || {}, 'licenseStatus', 'LicenseStatus') || $('fLicenseStatus').value || '—';
        $('factSubStart').textContent = '—';
        $('factSubExpiry').textContent = displayDate(value(tenant || company || {}, 'licenseExpiryDate', 'LicenseExpiryDate') || value(tenant || company || {}, 'expiryDate', 'ExpiryDate') || $('fLicenseExpiryDate').value);
        $('factSubDays').textContent = '—';
        $('factSubAmount').textContent = value(tenant || {}, 'lastSubscriptionAmount', 'LastSubscriptionAmount')
          ? Number(value(tenant, 'lastSubscriptionAmount', 'LastSubscriptionAmount')).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2})
          : '—';
        $('factSubRenewal').textContent = displayDate(value(tenant || company || {}, 'renewalDate', 'RenewalDate') || $('fRenewalDate').value);
        $('openSubscriptionLink').href = '/platform-subscription-card.html?mode=new';
        $('openSubscriptionLink').textContent = 'Post Subscription';
        return;
      }
      const id = value(sub, 'subscriptionId', 'SubscriptionId');
      $('factSubPlan').textContent = value(sub, 'planName', 'PlanName') || '—';
      $('factSubStatus').textContent = value(sub, 'subscriptionStatus', 'SubscriptionStatus') || '—';
      $('factSubStart').textContent = displayDate(value(sub, 'startDate', 'StartDate'));
      $('factSubExpiry').textContent = displayDate(value(sub, 'expiryDate', 'ExpiryDate'));
      $('factSubDays').textContent = value(sub, 'daysRemaining', 'DaysRemaining') ?? '—';
      $('factSubAmount').textContent = Number(value(sub, 'amount', 'Amount') || 0).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2});
      $('factSubRenewal').textContent = displayDate(value(sub, 'expiryDate', 'ExpiryDate'));
      $('openSubscriptionLink').href = `/platform-subscription-card.html?id=${encodeURIComponent(id)}`;
      $('openSubscriptionLink').textContent = 'Open Subscription Card';
    }catch{
      setDash();
    }
  }

  init();
})();
