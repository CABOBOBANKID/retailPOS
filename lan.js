const TOKEN='azumah_lan_token';
const app=document.getElementById('app');
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const money=v=>Number(v||0).toLocaleString(undefined,{minimumFractionDigits:2,maximumFractionDigits:2});
const units=['Piece','Meter','Roll','Bag','Box','Length','Kg','Litre','Pack'];
const token=()=>localStorage.getItem(TOKEN);
let lanBrand='Inventory';
async function loadLanBrand(){try{const r=await fetch('/api/lan/branding',{cache:'no-store'});if(!r.ok)return;const d=await r.json();if(d.name){lanBrand=String(d.name).slice(0,80);document.title=`${lanBrand} - LAN Inventory`;document.querySelectorAll('.brand-name').forEach(n=>n.textContent=lanBrand)}}catch{}}

function login(message=''){
 app.innerHTML=`<div class="card login"><h1 class="brand-name">${esc(lanBrand)}</h1><div class="muted">Offline LAN Inventory Manager</div>
 ${message?`<div class="status show error">${esc(message)}</div>`:''}
 <label>LAN Access Code</label><input id="code" type="password" autocomplete="off" placeholder="Enter the code from the Main PC">
 <div class="actions"><button class="primary" id="loginBtn">Connect</button></div>
 <div class="muted small" style="margin-top:12px">This page connects directly to the Main PC over the shop WiFi. It does not use the internet.</div></div>`;
 document.getElementById('loginBtn').onclick=doLogin;
}
async function doLogin(){
 const r=await fetch('/api/lan/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({accessCode:code.value})});
 if(!r.ok)return login('Invalid LAN access code.');
 const d=await r.json();localStorage.setItem(TOKEN,d.accessToken);load();
}
async function api(path,options={}){
 options.headers={...(options.headers||{}),Authorization:`Bearer ${token()}`};
 const r=await fetch(path,options);
 if(r.status===401){localStorage.removeItem(TOKEN);login('The LAN session expired.');throw Error('auth')}
 const text=await r.text();let d={};try{d=text?JSON.parse(text):{}}catch{d={error:text}}
 if(!r.ok)throw Error(d.error||'Request failed.');
 return d;
}
async function load(){
 if(!token())return login();
 app.innerHTML=`<div class="shell">
 <header><div><h1><span class="brand-name">${esc(lanBrand)}</span> - LAN Inventory</h1><div class="header-muted">Main PC database • Offline only</div></div><button class="ghost" id="logout">Logout</button></header>
 <div id="status" class="status"></div>
 <div class="grid"><section class="card"><div class="toolbar"><input id="search" placeholder="Search name, code or brand"><button class="secondary" id="refresh">Refresh</button></div><div id="products" class="list"></div></section>
 <section class="card"><h2 id="formTitle">Add Product</h2><div class="muted">Type the product details manually to create a product. Select a product and click Edit to change its details or price. Stock is changed separately through Restock.</div>
 <input type="hidden" id="pid">
 <div class="form-grid">
 <div class="full"><label>Product Name</label><input id="name"></div>
 <div><label>Product Code / Barcode</label><input id="barcode"></div>
 <div><label>Brand</label><input id="brand"></div>
 <div><label>Unit</label><select id="unit">${units.map(x=>`<option>${x}</option>`).join('')}</select></div>
 <div><label>Category</label><input id="category" value="General"></div>
 <div><label>Cost Price</label><input id="cost" type="number" min="0" step="0.01"></div>
 <div><label>Selling Price</label><input id="price" type="number" min="0" step="0.01"></div>
 <div id="initialStockWrap"><label>Initial Stock</label><input id="initialStock" type="number" min="0" step="0.01" value="0"></div>
 <div><label>Reorder Level</label><input id="reorder" type="number" min="0" step="0.01" value="5"></div>
 </div><div class="actions"><button class="primary" id="save">Save Product</button><button class="secondary" id="clear">Clear</button></div>
 </section></div>
 <section class="card" style="margin-top:16px"><h2>Restock</h2><div class="muted">For an existing code, the quantity is <b>added</b> to current stock. If the code does not exist, the row creates a new product.</div>
 <div class="form-grid">
 <div><label>Product Code / Barcode</label><input id="rcode"></div><div><label>Quantity to Add</label><input id="rqty" type="number" min="0.01" step="0.01"></div>
 <div><label>New Product Name (only if code is new)</label><input id="rname"></div><div><label>Brand</label><input id="rbrand"></div>
 <div><label>Unit</label><select id="runit">${units.map(x=>`<option>${x}</option>`).join('')}</select></div><div><label>Category</label><input id="rcategory" value="General"></div>
 <div><label>Cost Price</label><input id="rcost" type="number" min="0" step="0.01" value="0"></div><div><label>Selling Price</label><input id="rprice" type="number" min="0" step="0.01" value="0"></div>
 <div><label>Reorder Level</label><input id="rreorder" type="number" min="0" step="0.01" value="5"></div>
 </div><div class="actions"><button class="primary" id="restock">Add Stock</button></div>
 </section>
 <section class="card" style="margin-top:16px"><h2>Excel Restock</h2><div class="muted">Upload an <b>.xlsx</b> file. Required columns: Product Code, Quantity. For new codes also provide Product Name; optional columns are Brand, Unit, Category, Cost Price, Selling Price and Reorder Level.</div>
 <div class="drop"><input id="xlsx" type="file" accept=".xlsx"><div class="actions" style="justify-content:center"><button class="primary" id="upload">Upload & Restock</button><a class="ghost" href="/Restock_Template.xlsx" download>Download Template</a></div></div><div id="importResult"></div></section>
 </div>`;
 bind();
 await refresh();
}
function bind(){
 logout.onclick=()=>{localStorage.removeItem(TOKEN);login()};
 refresh.onclick=refresh;search.oninput=()=>refresh();
 clear.onclick=clearForm;save.onclick=saveProduct;restock.onclick=restockProduct;upload.onclick=uploadExcel;
}
async function refresh(){
 try{
  const a=await api('/api/lan/products?search='+encodeURIComponent(search?.value||''));
  window._lanProducts=new Map(a.map(p=>[p.id,p]));
  products.innerHTML=`<div class="row head"><div>Product</div><div>Code</div><div>Price</div><div>Stock</div><div></div></div>`+
   a.map(p=>`<div class="row"><div><b>${esc(p.name)}</b><div class="muted">${esc(p.brand||'')} • ${esc(p.category)}</div></div><div>${esc(p.barcode)}</div><div>GHS ${money(p.sellingPrice)}</div><div><span class="badge">${p.stockQuantity} ${esc(units[p.unit]||'')}</span></div><div><button class="ghost" onclick="editProduct(${p.id})">Edit</button></div></div>`).join('')||'<div style="padding:20px">No products found.</div>';
 }catch(e){if(e.message!=='auth')show(e.message,true)}
}
function editProduct(id){
 const p=window._lanProducts?.get(id); if(!p)return;
 pid.value=p.id;formTitle.textContent='Edit Product';name.value=p.name;barcode.value=p.barcode;brand.value=p.brand||'';unit.value=units[p.unit]||'Piece';category.value=p.category||'General';cost.value=p.costPrice;price.value=p.sellingPrice;reorder.value=p.reorderLevel;initialStockWrap.style.display='none';window.scrollTo({top:0,behavior:'smooth'});
}
function clearForm(){pid.value='';formTitle.textContent='Add Product';name.value='';barcode.value='';brand.value='';unit.value='Piece';category.value='General';cost.value='';price.value='';initialStock.value=0;reorder.value=5;initialStockWrap.style.display='block'}
async function saveProduct(){
 try{
  const id=pid.value;
  const body={name:name.value,barcode:barcode.value,brand:brand.value,unit:unit.value,category:category.value,costPrice:Number(cost.value||0),sellingPrice:Number(price.value||0),stockQuantity:Number(initialStock.value||0),reorderLevel:Number(reorder.value||0)};
  await api(id?`/api/lan/products/${id}`:'/api/lan/products',{method:id?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  show(id?'Product updated.':'Product added.');clearForm();await refresh();
 }catch(e){if(e.message!=='auth')show(e.message,true)}
}
async function restockProduct(){
 try{
  const body={barcode:rcode.value,quantity:Number(rqty.value||0),name:rname.value,brand:rbrand.value,unit:runit.value,category:rcategory.value,costPrice:Number(rcost.value||0),sellingPrice:Number(rprice.value||0),reorderLevel:Number(rreorder.value||0)};
  const p=await api('/api/lan/restock',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  show(`Stock updated: ${p.name} now has ${p.stockQuantity} ${units[p.unit]||''}.`);rcode.value='';rqty.value='';await refresh();
 }catch(e){if(e.message!=='auth')show(e.message,true)}
}
async function uploadExcel(){
 if(!xlsx.files.length)return show('Choose an .xlsx file first.',true);
 try{
  const fd=new FormData();fd.append('file',xlsx.files[0]);
  const d=await api('/api/lan/restock/xlsx',{method:'POST',body:fd});
  importResult.innerHTML=`<div class="status show ${d.failed?'error':''}">Rows: ${d.totalRows} • New products: ${d.added} • Restocked: ${d.restocked} • Failed: ${d.failed}</div>`+
   (d.results||[]).map(x=>`<div class="small">${x.ok?'✓':'✕'} Row ${x.row}: ${esc(x.code||'')} ${esc(x.action||x.error||'')}</div>`).join('');
  await refresh();
 }catch(e){if(e.message!=='auth')show(e.message,true)}
}
function show(message,error=false){status.textContent=message;status.className='status show'+(error?' error':'')}
loadLanBrand();
if(token())load();else login();
