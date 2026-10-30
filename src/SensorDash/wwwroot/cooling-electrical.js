'use strict';
// Electrical inventory and *bounded* estimates. This module never issues fan
// commands, never treats nameplate watts as measured draw, and does not create
// derived live sensors that could inadvertently enter a fan-control curve.
const CoolingElectrical=(()=>{
 const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
 const number=v=>v===null||v===''||v===undefined?null:Number.isFinite(Number(v))?Number(v):null;
 const fixed=(n,d=1)=>Number.isFinite(n)?Number(n).toLocaleString(undefined,{maximumFractionDigits:d,minimumFractionDigits:d}):'—';
 const presets={
  'p12-pro-pst-ln':{model:'ARCTIC P12 Pro PST LN',quantity:19,volts:12,ampsPerUnit:0.11,
    powerSource:'Powered PWM hub',
    powerCurveSource:'ARCTIC P12 Pro LN · 12 V · 2025-09-29',
    // Representative 12 V measurements; source and sampling policy in docs/power-curves.md.
    powerCurve:[[0,0,0],[5,null,0],[6,483,.108],[10,495,.096],[20,660,.204],
      [30,834,.264],[40,999,.300],[50,1167,.384],[60,1335,.468],
      [70,1503,.552],[80,1668,.672],[90,1836,.81532],[100,1998,.97119]]
      .map(([dutyPercent,rpm,watts])=>({dutyPercent,rpm,watts}))},
  'mcp355':{model:'Swiftech MCP355 / Laing DDC 3.2',quantity:1,volts:12,
    wattsPerUnit:18,powerSource:'Molex (PSU)'}
 };
 const fresh=(S,id,type)=>{
  const item=id&&S.byId?.get(id);
  if(!item || item.type!==type || !Number.isFinite(item.value))return null;
  const observed=item.observedAt?Date.parse(item.observedAt):NaN;
  const time=Number.isFinite(observed)?observed:Date.parse(S.frame?.timestamp||'');
  if(!Number.isFinite(time)||Math.abs(Date.now()-time)>30000)return null;
  return item;
 };
 const spec=c=>c?.electrical||null;
 const rate=e=>{
  if(!e)return null;
  const qty=number(e.quantity),v=number(e.volts),amps=number(e.ampsPerUnit),
        watt=number(e.wattsPerUnit);
  if(qty===null||qty<1||qty>100||!Number.isInteger(qty))return null;
  if(watt!==null&&watt>=0)return qty*watt;
  if(amps!==null&&amps>=0&&v!==null&&v>0)return qty*amps*v;
  return null;
 };
 const ratedAmps=e=>{
  const qty=number(e?.quantity),amps=number(e?.ampsPerUnit),
        v=number(e?.volts),w=number(e?.wattsPerUnit);
  if(qty===null||qty<1)return null;
  if(amps!==null)return qty*amps;
  if(v!==null&&v>0&&w!==null)return qty*w/v;
  return null;
 };
 const duty=(S,c)=>{
  const e=spec(c),direct=fresh(S,e?.controlSensorId,'Control');
  return direct&&direct.value>=0&&direct.value<=100?direct.value:null;
 };
 const curve=(e,percent,rpm)=>{
  if(!Number.isInteger(e?.quantity)||e.quantity<1||e.quantity>100)return null;
  const at=(value,key,max)=>{
   if(!Number.isFinite(value)||value<0||value>max)return null;
   const points=(e.powerCurve||[]).filter(p=>p&&Number.isFinite(p[key])&&p[key]>=0&&p[key]<=max&&
    Number.isFinite(p.watts)&&p.watts>=0&&p.watts<=1000).slice().sort((a,b)=>a[key]-b[key]);
   if(points.length<2||new Set(points.map(p=>p[key])).size!==points.length)return null;
   if(value<=points[0][key])return points[0].watts;
   for(let i=1;i<points.length;i++)if(value<=points[i][key]){
    const a=points[i-1],b=points[i];return a.watts+(b.watts-a.watts)*(value-a[key])/(b[key]-a[key]);
   }
   return points.at(-1).watts;
  };
  const watts=at(rpm,'rpm',100000)??at(percent,'dutyPercent',100);
  return watts===null?null:watts*e.quantity;
 };
 const curveRunning=(S,c)=>{
  const e=spec(c),rpm=fresh(S,e?.rpmSensorId,'Fan')||
   (!e?.rpmSensorId?(c.sensorIds||[]).map(id=>fresh(S,id,'Fan')).find(Boolean):null);
  const watts=curve(e,duty(S,c),rpm?.value);
  return watts===null?null:{watts,source:'curve',detail:'Per-unit power curve × quantity; uses RPM, or PWM when RPM is unavailable.'};
 };
 const running=(S,c)=>{
  const e=spec(c);
  if(!e)return null;
  const measured=fresh(S,e.powerSensorId,'Power');
  if(measured&&measured.value>=0)return {watts:measured.value,source:'measured',detail:S.settings?.aliases?.[measured.id]||measured.name};
  const low=number(e.measuredLowWatts),high=number(e.measuredHighWatts),
        lower=number(e.lowDutyPercent),upper=number(e.highDutyPercent),percent=duty(S,c);
  if(low===null||high===null||lower===null||upper===null||percent===null||
     low<0||high<0||high<low||lower<0||upper>100||upper<=lower)return curveRunning(S,c);
  // Endpoint measurements describe the ENTIRE fan group, so we don't
  // multiply the calibrated group wattage by fan count again.
  const ratio=Math.max(0,Math.min(1,(percent-lower)/(upper-lower)));
  return {watts:low+(high-low)*ratio,source:'calibrated',detail:
   'Linear interpolation between user-provided group power measurements'};
 };
 const summarize=(S,sys)=>{
  const entries=(sys.components||[]).filter(c=>['fan-group','pump'].includes(c.kind))
   .map(c=>{
    const e=spec(c),rated=rate(e),actual=running(S,c),amps=ratedAmps(e),
      limit=number(e?.sourceMaxAmps),
      overLimit=limit!==null&&amps!==null&&amps>limit+1e-7;
    const rpmCandidates=(c.sensorIds||[]).map(id=>S.byId?.get(id))
      .filter(s=>s?.type==='Fan');
    const selectedRpm=fresh(S,e?.rpmSensorId,'Fan')||
      rpmCandidates.map(s=>fresh(S,s.id,'Fan')).find(Boolean)||null;
    return {c,e,rated,actual,amps,limit,overLimit,rpm:selectedRpm,duty:duty(S,c)};
   });
  const rated=entries.reduce((sum,e)=>sum+(e.rated??0),0);
  const known=entries.filter(e=>e.rated!==null);
  const runningEntries=entries.filter(e=>e.actual!==null);
  const inferred=runningEntries.reduce((sum,e)=>sum+e.actual.watts,0);
  const allRated=entries.length>0&&known.length===entries.length;
  const allRunning=entries.length>0&&runningEntries.length===entries.length;
  return {entries,rated,allRated,known:known.length,runningWatts:inferred,
    runningKnown:runningEntries.length,allRunning};
 };
 const summaryCard=(S,sys)=>{
  const x=summarize(S,sys);
  if(!x.entries.length)return '';
  const rows=x.entries.map(({c,e,rated,actual,amps,limit,overLimit,rpm,duty})=>
   '<div class="electrical-row"><div><strong>'+esc(c.name)+'</strong>'+
   '<small>'+esc(e?.model||'Model not specified')+' · '+esc(e?.quantity||1)+' unit(s)'+
   (rpm?' · '+fixed(rpm.value,0)+' RPM':'')+
   (duty!==null?' · '+fixed(duty,0)+'% reported PWM':'')+'</small></div>'+
   '<div><strong>'+(rated!==null?fixed(rated,2)+' W rated':'No rating')+'</strong>'+
   '<small>'+(actual?fixed(actual.watts,2)+' W '+(actual.source==='measured'?'measured':actual.source==='curve'?'power curve estimate':'calibrated estimate'):
    'Running draw unknown')+'</small></div></div>'+
   (overLimit?'<p class="electrical-warning">Configured current '+fixed(amps,2)+
    ' A exceeds entered source limit '+fixed(limit,2)+' A. Check wiring and power source.</p>':'')
  ).join('');
  return '<section class="electrical-budget" data-electrical-budget="'+esc(sys.id)+'" aria-label="Cooling electrical budget">'+
    '<div class="cooling-insights-head"><div><h3>Cooling electrical budget</h3>'+
    '<p>Rated and measured power</p></div></div>'+
    '<div class="electrical-totals"><div><small>Rated power</small><strong>'+
    (x.known?fixed(x.rated,2)+' W':'—')+'</strong><span>'+x.known+'/'+x.entries.length+
    ' components rated</span></div><div><small>Running power</small><strong>'+
    (x.runningKnown?fixed(x.runningWatts,2)+' W':'—')+'</strong><span>'+
    (x.runningKnown?x.runningKnown+'/'+x.entries.length+' components (partial unless all present)':
       'Add a power sensor or calibration')+'</span></div></div>'+
    '<div class="electrical-rows">'+rows+'</div>'+
    '<p class="note">Rated power is not actual consumption.</p>'+
    '</section>';
 };
 const overview=(S)=>(S.settings?.coolingSystems||[]).map(sys=>
   '<div class="electrical-system-overview"><h3>'+esc(sys.name)+'</h3>'+summaryCard(S,sys)+'</div>')
   .filter(html=>html.includes('electrical-budget')).join('')||
   '<p class="note">Configure a fan group or pump in Cooling Systems to track its electrical rating.</p>';
 const textInput=(scope,key,value,placeholder,extra='')=>
  '<label>'+esc(placeholder)+'<input data-electrical-field="'+esc(key)+'" data-electrical-scope="'+esc(scope)+
  '" '+extra+' value="'+esc(value??'')+'"></label>';
 const numeric=(scope,key,value,title,step='any')=>
  textInput(scope,key,value,title,'type="number" min="0" step="'+step+'"'+(key==='wattsPerUnit'?' title="Used instead of volts × amps when provided"':''));
 const sensorSelect=(S,scope,field,type,chosen)=>{
  const options=(S.sensors||[]).filter(s=>s.type===type);
  return '<label>'+esc(field==='powerSensorId'?'Measured power sensor':
   field==='controlSensorId'?'PWM duty sensor (optional)':'Tachometer sensor')+
   '<select data-electrical-field="'+field+'" data-electrical-scope="'+esc(scope)+'">'+
   '<option value="">No sensor</option>'+options.map(s=>
    '<option value="'+esc(s.id)+'" '+(s.id===chosen?'selected':'')+'>'+esc(S.settings?.aliases?.[s.id]||(s.device+' / '+s.name))+'</option>')
    .join('')+'</select></label>';
 };
 const editor=(S,c,scope,editing)=>{
  if(!['fan-group','pump'].includes(c.kind))return '';
  const e=spec(c)||{};
  if(!editing)return '<p class="note">Electrical specs: '+esc(e.model||'Not configured')+
   (rate(e)!==null?' · '+fixed(rate(e),2)+' W nameplate':'')+'</p>';
  return '<details class="electrical-editor" data-electrical-editor="'+esc(scope)+'">'+
   '<summary>Electrical specifications · '+esc(e.model||'Optional')+'</summary>'+

   '<div class="w-actions">'+
   (c.kind==='fan-group'?'<button type="button" class="btn secondary" data-work="electrical-preset" data-electrical-preset="p12-pro-pst-ln" data-electrical-scope="'+esc(scope)+'">Use ARCTIC P12 Pro PST LN ×19</button>':
   '<button type="button" class="btn secondary" data-work="electrical-preset" data-electrical-preset="mcp355" data-electrical-scope="'+esc(scope)+'">Use MCP355 / DDC 3.2 (18 W)</button>')+
   '</div><div class="electrical-editor-grid">'+
   textInput(scope,'model',e.model,'Model / part number')+
   numeric(scope,'quantity',e.quantity??1,c.kind==='pump'?'Pump count':'Fan count','1')+
   numeric(scope,'volts',e.volts,'Rated voltage (V)')+
   numeric(scope,'ampsPerUnit',e.ampsPerUnit,'Rated amps per unit (A)')+
   numeric(scope,'wattsPerUnit',e.wattsPerUnit,'Rated watts per unit (W)')+
   textInput(scope,'powerSource',e.powerSource,'Power connection / source')+
   numeric(scope,'sourceMaxAmps',e.sourceMaxAmps,'Source current limit (A), optional')+
   sensorSelect(S,scope,'rpmSensorId','Fan',e.rpmSensorId)+
   sensorSelect(S,scope,'controlSensorId','Control',e.controlSensorId)+
   sensorSelect(S,scope,'powerSensorId','Power',e.powerSensorId)+
   '</div>'+
   curveEditor(e,scope)+
   '<details class="electrical-calibration"><summary>Power calibration (optional)</summary>'+
   '<p class="note">Enter measured power for the whole group at each speed.</p>'+
   '<div class="electrical-editor-grid">'+
   numeric(scope,'lowDutyPercent',e.lowDutyPercent,'Low PWM measurement (%)')+
   numeric(scope,'measuredLowWatts',e.measuredLowWatts,'Whole-group watts at low PWM')+
   numeric(scope,'highDutyPercent',e.highDutyPercent,'High PWM measurement (%)')+
   numeric(scope,'measuredHighWatts',e.measuredHighWatts,'Whole-group watts at high PWM')+
   '</div></details></details>';
 };
 const curveEditor=(e,scope)=>{
  const rows=e.powerCurve||[];
  const input=(p,i,key,label,max)=>'<input type="number" min="0" max="'+max+'" step="any" aria-label="'+label+' point '+(i+1)+'" title="0–'+max+'" data-electrical-field="powerCurve" data-electrical-scope="'+esc(scope)+'" data-curve-index="'+i+'" data-curve-key="'+key+'" value="'+esc(p[key]??'')+'">';
  return '<details class="electrical-calibration"><summary>Power curve · '+rows.length+' points</summary>'+
   '<p class="note">Watts per fan or pump <span tabindex="0" role="img" aria-label="Power curve information" title="Estimates use RPM, or PWM if RPM is unavailable, multiplied by quantity. Measurements and your group calibration take priority. Hub losses are excluded.">ⓘ</span></p>'+
   textInput(scope,'powerCurveSource',e.powerCurveSource,'Curve source (optional)')+
   '<div class="power-curve-table"><div class="power-curve-row"><span>PWM %</span><span>RPM</span><span>Watts / unit</span><span></span></div>'+
   rows.map((p,i)=>'<div class="power-curve-row">'+input(p,i,'dutyPercent','PWM percent',100)+input(p,i,'rpm','RPM',100000)+input(p,i,'watts','Watts per unit',1000)+
    '<button type="button" class="btn secondary" data-work="remove-power-point" data-electrical-scope="'+esc(scope)+'" data-point-index="'+i+'" aria-label="Delete power point '+(i+1)+'" title="Delete point">'+
    '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7"/></svg></button></div>').join('')+'</div>'+
   '<button type="button" class="btn secondary" data-work="add-power-point" data-electrical-scope="'+esc(scope)+'" '+(rows.length>=128?'disabled':'')+'>+ Point</button></details>';
 };
 let lastPaint=0;
 const paint=S=>{
  if(Date.now()-lastPaint<8000 || !['overview','cooling'].includes(S.view))return;
  lastPaint=Date.now();
  for(const node of document.querySelectorAll('[data-electrical-budget]')){
   const sys=S.settings?.coolingSystems?.find(x=>x.id===node.dataset.electricalBudget);
   if(!sys)continue;
   const next=summaryCard(S,sys);
   if(next && node.outerHTML!==next)node.outerHTML=next;
  }
 };
 const applyPreset=(S,scope,preset)=>{
  const [,sysId,componentId]=scope.split(':');
  const c=S.settings?.coolingSystems?.find(s=>s.id===sysId)?.components?.find(c=>c.id===componentId);
  if(!c||c.kind!==(preset==='mcp355'?'pump':'fan-group')||!presets[preset])return false;
  // Keep existing power and feedback mappings and user calibration.
  c.electrical={...(c.electrical||{}),...JSON.parse(JSON.stringify(presets[preset]))};
  return true;
 };
 return {presets,curve,rate,ratedAmps,running,duty,summarize,summaryCard,overview,editor,applyPreset,paint};
})();
