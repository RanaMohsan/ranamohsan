(() => {
  const $ = id => document.getElementById(id);
  let companies = [];
  let filteredCompanies = [];
  let selectedCode = '';

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function boolValue(row, camel, pascal){
    const v = value(row, camel, pascal);
    return v === true || v === 1 || String(v).toLowerCase() === 'true';
  }
  function dateOnly(v){ return v ? String(v).slice(0, 10) : ''; }
  function selectedCompany(){
    return companies.find(x => String(value(x, 'companyCode', 'CompanyCode')) === String(selectedCode));
  }
  function companyCardUrl(code, print = false){
    const query = new URLSearchParams();
    if(code) query.set('code', code);
    else query.set('mode', 'new');
    if(print) query.set('print', '1');
    return `/platform-company-card.html?${query}`;
  }
  function statusClass(text){
    const value = String(text || '').toLowerCase();
    if(value === 'active') return 'active';
    if(value === 'trial') return 'trial';
    if(value === 'expired' || value === 'suspended' || value === 'inactive') return 'inactive';
    return '';
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
    catch(error){ msg('status', error.message, false); return; }
    bindEvents();
    await loadCompanies();
  }

  function bindEvents(){
    $('newCompanyBtn').addEventListener('click', () => location.href = companyCardUrl(''));
    $('openCompanyBtn').addEventListener('click', openSelected);
    $('printCompanyBtn').addEventListener('click', printSelected);
    $('refreshCompaniesBtn').addEventListener('click', loadCompanies);
    $('subscriptionsBtn')?.addEventListener('click', () => location.href = '/platform-subscriptions.html');
    $('emailSecurityBtn').addEventListener('click', () => location.href = '/owner-email-security.html');
    $('apiDocsBtn').addEventListener('click', () => window.open('/swagger/index.html', '_blank', 'noopener'));
    $('applyFilterBtn').addEventListener('click', applyFilters);
    $('clearFilterBtn').addEventListener('click', () => {
      $('companySearch').value = '';
      $('statusFilter').value = '';
      $('licenseFilter').value = '';
      applyFilters();
    });
    $('companySearch').addEventListener('input', applyFilters);
    $('statusFilter').addEventListener('change', applyFilters);
    $('licenseFilter').addEventListener('change', applyFilters);
    document.addEventListener('keydown', event => {
      if(event.altKey && event.key.toLowerCase() === 'n') location.href = companyCardUrl('');
      if(event.key === 'Enter' && document.activeElement?.closest?.('#companyRows')) openSelected();
    });
  }

  async function loadCompanies(){
    try{
      $('companyRows').innerHTML = '<tr><td colspan="12" class="bc-empty-row">Loading registered companies...</td></tr>';
      companies = await api.get('/api/platform/companies') || [];
      if(selectedCode && !companies.some(x => String(value(x, 'companyCode', 'CompanyCode')) === String(selectedCode))) selectedCode = '';
      applyFilters();
      msg('status', `${companies.length} registered company record(s) loaded.`, true);
    }catch(error){
      companies = [];
      filteredCompanies = [];
      $('companyRows').innerHTML = `<tr><td colspan="12" class="bc-empty-row bad">${esc(error.message)}</td></tr>`;
      updateSummary();
      msg('status', error.message, false);
    }
  }

  function applyFilters(){
    const term = $('companySearch').value.trim().toLowerCase();
    const status = $('statusFilter').value.toLowerCase();
    const license = $('licenseFilter').value.toLowerCase();
    filteredCompanies = companies.filter(company => {
      const companyStatus = String(value(company, 'status', 'Status')).toLowerCase();
      const licenseStatus = String(value(company, 'licenseStatus', 'LicenseStatus')).toLowerCase();
      const matchesTerm = !term || [
        value(company, 'companyCode', 'CompanyCode'),
        value(company, 'companyName', 'CompanyName'),
        value(company, 'ownerName', 'OwnerName'),
        value(company, 'ownerEmail', 'OwnerEmail'),
        value(company, 'ownerMobile', 'OwnerMobile'),
        value(company, 'subscriptionPlan', 'SubscriptionPlan')
      ].some(v => String(v ?? '').toLowerCase().includes(term));
      return matchesTerm && (!status || companyStatus === status) && (!license || licenseStatus === license);
    });
    renderCompanies();
  }

  function renderCompanies(){
    const body = $('companyRows');
    if(!filteredCompanies.length){
      body.innerHTML = '<tr><td colspan="12" class="bc-empty-row">No companies match the current filter.</td></tr>';
      updateSummary();
      return;
    }

    body.innerHTML = filteredCompanies.map(company => {
      const code = String(value(company, 'companyCode', 'CompanyCode'));
      const name = value(company, 'companyName', 'CompanyName');
      const selected = code === String(selectedCode);
      const companyStatus = value(company, 'status', 'Status') || 'Active';
      const licenseStatus = value(company, 'licenseStatus', 'LicenseStatus') || 'Active';
      return `<tr class="${selected ? 'selected' : ''}" data-code="${esc(code)}" tabindex="0">
        <td class="bc-select-col"><input type="radio" name="selectedCompany" aria-label="Select ${esc(name)}" ${selected ? 'checked' : ''}></td>
        <td><a class="bc-record-link" href="${companyCardUrl(code)}">${esc(code)}</a></td>
        <td><a class="bc-record-link owner-company-name-link" href="${companyCardUrl(code)}">${esc(name)}</a></td>
        <td>${esc(value(company, 'ownerName', 'OwnerName'))}</td>
        <td>${esc(value(company, 'ownerEmail', 'OwnerEmail'))}</td>
        <td>${esc(value(company, 'subscriptionPlan', 'SubscriptionPlan') || 'Standard')}</td>
        <td><span class="bc-status-pill ${statusClass(companyStatus)}">${esc(companyStatus)}</span></td>
        <td><span class="bc-status-pill ${statusClass(licenseStatus)}">${esc(licenseStatus)}</span></td>
        <td class="number">${Number(value(company, 'maxBranches', 'MaxBranches') || 1).toLocaleString()}</td>
        <td>${boolValue(company, 'allowSandbox', 'AllowSandbox') ? '<span class="bc-status-pill active">Allowed</span>' : '<span class="bc-status-pill">No</span>'}</td>
        <td>${esc(dateOnly(value(company, 'licenseExpiryDate', 'LicenseExpiryDate') || value(company, 'expiryDate', 'ExpiryDate')) || '—')}</td>
        <td>${esc(dateOnly(value(company, 'createdAt', 'CreatedAt')) || '—')}</td>
      </tr>`;
    }).join('');

    body.querySelectorAll('tr[data-code]').forEach(row => {
      row.addEventListener('click', event => {
        if(event.target.closest('a')) return;
        selectRow(row.dataset.code);
      });
      row.addEventListener('dblclick', () => {
        selectRow(row.dataset.code);
        openSelected();
      });
      row.addEventListener('keydown', event => {
        if(event.key === 'Enter'){
          selectRow(row.dataset.code);
          openSelected();
        }
      });
    });
    updateSummary();
  }

  function selectRow(code){
    selectedCode = String(code || '');
    renderCompanies();
  }

  function updateSummary(){
    const selected = selectedCompany();
    $('recordCount').textContent = `${filteredCompanies.length} record${filteredCompanies.length === 1 ? '' : 's'}`;
    $('selectedCaption').textContent = selected
      ? `Selected: ${value(selected, 'companyCode', 'CompanyCode')} — ${value(selected, 'companyName', 'CompanyName')}`
      : 'No company selected';
    const active = filteredCompanies.filter(x => String(value(x, 'status', 'Status')).toLowerCase() === 'active').length;
    const sandbox = filteredCompanies.filter(x => boolValue(x, 'allowSandbox', 'AllowSandbox')).length;
    $('footerSummary').textContent = `${active} active compan${active === 1 ? 'y' : 'ies'} • ${sandbox} sandbox-enabled`;
  }

  function openSelected(){
    const selected = selectedCompany();
    if(!selected){ msg('status', 'Select a company first.', false); return; }
    location.href = companyCardUrl(value(selected, 'companyCode', 'CompanyCode'));
  }

  function printSelected(){
    const selected = selectedCompany();
    if(!selected){ msg('status', 'Select a company first.', false); return; }
    const url = companyCardUrl(value(selected, 'companyCode', 'CompanyCode'), true);
    const popup = window.open(url, '_blank');
    if(!popup) location.href = url;
  }

  init();
})();
