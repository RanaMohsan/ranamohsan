(() => {
  const $ = id => document.getElementById(id);
  function esc(value){
    return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  }
  function value(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function dateOnly(v){ return v ? String(v).slice(0, 10) : '—'; }
  function money(v){ return Number(v || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }

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
    $('backBtn').addEventListener('click', () => location.href = '/platform-subscriptions.html');
    $('loadBtn').addEventListener('click', loadReport);
    $('printBtn').addEventListener('click', printHtml);
    $('reportType').addEventListener('change', () => {
      $('statusFilter').disabled = $('reportType').value !== 'register';
    });
    $('statusFilter').disabled = false;
    await loadReport();
  }

  async function loadReport(){
    try{
      const type = $('reportType').value;
      const status = $('statusFilter').value;
      const qs = new URLSearchParams({ type });
      if(type === 'register' && status) qs.set('status', status);
      const report = await api.get(`/api/platform/subscriptions/report?${qs}`);
      const rows = report.rows || report.Rows || [];
      $('recordCount').textContent = `${rows.length} record${rows.length === 1 ? '' : 's'}`;
      if(type === 'revenue'){
        $('reportTitle').textContent = 'Monthly Revenue';
        $('head').innerHTML = '<tr><th>Period</th><th>Month</th><th class="number">Payments</th><th class="number">Amount</th></tr>';
        $('rows').innerHTML = rows.length ? rows.map(r => `<tr>
          <td>${esc(value(r,'periodLabel','PeriodLabel'))}</td>
          <td>${esc(value(r,'monthName','MonthName'))} ${esc(value(r,'yearNo','YearNo'))}</td>
          <td class="number">${esc(value(r,'paymentCount','PaymentCount'))}</td>
          <td class="number">${esc(money(value(r,'totalAmount','TotalAmount')))}</td>
        </tr>`).join('') : '<tr><td colspan="4" class="bc-empty-row">No revenue rows.</td></tr>';
      }else if(type === 'expiry'){
        $('reportTitle').textContent = 'Expiry Report (0-90 days)';
        $('head').innerHTML = '<tr><th>Subscription</th><th>Client</th><th>Plan</th><th class="number">Amount</th><th>Expiry</th><th>Days</th><th>Bucket</th><th>Status</th></tr>';
        $('rows').innerHTML = rows.length ? rows.map(r => `<tr>
          <td>${esc(value(r,'subscriptionNo','SubscriptionNo'))}</td>
          <td>${esc(value(r,'companyCode','CompanyCode'))} · ${esc(value(r,'companyName','CompanyName'))}</td>
          <td>${esc(value(r,'planName','PlanName'))}</td>
          <td class="number">${esc(money(value(r,'amount','Amount')))}</td>
          <td>${esc(dateOnly(value(r,'expiryDate','ExpiryDate')))}</td>
          <td class="number">${esc(value(r,'daysRemaining','DaysRemaining'))}</td>
          <td>${esc(value(r,'expiryBucket','ExpiryBucket'))}</td>
          <td>${esc(value(r,'subscriptionStatus','SubscriptionStatus'))}</td>
        </tr>`).join('') : '<tr><td colspan="8" class="bc-empty-row">No expiring subscriptions.</td></tr>';
      }else{
        $('reportTitle').textContent = 'Subscription Register';
        $('head').innerHTML = '<tr><th>Subscription</th><th>Client</th><th>Plan</th><th class="number">Amount</th><th>Start</th><th>Expiry</th><th>Payment</th><th>Status</th></tr>';
        $('rows').innerHTML = rows.length ? rows.map(r => `<tr>
          <td>${esc(value(r,'subscriptionNo','SubscriptionNo'))}</td>
          <td>${esc(value(r,'companyCode','CompanyCode'))} · ${esc(value(r,'companyName','CompanyName'))}</td>
          <td>${esc(value(r,'planName','PlanName'))}</td>
          <td class="number">${esc(money(value(r,'amount','Amount')))}</td>
          <td>${esc(dateOnly(value(r,'startDate','StartDate')))}</td>
          <td>${esc(dateOnly(value(r,'expiryDate','ExpiryDate')))}</td>
          <td>${esc(value(r,'paymentStatus','PaymentStatus'))}</td>
          <td>${esc(value(r,'subscriptionStatus','SubscriptionStatus'))}</td>
        </tr>`).join('') : '<tr><td colspan="8" class="bc-empty-row">No subscription rows.</td></tr>';
      }
      msg('status', `${rows.length} report row(s) loaded.`, true);
    }catch(error){
      msg('status', error.message, false);
    }
  }

  function printHtml(){
    const type = $('reportType').value;
    const status = $('statusFilter').value;
    const qs = new URLSearchParams({ type });
    if(type === 'register' && status) qs.set('status', status);
    window.open(`/api/platform/subscriptions/report/html?${qs}`, '_blank', 'noopener');
  }

  init();
})();
