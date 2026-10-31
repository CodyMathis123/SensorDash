'use strict';
const titles={

 cooling:['COOLING','Cooling systems','Model any number of air, liquid or custom cooling systems'],
 curves:['FAN CONTROL','Fan control','Control fans with temperature curves'],

 tray:['NOTIFICATION AREA','Tray readouts','Keep sensor readings in your Windows tray'],

 explorer:['SENSOR LIBRARY','Sensor Explorer','Discover, rename, search and pin sensor data'],
 events:['HISTORY','Event log','Alerts and recoveries'],
 settings:['CONFIGURATION','Settings','Collection intervals, aliases, monitoring thresholds and diagnostics']
};
const S={view:'cooling',frame:null,sensors:[],byId:new Map(),settings:null,events:[],filter:'Key',search:'',selected:'',deviceFilter:'',explorerGrouping:'hardware',showHidden:false,
 status:null,connected:false,diagnostics:null,telemetryConfig:null,simulation:null,fanControl:null,fanOutputSignature:'',collectionGroups:[],schemaKey:'',livePaintQueued:false};
const byId=id=>document.getElementById(id);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=(v,dec=1)=>v==null||!Number.isFinite(v)?'—':Number(v).toLocaleString(undefined,{maximumFractionDigits:dec,minimumFractionDigits:dec});
const when=t=>new Date(t).toLocaleString(undefined,{month:'short',day:'numeric',hour:'numeric',minute:'2-digit',second:'2-digit'});
const formatMb=bytes=>Number.isFinite(bytes)?(bytes/1048576).toFixed(1)+' MB':'—';
const typeUnit=t=>({Temperature:'°C',Fan:'RPM',Control:'%',Load:'%',Power:'W',Clock:'MHz',Voltage:'V',Throughput:'B/s',Data:'GB',Level:'%'}[t]||'');
const label=s=>{
 const alias=S.settings?.aliases?.[s?.id];
 if(alias)return alias;
 const name=s?.name||'Unavailable';
 return /^(Temperature|Fan|Control|Load|Power)\s*#\d+$/i.test(name)&&s?.device
   ? s.device+' · '+name:name;
};
const sensor=id=>S.byId.get(id);
const val=id=>sensor(id)?.value??null;
const metric=(name,s,accent,detail,unit,icon)=>`<div class="metric" style="--accent:${accent}"><div class="metric-top"><span>${esc(name)}</span><span class="metric-icon">${icon}</span></div><div class="metric-num"><b ${s?.id?`data-live-reading="${esc(s.id)}" data-live-format="value"`:''}>${fmt(s?.value,s?.type==='Fan'?0:1)}</b><span>${unit||typeUnit(s?.type)}</span></div><div class="metric-detail">${esc(detail)}</div></div>`;
const panel=(title,subtitle,content,tag='')=>`<section class="panel"><div class="panel-head"><div><h2>${esc(title)}</h2>${subtitle?' <p>'+esc(subtitle)+'</p>':''}</div>${tag}</div>${content}</section>`;
const stat=(name,s,unit)=>`<div class="stat-row"><label>${esc(name)}</label><strong ${s?.id?`data-live-reading="${esc(s.id)}" data-live-format="unit" data-live-unit="${esc(unit||typeUnit(s?.type))}"`:''}>${fmt(s?.value,s?.type==='Fan'?0:1)} ${unit||typeUnit(s?.type)}</strong></div>`;
const idMatch=(...ids)=>ids.map(id=>sensor(id)).filter(Boolean);
function latest(s) {return s?.value==null?'No reading':fmt(s.value,s.type==='Fan'?0:1)+' '+typeUnit(s.type);}
function explorer(){
 const usage=SDWorkspace.usageIndex(S);
 const hidden=x=>SDWorkspace.isHidden(S,x);
 const display=x=>SDWorkspace.displayName(S,x);
 const key=x=>x.deviceId||x.deviceType+'|'+x.device;
 const devices=[...new Map(S.sensors.map(x=>[key(x),x])).entries()].sort((a,b)=>a[1].device.localeCompare(b[1].device));
 const filterChoices=['Key','In use','Temperature','Fan','Control','Power','Load','Pinned','Hidden','All'];
 const candidates=S.sensors.filter(x=>{
  const type=S.filter==='All'||S.filter==='In use'||S.filter==='Hidden'||
   (S.filter==='Key'&&['Temperature','Fan','Control','Power','Load'].includes(x.type))||
   x.type===S.filter||(S.filter==='Pinned'&&S.settings?.pinnedSensors?.includes(x.id));
  const visible=S.filter==='Hidden'?hidden(x):S.filter==='In use'?usage.has(x.id):S.showHidden||!hidden(x);
  return type&&visible&&(!S.deviceFilter||key(x)===S.deviceFilter);
 });
 candidates.sort((a,b)=>{
  if(S.explorerGrouping==='hardware'){
   const group=(a.deviceType+' '+a.device).localeCompare(b.deviceType+' '+b.device);
   if(group)return group;
  }else if(S.explorerGrouping==='type'){
   const group=a.type.localeCompare(b.type);if(group)return group;
  }
  const pinned=Number(S.settings?.pinnedSensors?.includes(b.id))-Number(S.settings?.pinnedSensors?.includes(a.id));
  return pinned||a.type.localeCompare(b.type)||display(a).localeCompare(display(b));
 });
 const chosen=candidates.find(x=>x.id===S.selected)||candidates[0];
 if(chosen)S.selected=chosen.id;
 const indexText=x=>(display(x)+' '+x.name+' '+x.id+' '+x.device+' '+x.deviceType+' '+x.type+' '+
   (usage.get(x.id)||[]).map(u=>u.label).join(' ')).toLowerCase();
 const counts={used:[...usage.keys()].filter(id=>S.byId.has(id)).length,hidden:S.sensors.filter(hidden).length};
 const matches=candidates.filter(x=>indexText(x).includes(S.search.toLowerCase())).length;
 const headingGroup=x=>S.explorerGrouping==='hardware'?key(x):S.explorerGrouping==='type'?x.type:'';
 const groupName=x=>S.explorerGrouping==='hardware'?x.device+' · '+x.deviceType:x.type;
 let rows='',lastGroup=null;
 for(const x of candidates){
  const group=headingGroup(x),searchText=indexText(x),selected=x.id===S.selected;
  if(S.explorerGrouping!=='none'&&group!==lastGroup){
   rows+='<tr class="explorer-group" data-explorer-group="'+esc(group)+'"><td colspan="7">'+
     esc(groupName(x))+'</td></tr>';
   lastGroup=group;
  }
  const references=usage.get(x.id)||[],hide=hidden(x);
  const pinned=S.settings?.pinnedSensors?.includes(x.id);
  const directlyHidden=(S.settings?.hiddenSensorIds||[]).includes(x.id);
  rows+='<tr class="'+(selected?'chosen ':'')+(hide?'sensor-hidden':'')+'" data-select="'+esc(x.id)+
   '" data-group-key="'+esc(group)+'" data-search="'+esc(searchText)+'" '+
   (searchText.includes(S.search.toLowerCase())?'':'hidden')+'>'+
   '<td>'+esc(display(x))+(S.settings?.aliases?.[x.id]?'<small>Original: '+esc(x.name)+'</small>':'')+(hide?' <span class="tag">Hidden</span>':'')+'<small>'+esc(x.id)+'</small></td>'+
   '<td>'+esc(x.device)+'</td><td><span class="tag">'+esc(x.type)+'</span></td>'+
   '<td data-live-reading="'+esc(x.id)+'" data-live-format="unit" data-live-unit="'+esc(typeUnit(x.type))+'">'+latest(x)+'</td>'+
   '<td class="explorer-count">'+(references.length?'<b>'+references.length+'</b>':'—')+'</td>'+
   '<td><button class="pin '+(pinned?'pinned':'')+'" data-pin="'+esc(x.id)+'" title="Toggle history pin">★</button></td>'+
   '<td>'+(hide&&!directlyHidden?
       '<button class="btn tiny secondary" data-select="'+esc(x.id)+'" title="Hidden by hardware or category; select to manage">Group hidden</button>':
       '<button class="btn tiny secondary" data-hide-sensor="'+esc(x.id)+'">'+(directlyHidden?'Show':'Hide')+'</button>')+
   '</td></tr>';
 }
 const tools='<div class="toolbar explorer-toolbar">'+
  '<input id="search" placeholder="Search sensor, ID, hardware or usage…" value="'+esc(S.search)+'">'+
  filterChoices.map(v=>'<button class="filter-btn '+(S.filter===v?'selected':'')+'" data-filter="'+esc(v)+'">'+
    esc(v)+(v==='In use'?' ('+counts.used+')':v==='Hidden'?' ('+counts.hidden+')':'')+'</button>').join('')+
  '</div><div class="explorer-tools">'+
  '<label>Hardware<select id="explorerDevice"><option value="">All devices</option>'+
   devices.map(([k,x])=>'<option value="'+esc(k)+'" '+(S.deviceFilter===k?'selected':'')+'>'+
    esc(x.device+' · '+x.deviceType)+'</option>').join('')+'</select></label>'+
  '<label>Group by<select id="explorerGroup">'+
   [['hardware','Hardware'],['type','Sensor type'],['none','None']].map(([value,text])=>
    '<option value="'+value+'" '+(S.explorerGrouping===value?'selected':'')+'>'+text+'</option>').join('')+
  '</select></label>'+
  '<label class="explorer-check"><input type="checkbox" id="explorerShowHidden" '+(S.showHidden?'checked':'')+
  '> Include hidden sensors</label></div>';
 const selected=chosen?(()=>{
  const uses=usage.get(chosen.id)||[];
  const deviceId=chosen.deviceId||'';
  const direct=(S.settings?.hiddenSensorIds||[]).includes(chosen.id);
  const hideDevice=deviceId&&(S.settings?.hiddenDeviceIds||[]).includes(deviceId);
  const hideType=(S.settings?.hiddenHardwareTypes||[]).includes(chosen.deviceType);
  const inherited=(hideDevice||hideType)&&!direct;
  const referenceList=uses.length?'<div class="sensor-use-list">'+uses.map(u=>
   '<button class="sensor-use-link" data-usage-target="'+esc(u.page)+'" data-usage-dashboard="'+esc(u.dashboardId||'')+
   '" data-usage-system="'+esc(u.systemId||'')+'" data-usage-component="'+esc(u.componentId||'')+'">'+
   esc(u.label)+'</button>').join('')+'</div>':'<p class="note">Not used in a saved workspace.</p>';
  return '<div class="full">'+panel('Selected sensor · '+display(chosen),'Original: '+chosen.name+' · '+chosen.id+' · '+chosen.device,
   '<div class="alias-editor">'+
    '<div class="field"><label for="sensorAlias">Display name</label><input id="sensorAlias" maxlength="120" value="'+
      esc(S.settings?.aliases?.[chosen.id]||'')+'" placeholder="'+esc(chosen.name)+'"></div>'+
    '<button class="btn" data-save-alias>Save name</button>'+
    '<button class="btn secondary" data-clear-alias>Use original name</button></div>'+
   '<div class="explorer-visibility-actions">'+
    (inherited?'<span class="note">Hidden by a hardware/type rule. Remove that rule to show this reading.</span>':
     '<button class="btn secondary" data-hide-sensor="'+esc(chosen.id)+'">'+(direct?'Unhide sensor':'Hide sensor')+'</button>')+
    (deviceId?'<button class="btn secondary" data-hide-device="'+esc(deviceId)+'">'+(hideDevice?'Show this hardware':'Hide this hardware')+'</button>':'')+
    '<button class="btn secondary" data-hide-type="'+esc(chosen.deviceType)+'">'+(hideType?'Show all '+chosen.deviceType:'Hide all '+chosen.deviceType)+'</button></div>'+
   '<p class="note">Hidden sensors continue recording. Find them with the Hidden filter.</p>'+
   '<h3 class="sensor-used-title">Used in '+uses.length+' place'+(uses.length===1?'':'s')+'</h3>'+referenceList+
   '<div class="badge-line"><span class="note">Recorded peaks and long-term charts are in Grafana. This editor shows the current physical reading.</span></div>'+
   '<div class="badge-line"><span class="note">Pin this sensor for local recording and tray selection.</span>'+
    '<button class="btn tiny" data-pin="'+esc(chosen.id)+'">'+
     (S.settings?.pinnedSensors?.includes(chosen.id)?'Unpin sensor':'Pin sensor')+'</button></div>')+'</div>';
 })():'';
 return '<section class="panel full"><div class="panel-head"><div><h2>Sensor registry</h2>'+
  '<p>'+S.sensors.length+' discovered · '+counts.used+' in use · '+counts.hidden+
  ' hidden · <span id="matchCount">'+matches+'</span> matching</p></div>'+
  '<span class="pill mute">LIVE VALUES · STABLE ROWS</span></div>'+
  tools+
  '<div class="table-container" id="sensorList"><table><thead><tr><th>Sensor / ID</th><th>Hardware</th>'+
  '<th>Type</th><th>Live</th><th>Used</th><th>Pin</th><th>Visibility</th></tr></thead><tbody>'+
  rows+'</tbody></table>'+(candidates.length?'':'<div class="empty">No matching sensors. Try All, Hidden or reset the hardware filter.</div>')+'</div></section>'+
  selected;
}

