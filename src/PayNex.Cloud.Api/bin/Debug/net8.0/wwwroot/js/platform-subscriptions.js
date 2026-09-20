(() => {
  const $ = id => document.getElementById(id);
  let rows = [];
  let selectedId = 0;
  let plans = [];

  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function dateOnly(v){ return v ? String(v).slice(0, 10) : ''; }
  function money(v){ return Number(v || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }
  function statusClass(text){
    const v = String(text || '').toLowerCase();
    if(v === 'active') return 'active';
    if(v === 'trial') return 'trial';
    if(v === 'expired' || v === 'cancelled' || v === 'suspended' || v === 'pending') return 'inactive';
    return '';
  }
  function cardUrl(id){
    return id ? `/platform-subscription-card.html?id=${encodeURIComponent(id)}` : '/platform-subscription-card.html?mode=new';
  }
  function selectedRow(){
    return rows.find(x => Number(value(x, 'subscriptionId', 'SubscriptionId')) === Number(selectedId));
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
    await Promise.all([loadPlans(), loadDashboard(), loadRows()]);
  }

  function bindEvents(){
    $('newBtn').addEventListener('click', () => location.href = cardUrl(0));
    $('openBtn').addEventListener('click', openSelected);
    $('renewBtn').addEventListener('click', () => {
      const row = selectedRow();
      if(!row){ msg('status', 'Select a subscription first.', false); return; }
      location.href = `${cardUrl(value(row, 'subscriptionId', 'SubscriptionId'))}&action=renew`;
    });
    $('reportBtn').addEventListener('click', () => location.href = '/platform-subscription-report.html');
    $('refreshBtn').addEventListener('click', async () => {
      await Promise.all([loadDashboard(), loadRows()]);
    });
    $('applyFilterBtn').addEventListener('click', loadRows);
    $('clearFilterBtn').addEventListener('click', () => {
      $('searchInput').value = '';
      $('statusFilter').value = '';
      $('planFilter').value = '0';
      $('expiringFilter').value = '0';
      loadRows();
    });
  }

  async function loadPlans(){
    try{
      plans = await api.get('/api/platform/subscriptions/plans') || [];
      $('planFilter').innerHTML = '<option value="0">All plans</option>' + plans.map(p =>
        `<option value="${esc(value(p,'subscriptionPlanId','SubscriptionPlanId'))}">${esc(value(p,'planName','PlanName'))}</option>`
      ).join('');
    }catch{ plans = []; }
  }

  async function loadDashboard(){
    try{
      const dash = await api.get('/api/platform/subscriptions/dashboard') || {};
      const kpis = dash.kpis || dash.Kpis || {};
      $('kpiTotalClients').textContent = Number(value(kpis,'totalClients','TotalClients') || 0).toLocaleString();
      $('kpiActive').textContent = Number(value(kpis,'activeSubscriptions','ActiveSubscriptions') || 0).toLocaleString();
      $('kpiExpired').textContent = Number(value(kpis,'expiredSubscriptions','ExpiredSubscriptions') || 0).toLocaleString();
      $('kpiExpiring').textContent = Number(value(kpis,'expiringSoon','ExpiringSoon') || 0).toLocaleString();
      $('kpiPending').textContent = Number(value(kpis,'pendingPayments','PendingPayments') || 0).toLocaleString();
      $('kpiCancelled').textContent = Number(value(kpis,'cancelledSubscriptions','CancelledSubscriptions') || 0).toLocaleString();
      $('kpiMonthly').textContent = money(value(kpis,'monthlyRevenue','MonthlyRevenue'));
      $('kpiAnnual').textContent = money(value(kpis,'annualRevenue','AnnualRevenue'));

      const statusRows = dash.statusBreakdown || dash.StatusBreakdown || [];
      const statusMax = Math.max(1, ...statusRows.map(r => Number(value(r,'totalCount','TotalCount') || 0)));
      $('statusChart').innerHTML = statusRows.length
        ? statusRows.map(r => {
            const count = Number(value(r,'totalCount','TotalCount') || 0);
            const pct = Math.round((count / statusMax) * 100);
            return `<div class="sub-bar-row"><span>${esc(value(r,'status','Status'))}</span><div class="sub-bar-track"><div class="sub-bar-fill" style="width:${pct}%"></div></div><b>${count}</b></div>`;
          }).join('')
        : '<div class="muted">No subscription status data yet.</div>';

      const renewals = [
        ['Next 30 days', value(kpis,'renewalsNext30','RenewalsNext30')],
        ['Next 60 days', value(kpis,'renewalsNext60','RenewalsNext60')],
        ['Next 90 days', value(kpis,'renewalsNext90','RenewalsNext90')],
      ];
      const renewMax = Math.max(1, ...renewals.map(x => Number(x[1] || 0)));
      $('renewalChart').innerHTML = renewals.map(([label, count]) => {
        const n = Number(count || 0);
        const pct = Math.round((n / renewMax) * 100);
        return `<div class="sub-bar-row"><span>${esc(label)}</span><div class="sub-bar-track"><div class="sub-bar-fill" style="width:${pct}%"></div></div><b>${n}</b></div>`;
      }).join('');

      const monthly = dash.monthlyRevenue || dash.MonthlyRevenue || [];
      const revMax = Math.max(1, ...monthly.map(r => Number(value(r,'totalAmount','TotalAmount') || 0)));
      $('revenueChart').innerHTML = monthly.length
        ? monthly.map(r => {
            const amount = Number(value(r,'totalAmount','TotalAmount') || 0);
            const pct = Math.round((amount / revMax) * 100);
            const label = `${value(r,'monthName','MonthName') || value(r,'periodLabel','PeriodLabel')}`;
            return `<div class="sub-bar-row"><span>${esc(label)}</span><div class="sub-bar-track"><div class="sub-bar-fill" style="width:${pct}%"></div></div><b>${money(amount)}</b></div>`;
          }).join('')
        : '<div class="muted">No paid subscription revenue yet.</div>';
    }catch(error){
      msg('status', error.message, false);
    }
  }

  async function loadRows(){
    try{
      $('rows').innerHTML = '<tr><td colspan="9" class="bc-empty-row">Loading subscriptions...</td></tr>';
      const qs = new URLSearchParams();
      const term = $('searchInput').value.trim();
      const status = $('statusFilter').value;
      const planId = $('planFilter').value;
      const expiring = $('expiringFilter').value;
      if(term) qs.set('term', term);
      if(status) qs.set('status', status);
      if(planId && planId !== '0') qs.set('planId', planId);
      if(expiring && expiring !== '0') qs.set('expiringWithinDays', expiring);
      rows = await api.get(`/api/platform/subscriptions${qs.toString() ? `?${qs}` : ''}`) || [];
      if(selectedId && !rows.some(x => Number(value(x,'subscriptionId','SubscriptionId')) === Number(selectedId))) selectedId = 0;
      renderRows();
      msg('status', `${rows.length} subscription record(s) loaded.`, true);
    }catch(error){
      rows = [];
      $('rows').innerHTML = `<tr><td colspan="9" class="bc-empty-row bad">${esc(error.message)}</td></tr>`;
      updateSummary();
      msg('status', error.message, false);
    }
  }

  function renderRows(){
    const body = $('rows');
    if(!rows.length){
      body.innerHTML = '<tr><td colspan="9" class="bc-empty-row">No subscriptions match the current filter.</td></tr>';
      updateSummary();
      return;
    }
    body.innerHTML = rows.map(row => {
      const id = Number(value(row,'subscriptionId','SubscriptionId'));
      const selected = id === Number(selectedId);
      const status = value(row,'subscriptionStatus','SubscriptionStatus') || 'Active';
      const client = `${value(row,'companyCode','CompanyCode')} · ${value(row,'companyName','CompanyName')}`;
      return `<tr class="${selected ? 'selected' : ''}" data-id="${id}" tabindex="0">
        <td class="bc-select-col"><input type="radio" name="selectedSubscription" ${selected ? 'checked' : ''}></td>
        <td><a class="bc-record-link" href="${cardUrl(id)}">${esc(value(row,'subscriptionNo','SubscriptionNo'))}</a></td>
        <td>${esc(client)}</td>
        <td>${esc(value(row,'planName','PlanName'))}</td>
        <td class="number">${esc(money(value(row,'amount','Amount')))}</td>
        <td>${esc(dateOnly(value(row,'startDate','StartDate')) || '—')}</td>
        <td>${esc(dateOnly(value(row,'expiryDate','ExpiryDate')) || '—')}</td>
        <td><span class="bc-status-pill ${statusClass(value(row,'paymentStatus','PaymentStatus'))}">${esc(value(row,'paymentStatus','PaymentStatus') || '—')}</span></td>
        <td><span class="bc-status-pill ${statusClass(status)}">${esc(status)}</span></td>
      </tr>`;
    }).join('');

    body.querySelectorAll('tr[data-id]').forEach(tr => {
      tr.addEventListener('click', event => {
        if(event.target.closest('a')) return;
        selectedId = Number(tr.dataset.id);
        renderRows();
      });
      tr.addEventListener('dblclick', () => {
        selectedId = Number(tr.dataset.id);
        openSelected();
      });
    });
    updateSummary();
  }

  function updateSummary(){
    const selected = selectedRow();
    $('recordCount').textContent = `${rows.length} record${rows.length === 1 ? '' : 's'}`;
    $('selectedCaption').textContent = selected
      ? `Selected: ${value(selected,'subscriptionNo','SubscriptionNo')} — ${value(selected,'companyCode','CompanyCode')}`
      : 'No subscription selected';
    const active = rows.filter(x => String(value(x,'subscriptionStatus','SubscriptionStatus')).toLowerCase() === 'active').length;
    $('footerSummary').textContent = `${active} active subscription${active === 1 ? '' : 's'}`;
  }

  function openSelected(){
    const selected = selectedRow();
    if(!selected){ msg('status', 'Select a subscription first.', false); return; }
    location.href = cardUrl(value(selected,'subscriptionId','SubscriptionId'));
  }

  init();
})();
