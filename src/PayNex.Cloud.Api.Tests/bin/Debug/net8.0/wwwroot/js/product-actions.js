
function pval(o,a,b){ return o?.[a] ?? o?.[b] ?? ''; }
const pageType = document.body.dataset.productPage || 'retail';
async function init(){
  try{
    const me=await api.get('/api/me');
    const who=document.getElementById('who');
    if(who) who.textContent += `  ${me.companyName || me.CompanyName || ''} | ${me.displayName || me.DisplayName || me.userName || me.UserName || ''}`;
  }catch{location.href='/login.html'; return;}
  await loadProducts();
}
async function loadProducts(){
  try{
    const term=encodeURIComponent(document.getElementById('productSearch')?.value||'');
    let rows=await api.get('/api/products?term='+term);
    rows=rows||[];
    if(pageType==='released') rows=rows.filter(p=>pval(p,'isActive','IsActive') !== false);
    renderProducts(rows);
    msg('productStatus',`${rows.length} record(s) loaded.`,true);
  }catch(e){msg('productStatus',e.message,false)}
}
function renderProducts(rows){
  const head=document.getElementById('productHead'), body=document.getElementById('productBody');
  let cols=[];
  if(pageType==='variants'){
    cols=[['productCode','Item Code'],['productName','Item Name'],['unitOfMeasure','Unit'],['barcode','Variant/Barcode'],['retailPrice','Retail Price'],['stockOnHand','Stock'],['variantStatus','Variant Status']];
  }else if(pageType==='released'){
    cols=[['productCode','Released Product'],['productName','Name'],['unitOfMeasure','Unit'],['stockOnHand','Stock'],['minStockLevel','Min Stock'],['reorderLevel','Reorder Level'],['isActive','Released']];
  }else{
    cols=[['productCode','Product Code'],['productName','Product Name'],['barcode','Barcode'],['salePrice','Sale Price'],['retailPrice','Retail Price'],['stockOnHand','Stock'],['isActive','Status']];
  }
  head.innerHTML='<tr>'+cols.map(c=>`<th>${c[1]}</th>`).join('')+'</tr>';
  if(!rows.length){ body.innerHTML=`<tr><td colspan="${cols.length}" class="muted">No products found.</td></tr>`; return; }
  body.innerHTML=rows.map(p=>'<tr>'+cols.map(c=>`<td>${formatProductValue(p,c[0])}</td>`).join('')+'</tr>').join('');
}
function formatProductValue(p,key){
  if(key==='variantStatus') return 'Default variant';
  const value=pval(p,key,key.charAt(0).toUpperCase()+key.slice(1));
  if(['salePrice','retailPrice'].includes(key)) return money(value);
  if(key==='isActive') return value!==false ? '<span class="ok">Yes</span>' : '<span class="bad">No</span>';
  return value ?? '';
}
init();
