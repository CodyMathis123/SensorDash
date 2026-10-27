'use strict';
const IDS = {
 cpu:'/amdcpu/0/temperature/2', ccd1:'/amdcpu/0/temperature/3', ccd2:'/amdcpu/0/temperature/4',
 gpu:'/gpu-amd/0/temperature/0', hotspot:'/gpu-amd/0/temperature/7', vram:'/gpu-amd/0/temperature/1',
 nvme:'/nvme/0/temperature/0', cpuPower:'/amdcpu/0/power/0', gpuPower:'/gpu-amd/0/power/3',
 cpuLoad:'/amdcpu/0/load/0', gpuLoad:'/gpu-amd/0/load/0', ram:'/ram/load/0',
 radiator:'/lpc/it8688e/0/fan/0', pump:'/lpc/it8688e/0/fan/1', nvmeFan:'/lpc/it8688e/0/fan/2',
 fanDuty:'/lpc/it8688e/0/control/2'
};
const titles={overview:['SYSTEM OVERVIEW','Hardware at a glance','Live CPU, GPU, cooling and storage telemetry'],
 cooling:['THERMAL ANALYSIS','Water Cooling','A shared CPU + GPU loop · radiator fan bank · DDC pump tach'],
 nvme:['STORAGE COOLING','NVMe Cooling','SSD composite temperature, PWM readout and dedicated heatsink fan'],
 explorer:['SENSOR LIBRARY','Sensor Explorer','Every sensor reported by LibreHardwareMonitor, with stable sensor IDs'],
 events:['HISTORY & ALERTS','Event Log','Thermal threshold changes and pump-tach warnings'],
 settings:['CONFIGURATION','Settings','Polling, storage, sensor aliases and alert thresholds']};
const colors=['#62d5ce','#6fa7ed','#f2bd6d','#ec8792','#a991e8','#69b5cc','#e4a5cb'];
const S={view:'overview',frame:null,sensors:[],byId:new Map(),settings:null,events:[],filter:'Key',search:'',selected:IDS.cpu,
 hours:1,history:{},historyKey:'',historyFetch:0,historyPending:false,lastRefresh:0,liveCache:new Map(),connected:false,diagnostics:null,schemaKey:''};
const byId=id=>document.getElementById(id);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=(v,dec=1)=>v==null||!Number.isFinite(v)?'—':Number(v).toLocaleString(undefined,{maximumFractionDigits:dec,minimumFractionDigits:dec});
const when=t=>new Date(t).toLocaleString(undefined,{month:'short',day:'numeric',hour:'numeric',minute:'2-digit',second:'2-digit'});
const shorttime=t=>new Date(t).toLocaleTimeString(undefined,{hour:'numeric',minute:'2-digit'});
const typeUnit=t=>({Temperature:'°C',Fan:'RPM',Control:'%',Load:'%',Power:'W',Clock:'MHz',Voltage:'V',Throughput:'B/s',Data:'GB',Level:'%'}[t]||'');
const label=s=>S.settings?.aliases?.[s?.id]||s?.name||'Unavailable';
const sensor=id=>S.byId.get(id);
// A saved role binding takes priority; otherwise discover by metadata rather than
// assuming that current hardware has the exact sensor IDs from an old CSV log.
function resolveRole(key,legacy,type,namePattern,devicePattern) {
 const assigned=S.settings?.sensorBindings?.[key];
 if(assigned) return sensor(assigned)||null;
 const preferred=sensor(legacy);
 if(preferred?.type===type && namePattern.test(preferred.name) &&
    (!devicePattern || devicePattern.test(preferred.device+' '+preferred.deviceType))) return preferred;
 const compatible=S.sensors.filter(v=>v.type===type &&
   namePattern.test(v.name) && (!devicePattern||devicePattern.test(v.device+' '+v.deviceType)));
 return compatible.find(v=>v.value!=null && (type!=='Temperature'||v.value>0))||compatible[0]||preferred||null;
}

