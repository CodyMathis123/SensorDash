'use strict';
// Config-driven SensorDash workspace. No named CPU/GPU/motherboard sensors are required.
// All persisted objects refer to actual discovered sensor IDs.
const SDWorkspace = (() => {
 const uid=()=>crypto.randomUUID();
 const types=['Temperature','Fan','Control','Power','Load','Voltage','Clock','Flow','Throughput'];
 const kinds=['heat-source','radiator','pump','fan','sensor','other'];
 const icon={Temperature:'°',Fan:'↻',Power:'⚡',Load:'%',Flow:'↝'};
 const safe=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const attr=safe;
 const date=()=>new Date().toLocaleTimeString();
 const value=(s)=>s?.value==null?'—':Number(s.value).toLocaleString(undefined,{maximumFractionDigits:s.type==='Fan'?0:1});
 const unit=t=>({Temperature:'°C',Fan:'RPM',Control:'%',Load:'%',Power:'W',Voltage:'V',Flow:'L/h',Clock:'MHz',Throughput:'B/s',Data:'GiB'}[t]||'');
 const defaultSensors=(S,group,n=4)=>S.sensors.filter(s=>
     s.value!=null &&(group==='all'||s.type===group)
     &&s.deviceType!=='Virtual').slice(0,n);
 const select=(S,selected='',predicate=()=>true,empty='Select sensor',id='')=>
  '<select '+id+'><option value="">'+safe(empty)+'</option>'+
  S.sensors.filter(predicate).sort((a,b)=>a.device.localeCompare(b.device)||a.name.localeCompare(b.name))
    .map(s=>'<option value="'+attr(s.id)+'" '+(selected===s.id?'selected':'')+'>'+
      safe(s.device+' / '+(S.settings?.aliases?.[s.id]||s.name)+' ('+s.type+')')+'</option>').join('')+'</select>';
 const button=(action,text,data='',secondary=true)=>
  '<button type="button" class="btn '+(secondary?'secondary':'')+'" data-work="'+action+'" '+data+'>'+safe(text)+'</button>';
 const card=(title,description,body)=>
  '<section class="panel full"><div class="panel-head"><div><h2>'+safe(title)+'</h2><p>'+safe(description)+'</p></div></div>'+body+'</section>';
 const opts=(items,val)=>items.map(item=>'<option value="'+attr(item)+'" '+(item===val?'selected':'')+'>'+safe(item)+'</option>').join('');
 const textfield=(field,id,value,label='')=>'<label class="w-field">'+safe(label||field)+'<input data-field="'+field+'" data-object="'+attr(id)+'" value="'+attr(value)+'"></label>';
 const selectors=(S,assigned,scope)=>{
  const current=new Set(assigned);
  return '<div class="w-chips">'+assigned.map(id=>{
   const reading=S.byId.get(id);
   return '<span class="w-chip">'+safe(S.settings?.aliases?.[id]||reading?.name||id)+
    (reading?'<b data-live-reading="'+attr(id)+'" data-live-format="unit" data-live-unit="'+attr(unit(reading.type))+'">'+
      value(reading)+' '+unit(reading.type)+'</b>':'')+
    button('remove-sensor','×','data-scope="'+attr(scope)+'" data-sensor="'+attr(id)+'"')+'</span>';
  }).join('')+'</div>';
 };
 const heading=(title,desc)=>'<div class="w-intro"><p>'+safe(desc)+'</p></div>';
 const nothing=(label)=>'<div class="empty">'+safe(label)+'</div>';
 const renderOverview=(S,h)=>{
   const sensorSets=[
     ['Temperature',S.sensors.filter(s=>s.type==='Temperature'&&s.value!=null)],
     ['Fan',S.sensors.filter(s=>s.type==='Fan'&&s.value!=null)],
     ['Power',S.sensors.filter(s=>s.type==='Power'&&s.value!=null)]
   ];
   const featured=[
     ...sensorSets[0][1].filter(s=>/cpu/i.test(s.device)).slice(0,1),
     ...sensorSets[0][1].filter(s=>/gpu/i.test(s.device)).slice(0,1),
     ...sensorSets[0][1].filter(s=>/storage|nvme|ssd/i.test(s.deviceType+' '+s.device)).slice(0,1)
   ];
   for(const group of sensorSets)for(const s of group[1])if(featured.length<4&&!featured.some(f=>f.id===s.id))featured.push(s);
   const count=k=>S.sensors.filter(s=>s.type===k).length;
   const metricBody=featured.slice(0,4).map(s=>
     h.metric(S.settings?.aliases?.[s.id]||s.name,s,
       '#62d5ce',s.device,unit(s.type),icon[s.type]||'◈')).join('');
   const charts=featured.filter(s=>s.type==='Temperature');
   const trends=charts.length ? h.chart(charts,'°C') : nothing('Temperature sensors will appear when discovered.');
   return (metricBody?'<div class="metrics">'+metricBody+'</div>':'')+
      '<div class="two-col">'+h.panel('Temperature history','Detected physical temperature sensors',trends)+
      h.panel('Hardware catalog','Discovered from the elevated collector',
      ['Temperature','Fan','Control','Power','Flow','Load'].map(t=>
       '<div class="stat-row"><label>'+t+'</label><strong>'+count(t)+'</strong></div>').join(''))+
      '</div>'+
      card('Build your workspace','Use sensor IDs, not hardcoded hardware names',
      '<div class="w-grid">'+[
        ['dashboards','▦','Dashboards','Create views with metric cards, history charts and readings.'],
        ['cooling','◉','Cooling systems','Model any number of air, liquid or custom systems.'],
        ['curves','⌁','Curve sandbox','Simulate fan curves at 1 Hz; no fan outputs are written.'],
        ['tray','▣','Tray readouts','Add independent live notification-area sensor icons.']
      ].map(([v,ico,title,desc])=>'<button class="w-feature" data-view="'+v+'"><strong>'+ico+' '+title+'</strong><span>'+desc+'</span></button>').join('')+'</div>');
 };
 const renderDashboards=(S,h)=>{
   const all=S.settings?.dashboards||[];
   const chosen=all.find(d=>d.id===S.workspaceDashboardId)||all[0];
   if(chosen)S.workspaceDashboardId=chosen.id;
   const tabBar='<div class="w-tabs">'+all.map(d=>button('switch-dashboard',d.name,
      'data-id="'+attr(d.id)+'"',chosen?.id!==d.id)).join('')+
      button('add-dashboard','+ Blank dashboard')+'</div>'+
   '<div class="w-actions">'+button('add-dashboard','Thermal template','data-template="thermal"')+
   button('add-dashboard','Cooling telemetry template','data-template="cooling"')+
   button('add-dashboard','Storage template','data-template="storage"')+'</div>';
   if(!chosen)return tabBar+card('Start your first dashboard','Hardware-neutral metric/chart building blocks',nothing('Add a dashboard to begin.'));
   const widgets=chosen.widgets||[];
   const shown=widgets.map(w=>{
     const sensors=w.sensorIds.map(id=>S.byId.get(id)).filter(Boolean);
     let body='';
     if(w.kind==='chart')body=h.chart(sensors, sensors.length && sensors.every(s=>s.type===sensors[0].type)?unit(sensors[0].type):'');
     else if(w.kind==='readings')body=sensors.map(s=>'<div class="stat-row"><label>'+safe(S.settings?.aliases?.[s.id]||s.name)+'</label><strong data-live-reading="'+attr(s.id)+'" data-live-format="unit" data-live-unit="'+attr(unit(s.type))+'">'+value(s)+' '+unit(s.type)+'</strong></div>').join('');
     else body=sensors.slice(0,1).map(s=>h.metric(S.settings?.aliases?.[s.id]||s.name,s,
            '#62d5ce',s.device,unit(s.type),icon[s.type]||'◈')).join('');
     const scope='dashboard:'+chosen.id+':'+w.id;
     return '<div class="w-widget"><div class="w-widget-head"><div><strong>'+safe(w.title)+'</strong><span>'+safe(w.kind)+'</span></div>'+
       button('remove-widget','Remove','data-id="'+attr(chosen.id)+'" data-child="'+attr(w.id)+'"')+'</div>'+
       (body||nothing('Select a sensor for this widget'))+
       '<div class="w-widget-editor"><label>Title<input value="'+attr(w.title)+'" data-widget-title="'+attr(w.id)+'"></label>'+
       '<label>Kind<select data-widget-kind="'+attr(w.id)+'">'+opts(['metric','chart','readings'],w.kind)+'</select></label></div>'+
       selectors(S,w.sensorIds,scope)+
       '<div class="w-add-input">'+select(S,'',()=>true,'Choose a sensor','data-add-to="'+attr(scope)+'"')+
       button('add-sensor','+ Sensor','data-scope="'+attr(scope)+'"')+'</div></div>';
   }).join('');
   return tabBar+card(chosen.name,'Customizable widgets • saved in collector settings',
     '<div class="w-actions">'+textfield('dashboard-name',chosen.id,chosen.name,'Dashboard name')+
     button('save-editor','Save dashboard')+
     (all.length>1?button('remove-dashboard','Delete dashboard','data-id="'+attr(chosen.id)+'"'):'')+'</div>')+
     (widgets.length?'<div class="w-widget-grid">'+shown+'</div>':card('Empty dashboard','Add a widget below',nothing('No widgets have been configured yet.')))+
     '<div class="w-actions">'+button('add-widget','+ Metric','data-kind="metric" data-id="'+attr(chosen.id)+'"')+
     button('add-widget','+ Chart','data-kind="chart" data-id="'+attr(chosen.id)+'"')+
     button('add-widget','+ Readings','data-kind="readings" data-id="'+attr(chosen.id)+'"')+'</div>';
 };
 const renderCooling=(S)=>{
   const systems=S.settings?.coolingSystems||[];
   const starter='<div class="w-actions">'+button('add-system','+ Liquid system','data-kind="liquid"',false)+
      button('add-system','+ Air system','data-kind="air"')+
      button('add-system','+ Custom system','data-kind="custom"')+'</div>';
   if(!systems.length)return card('Your cooling systems','No assumptions about system topology',
       '<p class="note">Add a cooling system, then assign heat sources, pumps, fans, radiators, temperature probes, flow sensors or other components. A device may be shown in multiple systems, but this page never commands hardware.</p>'+starter);
   const html=systems.map(sys=>{
     const components=(sys.components||[]).map(c=>{
       const scope='component:'+sys.id+':'+c.id;
       return '<div class="w-component"><div class="w-actions w-compact">'+
         '<label>Component name<input data-component-name="'+attr(scope)+'" value="'+attr(c.name)+'"></label>'+
         '<label>Type<select data-component-kind="'+attr(scope)+'">'+opts(kinds,c.kind)+'</select></label>'+
         button('remove-component','Remove','data-scope="'+attr(scope)+'"')+'</div>'+
         selectors(S,c.sensorIds,scope)+
         '<div class="w-add-input">'+select(S,'',()=>true,'Associate physical or virtual sensor','data-add-to="'+attr(scope)+'"')+
           button('add-sensor','+ Sensor','data-scope="'+attr(scope)+'"')+'</div></div>';
     }).join('');
     return card(sys.name,sys.kind.toUpperCase()+' cooling system • '+(sys.components||[]).length+' components',
       '<div class="w-actions"><label>System name<input data-system-name="'+attr(sys.id)+'" value="'+attr(sys.name)+'"></label>'+
       '<label>Kind<select data-system-kind="'+attr(sys.id)+'">'+opts(['liquid','air','custom'],sys.kind)+'</select></label>'+
       button('save-editor','Save changes')+button('remove-system','Delete system','data-id="'+attr(sys.id)+'"')+'</div>'+
       (components||nothing('Add your first component.'))+
       '<div class="w-actions"><label>Component type<select data-new-kind="'+attr(sys.id)+'">'+opts(kinds,'sensor')+'</select></label>'+
       button('add-component','+ Component','data-id="'+attr(sys.id)+'"')+'</div>');
   }).join('');
   return heading('A sensor can appear in more than one visualization; control-output ownership will be enforced when hardware writes are implemented.')+
     starter+html;
 };
 const renderTray=(S)=>{
   const list=S.settings?.trayWidgets||[];
   const options=S.sensors.length?'':nothing('The collector has not reported sensors yet.');
   return card('Notification-area sensors','Choose up to six independent, live Windows tray badges',
      '<p class="note">Each badge shows a compact number. Hover in Windows for the sensor name, value and unit; double-click to open this screen. Windows decides whether icons appear on the taskbar or in the overflow menu.</p>'+
      options+list.map((w,i)=>
        '<div class="w-tray-row"><span class="w-tray-index">'+(i+1)+'</span>'+
        '<label class="w-tray-sensor">Sensor'+
        select(S,w.sensorId,()=>true,'Choose sensor','data-tray-sensor="'+attr(w.id)+'"')+'</label>'+
        '<label class="w-tray-label">Label<input data-tray-label="'+attr(w.id)+'" maxlength="40" value="'+attr(w.label)+'" placeholder="Optional label"></label>'+
        '<label class="w-tray-color">Color<input data-tray-color="'+attr(w.id)+'" type="color" value="'+attr(w.color||'#62d5ce')+'"></label>'+
        '<label class="w-tray-precision">Precision<select data-tray-decimals="'+attr(w.id)+'">'+opts(['0','1'],String(w.decimals))+'</select></label>'+
        '<div class="w-tray-remove">'+button('remove-tray','×','data-id="'+attr(w.id)+'"')+'</div></div>').join('')+
      '<div class="w-actions">'+button('save-editor','Save tray icons','',false)+
      (list.length<6?button('add-tray','+ Add sensor icon'):'<span class="note">Six-icon limit reached.</span>')+'</div>');
 };
 const curveDiagram=(points,activeTemperature,activePercent)=>{
  const sorted=[...points].filter(p=>Number.isFinite(p.temperature)&&Number.isFinite(p.percent))
    .sort((a,b)=>a.temperature-b.temperature);
  if(sorted.length<2)return '';
  const lo=Math.min(0,sorted[0].temperature-5),hi=Math.max(85,sorted[sorted.length-1].temperature+5);
  const x=t=>44+(t-lo)/(hi-lo)*392,y=p=>174-p/100*148;
  const poly=sorted.map(p=>x(p.temperature).toFixed(1)+','+y(p.percent).toFixed(1)).join(' ');
  const grid=[0,25,50,75,100].map(v=>'<line x1="44" y1="'+y(v)+'" x2="436" y2="'+y(v)+'" stroke="#293e4c"/>'+
    '<text x="36" y="'+(y(v)+4)+'" text-anchor="end" fill="#98b2c4" font-size="11">'+v+'%</text>').join('');
  const pointsMarkup=sorted.map(p=>'<circle cx="'+x(p.temperature)+'" cy="'+y(p.percent)+'" r="4" fill="#60d6cf">'+
     '<title>'+p.temperature+'°C → '+p.percent+'%</title></circle>').join('');
  const marker=Number.isFinite(activeTemperature)&&Number.isFinite(activePercent)
    ?'<circle cx="'+x(activeTemperature)+'" cy="'+y(activePercent)+'" r="6" fill="#f2bd6d" stroke="#132535" stroke-width="2"></circle>'+
     '<line x1="'+x(activeTemperature)+'" y1="20" x2="'+x(activeTemperature)+'" y2="180" stroke="#f2bd6d" stroke-dasharray="4 4"/>':'';
  return '<svg class="w-curve-plot" viewBox="0 0 460 205" role="img" aria-label="Simulated fan curve showing output percentage by input temperature">'+
    grid+'<line x1="44" y1="174" x2="436" y2="174" stroke="#7890a0"/>'+
    '<text x="44" y="194" fill="#98b2c4" font-size="11">'+lo+'°C</text>'+
    '<text x="436" y="194" text-anchor="end" fill="#98b2c4" font-size="11">'+hi+'°C</text>'+
    '<polyline points="'+poly+'" fill="none" stroke="#62d5ce" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>'+
    pointsMarkup+marker+'</svg>';
 };
 // A curve is a series of editable numeric rows, with one blank row always
 // available. Nothing is sent to the elevated collector until Save is clicked.
 const curvePointRow=(point=null)=>{
  const temp=point?.temperature==null?'':point.temperature;
  const percent=point?.percent==null?'':point.percent;
  return '<tr data-curve-point-row>'+
   '<td><input type="number" min="-30" max="150" step="1" inputmode="decimal" '+
    'data-curve-temperature value="'+attr(temp)+'" placeholder="°C" aria-label="Curve point temperature in °C"></td>'+
   '<td><input type="number" min="0" max="100" step="1" inputmode="decimal" '+
    'data-curve-percentage value="'+attr(percent)+'" placeholder="%" aria-label="Curve point fan duty percentage"></td>'+
   '<td>'+button('remove-curve-point','×','aria-label="Remove curve point"')+'</td></tr>';
 };
 const curveTable=(curve)=>
  '<div class="w-curve-table-wrap"><table class="w-curve-table" data-curve-table="'+attr(curve.id)+'">'+
  '<thead><tr><th>Temperature (°C)</th><th>Fan speed (%)</th><th class="w-curve-actions-col"></th></tr></thead>'+
  '<tbody>'+(curve.points||[]).map(point=>curvePointRow(point)).join('')+curvePointRow()+'</tbody></table></div>'+
  '<p class="note">Click the empty row to add a point; a new row appears automatically when both values are entered. Points are sorted by temperature when saved.</p>'+
  '<div class="w-curve-feedback" data-curve-feedback="'+attr(curve.id)+'" aria-live="polite"></div>';
 const readCurvePointRows=(table)=>{
  const rows=[...table.querySelectorAll('tbody tr[data-curve-point-row]')];
  const points=[];
  for(const row of rows){
   const inputTemperature=row.querySelector('[data-curve-temperature]');
   const inputPercent=row.querySelector('[data-curve-percentage]');
   const temp=inputTemperature?.value.trim()||'';
   const pct=inputPercent?.value.trim()||'';
   if(!temp&&!pct)continue;
   if(!temp||!pct)throw Error('Complete both temperature and percentage for each point.');
   const temperature=Number(temp),percent=Number(pct);
   if(!Number.isFinite(temperature)||!Number.isFinite(percent)||
      temperature < -30||temperature>150||percent<0||percent>100||
      !inputTemperature.validity.valid||!inputPercent.validity.valid)
      throw Error('Temperature must be -30 to 150°C and fan speed must be 0 to 100%, in steps of 1.');
   points.push({temperature,percent});
  }
  if(points.length<2)throw Error('Enter at least two complete curve points.');
  if(points.length>16)throw Error('A curve can contain at most 16 points.');
  points.sort((a,b)=>a.temperature-b.temperature);
  if(points.some((p,i)=>i>0&&p.temperature===points[i-1].temperature))
    throw Error('Each temperature must be unique.');
  return points;
 };
 const updateCurvePreview=(table,S)=>{
  const container=table.closest('.panel');
  const message=container?.querySelector('[data-curve-feedback]');
  const preview=container?.querySelector('[data-curve-preview]');
  if(!preview)return;
  try{
   const points=readCurvePointRows(table);
   const id=table.dataset.curveTable;
   const live=S.simulation?.results?.find(x=>x.id===id);
   preview.innerHTML=curveDiagram(points,live?.inputTemperature,null);
   if(message)message.textContent='Preview updated · save to apply this curve to simulation.';
  }catch(e){
   if(message)message.textContent=e.message;
  }
 };
 const handleCurvePointInput=(event,S)=>{
  const input=event.target.closest('[data-curve-temperature],[data-curve-percentage]');
  if(!input)return false;
  const table=input.closest('[data-curve-table]');
  if(!table)return false;
  const rows=[...table.querySelectorAll('tbody tr[data-curve-point-row]')];
  const last=rows.at(-1);
  if(last && last.querySelector('[data-curve-temperature]')?.value.trim() &&
     last.querySelector('[data-curve-percentage]')?.value.trim() &&
     rows.length<=16){
    last.insertAdjacentHTML('afterend',curvePointRow());
  }
  updateCurvePreview(table,S);
  return true;
 };
 const renderCurves=(S)=>{
   const curves=S.settings?.simulatedFanCurves||[];
   const simulation=S.simulation?.results||[];
   const cards=curves.map(curve=>{
     const sample=simulation.find(s=>s.id===curve.id);
     const source=S.byId.get(curve.sourceSensorId);
     const scope=curve.id;
     return card(curve.name,'1 Hz simulation • no motherboard, pump or controller writes',
       '<div data-curve-preview="'+attr(scope)+'">'+
       curveDiagram(curve.points||[],sample?.inputTemperature,sample?.targetPercent)+'</div>'+
       '<div class="w-sim-preview">'+
         '<div><span>Input</span><strong data-sim="'+attr(scope)+':input">'+
           (sample?.inputTemperature==null?'—':value({value:sample.inputTemperature,type:'Temperature'})+' °C')+'</strong></div>'+
         '<div><span>Calculated command</span><strong data-sim="'+attr(scope)+':output">'+
           (sample?.targetPercent==null?'—':value({value:sample.targetPercent,type:'Control'})+' %')+'</strong></div>'+
         '<div><span>State</span><strong data-sim="'+attr(scope)+':state">'+
           safe(sample?.state||'Waiting')+'</strong></div></div>'+
       '<div class="w-actions">'+
         '<label>Curve name<input data-curve-name="'+attr(scope)+'" value="'+attr(curve.name)+'"></label>'+
         '<label>Temperature input '+select(S,curve.sourceSensorId,s=>s.type==='Temperature','Select input sensor',
            'data-curve-input="'+attr(scope)+'"')+'</label></div>'+
       curveTable(curve)+
       '<div class="w-actions"><label>Read-only output reference'+
         select(S,curve.outputSensorId,s=>s.type==='Control'||s.type==='Fan','Optional RPM/control sensor',
           'data-curve-output="'+attr(scope)+'"')+'</label>'+
         '<label>Stale after (seconds)<input type="number" min="3" max="300" data-curve-age="'+attr(scope)+'" value="'+attr(curve.maxSampleAgeSeconds)+'"></label>'+
         button('save-editor','Save curve','',false)+button('remove-curve','Delete','data-id="'+attr(scope)+'"')+'</div>'+
       '<p class="note">Only interpolated percentages are produced. Unavailable/stale input results in no simulated target. Reference channels are shown for comparison and never written.</p>'
     );
   }).join('');
   return '<div class="help">Read-only mode is enforced in the elevated collector. This sandbox evaluates each saved curve every second without claiming a fan-output channel or calling hardware control APIs.</div>'+
      '<div class="w-actions">'+button('add-curve','+ New simulated curve','',false)+'</div>'+
      (cards||card('Create a fan curve','Choose a detected temperature sensor',nothing('No simulated curves yet.')));
 };
 const renderVirtual=(S)=>{
   const virtual=S.settings?.virtualSensors||[];
   return card('Virtual sensors','Combine real sensor readings into Max, Min, Average, or Delta',
     '<p class="note">Virtual sensors appear in the sensor catalog and can feed simulated curves, charts and tray icons. This first version uses physical sensor inputs only, preventing circular dependencies.</p>'+
     virtual.map(v=>{
       const scope='virtual:'+v.id;
       return '<div class="w-component"><div class="w-actions"><label>Name<input data-virtual-name="'+attr(v.id)+'" value="'+attr(v.name)+'"></label>'+
         '<label>Operator<select data-virtual-op="'+attr(v.id)+'">'+opts(['max','min','average','delta'],v.operation)+'</select></label>'+
         button('remove-virtual','Delete','data-id="'+attr(v.id)+'"')+'</div>'+
         selectors(S,v.sensorIds,scope)+
         '<div class="w-add-input">'+select(S,'',s=>s.deviceType!=='Virtual','Choose physical sensor','data-add-to="'+attr(scope)+'"')+
         button('add-sensor','+ Input','data-scope="'+attr(scope)+'"')+'</div></div>';
     }).join('')+button('add-virtual','+ Virtual sensor','',false)+button('save-editor','Save changes'));
 };
 const renderStorage=(S)=>{
   const storage=S.sensors.filter(s=>s.deviceType==='Storage');
   const diag=S.diagnostics;
   return card('Storage hardware','Storage sensors appear in the same catalog as every other device',
     '<p class="note">Storage polling can be slower than the control engine. Values carry timestamps so stale readings can be identified by simulations.</p>'+
     (storage.length?storage.map(s=>'<div class="stat-row"><label>'+
       safe((S.settings?.aliases?.[s.id]||s.name)+' · '+s.device)+'</label>'+
       '<strong data-live-reading="'+attr(s.id)+'" data-live-format="unit" data-live-unit="'+attr(unit(s.type))+'">'+value(s)+' '+unit(s.type)+'</strong></div>').join(''):nothing('No storage sensors discovered'))+
     '<div class="note">Collector access: '+(diag?.isAdministrator?'Elevated':'Unknown')+
     ' · '+(diag?.storagePollSeconds||'—')+' sec storage poll · '+(diag?.storageSensorCount??0)+' storage readings</div>');
 };
 async function persist(S,h){
   try{
     const response=await fetch('/api/settings',{method:'PUT',headers:{'Content-Type':'application/json'},
       body:JSON.stringify(S.settings)});
     if(!response.ok)throw Error(await response.text());
     S.settings=await response.json();
     h.toast('Configuration saved');
     h.render();
   }catch(e){h.toast('Could not save configuration: '+e.message);}
 }
 function syncInputs(S){
   const d=S.settings;
   for(const e of document.querySelectorAll('[data-system-name]')){
     const o=d.coolingSystems.find(c=>c.id===e.dataset.systemName);if(o)o.name=e.value;
   }
   for(const e of document.querySelectorAll('[data-system-kind]')){
     const o=d.coolingSystems.find(c=>c.id===e.dataset.systemKind);if(o)o.kind=e.value;
   }
   for(const e of document.querySelectorAll('[data-component-name],[data-component-kind]')){
     const scope=e.dataset.componentName||e.dataset.componentKind;
     const [,id,child]=scope.split(':');
     const o=d.coolingSystems.find(c=>c.id===id)?.components.find(c=>c.id===child);
     if(o)o[e.dataset.componentName?'name':'kind']=e.value;
   }
   for(const e of document.querySelectorAll('[data-field="dashboard-name"]')){
     const o=d.dashboards.find(c=>c.id===e.dataset.object);if(o)o.name=e.value;
   }
   for(const e of document.querySelectorAll('[data-widget-title],[data-widget-kind]')){
     const id=e.dataset.widgetTitle||e.dataset.widgetKind;
     const o=d.dashboards.flatMap(c=>c.widgets).find(w=>w.id===id);
     if(o)o[e.dataset.widgetTitle?'title':'kind']=e.value;
   }
   for(const e of document.querySelectorAll('[data-tray-sensor],[data-tray-label],[data-tray-color],[data-tray-decimals]')){
     const id=e.dataset.traySensor||e.dataset.trayLabel||e.dataset.trayColor||e.dataset.trayDecimals;
     const o=d.trayWidgets.find(w=>w.id===id);
     if(!o)continue;
     const field=e.dataset.traySensor?'sensorId':e.dataset.trayLabel?'label':e.dataset.trayColor?'color':'decimals';
     o[field]=field==='decimals'?Number(e.value):e.value;
   }
   for(const e of document.querySelectorAll('[data-curve-name],[data-curve-input],[data-curve-output],[data-curve-age]')){
     const id=e.dataset.curveName||e.dataset.curveInput||e.dataset.curveOutput||e.dataset.curveAge;
     const o=d.simulatedFanCurves.find(c=>c.id===id);if(!o)continue;
     if(e.dataset.curveName)o.name=e.value;
     if(e.dataset.curveInput)o.sourceSensorId=e.value;
     if(e.dataset.curveOutput)o.outputSensorId=e.value;
     if(e.dataset.curveAge)o.maxSampleAgeSeconds=Number(e.value);

   }
   for(const table of document.querySelectorAll('[data-curve-table]')){
     const curve=d.simulatedFanCurves.find(c=>c.id===table.dataset.curveTable);
     if(curve)curve.points=readCurvePointRows(table);
   }
   for(const e of document.querySelectorAll('[data-virtual-name],[data-virtual-op]')){
     const id=e.dataset.virtualName||e.dataset.virtualOp;
     const o=d.virtualSensors.find(v=>v.id===id);
     if(o)o[e.dataset.virtualName?'name':'operation']=e.value;
   }
 }
 async function handle(e,S,h){
   const el=e.target.closest('[data-work]');if(!el)return false;
   e.preventDefault();e.stopPropagation();
   const action=el.dataset.work,id=el.dataset.id,scope=el.dataset.scope;
   try{
     // Curve point row operations edit the local form. Saving is explicit,
     // so a blank/partially typed row must not be persisted or discarded.
     if(action==='remove-curve-point'){
       const row=el.closest('[data-curve-point-row]');
       const table=el.closest('[data-curve-table]');
       row?.remove();
       if(table && !table.querySelector('tbody tr:last-child [data-curve-temperature]:placeholder-shown')){
         table.querySelector('tbody')?.insertAdjacentHTML('beforeend',curvePointRow());
       }
       if(table)updateCurvePreview(table,S);
       return true;
     }
     // Preserve unsaved editor inputs when pressing Add/Remove.
     syncInputs(S);
     const cfg=S.settings;
     if(action==='switch-dashboard'){S.workspaceDashboardId=id;h.render();return true;}
     if(action==='add-dashboard'){
       const template=el.dataset.template||'blank';
       const newWidget=(title,kind,ids)=>({id:uid(),title,kind,sensorIds:ids});
       const physical=S.sensors.filter(sensor=>sensor.deviceType!=='Virtual');
       const sources=template==='thermal'?physical.filter(sensor=>sensor.type==='Temperature')
         :template==='cooling'?physical.filter(sensor=>['Fan','Control','Flow'].includes(sensor.type))
         :template==='storage'?physical.filter(sensor=>sensor.deviceType==='Storage'):[];
       const suggested=sources.slice(0,6).map(sensor=>sensor.id);
       const d={id:uid(),name:template==='thermal'?'Thermal overview':template==='cooling'?'Cooling telemetry':
         template==='storage'?'Storage telemetry':'New dashboard',
         widgets:template==='blank'?[]:[
          newWidget('Live readings','readings',suggested),
          newWidget('History','chart',suggested.filter(id=>S.byId.get(id)?.type===S.byId.get(suggested[0])?.type).slice(0,4))
         ]};
       cfg.dashboards.push(d);S.workspaceDashboardId=d.id;
     }
     if(action==='remove-dashboard'){
       if(!confirm('Delete this dashboard?'))return true;
       cfg.dashboards=cfg.dashboards.filter(d=>d.id!==id);S.workspaceDashboardId=cfg.dashboards[0]?.id;
     }
     if(action==='add-widget'){
       cfg.dashboards.find(d=>d.id===id)?.widgets.push({id:uid(),kind:el.dataset.kind||'metric',
          title:el.dataset.kind==='chart'?'Sensor history':'Sensor',sensorIds:[]});
     }
     if(action==='remove-widget'){
       const d=cfg.dashboards.find(d=>d.id===id);if(d)d.widgets=d.widgets.filter(w=>w.id!==el.dataset.child);
     }
     if(action==='add-system'){
       const kind=el.dataset.kind||'custom';
       const preset=kind==='liquid'?['heat-source','radiator','pump','fan','sensor']:
         kind==='air'?['heat-source','fan','sensor']:[];
       cfg.coolingSystems.push({id:uid(),name:kind==='liquid'?'Liquid cooling system':kind==='air'?'Air cooling system':'Custom cooling system',
         kind,components:preset.map(type=>({id:uid(),name:type.replace('-',' '),kind:type,sensorIds:[]}))});
     }
     if(action==='remove-system'){
       if(!confirm('Delete this cooling system?'))return true;
       cfg.coolingSystems=cfg.coolingSystems.filter(c=>c.id!==id);
     }
     if(action==='add-component'){
       const parent=cfg.coolingSystems.find(c=>c.id===id);
       const kind=document.querySelector('[data-new-kind="'+CSS.escape(id)+'"]')?.value||'sensor';
       parent?.components.push({id:uid(),name:kind.replace('-',' '),kind,sensorIds:[]});
     }
     if(action==='remove-component'){
       const [,sys,child]=scope.split(':');const parent=cfg.coolingSystems.find(s=>s.id===sys);
       if(parent)parent.components=parent.components.filter(c=>c.id!==child);
     }
     if(action==='add-tray'){
       const candidate=S.sensors.find(s=>s.value!=null&&!cfg.trayWidgets.some(w=>w.sensorId===s.id));
       cfg.trayWidgets.push({id:uid(),sensorId:candidate?.id||S.sensors[0]?.id||'',label:'',color:'#62d5ce',decimals:0});
     }
     if(action==='remove-tray')cfg.trayWidgets=cfg.trayWidgets.filter(w=>w.id!==id);
     if(action==='add-curve'){
       cfg.simulatedFanCurves.push({id:uid(),name:'New simulated curve',sourceSensorId:S.sensors.find(s=>s.type==='Temperature')?.id||'',
         outputSensorId:'',maxSampleAgeSeconds:30,
         points:[{temperature:30,percent:25},{temperature:50,percent:55},{temperature:75,percent:100}]});
     }
     if(action==='remove-curve')cfg.simulatedFanCurves=cfg.simulatedFanCurves.filter(c=>c.id!==id);
     if(action==='add-virtual'){
       cfg.virtualSensors.push({id:'virtual:'+uid(),name:'Maximum temperature',operation:'max',sensorIds:[]});
     }
     if(action==='remove-virtual')cfg.virtualSensors=cfg.virtualSensors.filter(v=>v.id!==id);
     if(action==='add-sensor'||action==='remove-sensor'){
       const [entity,root,child]=scope.split(':');
       const obj=entity==='dashboard'?cfg.dashboards.find(d=>d.id===root)?.widgets.find(w=>w.id===child)
         :entity==='component'?cfg.coolingSystems.find(c=>c.id===root)?.components.find(c=>c.id===child)
         :cfg.virtualSensors.find(v=>v.id===scope.slice('virtual:'.length));
       if(obj){
         if(action==='remove-sensor')obj.sensorIds=obj.sensorIds.filter(x=>x!==el.dataset.sensor);
         else {
           const input=document.querySelector('[data-add-to="'+CSS.escape(scope)+'"]');
           if(input?.value&&!obj.sensorIds.includes(input.value))obj.sensorIds.push(input.value);
         }
       }
     }
     await persist(S,h);
   }catch(e){h.toast(e.message);}
   return true;
 }
 const render=(S,view,h)=>{
   if(view==='overview')return renderOverview(S,h);
   if(view==='dashboards')return renderDashboards(S,h);
   if(view==='cooling')return renderCooling(S);
   if(view==='tray')return renderTray(S);
   if(view==='curves')return renderCurves(S);
   if(view==='virtual')return renderVirtual(S);
   if(view==='storage')return renderStorage(S);
   return nothing('View unavailable');
 };
 const paintSimulation=(S)=>{
   const readings=S.simulation?.results||[];
   document.querySelectorAll('[data-sim]').forEach(el=>{
     const [id,key]=el.dataset.sim.split(':');const row=readings.find(r=>r.id===id);
     const out=key==='input'?(row?.inputTemperature==null?'—':row.inputTemperature.toFixed(1)+' °C'):
       key==='output'?(row?.targetPercent==null?'—':row.targetPercent.toFixed(1)+' %'):row?.state||'waiting';
     if(el.textContent!==out)el.textContent=out;
   });
 };
 return {render,handle,paintSimulation,handleCurvePointInput};
})();
