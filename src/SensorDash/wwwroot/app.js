'use strict';
const titles={
 overview:['OVERVIEW','Hardware at a glance','Discovered sensors, live readings and configurable workspaces'],
 dashboards:['WORKSPACE','Dashboards','Build metric, chart and sensor-list layouts'],
 cooling:['COOLING','Cooling systems','Model any number of air, liquid or custom cooling systems'],
 curves:['SIMULATION','Fan curves','Calculate fan targets without writing hardware outputs'],
 virtual:['DERIVED','Virtual sensors','Define maximum, minimum, average and difference sensors'],
 tray:['NOTIFICATION AREA','Tray readouts','Configure up to six simultaneous Windows tray sensor icons'],
 storage:['HARDWARE','Storage sensors','SSD and drive telemetry in the unified sensor catalog'],
 explorer:['SENSOR LIBRARY','Sensor Explorer','Discover, rename, search and pin sensor data'],
 events:['HISTORY','Event log','Configurable threshold-crossing events'],
 settings:['CONFIGURATION','Settings','Collection intervals, aliases, monitoring thresholds and diagnostics']
};
const colors=['#62d5ce','#6fa7ed','#f2bd6d','#ec8792','#a991e8','#69b5cc','#e4a5cb'];
const S={view:'overview',frame:null,sensors:[],byId:new Map(),settings:null,events:[],filter:'Key',search:'',selected:'',
 hours:1,history:{},historyKey:'',historyFetch:0,historyPending:false,lastRefresh:0,liveCache:new Map(),connected:false,diagnostics:null,simulation:null,schemaKey:'',livePaintQueued:false,chartPaintTimer:null,chartDirty:false,lastChartPaint:0};
