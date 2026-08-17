#pragma once

#include <Arduino.h>

static const char DASHBOARD_HTML[] PROGMEM = R"DASHBOARD(
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="theme-color" content="#062E1C">
  <title>Light Sensor</title>
  <style>
    @font-face{font-family:"Space Grotesk";src:url(data:font/woff;base64,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) format("woff");font-style:normal;font-weight:300 900;font-display:swap}
    :root{--paper:#E8E4E1;--green:#004225;--deep:#062E1C;--muted:#8D888B;--ink:#030606;--white:#fff;--danger:#8a2f25;--shadow:0 8px 24px rgba(6,46,28,.09)}
    *{box-sizing:border-box;border-radius:0}
    html{background:var(--deep)}
    body{margin:0;background:var(--paper);color:var(--ink);font-family:"Space Grotesk","Arial Narrow",Arial,sans-serif;min-height:100vh}
    button,input,select{font:inherit}
    button:focus-visible,input:focus-visible,select:focus-visible{outline:3px solid rgba(0,66,37,.32);outline-offset:2px}
    .hero{background:var(--deep);color:var(--paper);padding:calc(24px + env(safe-area-inset-top)) 20px 28px}
    .hero-inner,.content{width:min(680px,100%);margin:0 auto}
    .eyebrow{display:flex;align-items:center;justify-content:space-between;gap:12px;font-size:12px;font-weight:800;letter-spacing:.15em;text-transform:uppercase}
    .logo{display:block;width:96px;height:auto;color:var(--white)}
    .logo *{fill:currentColor}
    .status{display:inline-flex;align-items:center;gap:7px;background:rgba(232,228,225,.08);border:.5px solid rgba(232,228,225,.35);padding:8px 11px;letter-spacing:.04em}
    .dot{width:7px;height:7px;background:var(--paper)}
    h1{font-size:clamp(38px,11vw,68px);line-height:.92;letter-spacing:-.045em;margin:34px 0 0;font-weight:800}
    .content{padding:22px 16px calc(32px + env(safe-area-inset-bottom))}
    .card{background:rgba(255,255,255,.58);border:.5px solid rgba(141,136,139,.42);box-shadow:var(--shadow);padding:18px;margin-bottom:16px}
    .card-head{display:flex;align-items:flex-end;justify-content:space-between;gap:14px;margin-bottom:14px}
    h2{font-size:21px;line-height:1.1;margin:0;color:var(--deep);letter-spacing:-.02em}
    .small{font-size:12px;color:var(--deep);opacity:.72;font-weight:600;text-align:right}
    .reading{display:grid;grid-template-columns:1fr auto;gap:16px;align-items:end}
    .reading-number{font-size:clamp(76px,22vw,126px);font-weight:800;line-height:.78;letter-spacing:-.075em;color:var(--deep);font-variant-numeric:tabular-nums}
    .reading-scale{font-size:16px;font-weight:800;color:var(--deep);padding-bottom:5px}
    .meter{height:10px;background:rgba(0,66,37,.13);margin:22px 0 14px;overflow:hidden}
    .meter-fill{height:100%;width:0;background:var(--green);transition:width .25s ease}
    .reading-foot{display:flex;align-items:center;justify-content:space-between;gap:14px;font-size:12px;color:var(--deep)}
    .iso-now{background:var(--deep);color:var(--paper);font-weight:800;padding:9px 11px;min-width:112px;text-align:center}
    .mapping-intro{font-size:12px;line-height:1.45;color:var(--deep);margin:-2px 0 14px}
    .mapping-head,.range-row{display:grid;grid-template-columns:minmax(0,1.2fr) minmax(100px,.8fr) 34px;gap:8px;align-items:center}
    .mapping-head{font-size:11px;font-weight:800;letter-spacing:.08em;text-transform:uppercase;color:var(--deep);opacity:.68;padding:0 0 7px}
    .range-row{border-top:.5px solid rgba(0,66,37,.18);padding:10px 0}
    .range-fields{display:grid;grid-template-columns:1fr 18px 1fr;gap:5px;align-items:center}
    .range-fields span{text-align:center;font-size:12px;font-weight:700;color:var(--deep)}
    .field{width:100%;min-width:0;border:.5px solid rgba(0,66,37,.32);background:var(--paper);color:var(--deep);padding:10px 9px;font-weight:800}
    .field[readonly]{border-color:transparent;background:rgba(6,46,28,.07)}
    .remove{height:38px;border:.5px solid rgba(138,47,37,.28);background:transparent;color:var(--danger);font-size:20px;line-height:1;cursor:pointer}
    .remove:disabled{visibility:hidden}
    .button-row{display:grid;grid-template-columns:1fr 1fr;gap:9px;margin-top:14px}
    .button{border:.5px solid rgba(0,66,37,.36);background:var(--paper);color:var(--deep);padding:12px 14px;font-size:13px;font-weight:800;cursor:pointer;transition:transform .12s ease,background .12s ease}
    .button.primary{background:var(--green);border-color:var(--green);color:var(--paper)}
    .button:active{transform:scale(.975)}
    .button:disabled{opacity:.45;cursor:not-allowed}
    .network-panels{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}
    .network-panel{border:.5px solid rgba(0,66,37,.28);padding:12px;min-width:0}
    .network-title{font-size:14px;font-weight:800;color:var(--deep);margin-bottom:10px}
    .network{display:grid;grid-template-columns:1fr;gap:4px;margin:0;font-size:12px}
    .network dt{font-weight:800;color:var(--deep)}
    .network dd{margin:0 0 7px;text-align:left;overflow-wrap:anywhere}
    .network dd:last-child{margin-bottom:0}
    .device-line{margin-top:10px;border-top:.5px solid rgba(0,66,37,.22);padding-top:10px;font-size:12px;color:var(--deep)}
    .footer{text-align:center;color:var(--deep);font-size:12px;font-weight:700;padding-top:4px}
    .toast{position:fixed;left:50%;bottom:calc(20px + env(safe-area-inset-bottom));transform:translate(-50%,20px);background:var(--ink);color:var(--paper);padding:11px 16px;font-size:13px;font-weight:700;opacity:0;pointer-events:none;transition:.2s;z-index:10}
    .toast.show{opacity:1;transform:translate(-50%,0)}
    @media(max-width:480px){.mapping-head,.range-row{grid-template-columns:minmax(0,1fr) 94px 32px}.card{padding:16px}.button-row{grid-template-columns:1fr}}
    @media(max-width:380px){.hero{padding-left:16px;padding-right:16px}.network-panels{grid-template-columns:1fr}.range-fields{grid-template-columns:1fr 15px 1fr}.field{padding-left:7px;padding-right:7px}}
    @media(prefers-reduced-motion:reduce){.meter-fill,.toast,.button{transition:none}}
  </style>
</head>
<body>
  <header class="hero">
    <div class="hero-inner">
      <div class="eyebrow">
        <svg class="logo" viewBox="0 0 62.856 17.008" role="img" aria-label="WAUD">
          <polygon points="40.55 17.008 36.665 17.008 36.665 16.994 36.665 13.605 36.665 4.649 40.55 4.649 40.55 13.605 43.343 13.605 43.343 4.649 47.229 4.649 47.229 13.605 47.229 16.994 47.229 17.008 43.343 17.008 40.55 17.008"/>
          <polygon points="15.701 4.649 14.185 12.549 12.669 4.649 10.944 4.649 8.713 4.649 6.988 4.649 5.472 12.549 3.956 4.649 0 4.649 2.371 17.008 4.617 17.008 6.327 17.008 8.573 17.008 9.829 10.463 11.084 17.008 13.33 17.008 15.041 17.008 17.286 17.008 19.657 4.649 15.701 4.649"/>
          <path d="M25.282 4.95c.602-.196 1.244-.301 1.912-.301v3.403a2.776 2.776 0 1 0 0 5.552v3.403a6.18 6.18 0 0 1-1.912-12.057Z"/>
          <path d="M29.94 4.649v5.792a2.776 2.776 0 0 0-2.746-2.389v5.553a2.776 2.776 0 0 0 2.746-2.389v5.792h3.885V4.649H29.94Z"/>
          <path d="M53.425 10.828a2.776 2.776 0 0 1 2.753-2.775V4.649a6.18 6.18 0 0 0 0 12.358v-3.404a2.776 2.776 0 0 1-2.753-2.775Z"/>
          <path d="M58.97 0v10.688a2.776 2.776 0 0 0-2.793-2.635v5.55a2.776 2.776 0 0 0 2.793-2.635v6.039h3.885V0H58.97Z"/>
        </svg>
        <span class="status"><i class="dot"></i><span id="connection">Connecting</span></span>
      </div>
      <h1>Light Sensor</h1>
    </div>
  </header>

  <main class="content">
    <section class="card" aria-labelledby="lightTitle">
      <div class="card-head">
        <h2 id="lightTitle">Live light sensing</h2>
        <span class="small">Mapped value &middot; 0–100</span>
      </div>
      <div class="reading" aria-live="polite">
        <output class="reading-number" id="lightValue">--</output>
        <span class="reading-scale">/ 100</span>
      </div>
      <div class="meter" role="meter" aria-label="Mapped live light value" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0">
        <div class="meter-fill" id="meterFill"></div>
      </div>
      <div class="reading-foot">
        <span id="rawValue">Raw ADC: ---- / 4095</span>
        <output class="iso-now" id="currentIso">ISO ----</output>
      </div>
    </section>

    <section class="card" aria-labelledby="mappingTitle">
      <div class="card-head">
        <h2 id="mappingTitle">ISO mapping</h2>
        <span class="small" id="rangeCount">4 ranges</span>
      </div>
      <p class="mapping-intro">Set which ISO value belongs to each light range. Ranges always connect and cover the full 0–100 scale.</p>
      <div class="mapping-head" aria-hidden="true"><span>Light range</span><span>ISO value</span><span></span></div>
      <div id="rangeRows"></div>
      <div class="button-row">
        <button class="button" id="addRange" type="button">Add range</button>
        <button class="button primary" id="saveMapping" type="button">Save ISO mapping</button>
      </div>
    </section>

    <section class="card" aria-labelledby="connectionTitle">
      <div class="card-head">
        <h2 id="connectionTitle">Connection</h2>
        <span class="small">ESP32-C3</span>
      </div>
      <div class="network-panels">
        <div class="network-panel">
          <div class="network-title">Sensor hotspot</div>
          <dl class="network">
            <dt>Status</dt><dd id="hotspotStatus">Starting</dd>
            <dt>Network</dt><dd id="hotspotSsid">JvdP-LightSensor</dd>
            <dt>Address</dt><dd id="hotspotIp">192.168.9.1</dd>
          </dl>
        </div>
        <div class="network-panel">
          <div class="network-title">Sensor input</div>
          <dl class="network">
            <dt>Input</dt><dd>GPIO 0</dd>
            <dt>Resolution</dt><dd>12-bit ADC</dd>
            <dt>Refresh</dt><dd>Live</dd>
          </dl>
        </div>
      </div>
      <div class="device-line"><strong>OTA READY</strong> &middot; jvdp-lightsensor.local</div>
    </section>

    <div class="footer">Locally controlled &middot; no internet required</div>
  </main>
  <div class="toast" id="toast" role="status">Saved</div>

  <script>
    const $=selector=>document.querySelector(selector);
    const rows=$('#rangeRows');
    const MAX_RANGES=8;
    const ISO_VALUES=[100,200,400,800,1600,3200,6400,12800];
    let editingBands=[];
    let mappingDirty=false;
    let toastTimer;

    function showToast(message){
      const element=$('#toast');
      element.textContent=message;
      element.classList.add('show');
      clearTimeout(toastTimer);
      toastTimer=setTimeout(()=>element.classList.remove('show'),1500);
    }

    async function request(path,options){
      const response=await fetch(path,options);
      const data=await response.json();
      if(!response.ok||data.ok===false)throw new Error(data.error||'Request failed');
      return data;
    }

    function renderBands(){
      rows.innerHTML='';
      editingBands.forEach((band,index)=>{
        const lower=index===0?0:editingBands[index-1].max+1;
        const last=index===editingBands.length-1;
        const row=document.createElement('div');
        row.className='range-row';
        row.innerHTML=`
          <div class="range-fields">
            <input class="field" type="number" value="${lower}" aria-label="Range ${index+1} lower limit" readonly>
            <span>to</span>
            <input class="field upper" type="number" min="${lower}" max="100" value="${band.max}" aria-label="Range ${index+1} upper limit" ${last?'readonly':''}>
          </div>
          <select class="field iso" aria-label="ISO value for range ${lower} to ${band.max}">${ISO_VALUES.map(value=>`<option value="${value}" ${value===band.iso?'selected':''}>ISO ${value}</option>`).join('')}</select>
          <button class="remove" type="button" aria-label="Remove range ${index+1}" title="Remove range" ${editingBands.length===1?'disabled':''}>&times;</button>`;

        row.querySelector('.upper').addEventListener('change',event=>{
          band.max=Math.max(0,Math.min(100,Number(event.target.value)||0));
          normaliseBands();
          mappingDirty=true;
          renderBands();
        });
        row.querySelector('.iso').addEventListener('change',event=>{
          band.iso=Number(event.target.value);
          mappingDirty=true;
        });
        row.querySelector('.remove').addEventListener('click',()=>removeBand(index));
        rows.appendChild(row);
      });
      $('#rangeCount').textContent=`${editingBands.length} ${editingBands.length===1?'range':'ranges'}`;
      $('#addRange').disabled=editingBands.length>=MAX_RANGES;
    }

    function normaliseBands(){
      editingBands.sort((a,b)=>a.max-b.max);
      for(let index=0;index<editingBands.length-1;index++){
        const minimum=index===0?0:editingBands[index-1].max+1;
        const maximum=99-(editingBands.length-2-index);
        editingBands[index].max=Math.max(minimum,Math.min(maximum,editingBands[index].max));
      }
      editingBands[editingBands.length-1].max=100;
    }

    function addBand(){
      if(editingBands.length>=MAX_RANGES)return;
      const lastIndex=editingBands.length-1;
      const lower=lastIndex===0?0:editingBands[lastIndex-1].max+1;
      if(lower>=100){showToast('No room for another range');return}
      const current=editingBands[lastIndex];
      const split=Math.floor((lower+99)/2);
      editingBands.splice(lastIndex,0,{max:split,iso:current.iso});
      normaliseBands();
      mappingDirty=true;
      renderBands();
    }

    function removeBand(index){
      if(editingBands.length<=1)return;
      editingBands.splice(index,1);
      normaliseBands();
      mappingDirty=true;
      renderBands();
    }

    function validateBands(){
      if(!editingBands.length)return false;
      let previous=-1;
      for(const band of editingBands){
        if(!Number.isInteger(band.max)||band.max<=previous||band.max>100)return false;
        if(!ISO_VALUES.includes(band.iso))return false;
        previous=band.max;
      }
      return previous===100;
    }

    function applyState(state){
      $('#connection').textContent='Online';
      const light=Math.max(0,Math.min(100,Number(state.light)||0));
      $('#lightValue').textContent=String(light).padStart(2,'0');
      $('#meterFill').style.width=light+'%';
      const meter=$('.meter');
      meter.setAttribute('aria-valuenow',light);
      $('#rawValue').textContent=`Raw ADC: ${state.rawLight} / 4095`;
      $('#currentIso').textContent=`ISO ${state.currentIso}`;
      $('#hotspotStatus').textContent=state.apActive?'Active':'Inactive';
      $('#hotspotSsid').textContent=state.apSsid;
      $('#hotspotIp').textContent=state.apIp;
      if(!mappingDirty&&Array.isArray(state.bands)){
        editingBands=state.bands.map(band=>({max:Number(band.max),iso:Number(band.iso)}));
        renderBands();
      }
    }

    async function refresh(){
      try{applyState(await request('/api/state'))}
      catch(error){$('#connection').textContent='Offline'}
    }

    async function saveMapping(){
      normaliseBands();
      if(!validateBands()){showToast('Check the range values');renderBands();return}
      const body=new URLSearchParams({
        bandCount:String(editingBands.length),
        bounds:editingBands.map(band=>band.max).join(','),
        isos:editingBands.map(band=>band.iso).join(',')
      });
      try{
        const result=await request('/api/action',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body});
        mappingDirty=false;
        applyState(result.state);
        showToast(result.message||'ISO mapping saved');
      }catch(error){showToast('No connection')}
    }

    $('#addRange').addEventListener('click',addBand);
    $('#saveMapping').addEventListener('click',saveMapping);
    refresh();
    setInterval(refresh,600);
  </script>
</body>
</html>
)DASHBOARD";