function events(){
 return '<div class="two-col">'+
 panel('Event history','Recorded threshold crossings and recoveries',
 '<div class="event-list">'+(S.events.length?S.events.map(e=>'<div class="event '+esc(e.severity)+'"><div class="dot"></div><div><b>'+esc(e.message)+'</b><p>'+when(e.t)+' · '+esc(e.sensor)+(e.value!=null?' · '+fmt(e.value):'')+'</p></div></div>').join('')
  :'<div class="empty">No events yet. Add a monitoring threshold in Settings.</div>')+'</div>')+
 panel('Alert behavior','Configurable monitoring',
 '<p class="help">Alerts do not change fan speeds. Configure automatic cooling in Fan Control.</p>')+
 '</div>';
}
function settingsCandidates(field){
 return S.sensors.filter(s=>!Object.hasOwn(S.settings?.[field]||{},s.id)&&
  (field==='aliases'||['Temperature','Fan','Power','Load','Flow','Control'].includes(s.type)))
  .sort((a,b)=>label(a).localeCompare(label(b)));
}

function telemetryConfigPanel(){
 const cfg=S.telemetryConfig;
 if(!cfg)return panel('Pi telemetry export','Outbound HTTPS to the Pi',
  '<p class="note">Reading exporter settings from the Windows collector…</p>');
 const ready=x=>x?'Ready':'Not installed';
 return panel('Pi telemetry export','Windows controls hardware; Grafana only records and displays.',
  '<div class="form-grid">'+
  '<div class="field"><label for="telemetryEndpoint">HTTPS ingest URL</label>'+
  '<input id="telemetryEndpoint" type="url" value="'+esc(cfg.endpoint||'')+
  '" placeholder="https://192.168.1.43:8429/ingest"></div>'+
  '<div class="field"><label for="telemetryHost">Computer label</label>'+
  '<input id="telemetryHost" value="'+esc(cfg.hostLabel||'main-desktop')+'"></div></div>'+
  '<label class="collection-toggle"><input type="checkbox" id="telemetryEnabled" '+
  (cfg.enabled?'checked':'')+'>Export recorded telemetry to the Pi</label>'+
  '<div class="form-grid">'+
  '<div class="field"><label for="telemetryToken">Bearer token · optional replacement</label>'+
  '<input id="telemetryToken" type="password" autocomplete="new-password" spellcheck="false" placeholder="'+
  (cfg.tokenReady?'Installed — leave blank to keep':'Paste Pi-issued token')+'"></div>'+
  '<div class="field"><label for="telemetryCa">CA certificate PEM · optional replacement</label>'+
  '<textarea id="telemetryCa" rows="3" spellcheck="false" placeholder="'+
  (cfg.caReady?'Installed — leave blank to keep':'Paste Pi-issued public CA certificate')+'"></textarea></div></div>'+
  '<p class="note">Status: '+(cfg.enabled?'Enabled':'Disabled')+' · token '+ready(cfg.tokenReady)+
   ' · CA '+ready(cfg.caReady)+(cfg.restartRequired?' · Collector restart required':'')+'</p>'+
  '<p class="note">Secrets remain in ProgramData, are never returned to the browser and do not affect fan-control approval. '+
  'Saving these settings never restarts the Windows service.</p>'+
  '<button type="button" class="btn secondary" id="saveTelemetryConfig">Save exporter settings</button>');
}
async function loadTelemetryConfig(){
 try{
  S.telemetryConfig=await fetchJSON('/api/telemetry-config');
  if(S.view==='settings')render();
 }catch(e){console.warn('Telemetry configuration unavailable:',e.message);}
}
async function saveTelemetryConfig(){
 if(!S.telemetryConfig){toast('Collector configuration unavailable');return;}
 const payload={
  enabled:byId('telemetryEnabled').checked,
  endpoint:byId('telemetryEndpoint').value.trim(),
  hostLabel:byId('telemetryHost').value.trim(),
  token:byId('telemetryToken').value.trim()||null,
  caCertificatePem:byId('telemetryCa').value.trim()||null
 };
 try{
  const r=await fetch('/api/telemetry-config',{
   method:'PUT',headers:{'Content-Type':'application/json','X-SensorDash-Action':'telemetry-config'},
   body:JSON.stringify(payload)
  });
  if(!r.ok)throw new Error('HTTP '+r.status+'. Verify HTTPS URL and credentials.');
  S.telemetryConfig=await r.json();
  toast('Telemetry saved. Restart the collector when it is safe to apply the change.');
  render();
 }catch(e){toast('Could not save exporter: '+e.message);}
}