const byId=id=>document.getElementById(id);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=(v,dec=1)=>v==null||!Number.isFinite(v)?'—':Number(v).toLocaleString(undefined,{maximumFractionDigits:dec,minimumFractionDigits:dec});
const when=t=>new Date(t).toLocaleString(undefined,{month:'short',day:'numeric',hour:'numeric',minute:'2-digit',second:'2-digit'});
const shorttime=t=>new Date(t).toLocaleTimeString(undefined,{hour:'numeric',minute:'2-digit'});
const typeUnit=t=>({Temperature:'°C',Fan:'RPM',Control:'%',Load:'%',Power:'W',Clock:'MHz',Voltage:'V',Throughput:'B/s',Data:'GB',Level:'%'}[t]||'');
const label=s=>S.settings?.aliases?.[s?.id]||s?.name||'Unavailable';
const sensor=id=>S.byId.get(id);
const val=id=>sensor(id)?.value??null;
const metric=(name,s,accent,detail,unit,icon)=>`<div class="metric" style="--accent:${accent}"><div class="metric-top"><span>${esc(name)}</span><span class="metric-icon">${icon}</span></div><div class="metric-num"><b ${s?.id==='__calculated__'?'data-live-calc="nvmeHeadroom"':s?.id?`data-live-reading="${esc(s.id)}" data-live-format="value"`:''}>${fmt(s?.value,s?.type==='Fan'?0:1)}</b><span>${unit||typeUnit(s?.type)}</span></div><div class="metric-detail">${esc(detail)}</div></div>`;
const panel=(title,subtitle,content,tag='')=>`<section class="panel"><div class="panel-head"><div><h2>${esc(title)}</h2>${subtitle?' <p>'+esc(subtitle)+'</p>':''}</div>${tag}</div>${content}</section>`;
const stat=(name,s,unit)=>`<div class="stat-row"><label>${esc(name)}</label><strong ${s?.id?`data-live-reading="${esc(s.id)}" data-live-format="unit" data-live-unit="${esc(unit||typeUnit(s?.type))}"`:''}>${fmt(s?.value,s?.type==='Fan'?0:1)} ${unit||typeUnit(s?.type)}</strong></div>`;
const idMatch=(...ids)=>ids.map(id=>sensor(id)).filter(Boolean);
function chart(ids,unit='',small=false){
 const valid=ids.map(id=>typeof id==='string'?sensor(id):id).filter(Boolean);
 const labels=valid.map((s,i)=>`<span><i class="legend-key" style="background:${colors[i%colors.length]}"></i>${esc(label(s))}</span>`).join('');
 return `<div class="chart-wrap ${small?'small':''}"><canvas data-chart="${valid.map(s=>esc(s.id)).join('|')}" data-unit="${esc(unit)}"></canvas><canvas class="chart-hover-overlay" aria-hidden="true"></canvas></div><div class="chart-legend">${labels||'Waiting for compatible sensor data'}</div>`;
}
function latest(s) {return s?.value==null?'No reading':fmt(s.value,s.type==='Fan'?0:1)+' '+typeUnit(s.type);}
function explorer(){
 const candidates=S.sensors.filter(s=>{
   const typed=S.filter==='All'||(S.filter==='Key'&&['Temperature','Fan','Control','Power','Load'].includes(s.type))||
      s.type===S.filter||(S.filter==='Pinned'&&S.settings?.pinnedSensors?.includes(s.id));
   return typed;
 });
 candidates.sort((a,b)=>((S.settings?.pinnedSensors?.includes(b.id)?1:0)-(S.settings?.pinnedSensors?.includes(a.id)?1:0))||a.type.localeCompare(b.type)||label(a).localeCompare(label(b)));
 const selected=sensor(S.selected)||candidates[0];
 if(selected)S.selected=selected.id;
 const matches=candidates.filter(s=>(label(s)+' '+s.id+' '+s.device+' '+s.type).toLowerCase().includes(S.search.toLowerCase())).length;
 return `<section class="panel full">
 <div class="panel-head"><div><h2>Sensor registry</h2><p>${S.sensors.length} sensors available · <span id="matchCount">${matches}</span> matching · click a row to inspect</p></div><span class="pill mute">LIVE VALUES · STATIC LIST</span></div>
 <div class="toolbar"><input id="search" placeholder="Search sensor, ID, device or type…" value="${esc(S.search)}">
   ${['Key','Temperature','Fan','Control','Power','Load','Pinned','All'].map(v=>`<button class="filter-btn ${S.filter===v?'selected':''}" data-filter="${v}">${v}</button>`).join('')}
 </div>
 <div class="table-container" id="sensorList"><table><thead><tr><th>Sensor / identifier</th><th>Hardware</th><th>Type</th><th>Live</th><th>Pin</th></tr></thead><tbody>
 ${candidates.map(s=>`<tr data-select="${esc(s.id)}" data-search="${esc((label(s)+' '+s.id+' '+s.device+' '+s.type).toLowerCase())}" ${(label(s)+' '+s.id+' '+s.device+' '+s.type).toLowerCase().includes(S.search.toLowerCase())?'':'hidden'} class="${S.selected===s.id?'chosen':''}">
 <td>${esc(label(s))}<small>${esc(s.id)}</small></td>
 <td>${esc(s.device)}</td><td><span class="tag">${esc(s.type)}</span></td>
 <td data-live-reading="${esc(s.id)}" data-live-format="unit" data-live-unit="${esc(typeUnit(s.type))}">${latest(s)}</td>
 <td><button class="pin ${S.settings?.pinnedSensors?.includes(s.id)?'pinned':''}" data-pin="${esc(s.id)}" title="Toggle history pin">★</button></td></tr>`).join('')}
 </tbody></table>${candidates.length?'':'<div class="empty">No sensors in this category.</div>'}</div></section>
 ${selected?`<div class="full">${panel('Selected sensor · '+label(selected),selected.id+' · '+selected.device,
   `<div class="alias-editor">
    <div class="field"><label for="sensorAlias">Custom sensor name</label><input id="sensorAlias" maxlength="120" value="${esc(S.settings?.aliases?.[selected.id]||'')}" placeholder="${esc(selected.name)}"></div>
    <button class="btn" data-save-alias>Save name</button>
    <button class="btn secondary" data-clear-alias>Use original name</button>
   </div>
   <p class="note">This alias applies everywhere this sensor appears. Its stable sensor ID stays unchanged.</p>
   ${chart([selected],typeUnit(selected.type))}
   <div class="badge-line"><span class="note">Pin this sensor to keep recording it in history.</span><button class="btn tiny" data-pin="${esc(selected.id)}">${S.settings?.pinnedSensors?.includes(selected.id)?'Unpin sensor':'Pin for history'}</button></div>`)}
 </div>`:''}`;
}

