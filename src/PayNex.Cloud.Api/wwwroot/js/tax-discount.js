let products=[], rows=[];
const v=(o,a,b)=>o?.[a] ?? o?.[b] ?? '';
async function init(){
  try{const me=await api.get('/api/me'); who.textContent=`${me.companyName||me.CompanyName} | ${me.displayName||me.DisplayName} | ${me.roleName||me.RoleName}`;}catch{location.href='/login.html';return;}
  products=await api.get('/api/products?term=');
  productId.innerHTML=products.map(p=>`<option value="${v(p,'productId','ProductId')}">${v(p,'productCode','ProductCode')} - ${v(p,'productName','ProductName')}</option>`).join('');
  productId.onchange=fillFromSelected; await loadRows(); fillFromSelected();
}
function fillFromSelected(){const p=products.find(x=>String(v(x,'productId','ProductId'))===String(productId.value)); if(!p)return; discountPercent.value=Number(v(p,'productDiscountPercent','ProductDiscountPercent')||0); taxPercent.value=Number(v(p,'taxPercent','TaxPercent')||0);}
async function loadRows(){
  try{
    rows=await api.get('/api/product-tax-discounts?term='+encodeURIComponent(searchTerm?.value||''));
    rowsBody.innerHTML=rows.length?rows.map((r,i)=>`<tr><td>${v(r,'productCode','ProductCode')}</td><td>${v(r,'productName','ProductName')}</td><td>${money(v(r,'salePrice','SalePrice'))}</td><td>${Number(v(r,'productDiscountPercent','ProductDiscountPercent')||0).toFixed(2)}</td><td>${Number(v(r,'taxPercent','TaxPercent')||0).toFixed(2)}</td><td>${v(r,'taxInclusive','TaxInclusive')?'Inclusive':'Exclusive'}</td><td><button class="secondary" onclick="editRow(${i})">Edit</button></td></tr>`).join(''):'<tr><td colspan="7" class="muted">No items found.</td></tr>';
  }catch(e){msg('tdStatus',e.message,false)}
}
function editRow(i){const r=rows[i]; if(!r)return; productId.value=v(r,'productId','ProductId'); discountPercent.value=Number(v(r,'productDiscountPercent','ProductDiscountPercent')||0); taxPercent.value=Number(v(r,'taxPercent','TaxPercent')||0);}
async function saveTaxDiscount(){
  try{
    const body={productId:Number(productId.value),discountPercent:Number(discountPercent.value||0),taxPercent:Number(taxPercent.value||0)};
    if(!body.productId){msg('tdStatus','Select item first.',false);return;}
    if(body.discountPercent<0 || body.discountPercent>100 || body.taxPercent<0){msg('tdStatus','Discount must be 0-100 and tax must be non-negative.',false);return;}
    const r=await api.post('/api/product-tax-discounts',body); msg('tdStatus',r.message||'Updated.',true);
    products=await api.get('/api/products?term='); await loadRows();
  }catch(e){msg('tdStatus',e.message,false)}
}
init();
