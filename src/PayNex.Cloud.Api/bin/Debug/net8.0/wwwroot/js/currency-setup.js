(() => {
  const $ = id => document.getElementById(id);
  const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  let currencies = [];

  function field(row, camel, pascal){ return row?.[camel] ?? row?.[pascal]; }
  function isTrue(value){ return value === true || value === 1 || String(value).toLowerCase() === 'true'; }

  function newCurrency(){
    $('currencyId').value='0';
    $('currencyCode').value='';
    $('currencyName').value='';
    $('currencySymbol').value='';
    $('currencyDecimals').value='2';
    $('currencyRate').value='1';
    $('currencyActive').value='true';
    $('currencyIsBase').checked=false;
    $('currencyCardTitle').textContent='New Currency';
    $('currencyCardBadge').textContent='New';
    $('currencyCode').focus();
  }

  function selectCurrency(id){
    const row=currencies.find(x=>Number(field(x,'currencyId','CurrencyId'))===Number(id));
    if(!row)return;
    $('currencyId').value=field(row,'currencyId','CurrencyId');
    $('currencyCode').value=field(row,'currencyCode','CurrencyCode')||'';
    $('currencyName').value=field(row,'currencyName','CurrencyName')||'';
    $('currencySymbol').value=field(row,'symbol','Symbol')||'';
    $('currencyDecimals').value=String(field(row,'decimalPlaces','DecimalPlaces')??2);
    $('currencyRate').value=Number(field(row,'exchangeRate','ExchangeRate')||1);
    $('currencyActive').value=String(isTrue(field(row,'isActive','IsActive')));
    $('currencyIsBase').checked=isTrue(field(row,'isBase','IsBase'));
    $('currencyCardTitle').textContent=`${$('currencyCode').value} Currency`;
    $('currencyCardBadge').textContent=$('currencyIsBase').checked?'Base Currency':'Currency Card';
  }

  function render(){
    const base=currencies.find(x=>isTrue(field(x,'isBase','IsBase'))&&isTrue(field(x,'isActive','IsActive')));
    $('currencyCount').textContent=`${currencies.length} record${currencies.length===1?'':'s'}`;
    $('baseCurrencyCaption').textContent=base?`Base currency: ${field(base,'currencyCode','CurrencyCode')} • ${field(base,'symbol','Symbol')}`:'No active base currency';
    $('currencyBody').innerHTML=currencies.length?currencies.map(row=>{
      const id=Number(field(row,'currencyId','CurrencyId'));
      const baseRow=isTrue(field(row,'isBase','IsBase'));
      const active=isTrue(field(row,'isActive','IsActive'));
      return `<tr data-id="${id}" tabindex="0"><td><a href="#" data-open="${id}" class="bc-record-link">${esc(field(row,'currencyCode','CurrencyCode'))}</a></td><td>${esc(field(row,'currencyName','CurrencyName'))}</td><td>${esc(field(row,'symbol','Symbol'))}</td><td class="number">${Number(field(row,'exchangeRate','ExchangeRate')||0).toFixed(6)}</td><td><span class="status-chip ${baseRow?'ok':''}">${baseRow?'Yes':'No'}</span></td><td><span class="status-chip ${active?'ok':'off'}">${active?'Active':'Inactive'}</span></td></tr>`;
    }).join(''):'<tr><td colspan="6" class="bc-empty-row">No currencies are configured.</td></tr>';
    $('currencyBody').querySelectorAll('tr[data-id]').forEach(row=>{
      row.onclick=event=>{event.preventDefault();selectCurrency(row.dataset.id)};
      row.onkeydown=event=>{if(event.key==='Enter')selectCurrency(row.dataset.id)};
    });
  }

  async function loadCurrencies(selectId=0){
    currencies=await api.get('/api/currencies')||[];
    render();
    const id=Number(selectId||$('currencyId').value||0);
    if(id&&currencies.some(x=>Number(field(x,'currencyId','CurrencyId'))===id))selectCurrency(id);
    else if(currencies.length)selectCurrency(field(currencies[0],'currencyId','CurrencyId'));
    else newCurrency();
  }

  async function saveCurrency(){
    const body={
      currencyId:Number($('currencyId').value||0),
      currencyCode:$('currencyCode').value.trim(),
      currencyName:$('currencyName').value.trim(),
      symbol:$('currencySymbol').value.trim(),
      decimalPlaces:Number($('currencyDecimals').value||2),
      exchangeRate:Number($('currencyRate').value||0),
      isBase:$('currencyIsBase').checked,
      isActive:$('currencyActive').value==='true'
    };
    if(!body.currencyCode||!body.currencyName){msg('currencyStatus','Currency code and name are required.',false);return;}
    if(body.isBase){body.isActive=true;body.exchangeRate=1;$('currencyActive').value='true';$('currencyRate').value='1';}
    try{
      $('saveCurrencyBtn').disabled=true;
      const result=await api.post('/api/currencies',body);
      await loadCurrencies(result.currencyId);
      msg('currencyStatus',result.message||'Currency setup saved.',true);
    }catch(error){msg('currencyStatus',error.message,false)}
    finally{$('saveCurrencyBtn').disabled=false}
  }

  async function init(){
    try{
      const me=await api.get('/api/me');
      $('who').textContent=`${me.companyName||me.CompanyName} | ${me.displayName||me.DisplayName} | ${me.roleName||me.RoleName}`;
      if(!hasPermission(me,'system.generalConfiguration'))$('saveCurrencyBtn').hidden=true;
      await loadCurrencies();
    }catch(error){
      if(/login|required|auth/i.test(error.message||''))location.href='/login.html?returnUrl='+encodeURIComponent(location.pathname);
      else msg('currencyStatus',error.message,false);
    }
  }

  $('newCurrencyBtn').onclick=newCurrency;
  $('saveCurrencyBtn').onclick=saveCurrency;
  $('refreshCurrencyBtn').onclick=()=>loadCurrencies().catch(error=>msg('currencyStatus',error.message,false));
  $('currencyIsBase').onchange=()=>{if($('currencyIsBase').checked){$('currencyRate').value='1';$('currencyActive').value='true';}};
  init();
})();