function events(){
 return '<div class="two-col">'+
 panel('Event history','Recorded threshold crossings and recoveries',
 '<div class="event-list">'+(S.events.length?S.events.map(e=>'<div class="event '+esc(e.severity)+'"><div class="dot"></div><div><b>'+esc(e.message)+'</b><p>'+when(e.t)+' · '+esc(e.sensor)+(e.value!=null?' · '+fmt(e.value):'')+'</p></div></div>').join('')
  :'<div class="empty">No events yet. Add a monitoring threshold in Settings.</div>')+'</div>')+
 panel('Alert behavior','Configurable monitoring',
 '<p class="help">Alerts are evaluated by the elevated collector. High-value thresholds are configured for individual sensor IDs; recovery uses a two-unit hysteresis. Simulated curves do not provide hardware protection and never write fan outputs.</p>')+
 '</div>';
}
function settingsView(){
 const cfg=S.settings;if(!cfg)return '<div class="loading">Waiting for collector settings…</div>';
 const aliases=Object.entries(cfg.aliases||{}).sort((a,b)=>a[0].localeCompare(b[0]));
 const thresholds=Object.entries(cfg.highThresholds||{});
 const choices=S.sensors.filter(s=>['Temperature','Fan','Power','Load','Flow','Control'].includes(s.type));
 return '<div class="settings-grid">'+
 panel('Collection intervals','Configure hardware sampling, logging and retention',
 '<div class="form-grid">'+[['fastPollSeconds','Core sensor refresh (sec)'],['storagePollSeconds','Storage refresh (sec)'],
 ['saveEverySeconds','SQLite save interval (sec)'],['retentionDays','History retention (days)']]
 .map(([key,title])=>'<div class="field"><label for="'+key+'">'+title+'</label><input id="'+key+'" type="number" min="1" max="365" value="'+cfg[key]+'"></div>').join('')+
 '</div><p class="note">The simulation engine evaluates curves at 1 Hz using the newest timestamped samples. Storage can poll independently.</p>')+
 panel('Sensor aliases','Rename any physical or virtual sensor',
 aliases.length?aliases.map(([id,name])=>
 '<div class="alias-row"><label title="'+esc(id)+'">'+esc(sensor(id)?.name||id)+'</label>'+
 '<input data-alias="'+esc(id)+'" value="'+esc(name)+'" placeholder="Optional display alias"></div>').join(''):
 '<p class="note">No custom aliases. Select a sensor in Sensor Explorer to rename it.</p>')+
 panel('Monitoring thresholds','Alert when a selected sensor crosses its assigned value',
 thresholds.map(([id,number])=>'<div class="alias-row"><label title="'+esc(id)+'">'+esc(label(sensor(id)||{id,name:id}))+'</label>'+
 '<input type="number" step="any" data-threshold="'+esc(id)+'" value="'+number+'">'+
 '<button class="btn secondary tiny" data-remove-threshold="'+esc(id)+'">Remove</button></div>').join('')+
 '<div class="w-add-input"><select id="newThresholdId"><option value="">Choose sensor to monitor</option>'+
 choices.filter(s=>!(s.id in (cfg.highThresholds||{}))).map(s=>
 '<option value="'+esc(s.id)+'">'+esc(s.device+' / '+s.name+' ('+s.type+')')+'</option>').join('')+
 '</select><button class="btn secondary" id="addThreshold">+ Threshold</button></div>')+
 panel('Service and storage','Local-only hardware access',
 '<div class="stat-row"><label>Web address</label><strong>127.0.0.1:8765</strong></div>'+
 '<div class="stat-row"><label>Collector</label><strong>Windows Service</strong></div>'+
 '<div class="stat-row"><label>History and settings</label><strong>%ProgramData%\\SensorDash</strong></div>'+
 '<div class="stat-row"><label>Hardware controls</label><strong>Simulation only · No writes</strong></div>'+
 '<div class="stat-row"><label>Discovered storage devices</label><strong>'+(S.diagnostics?.storageDevices?.length??'—')+'</strong></div>'+
 '<p class="note">The browser host is unelevated; only the named-pipe collector reads hardware. Storage telemetry is available in the unified catalog.</p>')+
 '</div><div class="badge-line"><p class="note">Changes are validated by the collector service.</p>'+
 '<button class="btn" id="saveSettings">Save settings</button></div>';
}
function heading(){
 const [eyebrow,title,desc]=titles[S.view];
 byId('crumbName').textContent=eyebrow;
 byId('heading').innerHTML=`<div class="page-head"><div><div class="eyebrow">${esc(eyebrow)}</div><h1>${esc(title)}</h1><p class="sub">${esc(desc)}</p></div><div class="small-date">${new Date().toLocaleDateString(undefined,{weekday:'long',month:'long',day:'numeric',year:'numeric'})}<br>${S.sensors.length} discovered sensors</div></div>`;
}
function render(){
 heading();
 document.querySelectorAll('.nav').forEach(b=>b.classList.toggle('active',b.dataset.view===S.view));
 const frame=S.frame;
 const error=frame?.error?`<div class="help" style="border-left-color:#ec8792">Sensor collector: ${esc(frame.error)}. The web app is running; readings may require administrator access or a supported hardware driver.</div>`:'';
 const content=['overview','dashboards','cooling','curves','tray','virtual','storage'].includes(S.view)
  ? SDWorkspace.render(S,S.view,{metric,panel,chart})
  : ({explorer,events,settings:settingsView})[S.view]?.() ?? '<div class="empty">Choose a workspace from the menu.</div>';
 const scroll=S.view==='explorer'?byId('sensorList')?.scrollTop:null;
 byId('content').innerHTML=error+content;
 if(scroll!=null&&byId('sensorList'))byId('sensorList').scrollTop=scroll;
 ensureHistory();
}
// Collecting data and painting the UI run independently. Incoming 2-second sensor
// frames must never force a costly canvas redraw in the middle of a graph hover.
function scheduleLivePaint() {
 if(S.livePaintQueued||document.hidden)return;
 S.livePaintQueued=true;
 requestAnimationFrame(()=>{
   S.livePaintQueued=false;
   if(document.hidden)return;
   updateLiveValues();
 });
}
function updateLiveValues(){
 document.querySelectorAll('[data-live-reading]').forEach(el=>{
    const reading=sensor(el.dataset.liveReading);
    const next=fmt(reading?.value,reading?.type==='Fan'?0:1);
    const rendered=el.dataset.liveFormat==='value'?next:next+' '+(el.dataset.liveUnit||typeUnit(reading?.type));
    // Assigning textContent unnecessarily invalidates styles/layout, particularly
    // in the large explorer table (hundreds of rows). Only mutate changed values.
    if(el.textContent!==rendered)el.textContent=rendered;
 });
 SDWorkspace.paintSimulation(S);
}
// Graph history draws at most once every 8 seconds. While a graph is being
// inspected/dragged, freeze its base paint so the hover remains uninterrupted.
// Telemetry keeps recording at full speed regardless of cursor position.
const CHART_PAINT_INTERVAL_MS=8000;
function chartInUse(){
 return !!document.querySelector('.chart-wrap:hover');
}
function scheduleChartPaint(){
 S.chartDirty=true;
 if(S.chartPaintTimer!==null||document.hidden||!document.querySelector('canvas[data-chart]'))return;
 const delay=Math.max(0,CHART_PAINT_INTERVAL_MS-(performance.now()-S.lastChartPaint));
 S.chartPaintTimer=setTimeout(()=>{
   S.chartPaintTimer=null;
   if(!S.chartDirty||document.hidden||chartInUse())return;
   S.chartDirty=false;
   S.lastChartPaint=performance.now();
   requestAnimationFrame(()=>{if(chartInUse()){S.chartDirty=true;return}drawAll()});
 },delay);
}
function flushChartAfterHover(){
 if(!S.chartDirty||chartInUse()||document.hidden)return;
 // Avoid an immediate paint during the last mouse event of the interaction.
 setTimeout(()=>{
   if(chartInUse()||!S.chartDirty||document.hidden)return;
   if(S.chartPaintTimer!==null){clearTimeout(S.chartPaintTimer);S.chartPaintTimer=null}
   S.chartDirty=false;
   S.lastChartPaint=performance.now();
   requestAnimationFrame(()=>{if(!chartInUse())drawAll();else S.chartDirty=true});
 },120);
}