function settingsView(){
 const cfg=S.settings;if(!cfg)return '<div class="loading">Waiting for collector settings…</div>';
 const aliases=Object.entries(cfg.aliases||{}).sort((a,b)=>a[0].localeCompare(b[0]));
 const thresholds=Object.entries(cfg.highThresholds||{});
 const aliasChoices=settingsCandidates('aliases'),thresholdChoices=settingsCandidates('highThresholds');
 const types=[...new Set([...S.sensors.map(s=>s.deviceType).filter(Boolean),
    ...(cfg.excludedHistoryHardwareTypes||[])])].sort((a,b)=>a.localeCompare(b));
 // These switches correspond to the LHM Computer's eight hardware families.
 // They are general collection capabilities, not special handling for a
 // particular user's motherboard, GPU, SSD or network card.
 const collectionGroups=S.collectionGroups;
 const liveDevices=S.frame?.devices||[];
 const uniqueDevices=[...new Map(liveDevices.map(d=>[d.id,d])).values()]
   .sort((a,b)=>a.name.localeCompare(b.name));
 return '<div class="settings-grid">'+
 panel('Hardware driver','', '<div id="driverDetails">'+DriverStatus.details(S.frame?.pawnIo)+'</div>')+
 panel('Collection intervals','Configure hardware sampling, logging and retention',
 '<div class="form-grid">'+[['fastPollSeconds','Core sensor refresh (sec)'],['storagePollSeconds','Storage refresh (sec)'],
 ['saveEverySeconds','History save interval (sec)'],['retentionDays','History retention (days)']]
 .map(([key,title])=>'<div class="field"><label for="'+key+'">'+title+'</label><input id="'+key+'" type="number" min="'+(['storagePollSeconds','saveEverySeconds'].includes(key)?5:1)+'" max="'+(key==='retentionDays'?'1095':key==='fastPollSeconds'?'30':'120')+'" title="'+(key==='retentionDays'?'1–1,095 days. Older history is deleted.':key==='fastPollSeconds'?'1–30 seconds':'5–120 seconds')+'" value="'+cfg[key]+'"></div>').join('')+
 '</div>')+
 panel('Recorded sensors','Choose what to monitor and save',
 '<div class="w-history-controls"><label>History recording mode<select id="historyMode">'+
 [['balanced','Balanced · key temperatures, fans, power and activity'],
  ['in-use','In use · cooling, tray, curves, alerts and pins'],
  ['all','All sensors · largest history database']].map(([val,text])=>
   '<option value="'+val+'" '+(cfg.historyMode===val?'selected':'')+'>'+esc(text)+'</option>').join('')+
 '</select></label>'+
 '</div>'+
 '<h3 class="sensor-used-title">Hardware family collection</h3>'+
 '<div class="w-collection-groups">'+collectionGroups.map(({id,name,description})=>
   '<label class="w-collection-group"><input type="checkbox" data-collect-group="'+esc(id)+'" '+
      (!(cfg.disabledCollectionGroups||[]).includes(id)?'checked':'')+'>'+
   '<span><strong>'+esc(name)+'</strong><small>'+esc(description)+'</small></span></label>'
 ).join('')+'</div>'+
 '<p class="note">Disabled hardware stops reporting readings.</p>'+
 '<h3 class="sensor-used-title">Individual discovered devices</h3>'+
 '<div class="w-collection-devices">'+
   (uniqueDevices.length?uniqueDevices.map(device=>
     '<label class="w-collection-group"><input type="checkbox" data-collect-device="'+esc(device.id)+'" '+
       (!(cfg.disabledCollectionDeviceIds||[]).includes(device.id)?'checked':'')+'>'+
     '<span><strong>'+esc(device.name)+'</strong><small>'+esc(device.type)+
       ' · '+device.sensorCount+' detected sensors</small></span></label>'
   ).join(''):'<p class="note">No devices discovered. Family options remain available.</p>')+
 '</div>'+
 '<p class="note">Paused devices stop updating their readings.</p>'+
 '<h3 class="sensor-used-title">Exclude from history</h3>'+
 '<div class="w-history-types">'+types.map(t=>
  '<label class="collection-toggle"><input type="checkbox" data-history-exclude="'+esc(t)+'" '+
  ((cfg.excludedHistoryHardwareTypes||[]).includes(t)?'checked':'')+'>'+
  esc(t)+'</label>').join('')+'</div>'+
 '<p class="note">Excluded categories remain visible live but are not recorded.</p>')+
 panel('Sensor aliases','Rename discovered physical sensors',
 (aliases.length?aliases.map(([id,name])=>
 '<div class="alias-row"><label title="'+esc((sensor(id)?.name||id)+' · '+id)+'">'+esc(sensor(id)?.name||id)+'</label>'+
 '<input aria-label="Alias for '+esc(sensor(id)?.name||id)+'" data-alias="'+esc(id)+'" value="'+esc(name)+'" placeholder="Display name">'+
 '<button type="button" class="btn secondary row-delete" title="Remove alias" aria-label="Remove alias for '+esc(sensor(id)?.name||id)+'" data-remove-alias="'+esc(id)+'"><svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 10v7M14 10v7"/></svg></button></div>').join(''):
 '<p class="note">No custom aliases.</p>')+
 '<div class="w-add-input"><select id="newAliasId" aria-label="Sensor to alias" '+(!aliasChoices.length?'disabled':'')+'><option value="">'+(aliasChoices.length?'Choose sensor to rename':'All available sensors have aliases')+'</option>'+
 aliasChoices.map(s=>'<option value="'+esc(s.id)+'">'+esc([s.name,s.device].filter(Boolean).join(' · ')+' ('+s.type+')')+'</option>').join('')+
 '</select><button type="button" class="btn secondary" id="addAlias" '+(!aliasChoices.length?'disabled':'')+'>+ Alias</button></div>')+
 panel('Monitoring thresholds','Alert when a selected sensor crosses its assigned value',
 thresholds.map(([id,number])=>'<div class="alias-row"><label title="'+esc(label(sensor(id)||{id,name:id})+' · '+id)+'">'+esc(label(sensor(id)||{id,name:id}))+'</label>'+
 '<div class="threshold-value"><input aria-label="Alert threshold for '+esc(label(sensor(id)||{id,name:id}))+'" type="number" step="any" data-threshold="'+esc(id)+'" value="'+number+'"><span>'+esc(typeUnit(sensor(id)?.type))+'</span></div>'+
 '<button type="button" class="btn secondary row-delete" title="Remove threshold" aria-label="Remove threshold for '+esc(label(sensor(id)||{id,name:id}))+'" data-remove-threshold="'+esc(id)+'"><svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 10v7M14 10v7"/></svg></button></div>').join('')+
 '<div class="w-add-input"><select id="newThresholdId" aria-label="Sensor to monitor" '+(!thresholdChoices.length?'disabled':'')+'><option value="">'+(thresholdChoices.length?'Choose sensor to monitor':'All compatible sensors are monitored')+'</option>'+
 thresholdChoices.map(s=>
 '<option value="'+esc(s.id)+'">'+esc(label(s)+' ('+s.type+')')+'</option>').join('')+
 '</select><button type="button" class="btn secondary" id="addThreshold" '+(!thresholdChoices.length?'disabled':'')+'>+ Threshold</button></div>')+
 telemetryConfigPanel()+
  '<details class="panel full"><summary>Performance diagnostics</summary>'+panel('Performance diagnostics','Read-only collector and browser memory',

 [['Desktop / browser host',S.status?.process],['Elevated collector',S.diagnostics?.process]].map(([name,p])=>
  '<h3 class="sensor-used-title">'+esc(name)+'</h3>'+
  '<div class="stat-row"><label>Working set</label><strong>'+formatMb(p?.workingSetBytes)+'</strong></div>'+
  '<div class="stat-row"><label>Private memory</label><strong>'+formatMb(p?.privateBytes)+'</strong></div>'+
  '<div class="stat-row"><label>Managed heap</label><strong>'+formatMb(p?.managedHeapBytes)+'</strong></div>'+
  '<div class="stat-row"><label>Process threads</label><strong>'+(p?.threadCount??'—')+'</strong></div>'
 ).join('')+
 '<h3 class="sensor-used-title">Historical analysis</h3>'+
  '<p class="note">Grafana owns long-term charts and archive retention. Windows retains the durable export buffer.</p>'+
  '<button class="btn secondary" id="refreshDiagnostics">Refresh diagnostics</button>')+'</details>'+
 '<details class="panel full"><summary>About SensorDash</summary>'+panel('Service and storage','Local-only hardware access',
 '<div class="stat-row"><label>Web address</label><strong>127.0.0.1:8765</strong></div>'+
 '<div class="stat-row"><label>Collector</label><strong>Windows Service</strong></div>'+
 '<div class="stat-row"><label>History and settings</label><strong>%ProgramData%\\SensorDash</strong></div>'+
 '<div class="stat-row"><label>Hardware controls</label><strong>Explicit UAC approval for persistent fan profiles</strong></div>'+
 '<div class="stat-row"><label>Discovered storage devices</label><strong>'+(S.diagnostics?.storageDevices?.length??'—')+'</strong></div>'+
 '')+
 '</details></div><div class="badge-line">'+
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
 const content=['cooling','curves','tray'].includes(S.view)
  ? SDWorkspace.render(S,S.view,{metric,panel})
  : ({explorer,events,settings:settingsView})[S.view]?.() ?? '<div class="empty">Choose a workspace from the menu.</div>';
 const scroll=S.view==='explorer'?byId('sensorList')?.scrollTop:null;
 byId('content').innerHTML='<div id="driverWarning">'+DriverStatus.warning(frame?.pawnIo)+'</div>'+error+content;
 if(scroll!=null&&byId('sensorList'))byId('sensorList').scrollTop=scroll;
 
}
// Collecting data and painting the UI run independently. Incoming 2-second sensor
// frames update local readouts without any historical chart render.
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
 SDWorkspace.paintCooling(S);
 CoolingElectrical.paint(S);
 SDWorkspace.paintFanControl(S);
 SDWorkspace.paintProfiles(S);
}
async function loadStatus(shouldRender=false){
 try{
  S.status=await fetchJSON('/api/status');
  if(shouldRender&&S.view==='settings')render();
 }catch(e){console.warn('Desktop process status unavailable:',e.message)}
}
async function loadDiagnostics(shouldRender=true){
 try {
  const previous=JSON.stringify(S.diagnostics);
  S.diagnostics=await fetchJSON('/api/diagnostics');
  if(shouldRender&&S.view==='settings'&&previous!==JSON.stringify(S.diagnostics))render();
 }catch(e){console.warn('Collector diagnostics unavailable:',e.message);}
}
async function saveAlias(clear=false){
 const current=S.selected;if(!sensor(current))return;
 const cfg=structuredClone(S.settings);cfg.aliases=cfg.aliases||{};
 const name=clear?'':byId('sensorAlias')?.value.trim()||'';
 if(name)cfg.aliases[current]=name;else delete cfg.aliases[current];
 try {
   await SDWorkspace.saveSettings(S,cfg);
   toast(name?'Sensor name saved':'Original sensor name restored');render();
 }catch(e){toast('Could not save alias: '+e.message);}
}
function updateFrame(frame){
 S.frame=frame;S.sensors=frame.sensors||[];
 DriverStatus.paint(frame.pawnIo);
 S.byId=new Map(S.sensors.map(s=>[s.id,s]));
 const t=Date.parse(frame.timestamp);
 S.connected=!frame.error;
 document.body.classList.toggle('offline',!S.connected);
 byId('liveStatus').textContent=S.connected?'Live telemetry':'Sensor unavailable';
 byId('collectorStatus').textContent=S.connected?'Collector online':'Collector warning';
 byId('lastUpdated').textContent='Sample: '+when(t);
 const signature=S.sensors.map(s=>s.id).join('|');
 if(S.schemaKey!==signature){S.schemaKey=signature;render();return}
 scheduleLivePaint();
 FanFeedback.paint(S);
 }
