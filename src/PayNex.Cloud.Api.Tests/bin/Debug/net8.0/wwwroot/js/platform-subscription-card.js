(() => {
  const $ = id => document.getElementById(id);
  const params = new URLSearchParams(location.search);
  let subscriptionId = Number(params.get('id') || 0);
  const isNew = params.get('mode') === 'new' || !subscriptionId;
  const openRenew = params.get('action') === 'renew';
  let plans = [];
  let companies = [];
  let current = null;

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function dateOnly(v){ return v ? String(v).slice(0, 10) : ''; }
  function today(){
    const d = new Date();
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  }
  function money(v){ return Number(v || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }
  function statusClass(text){
    const v = String(text || '').toLowerCase();
    if(v === 'active') return 'active';
    if(v === 'expired' || v === 'cancelled' || v === 'suspended') return 'inactive';
    return 'draft';
  }

  async function ensureOwner(){
    const me = await api.get('/api/me');
    if(!(me.isPlatformOwner || me.IsPlatformOwner)){
      location.href = '/workspace.html';
      throw new Error('InterNex Owner access is required.');
    }
    $('who').textContent = `${me.displayName || me.DisplayName || 'InterNex Owner'} | Platform Super Admin`;
  }

  async function init(){
    try{ await ensureOwner(); }
    catch(error){ msg('status', error.message, false); return; }
    bindEvents();
    await Promise.all([loadCompanies(), loadPlans()]);
    if(isNew){
      setNewMode();
    }else{
      await loadDetail(subscriptionId);
      if(openRenew) showRenew(true);
    }
  }

  function bindEvents(){
    $('backBtn').addEventListener('click', () => location.href = '/platform-subscriptions.html');
    $('newBtn').addEventListener('click', () => location.href = '/platform-subscription-card.html?mode=new');
    $('refreshBtn').addEventListener('click', async () => {
      if(subscriptionId) await loadDetail(subscriptionId);
      else msg('status', 'New subscription — nothing to refresh.', true);
    });
    $('postBtn').addEventListener('click', postNew);
    $('renewBtn').addEventListener('click', () => showRenew(true));
    $('cancelBtn').addEventListener('click', () => showCancel(true));
    $('confirmRenewBtn').addEventListener('click', postRenew);
    $('confirmCancelBtn').addEventListener('click', postCancel);
    $('fPlan').addEventListener('change', previewExpiry);
    $('fStartDate').addEventListener('change', previewExpiry);
    $('fCompany').addEventListener('change', () => {
      const code = $('fCompany').value;
      const company = companies.find(c => String(value(c,'companyCode','CompanyCode')) === code);
      $('factClient').textContent = company ? `${value(company,'companyCode','CompanyCode')} · ${value(company,'companyName','CompanyName')}` : '—';
    });
  }

  async function loadCompanies(){
    companies = await api.get('/api/platform/companies') || [];
    $('fCompany').innerHTML = '<option value="">Select company...</option>' + companies.map(c =>
      `<option value="${esc(value(c,'companyCode','CompanyCode'))}">${esc(value(c,'companyCode','CompanyCode'))} — ${esc(value(c,'companyName','CompanyName'))}</option>`
    ).join('');
  }

  async function loadPlans(){
    plans = await api.get('/api/platform/subscriptions/plans') || [];
    $('fPlan').innerHTML = '<option value="">Select plan...</option>' + plans.map(p =>
      `<option value="${esc(value(p,'subscriptionPlanId','SubscriptionPlanId'))}" data-months="${esc(durationMonths(p))}" data-amount="${esc(value(p,'defaultAmount','DefaultAmount') || '')}">${esc(value(p,'planName','PlanName'))}</option>`
    ).join('');
  }

  function durationMonths(plan){
    const type = String(value(plan,'durationType','DurationType') || 'Month');
    const val = Number(value(plan,'durationValue','DurationValue') || 1);
    return type.toLowerCase() === 'year' ? val * 12 : val;
  }

  function addMonths(dateText, months){
    const parts = String(dateText || '').split('-').map(Number);
    if(parts.length < 3 || !parts[0] || !parts[1] || !parts[2]) return '';
    const d = new Date(parts[0], parts[1] - 1, parts[2]);
    if(Number.isNaN(d.getTime())) return '';
    d.setMonth(d.getMonth() + Number(months || 0));
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  }

  function previewExpiry(){
    if(subscriptionId && current) return;
    const opt = $('fPlan').selectedOptions[0];
    const months = Number(opt?.dataset?.months || 0);
    const start = $('fStartDate').value || today();
    if(!months){ $('fExpiryDate').value = ''; return; }
    $('fExpiryDate').value = addMonths(start, months);
    if(opt?.dataset?.amount) $('fAmount').value = opt.dataset.amount;
  }

  function setNewMode(){
    subscriptionId = 0;
    current = null;
    $('documentTitle').textContent = 'New Subscription';
    $('documentNo').textContent = 'NEW';
    $('documentStatus').textContent = 'New';
    $('documentStatus').className = 'bc-status-pill draft';
    $('fStartDate').value = today();
    $('fCurrency').value = 'PKR';
    $('fPaymentStatus').value = 'Paid';
    $('fPaymentMethod').value = 'Cash';
    $('fAmount').value = '';
    $('fReferenceNo').value = '';
    $('fNotes').value = '';
    $('fExpiryDate').value = '';
    $('fDaysRemaining').value = '';
    $('postBtn').hidden = false;
    $('renewBtn').hidden = true;
    $('cancelBtn').hidden = true;
    $('renewSection').hidden = true;
    $('cancelSection').hidden = true;
    $('fCompany').disabled = false;
    $('fPlan').disabled = false;
    $('fStartDate').readOnly = false;
    $('fAmount').readOnly = false;
    $('historyRows').innerHTML = '<tr><td colspan="7" class="bc-empty-row">No history yet.</td></tr>';
    $('paymentRows').innerHTML = '<tr><td colspan="6" class="bc-empty-row">No payments yet.</td></tr>';
    updateFacts(null);
  }

  async function loadDetail(id){
    const detail = await api.get(`/api/platform/subscriptions/${id}`);
    const sub = detail.subscription || detail.Subscription;
    if(!sub){ msg('status', 'Subscription was not found.', false); return; }
    current = sub;
    subscriptionId = Number(value(sub,'subscriptionId','SubscriptionId'));
    const status = value(sub,'subscriptionStatus','SubscriptionStatus') || 'Active';
    $('documentTitle').textContent = `${value(sub,'companyCode','CompanyCode')} Subscription`;
    $('documentNo').textContent = value(sub,'subscriptionNo','SubscriptionNo') || '—';
    $('documentStatus').textContent = status;
    $('documentStatus').className = `bc-status-pill ${statusClass(status)}`;
    $('fCompany').value = value(sub,'companyCode','CompanyCode');
    $('fCompany').disabled = true;
    $('fPlan').value = String(value(sub,'subscriptionPlanId','SubscriptionPlanId'));
    $('fPlan').disabled = true;
    $('fStartDate').value = dateOnly(value(sub,'startDate','StartDate'));
    $('fStartDate').readOnly = true;
    $('fExpiryDate').value = dateOnly(value(sub,'expiryDate','ExpiryDate'));
    $('fAmount').value = Number(value(sub,'amount','Amount') || 0);
    $('fAmount').readOnly = true;
    $('fCurrency').value = value(sub,'currency','Currency') || 'PKR';
    $('fPaymentStatus').value = value(sub,'paymentStatus','PaymentStatus') || 'Paid';
    $('fDaysRemaining').value = value(sub,'daysRemaining','DaysRemaining') ?? '';
    $('fNotes').value = '';
    $('postBtn').hidden = true;
    const canAct = !['cancelled'].includes(String(status).toLowerCase());
    $('renewBtn').hidden = !canAct;
    $('cancelBtn').hidden = !canAct;
    $('fRenewAmount').value = Number(value(sub,'amount','Amount') || 0);
    $('fCancelDate').value = today();
    renderHistory(detail.history || detail.History || []);
    renderPayments(detail.payments || detail.Payments || []);
    updateFacts(sub);
    msg('status', `Loaded ${value(sub,'subscriptionNo','SubscriptionNo')}.`, true);
  }

  function renderHistory(items){
    $('historyCountLabel').textContent = `${items.length} record${items.length === 1 ? '' : 's'}`;
    $('historyRows').innerHTML = items.length ? items.map(item => `<tr>
      <td>${esc(dateOnly(value(item,'createdAt','CreatedAt')) || '—')}</td>
      <td>${esc(value(item,'actionType','ActionType'))}</td>
      <td>${esc(dateOnly(value(item,'previousExpiryDate','PreviousExpiryDate')) || '—')}</td>
      <td>${esc(dateOnly(value(item,'newExpiryDate','NewExpiryDate')) || '—')}</td>
      <td class="number">${esc(money(value(item,'amount','Amount')))}</td>
      <td>${esc(value(item,'createdBy','CreatedBy') || '—')}</td>
      <td>${esc(value(item,'notes','Notes') || '—')}</td>
    </tr>`).join('') : '<tr><td colspan="7" class="bc-empty-row">No history yet.</td></tr>';
  }

  function renderPayments(items){
    $('paymentCountLabel').textContent = `${items.length} record${items.length === 1 ? '' : 's'}`;
    $('paymentRows').innerHTML = items.length ? items.map(item => `<tr>
      <td>${esc(dateOnly(value(item,'paymentDate','PaymentDate')) || '—')}</td>
      <td>${esc(value(item,'paymentMethod','PaymentMethod') || '—')}</td>
      <td>${esc(value(item,'referenceNo','ReferenceNo') || '—')}</td>
      <td class="number">${esc(money(value(item,'amount','Amount')))}</td>
      <td>${esc(value(item,'status','Status') || '—')}</td>
      <td>${esc(value(item,'createdBy','CreatedBy') || '—')}</td>
    </tr>`).join('') : '<tr><td colspan="6" class="bc-empty-row">No payments yet.</td></tr>';
  }

  function updateFacts(sub){
    if(!sub){
      $('factClient').textContent = '—';
      $('factPlan').textContent = '—';
      $('factStatus').textContent = 'New';
      $('factPayment').textContent = '—';
      $('factExpiry').textContent = '—';
      $('factDays').textContent = '—';
      return;
    }
    $('factClient').textContent = `${value(sub,'companyCode','CompanyCode')} · ${value(sub,'companyName','CompanyName')}`;
    $('factPlan').textContent = value(sub,'planName','PlanName') || '—';
    $('factStatus').textContent = value(sub,'subscriptionStatus','SubscriptionStatus') || '—';
    $('factPayment').textContent = value(sub,'paymentStatus','PaymentStatus') || '—';
    $('factExpiry').textContent = dateOnly(value(sub,'expiryDate','ExpiryDate')) || '—';
    $('factDays').textContent = value(sub,'daysRemaining','DaysRemaining') ?? '—';
  }

  function showRenew(show){
    $('renewSection').hidden = !show;
    if(show) $('cancelSection').hidden = true;
  }
  function showCancel(show){
    $('cancelSection').hidden = !show;
    if(show) $('renewSection').hidden = true;
  }

  async function postNew(){
    try{
      const body = {
        companyCode: $('fCompany').value,
        subscriptionPlanId: Number($('fPlan').value || 0),
        startDate: $('fStartDate').value || null,
        expiryDate: $('fExpiryDate').value || null,
        amount: Number($('fAmount').value || 0),
        currency: $('fCurrency').value || 'PKR',
        paymentStatus: $('fPaymentStatus').value,
        paymentMethod: $('fPaymentMethod').value,
        referenceNo: $('fReferenceNo').value || null,
        notes: $('fNotes').value || null,
      };
      const result = await api.post('/api/platform/subscriptions', body);
      msg('status', result.message || 'Subscription posted.', true);
      location.href = `/platform-subscription-card.html?id=${encodeURIComponent(result.subscriptionId || result.SubscriptionId)}`;
    }catch(error){
      msg('status', error.message, false);
    }
  }

  async function postRenew(){
    if(!subscriptionId){ msg('status', 'Save/open a subscription first.', false); return; }
    try{
      const body = {
        durationMonths: Number($('fRenewMonths').value || 12),
        amount: Number($('fRenewAmount').value || 0),
        currency: $('fCurrency').value || 'PKR',
        paymentStatus: $('fRenewPaymentStatus').value,
        paymentMethod: $('fRenewPaymentMethod').value,
        referenceNo: $('fRenewReferenceNo').value || null,
        notes: 'Renewal',
      };
      const result = await api.post(`/api/platform/subscriptions/${subscriptionId}/renew`, body);
      msg('status', result.message || 'Subscription renewed.', true);
      showRenew(false);
      await loadDetail(subscriptionId);
    }catch(error){
      msg('status', error.message, false);
    }
  }

  async function postCancel(){
    if(!subscriptionId){ msg('status', 'Open a subscription first.', false); return; }
    try{
      const body = {
        cancellationDate: $('fCancelDate').value || null,
        reason: $('fCancelReason').value || '',
      };
      const result = await api.post(`/api/platform/subscriptions/${subscriptionId}/cancel`, body);
      msg('status', result.message || 'Subscription cancelled.', true);
      showCancel(false);
      await loadDetail(subscriptionId);
    }catch(error){
      msg('status', error.message, false);
    }
  }

  init();
})();