const val=id=>sensor(id)?.value??null;
const metric=(name,s,accent,detail,unit,icon)=>`<div class="metric" style="--accent:${accent}"><div class="metric-top"><span>${esc(name)}</span><span class="metric-icon">${icon}</span></div><div class="metric-num"><b ${s?.id==='__calculated__'?'data-live-calc="nvmeHeadroom"':s?.id?`data-live-reading="${esc(s.id)}" data-live-format="value"`:''}>${fmt(s?.value,s?.type==='Fan'?0:1)}</b><span>${unit||typeUnit(s?.type)}</span></div><div class="metric-detail">${esc(detail)}</div></div>`;
const panel=(title,subtitle,content,tag='')=>`<section class="panel"><div class="panel-head"><div><h2>${esc(title)}</h2>${subtitle?' <p>'+esc(subtitle)+'</p>':''}</div>${tag}</div>${content}</section>`;
const stat=(name,s,unit)=>`<div class="stat-row"><label>${esc(name)}</label><strong ${s?.id?`data-live-reading="${esc(s.id)}" data-live-format="unit" data-live-unit="${esc(unit||typeUnit(s?.type))}"`:''}>${fmt(s?.value,s?.type==='Fan'?0:1)} ${unit||typeUnit(s?.type)}</strong></div>`;
const idMatch=(...ids)=>ids.map(id=>sensor(id)).filter(Boolean);
function chart(ids,unit='',small=false){
 const valid=ids.map(id=>typeof id==='string'?sensor(id):id).filter(Boolean);
 const labels=valid.map((s,i)=>`<span><i class="legend-key" style="background:${colors[i%colors.length]}"></i>${esc(label(s))}</span>`).join('');
 return `<div class="chart-wrap ${small?'small':''}"><canvas data-chart="${valid.map(s=>esc(s.id)).join('|')}" data-unit="${esc(unit)}"></canvas></div><div class="chart-legend">${labels||'Waiting for compatible sensor data'}</div>`;
}
function latest(s) {return s?.value==null?'No reading':fmt(s.value,s.type==='Fan'?0:1)+' '+typeUnit(s.type);}
function getDefaults(){
 return {
 cpu:resolveRole('cpu',IDS.cpu,'Temperature',/Tctl\/Tdie|CPU Package|CPU Core/i,/CPU|Ryzen|AMD/i),
 gpu:resolveRole('gpu',IDS.gpu,'Temperature',/GPU Core/i,/GPU|Radeon/i),
 hotspot:resolveRole('hotspot',IDS.hotspot,'Temperature',/Hot.?Spot/i,/GPU|Radeon/i),
 nvme:resolveRole('nvme',IDS.nvme,'Temperature',/Composite|Drive Temperature|Temperature/i,/Storage|NVMe|SSD|FireCuda|Seagate|Samsung|WD|Western Digital/i),
 radiator:resolveRole('radiator',IDS.radiator,'Fan',/^Fan #1$|Radiator/i,/LPC|IT8688|Motherboard/i),
 pump:resolveRole('pump',IDS.pump,'Fan',/^Fan #2$|Pump/i,/LPC|IT8688|Motherboard/i),
 nvmeFan:resolveRole('nvmeFan',IDS.nvmeFan,'Fan',/^Fan #3$|NVMe/i,/LPC|IT8688|Motherboard/i),
 cpuPower:resolveRole('cpuPower',IDS.cpuPower,'Power',/Package/i,/CPU|Ryzen|AMD/i),
 gpuPower:resolveRole('gpuPower',IDS.gpuPower,'Power',/GPU Package|Total Board/i,/GPU|Radeon/i),
 ram:resolveRole('ram',IDS.ram,'Load',/Memory/i,/Memory|RAM/i),
 ccd1:sensor(IDS.ccd1),ccd2:sensor(IDS.ccd2),vram:sensor(IDS.vram),
 fanDuty:resolveRole('fanDuty',IDS.fanDuty,'Control',/Fan #3|Fan/i,/LPC|IT8688|Motherboard/i),
 cpuLoad:sensor(IDS.cpuLoad),gpuLoad:sensor(IDS.gpuLoad)
 };
}

function overview(){
 const d=getDefaults();
 const out=`<div class="metrics">
  ${metric('CPU temperature',d.cpu,'#62d5ce','Ryzen processor · Tctl/Tdie','°C','◉')}
  ${metric('GPU core',d.gpu,'#6fa7ed','Radeon water block','°C','▣')}
  ${metric('GPU hotspot',d.hotspot,'#f2bd6d','GPU junction temperature','°C','♨')}
  ${metric('NVMe SSD',d.nvme,'#a991e8','Composite temperature','°C','▰')}
 </div>
 <div class="two-col">
  ${panel('Thermal timeline','The temperature story, not just the latest number',chart([d.cpu,d.gpu,d.hotspot,d.nvme],'°C'))}
  ${panel('Cooling hardware','Live fan and pump tachometers',
    stat('ALL RADS · shared fan bank',d.radiator,'RPM')+
    stat('DDC pump · monitoring only',d.pump,'RPM')+
    stat('NVMe heatsink fan',d.nvmeFan,'RPM')+
    '<div class="help">Fan Control owns the curves and output. SensorDash only reads the resulting RPM and exposed control values.</div>'
  )}
 </div>
 <div class="mini-rows">
  <div class="mini"><div class="label">CPU PACKAGE POWER</div><strong>${fmt(d.cpuPower?.value)} W</strong><small>Heat entering your CPU block</small></div>
  <div class="mini"><div class="label">GPU PACKAGE POWER</div><strong>${fmt(d.gpuPower?.value)} W</strong><small>Heat entering your GPU block</small></div>
  <div class="mini"><div class="label">SYSTEM MEMORY</div><strong>${fmt(d.ram?.value)} %</strong><small>Reported utilization</small></div>
 </div>
 <div class="two-col">
  ${panel('Power over time','CPU + GPU package power',chart([d.cpuPower,d.gpuPower],'W',true))}
  ${panel('Live component load','Useful context for thermal peaks',stat('CPU total load',d.cpuLoad,'%')+stat('GPU core load',d.gpuLoad,'%')+stat('GPU memory temperature',d.vram,'°C')+stat('CPU CCD 1',d.ccd1,'°C')+stat('CPU CCD 2',d.ccd2,'°C'))}
 </div>`;
 return out;
}
function cooling(){
 const d=getDefaults();
 return `<div class="metrics">
  ${metric('CPU',d.cpu,'#62d5ce','Core Tctl/Tdie','°C','◉')}
  ${metric('GPU hotspot',d.hotspot,'#f2bd6d','GPU junction','°C','♨')}
  ${metric('Radiator bank',d.radiator,'#6fa7ed','Fan Control · ALL RADS','RPM','❋')}
  ${metric('DDC pump',d.pump,'#67caa2','Tach only · non-PWM','RPM','◌')}
 </div>
 <div class="full">${panel('Core thermal behavior','CPU, GPU core and hotspot temperatures',chart([d.cpu,d.gpu,d.hotspot],'°C'))}</div>
 <div class="half-col">
  ${panel('Cooling RPM history','Radiator fans versus pump tachometer',chart([d.radiator,d.pump],'RPM',true))}
  ${panel('Heat input','CPU and GPU package power',chart([d.cpuPower,d.gpuPower],'W',true))}
 </div>
 <div class="half-col">
  ${panel('Cooling snapshot','All measurements sourced from hardware sensors',
    stat('Radiator fans',d.radiator,'RPM')+stat('DDC pump tach',d.pump,'RPM')+
    stat('GPU core temperature',d.gpu,'°C')+stat('GPU hotspot temperature',d.hotspot,'°C')+
    stat('GPU hotspot - core Δ',d.gpu?.value!=null&&d.hotspot?.value!=null?{value:d.hotspot.value-d.gpu.value,type:'Temperature'}:null,'°C'))}
  ${panel('Loop diagnostics','How to interpret these readings',
    '<div class="help">The CPU and GPU share a water loop. Your <b>ALL RADS</b> bank follows Fan Control’s maximum of CPU and GPU temperature curves.</div>'+
    '<p class="note">Pump RPM is telemetry only. A zero tach reading can indicate an unavailable sensor or disconnected tach cable; it does not by itself prove the pump stopped. A coolant temperature probe would give a better indication of sustained radiator load.</p>')}
 </div>`;
}
function nvme(){
 const d=getDefaults();const curve=[[35,30],[40,30],[45,40],[50,55],[55,75],[60,100]];
 const poly=curve.map(([t,p])=>((t-30)/35*350+40)+','+(178-p/100*140)).join(' ');
 const diag=S.diagnostics;
 const storage=(diag?.storageDevices||[]).map(x=>x.name);
 const temps=diag?.storageTemperatures||[];
 const diagnostic=diag?panel('Storage sensor diagnostics','What the embedded LibreHardwareMonitor library discovers',
    '<div class="stat-row"><label>Windows administrator access</label><strong>'+ (diag.isAdministrator?'Yes':'No (standard user)')+'</strong></div>'+
    '<div class="stat-row"><label>Detected storage devices</label><strong>'+storage.length+'</strong></div>'+
    '<div class="stat-row"><label>Storage temperature sensors</label><strong>'+temps.length+'</strong></div>'+
    '<div class="stat-row"><label>Storage poll interval</label><strong>'+diag.storagePollSeconds+' seconds</strong></div>'+
    (storage.length?'<p class="note">'+storage.map(esc).join(' · ')+'</p>':'')+
    (temps.length?'<div class="note">'+temps.map(x=>esc(x.device+' · '+x.name+' ['+x.id+'] = '+(x.value??'N/A'))).join('<hr>')+'</div>':'')+
    '<div class="help">'+esc(diag.note)+'</div>'+
    (!diag.isAdministrator?'<p class="note">For a diagnostic comparison, close SensorDash and run it once as Administrator. You can still keep everyday startup under your normal user account.</p>':''))
    :'<div class="help">Loading storage inventory and access diagnostics…</div>';
 return `<div class="metrics">
  ${metric('SSD composite',d.nvme,'#a991e8','Measured storage temperature','°C','▰')}
  ${metric('NVMe heatsink',d.nvmeFan,'#62d5ce','Fan tachometer','RPM','❋')}
  ${metric('Reported fan duty',d.fanDuty,'#6fa7ed','Motherboard channel, if exposed','%','◉')}
  ${metric('Thermal headroom',d.nvme?.value!=null?{value:60-d.nvme.value,type:'Temperature',id:'__calculated__'}:null,'#f2bd6d','Below your 60°C curve endpoint','°C','◈')}
 </div>
 <div class="half-col">
  ${panel('NVMe thermal timeline','SSD composite temperature',chart([d.nvme],'°C'))}
  ${panel('NVMe fan tachometer','Measured RPM',chart([d.nvmeFan],'RPM'))}
 </div>
 <div class="half-col">
  ${panel('Current suggested curve','Visualization only · controlled separately in Fan Control',
   `<svg class="curve-svg" viewBox="0 0 450 220" role="img" aria-label="NVMe target fan curve">
      ${[0,25,50,75,100].map(p=>`<line x1="40" y1="${178-p/100*140}" x2="420" y2="${178-p/100*140}" stroke="#334b5d" stroke-dasharray="3 7"/><text x="6" y="${182-p/100*140}" fill="#94aabd" font-size="11">${p}%</text>`).join('')}
      <polyline fill="none" stroke="#62d5ce" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" points="${poly}"/>
      ${curve.map(([t,p])=>`<circle cx="${(t-30)/35*350+40}" cy="${178-p/100*140}" r="4" fill="#62d5ce"/><text x="${(t-30)/35*350+40}" y="203" fill="#94aabd" text-anchor="middle" font-size="11">${t}°</text>`).join('')}
    </svg><div class="curve-grid">${curve.map(([t,p])=>`<div class="curve-point"><b>${t}°C</b><small>${p}%</small></div>`).join('')}</div>`
  )}
  ${panel('Fan observations','Storage temperature versus fan control',stat('SSD composite',d.nvme,'°C')+
    stat('NVMe heatsink fan',d.nvmeFan,'RPM')+
    stat('Reported control duty',d.fanDuty,'%')+
    '<div class="help">Suggested Fan Control settings: storage polling <b>10 seconds</b>, hysteresis <b>2°C</b>, response time <b>5 seconds</b>. Verify reliable startup at 30% duty.</div>'+
    '<p class="note">The graph represents the proposed Fan Control curve, not a curve stored or applied by SensorDash. The reported control sensor may not correspond to the NVMe fan on every board.</p>')}
 </div><div class="full">${diagnostic}</div>`;
}
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
 return `<div class="two-col">
  ${panel('Event history','Most recent alerts and recovery events',`<div class="event-list">${S.events.length?S.events.map(e=>`<div class="event ${esc(e.severity)}"><div class="dot"></div><div><b>${esc(e.message)}</b><p>${when(e.t)} · ${esc(e.sensor)}${e.value!=null?' · '+fmt(e.value):''}</p></div></div>`).join(''):'<div class="empty">No events yet. Alerts appear here when configured thresholds are crossed.</div>'}</div>`)}
  ${panel('Alert behavior','Monitoring safeguards','<div class="help">Thresholds are evaluated on persisted samples; default checks include CPU, GPU hotspot, NVMe and the dedicated DDC pump tachometer.</div><p class="note">A pump low-RPM warning is raised only after the tach has previously reported a running value and then stays below 1,000 RPM for at least 20 seconds. Verify cooling independently if hardware temperatures rise unexpectedly.</p><p class="note">Threshold recovery requires cooling at least 2°C below the configured limit.</p><button class="btn secondary" data-view="settings">Configure thresholds</button>')}
 </div>`;
}
function settingsView(){
 const cfg=S.settings;if(!cfg)return '<div class="loading">Loading settings…</div>';
 const selected=[IDS.radiator,IDS.pump,IDS.nvmeFan,IDS.cpu,IDS.gpu,IDS.hotspot,IDS.nvme];

 // Keep sensor roles independent of the original CSV's hardware IDs.
 const roles=[
  ['cpu','CPU temperature','Temperature'],
  ['gpu','GPU core temperature','Temperature'],
  ['hotspot','GPU hotspot','Temperature'],
  ['nvme','NVMe temperature','Temperature'],
  ['radiator','ALL RADS fan tach','Fan'],
  ['pump','DDC pump tach','Fan'],
  ['nvmeFan','NVMe heatsink fan tach','Fan'],
  ['fanDuty','NVMe fan control readout','Control'],
  ['cpuPower','CPU package power','Power'],
  ['gpuPower','GPU package power','Power']
 ];
 const detected=getDefaults();
 const roleEditor=roles.map(([key,title,type])=>{
   const saved=cfg.sensorBindings?.[key]||'';
   const possible=S.sensors.filter(x=>x.type===type).sort((a,b)=>a.device.localeCompare(b.device)||a.name.localeCompare(b.name));
   const unavailable=saved&&!possible.some(x=>x.id===saved);
   const auto=detected[key]?label(detected[key])+' ('+detected[key].id+')':'no matching sensor';
   return `<div class="field sensor-role"><label for="role-${key}">${esc(title)}</label>
    <select id="role-${key}" data-sensor-role="${key}">
      <option value="">Automatic · ${esc(auto)}</option>
      ${unavailable?`<option selected value="${esc(saved)}">Unavailable: ${esc(saved)}</option>`:''}
      ${possible.map(x=>`<option value="${esc(x.id)}" ${saved===x.id?'selected':''}>${esc(x.device+' / '+x.name+' ['+x.id+']')}</option>`).join('')}
    </select></div>`;
 }).join('');
 return `<div class="settings-grid">
  ${panel('Collection intervals','Adjust sensor refresh and history retention',`<div class="form-grid">
   ${[['fastPollSeconds','Core sensor refresh (sec)'],['storagePollSeconds','Storage refresh (sec)'],['saveEverySeconds','SQLite save interval (sec)'],['retentionDays','History retention (days)']].map(([key,title])=>`<div class="field"><label for="${key}">${title}</label><input id="${key}" type="number" min="1" max="365" value="${cfg[key]}"></div>`).join('')}
  </div><p class="note">NVMe polling is intentionally slower than CPU/GPU polling. History is stored in your Windows user profile.</p>`)}
  ${panel('Sensor role assignments','Choose the actual source for each dashboard gauge; Automatic is the default',
    '<div class="role-list">'+roleEditor+'</div><p class="note">Only sensors currently detected by LibreHardwareMonitor can be selected. If your SSD is absent, consult the Storage Diagnostics card on the NVMe page.</p>')}
  ${panel('Sensor aliases','Edit these here, or rename ANY sensor directly from Sensor Explorer',
    [...new Set([...selected,...Object.keys(cfg.aliases||{})])].map(id=>`<div class="alias-row"><label title="${esc(id)}">${esc(sensor(id)?.name||id)}</label><input data-alias="${esc(id)}" value="${esc(cfg.aliases?.[id]||'')}" placeholder="Friendly name"></div>`).join(''))}
  ${panel('High-temperature thresholds','Alert whenever a reading crosses its configured value',[IDS.cpu,IDS.hotspot,IDS.nvme].map(id=>`<div class="alias-row"><label title="${esc(id)}">${esc(label(sensor(id)||{id,name:id}))}</label><input type="number" min="0" max="150" step="1" data-threshold="${esc(id)}" value="${cfg.highThresholds?.[id]??''}"></div>`).join('')+'<p class="note">Pump tach warning uses a fixed safety threshold of 1,000 RPM for 20 seconds, after it has first reported a running value.</p>')}
  ${panel('Application & startup','Keep everything local','<div class="stat-row"><label>Dashboard address</label><strong>127.0.0.1:8765</strong></div><div class="stat-row"><label>Fan control</label><strong>Disabled (read-only)</strong></div><div class="stat-row"><label>Storage</label><strong>%LOCALAPPDATA%\\SensorDash</strong></div><p class="note">To start when you sign in, run Install-Startup.ps1 from the extracted release. It creates a shortcut only for your Windows user account. Remove-Startup.ps1 undoes it.</p><p class="note">If a sensor is unavailable, try starting the application as Administrator for comparison. Don’t grant elevated access to an exposed network server; this app binds to localhost.</p>')}
 </div><div class="badge-line"><p class="note">Changes apply to the live collector without restarting.</p><button class="btn" id="saveSettings">Save settings</button></div>`;
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
 const content=({overview,cooling,nvme,explorer,events,settings:settingsView})[S.view]();
 const scroll=S.view==='explorer'?byId('sensorList')?.scrollTop:null;
 byId('content').innerHTML=error+content;
 if(scroll!=null&&byId('sensorList'))byId('sensorList').scrollTop=scroll;
 ensureHistory();
}
function updateLiveValues(){
 document.querySelectorAll('[data-live-reading]').forEach(el=>{
    const s=sensor(el.dataset.liveReading);
    if(el.dataset.liveFormat==='value')el.textContent=fmt(s?.value,s?.type==='Fan'?0:1);
    else el.textContent=fmt(s?.value,s?.type==='Fan'?0:1)+' '+(el.dataset.liveUnit||typeUnit(s?.type));
 });
 const h=document.querySelector('[data-live-calc="nvmeHeadroom"]');
 if(h){const t=getDefaults().nvme?.value;h.textContent=fmt(t==null?null:60-t);}
}
async function loadDiagnostics(shouldRender=true){
 try {
  const previous=JSON.stringify(S.diagnostics);
  S.diagnostics=await fetchJSON('/api/diagnostics');
  if(shouldRender&&S.view==='nvme'&&previous!==JSON.stringify(S.diagnostics))render();
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
 updateLiveValues();
 drawAll();
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
   .then(data=>{if(S.historyKey!==key)return;S.history=Object.fromEntries((data.series||[]).map(s=>[s.id,s.points]));drawAll()})
   .catch(()=>drawAll()).finally(()=>{S.historyPending=false});
 drawAll();
}
function drawAll(){document.querySelectorAll('canvas[data-chart]').forEach(drawCanvas)}
function drawCanvas(canvas){
 const ids=canvas.dataset.chart.split('|').filter(Boolean), series=ids.map((id,i)=>{
   const base=S.history[id]||[], live=S.liveCache.get(id)||[], first=Date.now()-S.hours*3600000;
   const points=base.filter(p=>p.t>=first);
   live.forEach(p=>{if(p.t>=first&&(!points.length||p.t>points[points.length-1].t))points.push(p)});
   return {id,color:colors[i%colors.length],points};
 }).filter(s=>s.points.length);
 const rect=canvas.getBoundingClientRect(),w=Math.max(100,rect.width),h=Math.max(100,rect.height);
 const ratio=Math.min(window.devicePixelRatio||1,2);
 canvas.width=Math.round(w*ratio);canvas.height=Math.round(h*ratio);
 const ctx=canvas.getContext('2d');ctx.scale(ratio,ratio);ctx.clearRect(0,0,w,h);
 const p={l:49,r:14,t:17,b:31};const cw=w-p.l-p.r,ch=h-p.t-p.b;
 if(!series.length){ctx.fillStyle='#829aad';ctx.font='12px Segoe UI';ctx.textAlign='center';ctx.fillText('Collecting sensor history…',w/2,h/2);return}
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
 canvas.onmousemove=e=>{
   const x=e.clientX-canvas.getBoundingClientRect().left;
   const desired=xmin+(x-p.l)/cw*(xmax-xmin);
   drawCanvasOverlay(ctx,series,{tx,ty,p,h,w,cw,ch},desired);
 };
 canvas.onmouseleave=()=>drawCanvas(canvas);
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
 const v=e.target.closest('[data-view]');if(v){S.view=v.dataset.view;S.historyKey='';if(S.view==='events')loadEvents();if(S.view==='nvme')loadDiagnostics().catch(()=>{});render();return}
 const f=e.target.closest('[data-filter]');if(f){S.filter=f.dataset.filter;render();return}
 const pin=e.target.closest('[data-pin]');if(pin){e.stopPropagation();togglePin(pin.dataset.pin);return}
 const sel=e.target.closest('[data-select]');if(sel){S.selected=sel.dataset.select;S.historyKey='';render();return}
 if(e.target.closest('[data-save-alias]')){saveAlias(false);return}
 if(e.target.closest('[data-clear-alias]')){saveAlias(true);return}
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
window.addEventListener('resize',()=>drawAll());
async function bootstrap(){
 try{
   const [cfg,frame]=await Promise.all([fetchJSON('/api/settings'),fetchJSON('/api/live')]);
   S.settings=cfg;updateFrame(frame);render();loadEvents();loadDiagnostics(false);
   const stream=new EventSource('/api/stream');
   stream.onmessage=e=>{try{updateFrame(JSON.parse(e.data))}catch(err){console.warn('Sensor frame error',err)}};
   stream.onerror=()=>{byId('liveStatus').textContent='Reconnecting…'};
   setInterval(loadEvents,30000);
   setInterval(()=>loadDiagnostics(false),30000);
   setInterval(()=>{if(Date.now()-Date.parse(S.frame?.timestamp||0)>12000)fetchJSON('/api/live').then(updateFrame).catch(()=>{})},5000);
 }catch(err){
   byId('content').innerHTML='<div class="help">Could not connect to the local API: '+esc(err.message)+'. Restart SensorDash.exe and refresh this page.</div>';
   document.body.classList.add('offline');
 }
}
bootstrap();
