(() => {
  const $ = id => document.getElementById(id);
  function money(v){ return 'Rs. ' + Number(v || 0).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2}); }
  function val(row, camel, pascal){ return row?.[camel] ?? row?.[pascal] ?? ''; }
  function todayIso(){ return new Date().toISOString().slice(0,10); }

  async function init(){
    try{
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName || me.CompanyName} | ${me.displayName || me.DisplayName} | ${me.branchName || me.BranchName || me.storeName || ''}`;
    }catch{ location.href='/login.html'; return; }
    $('businessDate').value = todayIso();
    await loadPreview();
    await loadReport();
  }

  async function loadPreview(){
    try{
      const date = $('businessDate').value || todayIso();
      const data = await api.get(`/api/day-closing/preview?businessDate=${encodeURIComponent(date)}`);
      const s = data.summary || data.Summary || {};
      const closed = !!(data.isClosed ?? data.IsClosed);
      $('dayBadge').textContent = closed ? 'Closed' : 'Open';
      $('dayBadge').className = 'status-pill ' + (closed ? 'warn-pill' : 'ok-pill');
      $('dayTitle').textContent = `Business Day ${date}`;
      $('dayText').textContent = closed
        ? `Closed by ${val(data.closing || data.Closing || {}, 'closedByName', 'ClosedByName') || 'user'} at ${val(data.closing || data.Closing || {}, 'closedAt', 'ClosedAt') || ''}`
        : 'Day is open. Close all shifts, then close the day.';
      $('kSales').textContent = money(val(s,'totalSales','TotalSales'));
      $('kProfit').textContent = money(val(s,'totalProfit','TotalProfit'));
      $('kRefunds').textContent = money(val(s,'totalRefunds','TotalRefunds'));
      $('closeDayBtn').disabled = closed;
      msg('dayStatus', closed ? 'This day is already closed.' : `${val(s,'invoiceCount','InvoiceCount')||0} invoice(s), ${val(s,'shiftCount','ShiftCount')||0} shift(s).`, true);
    }catch(e){ msg('dayStatus', e.message, false); }
  }

  async function closeDay(){
    try{
      const r = await api.post('/api/day-closing/close', {
        businessDate: $('businessDate').value || todayIso(),
        remarks: $('closeRemarks').value.trim()
      });
      msg('dayStatus', r.message || 'Day closed.', true);
      await loadPreview();
      await loadReport();
    }catch(e){ msg('dayStatus', e.message, false); }
  }

  function kpi(label, value){ return `<div><span>${label}</span><b>${value}</b></div>`; }

  async function loadReport(){
    try{
      const date = $('businessDate').value || todayIso();
      const r = await api.get(`/api/day-closing/report?businessDate=${encodeURIComponent(date)}`);
      const s = r.summary || r.Summary || {};
      const closing = r.closing || r.Closing || {};
      $('dayReport').innerHTML = [
        kpi('Company', r.companyName || r.CompanyName || ''),
        kpi('Branch', `${r.branchCode || r.BranchCode || ''} — ${r.branchName || r.BranchName || ''}`),
        kpi('Business Date', r.businessDate || r.BusinessDate || date),
        kpi('Status', (r.isClosed ?? r.IsClosed) ? 'Closed' : 'Open (preview)'),
        kpi('Closed At', val(closing,'closedAt','ClosedAt') || '—'),
        kpi('Closed By', val(closing,'closedByName','ClosedByName') || '—'),
        kpi('Shifts', val(s,'shiftCount','ShiftCount')),
        kpi('Invoices', val(s,'invoiceCount','InvoiceCount')),
        kpi('Returns', val(s,'returnCount','ReturnCount')),
        kpi('Total Sales', money(val(s,'totalSales','TotalSales'))),
        kpi('Cash Sales', money(val(s,'cashSales','CashSales'))),
        kpi('Bank Sales', money(val(s,'bankSales','BankSales'))),
        kpi('Non-Cash Sales', money(val(s,'nonCashSales','NonCashSales'))),
        kpi('Opening Cash', money(val(s,'openingCashBalance','OpeningCashBalance'))),
        kpi('Cash In (today)', money(val(s,'cashInflow','CashInflow'))),
        kpi('Cash Out (today)', money(val(s,'cashOutflow','CashOutflow'))),
        kpi('Closing Cash', money(val(s,'closingCashBalance','ClosingCashBalance'))),
        kpi('Opening Bank', money(val(s,'openingBankBalance','OpeningBankBalance'))),
        kpi('Bank In (today)', money(val(s,'bankInflow','BankInflow'))),
        kpi('Bank Out (today)', money(val(s,'bankOutflow','BankOutflow'))),
        kpi('Closing Bank', money(val(s,'closingBankBalance','ClosingBankBalance'))),
        kpi('Total Tax', money(val(s,'totalTax','TotalTax'))),
        kpi('Total Discount', money(val(s,'totalDiscount','TotalDiscount'))),
        kpi('Total Cost', money(val(s,'totalCost','TotalCost'))),
        kpi('Total Profit', money(val(s,'totalProfit','TotalProfit'))),
        kpi('Total Refunds', money(val(s,'totalRefunds','TotalRefunds')))
      ].join('');

      const bankBalances = r.bankBalances || r.BankBalances || [];
      if(bankBalances.length){
        $('cashBankBreakdown').innerHTML = `<h3>Cash &amp; Bank Balances</h3>
          <div class="branch-table-wrap"><table class="branch-table">
            <thead><tr><th>Account</th><th>Code / Name</th><th>Opening</th><th>In</th><th>Out</th><th>Closing</th></tr></thead>
            <tbody>
              <tr>
                <td>Cash</td>
                <td>Cash Account</td>
                <td>${money(val(s,'openingCashBalance','OpeningCashBalance'))}</td>
                <td>${money(val(s,'cashInflow','CashInflow'))}</td>
                <td>${money(val(s,'cashOutflow','CashOutflow'))}</td>
                <td>${money(val(s,'closingCashBalance','ClosingCashBalance'))}</td>
              </tr>
              ${bankBalances.map(x => `<tr>
                <td>Bank</td>
                <td>${esc(val(x,'bankCode','BankCode'))} — ${esc(val(x,'bankName','BankName'))} (${esc(val(x,'accountNo','AccountNo'))})</td>
                <td>${money(val(x,'openingBalance','OpeningBalance'))}</td>
                <td>${money(val(x,'inflow','Inflow'))}</td>
                <td>${money(val(x,'outflow','Outflow'))}</td>
                <td>${money(val(x,'closingBalance','ClosingBalance'))}</td>
              </tr>`).join('')}
            </tbody>
          </table></div>`;
      }else{
        $('cashBankBreakdown').innerHTML = `<h3>Cash &amp; Bank Balances</h3>
          <div class="z-summary-grid">
            ${kpi('Opening Cash', money(val(s,'openingCashBalance','OpeningCashBalance')))}
            ${kpi('Closing Cash', money(val(s,'closingCashBalance','ClosingCashBalance')))}
            ${kpi('Opening Bank', money(val(s,'openingBankBalance','OpeningBankBalance')))}
            ${kpi('Closing Bank', money(val(s,'closingBankBalance','ClosingBankBalance')))}
          </div>`;
      }

      const branches = r.branches || r.Branches || [];
      if(branches.length){
        $('branchBreakdown').innerHTML = `<h3>Branch-wise Summary</h3>
          <div class="branch-table-wrap"><table class="branch-table">
            <thead><tr><th>Branch</th><th>Name</th><th>Shifts</th><th>Invoices</th><th>Sales</th><th>Tax</th><th>Cost</th><th>Refunds</th><th>Profit</th><th>Day</th></tr></thead>
            <tbody>${branches.map(x => {
              const sales = Number(val(x,'totalSales','TotalSales')||0);
              const tax = Number(val(x,'totalTax','TotalTax')||0);
              const cost = Number(val(x,'totalCost','TotalCost')||0);
              const refunds = Number(val(x,'totalRefunds','TotalRefunds')||0);
              const profit = sales - tax - cost - refunds;
              const closed = !!(val(x,'isClosed','IsClosed'));
              return `<tr>
                <td>${esc(val(x,'branchCode','BranchCode'))}</td>
                <td>${esc(val(x,'branchName','BranchName'))}</td>
                <td>${val(x,'shiftCount','ShiftCount')}</td>
                <td>${val(x,'invoiceCount','InvoiceCount')}</td>
                <td>${money(sales)}</td>
                <td>${money(tax)}</td>
                <td>${money(cost)}</td>
                <td>${money(refunds)}</td>
                <td>${money(profit)}</td>
                <td>${closed ? 'Closed' : 'Open'}</td>
              </tr>`;
            }).join('')}</tbody>
          </table></div>`;
      }else{
        $('branchBreakdown').innerHTML = '';
      }

      const shifts = r.shifts || r.Shifts || [];
      if(!shifts.length){
        $('shiftBreakdown').innerHTML = '<p class="muted">No shifts for this date on the current branch.</p>';
        return;
      }
      $('shiftBreakdown').innerHTML = `<h3>Shift-wise Summary (Current Branch)</h3>
        <div class="branch-table-wrap"><table class="branch-table">
          <thead><tr><th>Shift</th><th>Counter</th><th>Cashier</th><th>Status</th><th>Sales</th><th>Refunds</th><th>Opened</th><th>Closed</th></tr></thead>
          <tbody>${shifts.map(x => `<tr>
            <td>#${val(x,'shiftId','ShiftId')}</td>
            <td>${esc(val(x,'counterName','CounterName'))}</td>
            <td>${esc(val(x,'cashier','Cashier'))}</td>
            <td>${esc(val(x,'status','Status'))}</td>
            <td>${money(val(x,'shiftSales','ShiftSales'))}</td>
            <td>${money(val(x,'shiftRefunds','ShiftRefunds'))}</td>
            <td>${esc(String(val(x,'openedAt','OpenedAt')).slice(0,19).replace('T',' '))}</td>
            <td>${esc(String(val(x,'closedAt','ClosedAt')||'—').slice(0,19).replace('T',' '))}</td>
          </tr>`).join('')}</tbody>
        </table></div>`;
    }catch(e){
      $('dayReport').innerHTML = `<p class="muted">${esc(e.message)}</p>`;
    }
  }

  function esc(s){ return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

  window.loadPreview = loadPreview;
  window.closeDay = closeDay;
  window.loadReport = loadReport;
  init();
})();