async function loadDiagnostics(shouldRender=true){
 try {
  const previous=JSON.stringify(S.diagnostics);
  S.diagnostics=await fetchJSON('/api/diagnostics');
  if(shouldRender&&S.view==='storage'&&previous!==JSON.stringify(S.diagnostics))render();
 }catch(e){console.warn('Storage diagnostics unavailable:',e.message);}
}
async function saveAlias(clear=false){
 const current=S.selected;if(!sensor(current))return;
 const cfg=structuredClone(S.settings);cfg.aliases=cfg.aliases||{};
 const name=clear?'':byId('sensorAlias')?.value.trim()||'';
 if(name)cfg.aliases[current]=name;else delete cfg.aliases[current];
 try {
   S.settings=await fetchJSON('/api/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(cfg)});
   toast(name?'Sensor name saved':'Original sensor name restored');render();
 }catch(e){toast('Could not save alias: '+e.message);}
}
function updateFrame(frame){
 S.frame=frame;S.sensors=frame.sensors||[];
 S.byId=new Map(S.sensors.map(s=>[s.id,s]));
 const t=Date.parse(frame.timestamp);
 S.sensors.forEach(s=>{
    if(s.value==null)return;
    const a=S.liveCache.get(s.id)||[];
    if(!a.length||a[a.length-1].t!==t)a.push({t,v:s.value});
    while(a.length>140)a.shift();
    S.liveCache.set(s.id,a);
 });
 S.connected=!frame.error;
 document.body.classList.toggle('offline',!S.connected);
 byId('liveStatus').textContent=S.connected?'Live telemetry':'Sensor unavailable';
 byId('collectorStatus').textContent=S.connected?'Collector online':'Collector warning';
 byId('lastUpdated').textContent='Sample: '+when(t);
 const signature=S.sensors.map(s=>s.id).join('|');
 if(S.schemaKey!==signature){S.schemaKey=signature;render();return}
 scheduleLivePaint();
 scheduleChartPaint();
}
async function loadEvents(){
 try{S.events=await fetchJSON('/api/events');byId('eventCount').textContent=S.events.filter(e=>e.severity==='warning').length||'';if(S.view==='events')render()}catch{}
}
async function fetchJSON(url,opts){
 const r=await fetch(url,opts);if(!r.ok)throw new Error(await r.text()||'Request failed');return r.json();
}
function ensureHistory(){
 const nodes=[...document.querySelectorAll('canvas[data-chart]')];
 const ids=[...new Set(nodes.flatMap(c=>c.dataset.chart.split('|').filter(Boolean)))];
 if(!ids.length)return;
 const key=ids.sort().join(',')+'|'+S.hours;
 const now=Date.now();
 if(key===S.historyKey&&now-S.historyFetch<20000){drawAll();return}
 if(S.historyPending&&key===S.historyKey){drawAll();return}
 S.historyKey=key;S.historyFetch=now;S.historyPending=true;
 fetchJSON('/api/history?ids='+encodeURIComponent(ids.join(','))+'&hours='+encodeURIComponent(S.hours))
   .then(data=>{if(S.historyKey!==key)return;S.history=Object.fromEntries((data.series||[]).map(s=>[s.id,s.points]));scheduleChartPaint()})
   .catch(err=>{console.warn('History fetch:',err.message)}).finally(()=>{S.historyPending=false});
 drawAll();
}
function drawAll(){
 if(document.hidden||chartInUse()){S.chartDirty=true;return}
 document.querySelectorAll('canvas[data-chart]').forEach(drawCanvas);
 S.lastChartPaint=performance.now();
 S.chartDirty=false;
}
function drawCanvas(canvas){
 const ids=canvas.dataset.chart.split('|').filter(Boolean), series=ids.map((id,i)=>{
   const base=S.history[id]||[], live=S.liveCache.get(id)||[], first=Date.now()-S.hours*3600000;
   const points=base.filter(p=>p.t>=first);
   live.forEach(p=>{if(p.t>=first&&(!points.length||p.t>points[points.length-1].t))points.push(p)});
   return {id,color:colors[i%colors.length],points};
 }).filter(s=>s.points.length);
 const rect=canvas.getBoundingClientRect(),w=Math.max(100,rect.width),h=Math.max(100,rect.height);
 const ratio=Math.min(window.devicePixelRatio||1,2);
 const pixelWidth=Math.round(w*ratio),pixelHeight=Math.round(h*ratio);
 // Canvas dimensions erase the bitmap and reset its context, so never assign
 // them when unchanged. Only full/redraws should touch these buffers.
 if(canvas.width!==pixelWidth)canvas.width=pixelWidth;
 if(canvas.height!==pixelHeight)canvas.height=pixelHeight;
 const ctx=canvas.getContext('2d');ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,w,h);
 const p={l:49,r:14,t:17,b:31};const cw=w-p.l-p.r,ch=h-p.t-p.b;
 if(!series.length){const overlay=canvas.nextElementSibling;if(overlay?.classList.contains('chart-hover-overlay')){overlay.width=canvas.width;overlay.height=canvas.height}ctx.fillStyle='#829aad';ctx.font='12px Segoe UI';ctx.textAlign='center';ctx.fillText('Collecting sensor history…',w/2,h/2);return}
 let lo=Math.min(...series.flatMap(s=>s.points.map(p=>p.v))),hi=Math.max(...series.flatMap(s=>s.points.map(p=>p.v)));
 if(lo===hi){lo-=5;hi+=5}
 const pad=(hi-lo)*.12;lo-=pad;hi+=pad;
 const xmin=Date.now()-S.hours*3600000,xmax=Date.now();
 const tx=t=>p.l+Math.max(0,Math.min(1,(t-xmin)/(xmax-xmin)))*cw;
 const ty=v=>p.t+ch-(v-lo)/(hi-lo)*ch;
 ctx.lineWidth=1;ctx.font='11px Segoe UI';
 for(let i=0;i<=4;i++){
   const y=p.t+i*ch/4;ctx.strokeStyle='#263b4a';ctx.beginPath();ctx.moveTo(p.l,y);ctx.lineTo(w-p.r,y);ctx.stroke();
   ctx.textAlign='right';ctx.fillStyle='#7990a3';ctx.fillText(fmt(hi-i*(hi-lo)/4,0),p.l-9,y+4);
 }
 for(let i=0;i<=4;i++){
   const x=p.l+i*cw/4;ctx.fillStyle='#7990a3';ctx.textAlign='center';
   ctx.fillText(shorttime(xmin+i*(xmax-xmin)/4),x,h-8);
 }
 series.forEach((s,i)=>{
    ctx.strokeStyle=s.color;ctx.lineWidth=2;ctx.lineJoin='round';ctx.lineCap='round';
    ctx.beginPath();s.points.forEach((q,j)=>{const x=tx(q.t),y=ty(q.v);if(j===0)ctx.moveTo(x,y);else ctx.lineTo(x,y)});
    ctx.stroke();if(s.points.length===1){ctx.beginPath();ctx.arc(tx(s.points[0].t),ty(s.points[0].v),3,0,7);ctx.fillStyle=s.color;ctx.fill()}
 });
 // The base canvas contains the graph only. Mouse interaction draws into a
 // transparent, independently cleared overlay so moving the cursor cannot leave
 // crosshairs or tooltip "ghosts" behind.
 const overlay=canvas.nextElementSibling;
 if(!overlay || !overlay.classList.contains('chart-hover-overlay'))return;
 if(overlay.width!==canvas.width)overlay.width=canvas.width;
 if(overlay.height!==canvas.height)overlay.height=canvas.height;
 const hoverCtx=overlay.getContext('2d');
 hoverCtx.setTransform(ratio,0,0,ratio,0,0);
 const clearHover=()=>hoverCtx.clearRect(0,0,w,h);
 canvas.onmousemove=e=>{
   clearHover();
   const x=e.clientX-canvas.getBoundingClientRect().left;
   const desired=xmin+(x-p.l)/cw*(xmax-xmin);
   if(desired>=xmin&&desired<=xmax)
     drawCanvasOverlay(hoverCtx,series,{tx,ty,p,h,w,cw,ch},desired);
 };
 canvas.onmouseleave=()=>{clearHover();flushChartAfterHover()};
}
function drawCanvasOverlay(ctx,series,layout,desired){
 const {tx,ty,p,h,w}=layout;
 if(desired<Date.now()-S.hours*3600000||desired>Date.now())return;
 const values=series.map(s=>s.points.reduce((a,b)=>Math.abs(a.t-desired)<=Math.abs(b.t-desired)?a:b));
 if(!values.length)return;
 const x=tx(values[0].t);
 ctx.save();ctx.setLineDash([4,4]);ctx.strokeStyle='#819aab';ctx.beginPath();ctx.moveTo(x,p.t);ctx.lineTo(x,h-p.b);ctx.stroke();ctx.restore();
 ctx.fillStyle='#101923d9';ctx.strokeStyle='#3b5867';const tipW=130,tipH=26+values.length*18,tipX=Math.max(5,Math.min(w-tipW-5,x+9)),tipY=5;
 ctx.fillRect(tipX,tipY,tipW,tipH);ctx.strokeRect(tipX,tipY,tipW,tipH);
 ctx.font='11px Segoe UI';ctx.textAlign='left';ctx.fillStyle='#a8bdc9';ctx.fillText(shorttime(values[0].t),tipX+9,tipY+16);
 values.forEach((q,i)=>{ctx.fillStyle=series[i].color;ctx.fillText(fmt(q.v,1),tipX+9,tipY+34+i*18)});
}
async function loadSimulation(){
 if(S.view!=='curves')return;
 try{S.simulation=await fetchJSON('/api/simulation');SDWorkspace.paintSimulation(S);}
 catch(e){console.warn('Simulation preview:',e.message);}
}
async function changeThreshold(id,remove=false){
 if(!id)return;
 const cfg=structuredClone(S.settings);cfg.highThresholds=cfg.highThresholds||{};
 if(remove)delete cfg.highThresholds[id];
 else {
  const reading=sensor(id);
  cfg.highThresholds[id]=Math.round((reading?.value??40)+10);
 }
 try{
  S.settings=await fetchJSON('/api/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(cfg)});
  render();toast(remove?'Threshold removed':'Threshold added');
 }catch(e){toast('Could not update threshold: '+e.message)}
}
async function saveSettings(){
 const cfg=structuredClone(S.settings);
 for(const key of ['fastPollSeconds','storagePollSeconds','saveEverySeconds','retentionDays'])cfg[key]=Number(byId(key).value);
 cfg.sensorBindings=cfg.sensorBindings||{};
 document.querySelectorAll('[data-sensor-role]').forEach(el=>{
   if(el.value)cfg.sensorBindings[el.dataset.sensorRole]=el.value;
   else delete cfg.sensorBindings[el.dataset.sensorRole];
 });
 document.querySelectorAll('[data-alias]').forEach(el=>{if(el.value.trim())cfg.aliases[el.dataset.alias]=el.value.trim();else delete cfg.aliases[el.dataset.alias]});
 document.querySelectorAll('[data-threshold]').forEach(el=>{if(el.value.trim())cfg.highThresholds[el.dataset.threshold]=Number(el.value);else delete cfg.highThresholds[el.dataset.threshold]});
 try{S.settings=await fetchJSON('/api/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(cfg)});toast('Settings saved');render()}
 catch(err){toast('Could not save settings: '+err.message)}
}
async function togglePin(id){
 const cfg=structuredClone(S.settings);
 cfg.pinnedSensors=cfg.pinnedSensors||[];
 cfg.pinnedSensors=cfg.pinnedSensors.includes(id)?cfg.pinnedSensors.filter(x=>x!==id):[...cfg.pinnedSensors,id];
 try{S.settings=await fetchJSON('/api/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(cfg)});toast(cfg.pinnedSensors.includes(id)?'Sensor pinned':'Sensor unpinned');render()}
 catch(err){toast(err.message)}
}
let toastTimer;function toast(message){const el=byId('toast');el.textContent=message;el.classList.add('show');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('show'),3100)}
document.addEventListener('click',e=>{
 const v=e.target.closest('[data-view]');if(v){S.view=v.dataset.view;history.replaceState(null,'','#'+S.view);S.historyKey='';if(S.view==='events')loadEvents();if(S.view==='storage'||S.view==='settings')loadDiagnostics().catch(()=>{});render();return}
 if(e.target.closest('[data-work]')){SDWorkspace.handle(e,S,{render,toast});return}
 const f=e.target.closest('[data-filter]');if(f){S.filter=f.dataset.filter;render();return}
 const pin=e.target.closest('[data-pin]');if(pin){e.stopPropagation();togglePin(pin.dataset.pin);return}
 const sel=e.target.closest('[data-select]');if(sel){S.selected=sel.dataset.select;S.historyKey='';render();return}
 if(e.target.closest('[data-save-alias]')){saveAlias(false);return}
 if(e.target.closest('[data-clear-alias]')){saveAlias(true);return}
 if(e.target.closest('#addThreshold')){changeThreshold(byId('newThresholdId')?.value);return}
 const rm=e.target.closest('[data-remove-threshold]');if(rm){changeThreshold(rm.dataset.removeThreshold,true);return}
 if(e.target.closest('#saveSettings'))saveSettings();
});
document.addEventListener('input',e=>{
 if(e.target.id==='search'){
  S.search=e.target.value;const query=S.search.toLowerCase();let shown=0;
  document.querySelectorAll('#sensorList tr[data-select]').forEach(row=>{
   const visible=row.dataset.search.includes(query);row.hidden=!visible;if(visible)shown++;
  });
  if(byId('matchCount'))byId('matchCount').textContent=shown;
 }
});
byId('range').addEventListener('change',e=>{S.hours=Number(e.target.value);S.historyKey='';render()});
byId('refresh').addEventListener('click',()=>{S.historyKey='';loadEvents();render();toast('Refreshing sensor history')});
let resizePaint;
window.addEventListener('resize',()=>{
  clearTimeout(resizePaint);
  resizePaint=setTimeout(()=>{S.chartDirty=true;flushChartAfterHover();if(!chartInUse())drawAll()},200);
});
document.addEventListener('visibilitychange',()=>{
 if(!document.hidden){scheduleLivePaint();scheduleChartPaint()}
});
// A history query is cheap compared to polling hardware; perform it separately
// every 30 seconds to keep long-running graphs complete beyond liveCache's 140 samples.
setInterval(()=>{
 if(document.hidden||!document.querySelector('canvas[data-chart]'))return;
 if(Date.now()-S.historyFetch>=30000)ensureHistory();
},30000);
async function bootstrap(){
 try{
   const [cfg,frame]=await Promise.all([fetchJSON('/api/settings'),fetchJSON('/api/live')]);
   S.settings=cfg;const initial=location.hash.slice(1);if(titles[initial])S.view=initial;updateFrame(frame);render();loadEvents();loadDiagnostics(false);loadSimulation();
   const stream=new EventSource('/api/stream');
   stream.onmessage=e=>{try{updateFrame(JSON.parse(e.data))}catch(err){console.warn('Sensor frame error',err)}};
   stream.onerror=()=>{byId('liveStatus').textContent='Reconnecting…'};
   setInterval(loadEvents,30000);
   setInterval(loadSimulation,2000);
   setInterval(()=>loadDiagnostics(false),30000);
   setInterval(()=>{if(Date.now()-Date.parse(S.frame?.timestamp||0)>12000)fetchJSON('/api/live').then(updateFrame).catch(()=>{})},5000);
 }catch(err){
   byId('content').innerHTML='<div class="help">Could not connect to the local API: '+esc(err.message)+'. Restart SensorDash.exe and refresh this page.</div>';
   document.body.classList.add('offline');
 }
}
bootstrap();
