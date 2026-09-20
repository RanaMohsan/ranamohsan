(() => {
  const $=id=>document.getElementById(id); const q=new URLSearchParams(location.search); let id=Number(q.get('id')||0),record=null;
  const num=v=>Number(v||0); const val=id=>$(id).value; const set=(id,v)=>{$(id).value=v??'';};
  const pictureHint='The selected image is stored against the item record.';
  async function init(){try{const me=await api.get('/api/me');$('who').textContent=`${me.companyName} | ${me.displayName} | ${me.roleName}`;}catch{return;}bind();if(id)await load();else await reset();if(q.get('print')==='1')setTimeout(()=>window.print(),400);}
  function bind(){$('backBtn').onclick=()=>location.href='/items.html';$('newBtn').onclick=()=>location.href='/item-card.html';$('saveBtn').onclick=save;$('printBtn').onclick=()=>window.print();$('generateCodeBtn').onclick=generateCode;$('itemImage').onchange=readImage;['productCode','barcode','productName','unitOfMeasure','purchasePrice','salePrice','stockOnHand','minStockLevel','reorderLevel','isActive'].forEach(x=>$(x).addEventListener('input',facts));}
  async function generateCode(){try{const r=await api.get('/api/products/next-code');set('productCode',r.productCode);facts();}catch(e){msg('status',e.message,false);}}
  async function reset(){id=0;record=null;set('productId',0);set('productCode','');set('barcode','');set('productName','');set('unitOfMeasure','PCS');set('purchasePrice',0);set('salePrice',0);set('retailPrice',0);set('taxPercent',0);set('stockOnHand',0);set('minStockLevel',0);set('reorderLevel',0);set('isActive','true');$('discountAllowed').checked=true;set('imageBase64','');$('itemImage').value='';showImage('');pictureMsg(pictureHint,true);await generateCode();facts();$('productName').focus();}
  async function load(){try{record=await api.get(`/api/products/${id}`);set('productId',record.productId);set('productCode',record.productCode);set('barcode',record.barcode);set('productName',record.productName);set('unitOfMeasure',record.unitOfMeasure||'PCS');set('purchasePrice',num(record.purchasePrice));set('salePrice',num(record.salePrice));set('retailPrice',num(record.retailPrice));set('taxPercent',num(record.taxPercent));set('stockOnHand',num(record.stockOnHand));set('minStockLevel',num(record.minStockLevel));set('reorderLevel',num(record.reorderLevel));set('isActive',String(record.isActive!==false));$('discountAllowed').checked=record.discountAllowed!==false;set('imageBase64','');showImage(record.hasImage?`/api/products/${id}/image`:'');pictureMsg(pictureHint,true);facts();msg('status','Item card loaded.',true);}catch(e){msg('status',e.message,false);}}
  function showImage(src){if(src){$('imagePreview').src=src;$('imagePreview').style.display='block';$('picturePlaceholder').style.display='none';}else{$('imagePreview').removeAttribute('src');$('imagePreview').style.display='none';$('picturePlaceholder').style.display='block';}}
  function pictureMsg(text,ok){const el=$('pictureStatus');if(!el)return;el.className=ok?'muted mini':'bad mini';el.textContent=text||pictureHint;}
  function fileToBase64(file){return new Promise((resolve,reject)=>{const r=new FileReader();r.onload=()=>{const data=String(r.result||'');resolve(data.includes(',')?data.split(',')[1]:data);};r.onerror=()=>reject(new Error('The selected file is not a valid image.'));r.readAsDataURL(file);});}
  function allowedImage(file){const name=(file.name||'').toLowerCase();const type=(file.type||'').toLowerCase();return type==='image/jpeg'||type==='image/jpg'||type==='image/png'||type==='image/webp'||/\.(jpe?g|png|webp)$/.test(name);}
  async function readImage(){
    const input=$('itemImage');
    const f=input.files?.[0];
    if(!f){set('imageBase64','');pictureMsg(pictureHint,true);return;}
    if(!allowedImage(f)){input.value='';set('imageBase64','');pictureMsg('Please select a JPG, PNG or WebP image.',false);return;}
    if(f.size>25*1024*1024){input.value='';set('imageBase64','');pictureMsg('Image cannot exceed 25 MB.',false);return;}
    input.disabled=true;
    pictureMsg('Optimizing image...',true);
    try{
      const imageBase64=await fileToBase64(f);
      const r=await api.post('/api/products/optimize-image',{imageBase64});
      const b64=r.imageBase64||'';
      const type=r.contentType||'image/webp';
      set('imageBase64',b64);
      showImage(b64?`data:${type};base64,${b64}`:'');
      const line=[r.message,r.details].filter(Boolean).join('  ·  ');
      pictureMsg(line||'Image optimized.',true);
    }catch(e){
      set('imageBase64','');
      input.value='';
      showImage(id&&record?.hasImage?`/api/products/${id}/image`:'');
      pictureMsg(e.message||'Image could not be optimized. Please try another image.',false);
    }finally{
      input.disabled=false;
    }
  }
  function facts(){const active=val('isActive')==='true',code=val('productCode')||'NEW',name=val('productName')||'New Item',cost=num(val('purchasePrice')),price=num(val('salePrice')),margin=price?((price-cost)/price*100):0;$('cardTitle').textContent=id?name:'New Item';$('recordNo').textContent=code;$('recordStatus').textContent=id?(active?'Active':'Inactive'):'New';$('recordStatus').className=`bc-status-pill ${id?(active?'active':'inactive'):'draft'}`;$('factInventory').textContent=num(val('stockOnHand')).toLocaleString();$('factMinimum').textContent=num(val('minStockLevel')).toLocaleString();$('factReorder').textContent=num(val('reorderLevel')).toLocaleString();$('factMargin').textContent=`${margin.toFixed(2)}%`;$('factStatus').textContent=id?(active?'Active':'Inactive'):'New';$('factNo').textContent=code;$('factBarcode').textContent=val('barcode')||'—';$('factUom').textContent=val('unitOfMeasure')||'PCS';$('factCreated').textContent=record?.createdAt?new Date(record.createdAt).toLocaleString():'—';}
  async function save(){try{if(!val('productCode').trim())throw new Error('Item No. is required.');if(!val('productName').trim())throw new Error('Description is required.');const body={productId:num(val('productId')),productCode:val('productCode').trim(),barcode:val('barcode').trim()||val('productCode').trim(),productName:val('productName').trim(),unitOfMeasure:val('unitOfMeasure').trim()||'PCS',purchasePrice:num(val('purchasePrice')),salePrice:num(val('salePrice')),retailPrice:num(val('retailPrice'))||num(val('salePrice')),stockOnHand:num(val('stockOnHand')),discountAllowed:$('discountAllowed').checked,minStockLevel:num(val('minStockLevel')),reorderLevel:num(val('reorderLevel')),taxPercent:num(val('taxPercent')),isActive:val('isActive')==='true',imageBase64:val('imageBase64')||''};const r=await api.post('/api/products',body);id=num(r.productId);history.replaceState(null,'',`/item-card.html?id=${id}`);await load();msg('status','Item saved successfully.',true);}catch(e){msg('status',e.message,false);}}
  init();
})();
