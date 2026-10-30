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
    powerSource:'Powered PWM hub'},
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
 const running=(S,c)=>{
  const e=spec(c);
  if(!e)return null;
  const measured=fresh(S,e.powerSensorId,'Power');
  if(measured&&measured.value>=0)return {watts:measured.value,source:'measured',detail:measured.name};
  const low=number(e.measuredLowWatts),high=number(e.measuredHighWatts),
        lower=number(e.lowDutyPercent),upper=number(e.highDutyPercent),percent=duty(S,c);
  if(low===null||high===null||lower===null||upper===null||percent===null||
     low<0||high<0||high<low||lower<0||upper>100||upper<=lower)return null;
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
   '<small>'+(actual?fixed(actual.watts,2)+' W '+(actual.source==='measured'?'measured':'calibrated estimate'):
    'Running draw unknown')+'</small></div></div>'+
   (overLimit?'<p class="electrical-warning">Configured current '+fixed(amps,2)+
    ' A exceeds entered source limit '+fixed(limit,2)+' A. Check wiring and power source.</p>':'')
  ).join('');
  return '<section class="electrical-budget" data-electrical-budget="'+esc(sys.id)+'" aria-label="Cooling electrical budget">'+
    '<div class="cooling-insights-head"><div><h3>Cooling electrical budget</h3>'+
    '<p>Electrical component ratings and optional measurement-based estimates; no PWM control changes.</p></div></div>'+
    '<div class="electrical-totals"><div><small>Known nameplate rating</small><strong>'+
    (x.known?fixed(x.rated,2)+' W':'—')+'</strong><span>'+x.known+'/'+x.entries.length+
    ' components rated</span></div><div><small>Observed / calibrated running load</small><strong>'+
    (x.runningKnown?fixed(x.runningWatts,2)+' W':'—')+'</strong><span>'+
    (x.runningKnown?x.runningKnown+'/'+x.entries.length+' components (partial unless all present)':
       'Unavailable until power measurement or calibrated PWM endpoints')+'</span></div></div>'+
    '<div class="electrical-rows">'+rows+'</div>'+
    '<p class="note">Nameplate values are specifications, not current electricity use or an upper bound guaranteed in every transient. Running totals combine only explicitly measured or calibrated values; unknowns are not treated as zero. A single tachometer on a PWM splitter does not measure every fan. Ratings are not added to CPU/GPU package power because power domains may overlap.</p>'+
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
  textInput(scope,key,value,title,'type="number" min="0" step="'+step+'"');
 const sensorSelect=(S,scope,field,type,chosen)=>{
  const options=(S.sensors||[]).filter(s=>s.type===type);
  return '<label>'+esc(field==='powerSensorId'?'Measured power sensor':
   field==='controlSensorId'?'PWM duty sensor (optional)':'Tachometer sensor')+
   '<select data-electrical-field="'+field+'" data-electrical-scope="'+esc(scope)+'">'+
   '<option value="">No sensor</option>'+options.map(s=>
    '<option value="'+esc(s.id)+'" '+(s.id===chosen?'selected':'')+'>'+esc(s.device+' / '+s.name)+'</option>')
    .join('')+'</select></label>';
 };
 const editor=(S,c,scope,editing)=>{
  if(!['fan-group','pump'].includes(c.kind))return '';
  const e=spec(c)||{};
  if(!editing)return '<p class="note">Electrical specs: '+esc(e.model||'Not configured')+
   (rate(e)!==null?' · '+fixed(rate(e),2)+' W nameplate':'')+'</p>';
  return '<details class="electrical-editor" data-electrical-editor="'+esc(scope)+'">'+
   '<summary>Electrical specifications · '+esc(e.model||'Optional')+'</summary>'+
   '<p class="note">Optional inventory fields. Changing these does not change PWM authorization or fan operation.</p>'+
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
   '</div><p class="note">If both watts and amps are entered, watts take precedence for the power rating. Current checks use amps where supplied.</p>'+
   '<details class="electrical-calibration"><summary>Optional measured group wattage at two PWM settings</summary>'+
   '<p class="note">Enter actual measured watts for the ENTIRE fan group at each duty, then select a live Control sensor. Values between measurements use a rough linear interpolation—not a physics-based PWM power model.</p>'+
   '<div class="electrical-editor-grid">'+
   numeric(scope,'lowDutyPercent',e.lowDutyPercent,'Low PWM measurement (%)')+
   numeric(scope,'measuredLowWatts',e.measuredLowWatts,'Whole-group watts at low PWM')+
   numeric(scope,'highDutyPercent',e.highDutyPercent,'High PWM measurement (%)')+
   numeric(scope,'measuredHighWatts',e.measuredHighWatts,'Whole-group watts at high PWM')+
   '</div></details></details>';
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
  c.electrical={...(c.electrical||{}),...presets[preset]};
  return true;
 };
 return {presets,rate,ratedAmps,running,duty,summarize,summaryCard,overview,editor,applyPreset,paint};
})();