async function loadEvents(){
 try{
  S.events=await fetchJSON('/api/events');
  byId('eventCount').textContent=S.events.filter(e=>e.severity==='warning').length||'';
  if(S.view==='events')render();
   }catch{}
}
async function fetchJSON(url,opts){
 const r=await fetch(url,opts);if(!r.ok)throw new Error(await r.text()||'Request failed');return r.json();
}
async function loadSimulation(){
 if(S.view!=='curves')return;
 try{S.simulation=await fetchJSON('/api/simulation');SDWorkspace.paintSimulation(S);}
 catch(e){console.warn('Simulation preview:',e.message);}
}
async function loadFanControl(){
 try{
  const data=await fetchJSON('/api/fan-control');
  const signature=JSON.stringify({
    outputs:(data.outputs||[]).map(o=>[o.id,o.name,o.device,o.writable]),
    enabled:(data.persistent?.profiles||[]).map(p=>[p.id,p.needsReapproval,p.reviewReasons]).sort((a,b)=>a[0].localeCompare(b[0]))
  });
  const changed=signature!==S.fanOutputSignature;
  S.fanControl=data;
  S.fanOutputSignature=signature;
  const enabled=data.persistent?.profiles?.length||0;
  const footer=byId('fanOutputStatus');
  if(footer)footer.textContent=enabled?enabled+' enabled fan profile'+(enabled===1?'':'s'):
    data.status?.state==='active'?'Manual test active':'Firmware/default';
  if(changed&&S.view==='curves')render();
  else {SDWorkspace.paintFanControl(S);SDWorkspace.paintProfiles(S);}
  FanCommissioning.paint(S);
  FanFeedback.paint(S);
 }catch(e){console.warn('Live fan channel inventory:',e.message);}
}
async function changeVisibility(field,value){
 if(!value||!S.settings)return;
 const draft=structuredClone(S.settings);
 draft[field]=draft[field]||[];
 const has=draft[field].includes(value);
 draft[field]=has?draft[field].filter(s=>s!==value):[...draft[field],value];
 try{
  await SDWorkspace.saveSettings(S,draft);
  render();toast((has?'Showing':'Hiding')+' '+value);
 }catch(e){toast('Cannot update visibility: '+e.message)}
}
async function addAlias(id){
 const reading=settingsCandidates('aliases').find(s=>s.id===id);
 if(!reading)return;
 const saved=await saveSettings(cfg=>{
  if(Object.hasOwn(cfg.aliases,id))return false;
  cfg.aliases[id]=reading.name||id;
 },'Alias added');
 if(saved&&S.view==='settings'){
  const field=[...document.querySelectorAll('[data-alias]')].find(el=>el.dataset.alias===id);
  field?.focus();field?.select();
 }
}
async function changeThreshold(id,remove=false){
 if(!id)return;
 if(!remove&&!settingsCandidates('highThresholds').some(s=>s.id===id))return;
 await saveSettings(cfg=>{
  if(remove)delete cfg.highThresholds[id];
  else {
   if(Object.hasOwn(cfg.highThresholds,id))return false;
   cfg.highThresholds[id]=Math.round((sensor(id)?.value??40)+10);
  }
 },remove?'Threshold removed':'Threshold added');
}
async function saveSettings(update=null,message='Settings saved'){
 const cfg=structuredClone(S.settings);
 cfg.aliases=cfg.aliases||{};cfg.highThresholds=cfg.highThresholds||{};
 for(const key of ['fastPollSeconds','storagePollSeconds','saveEverySeconds','retentionDays'])cfg[key]=Number(byId(key).value);
 cfg.historyMode=byId('historyMode')?.value||'balanced';
 cfg.disabledCollectionGroups=[...document.querySelectorAll('[data-collect-group]:not(:checked)')]
   .map(el=>el.dataset.collectGroup);
 const seenDevices=[...document.querySelectorAll('[data-collect-device]')];
 const seenIds=new Set(seenDevices.map(el=>el.dataset.collectDevice));
 cfg.disabledCollectionDeviceIds=[
   ...(cfg.disabledCollectionDeviceIds||[]).filter(id=>!seenIds.has(id)),
   ...seenDevices.filter(el=>!el.checked).map(el=>el.dataset.collectDevice)
 ];
 cfg.excludedHistoryHardwareTypes=[...document.querySelectorAll('[data-history-exclude]:checked')].map(el=>el.dataset.historyExclude);
 const newlyDisabledGroups=cfg.disabledCollectionGroups.filter(id=>!(S.settings.disabledCollectionGroups||[]).includes(id));
 const newlyPausedDevices=cfg.disabledCollectionDeviceIds.filter(id=>!(S.settings.disabledCollectionDeviceIds||[]).includes(id));
 if((newlyDisabledGroups.length||newlyPausedDevices.length)&&
    !confirm('Disable collection for '+(newlyDisabledGroups.length+newlyPausedDevices.length)+
       ' hardware families/devices? Dependent live dashboards, tray icons and simulated curves will lose those readings.'))
   return;

 document.querySelectorAll('[data-alias]').forEach(el=>{if(el.value.trim())cfg.aliases[el.dataset.alias]=el.value.trim();else delete cfg.aliases[el.dataset.alias]});
 document.querySelectorAll('[data-threshold]').forEach(el=>{if(el.value.trim())cfg.highThresholds[el.dataset.threshold]=Number(el.value);else delete cfg.highThresholds[el.dataset.threshold]});
 if(update&&update(cfg)===false)return false;
 try{await SDWorkspace.saveSettings(S,cfg);toast(message);render();return true}
 catch(err){toast('Could not save settings: '+err.message);return false}
}
async function togglePin(id){
 const cfg=structuredClone(S.settings);
 cfg.pinnedSensors=cfg.pinnedSensors||[];
 cfg.pinnedSensors=cfg.pinnedSensors.includes(id)?cfg.pinnedSensors.filter(x=>x!==id):[...cfg.pinnedSensors,id];
 try{await SDWorkspace.saveSettings(S,cfg);toast(cfg.pinnedSensors.includes(id)?'Sensor pinned':'Sensor unpinned');render()}
 catch(err){toast(err.message)}
}
let toastTimer;function toast(message){const el=byId('toast');el.textContent=message;el.classList.add('show');clearTimeout(toastTimer);toastTimer=setTimeout(()=>el.classList.remove('show'),3100)}
document.addEventListener('click',e=>{if(S.view==='cooling'&&e.target.closest('[data-view],[data-usage-target]'))void SDWorkspace.flushCooling(S).catch(()=>{});
 const v=e.target.closest('[data-view]');if(v){S.focusCoolingTarget=null;S.view=v.dataset.view;history.replaceState(null,'','#'+S.view);if(S.view==='events'||S.view==='cooling')loadEvents();if(S.view==='settings'){loadDiagnostics().catch(()=>{});void loadTelemetryConfig();}
 if(S.view==='settings')loadStatus(true);
 if(S.view==='curves'){loadSimulation();loadFanControl();}
 render();return}
 const coolingFold=e.target.closest('.cooling-config-fold > summary');
 if(coolingFold){
  const details=coolingFold.parentElement;
  S.coolingConfigOpen=S.coolingConfigOpen||{};
  S.coolingConfigOpen[details.dataset.coolingConfig]=!details.open;
  return;
 }
 const fold=e.target.closest('.w-fan-fold > summary');
 if(fold){
  const details=fold.parentElement;
  S.fanExpanded=S.fanExpanded||{};
  S.fanExpanded[details.dataset.fanFold]=!details.open;
  return;
 }
 if(FanControlDebug.handle(e,{toast}))return;
 if(FanCommissioning.handle(e,S,{render,toast}))return;
 if(e.target.closest('[data-work]')){SDWorkspace.handle(e,S,{render,toast,refreshFanControl:loadFanControl});return}
 const usages=e.target.closest('[data-usage-target]');if(usages){
   const target=usages.dataset.usageTarget;S.focusCoolingTarget=target==='cooling'?{
     systemId:usages.dataset.usageSystem,
     componentId:usages.dataset.usageComponent
   }:null;
   if(target==='cooling'&&usages.dataset.usageSystem){
     S.coolingConfigOpen=S.coolingConfigOpen||{};
     S.coolingConfigOpen[usages.dataset.usageSystem]=true;
   }
   S.view=target;history.replaceState(null,'','#'+target);render();
   if(target==='cooling'&&S.focusCoolingTarget){
     const focus=S.focusCoolingTarget;
     requestAnimationFrame(()=>{
       const components=[...document.querySelectorAll('[data-cooling-component-id]')];
       const component=components.find(n=>n.dataset.coolingSystemId===focus.systemId&&
         n.dataset.coolingComponentId===focus.componentId);
       const system=[...document.querySelectorAll('.w-cooling-configuration[data-cooling-system-id]')]
         .find(n=>n.dataset.coolingSystemId===focus.systemId);
       (component||system)?.scrollIntoView({block:'center',behavior:'smooth'});
     });
   }
   return;
 }
 const hSensor=e.target.closest('[data-hide-sensor]');
 if(hSensor){e.stopPropagation();changeVisibility('hiddenSensorIds',hSensor.dataset.hideSensor);return;}
 const hDevice=e.target.closest('[data-hide-device]');
 if(hDevice){e.stopPropagation();changeVisibility('hiddenDeviceIds',hDevice.dataset.hideDevice);return;}
 const hType=e.target.closest('[data-hide-type]');
 if(hType){e.stopPropagation();changeVisibility('hiddenHardwareTypes',hType.dataset.hideType);return;}
 const f=e.target.closest('[data-filter]');if(f){S.filter=f.dataset.filter;render();return}
 const pin=e.target.closest('[data-pin]');if(pin){e.stopPropagation();togglePin(pin.dataset.pin);return}
 const sel=e.target.closest('[data-select]');if(sel){S.selected=sel.dataset.select;render();return}
 if(e.target.closest('[data-save-alias]')){saveAlias(false);return}
 if(e.target.closest('[data-clear-alias]')){saveAlias(true);return}
 if(e.target.closest('#addAlias')){addAlias(byId('newAliasId')?.value);return}
 if(e.target.closest('#addThreshold')){changeThreshold(byId('newThresholdId')?.value);return}
 const removeAlias=e.target.closest('[data-remove-alias]');
 if(removeAlias){removeAlias.closest('.alias-row').querySelector('[data-alias]').value='';saveSettings();return}
 const rm=e.target.closest('[data-remove-threshold]');if(rm){changeThreshold(rm.dataset.removeThreshold,true);return}
 if(e.target.closest('#refreshDiagnostics')){
  Promise.all([loadStatus(false),loadDiagnostics(false)]).then(()=>{if(S.view==='settings')render()});
  return;
 }
 if(e.target.closest('#saveSettings'))saveSettings();
  if(e.target.closest('#saveTelemetryConfig'))void saveTelemetryConfig();
  if(e.target.closest('#refresh'))void Promise.all([loadEvents(),loadFanControl(),loadStatus(false),loadTelemetryConfig()]).then(()=>toast('Local status refreshed'));

});
document.addEventListener('input',e=>{
 if(SDWorkspace.handleCoolingInput(e,S,{render,toast}))return;
 if(S.view==='curves')FanControlUx.remindUnsaved(e.target);
 // Curve editor adds another blank key/value row once both numeric cells
 // are filled, without persisting or rebuilding the current form.
 if(SDWorkspace.handleCurvePointInput(e,S))return;
 if(e.target.id==='search'){
  S.search=e.target.value;const query=S.search.toLowerCase();let shown=0;
  document.querySelectorAll('#sensorList tr[data-select]').forEach(row=>{
   const visible=row.dataset.search.includes(query);row.hidden=!visible;if(visible)shown++;
  });
  document.querySelectorAll('#sensorList tr[data-explorer-group]').forEach(header=>{
    const key=header.dataset.explorerGroup;
    header.hidden=![...document.querySelectorAll('#sensorList tr[data-select]')].some(row=>
      !row.hidden&&row.dataset.groupKey===key);
  });
  if(byId('matchCount'))byId('matchCount').textContent=shown;
 }
});
document.addEventListener('change',e=>{
 if(SDWorkspace.handleCoolingInput(e,S,{render,toast}))return;if(S.view==='curves')FanControlUx.remindUnsaved(e.target);
 if(FanCommissioning.change(e,S,{render,toast}))return;
 if(e.target.id==='explorerDevice'){S.deviceFilter=e.target.value;render()}
 if(e.target.id==='explorerGroup'){S.explorerGrouping=e.target.value;render()}
 if(e.target.id==='explorerShowHidden'){S.showHidden=e.target.checked;render()}
});
window.addEventListener('beforeunload',e=>{
 if(SDWorkspace.hasPendingCooling(S)){e.preventDefault();e.returnValue='';}
});
document.addEventListener('visibilitychange',()=>{
 if(document.hidden)void SDWorkspace.flushCooling(S).catch(()=>{});
 if(!document.hidden) scheduleLivePaint();
});
async function bootstrap(){
 try{
   const [cfg,frame,groups,status]=await Promise.all([
     fetchJSON('/api/settings'),fetchJSON('/api/live'),
     fetchJSON('/api/collection-groups'),fetchJSON('/api/status'),CoolingElectrical.loadCatalog()
   ]);
   const version=byId('appVersion');if(version)version.textContent=status.version||'unknown';
   S.collectionGroups=groups;
   S.status=status;S.settings=cfg;const initial=location.hash.slice(1);if(titles[initial])S.view=initial;updateFrame(frame);render();loadEvents();loadDiagnostics(false);void loadTelemetryConfig();loadSimulation();loadFanControl();
   const stream=new EventSource('/api/stream');
   stream.onmessage=e=>{try{updateFrame(JSON.parse(e.data))}catch(err){console.warn('Sensor frame error',err)}};
   stream.onerror=()=>{byId('liveStatus').textContent='Reconnecting…'};
   setInterval(loadEvents,30000);
   setInterval(loadSimulation,2000);
   setInterval(loadFanControl,3000);
   setInterval(()=>loadDiagnostics(false),30000);
   setInterval(()=>{if(S.view==='settings')loadStatus(true)},30000);
   setInterval(()=>{if(Date.now()-Date.parse(S.frame?.timestamp||0)>12000)fetchJSON('/api/live').then(updateFrame).catch(()=>{})},5000);
 }catch(err){
   byId('content').innerHTML='<div class="help">Could not connect to the local API: '+esc(err.message)+'. Restart SensorDash.exe and refresh this page.</div>';
   document.body.classList.add('offline');
 }
}
bootstrap();
