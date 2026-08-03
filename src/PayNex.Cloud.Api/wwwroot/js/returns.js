(() => {
  const $ = id => document.getElementById(id);
  let returnLines = [];

  async function init(){
    try{
      const me = await api.get('/api/me');
      $('who').textContent = `${me.companyName || me.CompanyName} | ${me.displayName || me.DisplayName} | ${me.branchName || me.BranchName || ''}`;
    }catch{ location.href='/login.html'; }
  }

  async function loadLines(){
    try{
      const inv = ($('invoiceNo').value || '').trim();
      if(!inv) throw new Error('Invoice number is required.');
      returnLines = await api.get('/api/returns/sale-lines?invoiceNo=' + encodeURIComponent(inv));
      const rows = returnLines || [];
      $('returnLinesBody').innerHTML = rows.length
        ? rows.map(x => {
            const id = x.saleLineId ?? x.SaleLineId;
            const avail = Number(x.availableToReturn ?? x.AvailableToReturn ?? 0);
            const sold = Number(x.soldQuantity ?? x.SoldQuantity ?? 0);
            const name = x.productName ?? x.ProductName ?? '';
            return `<tr>
              <td><input style="width:auto" type="checkbox" data-id="${id}" ${avail>0?'checked':''} ${avail>0?'':'disabled'}></td>
              <td>${esc(name)}</td>
              <td>${sold}</td>
              <td>${avail}</td>
              <td><input style="max-width:110px" type="number" min="0" max="${avail}" step="0.01" value="${avail}" data-qty="${id}" ${avail>0?'':'disabled'}></td>
            </tr>`;
          }).join('')
        : '<tr><td colspan="5" class="muted">No returnable lines found for this invoice.</td></tr>';
      msg('returnStatus', rows.length ? `${rows.length} line(s) loaded.` : 'No lines found.', true);
    }catch(e){
      $('returnLinesBody').innerHTML = '<tr><td colspan="5" class="muted">Could not load lines.</td></tr>';
      msg('returnStatus', e.message, false);
    }
  }

  async function postReturn(){
    try{
      const inv = ($('invoiceNo').value || '').trim();
      if(!inv) throw new Error('Invoice number is required.');
      const lines = [...document.querySelectorAll('[data-id]')]
        .filter(x => x.checked && !x.disabled)
        .map(x => ({
          saleLineId: Number(x.dataset.id),
          returnQuantity: Number(document.querySelector(`[data-qty="${x.dataset.id}"]`)?.value || 0)
        }))
        .filter(x => x.returnQuantity > 0);
      if(!lines.length) throw new Error('Select at least one item with return quantity.');
      const r = await api.post('/api/returns', {
        originalInvoiceNo: inv,
        reason: ($('returnReason').value || 'Customer return').trim(),
        lines
      });
      msg('returnStatus', `Return posted: ${r.returnNo || r.ReturnNo || ''} | Refund ${money(r.refundAmount ?? r.RefundAmount)}`, true);
      await loadLines();
    }catch(e){ msg('returnStatus', e.message, false); }
  }

  function money(v){ return 'Rs. ' + Number(v||0).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2}); }
  function esc(s){ return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

  window.loadLines = loadLines;
  window.postReturn = postReturn;
  init();
})();
