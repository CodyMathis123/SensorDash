'use strict';
// Read-only live cooling analysis. All assignments and fan control remain owned
// by SensorDash; this view never guesses missing measurements or issues PWM.
const CoolingInsights=(()=>{
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const roles=[
  {id:'coolant-probe',name:'Coolant probe',hint:'Liquid temperature at the selected point in the loop'},
  {id:'case-air',name:'Case air probe',hint:'Air temperature inside the computer'},
  {id:'room-ambient',name:'Room ambient probe',hint:'Air temperature away from computer exhaust'}
 ];
 const units={Temperature:'°C',Power:'W',Fan:'RPM',Control:'%',Flow:'L/h',
  Conductivity:'µS/cm',Level:'%',Voltage:'V',Factor:''};
 const fmt=s=>Number.isFinite(s?.value)?Number(s.value).toLocaleString(undefined,{
  maximumFractionDigits:s.type==='Fan'?0:1})+(units[s.type]?' '+units[s.type]:''):'—';
 const assigned=(S,part,type)=>[...new Set(part.sensorIds||[])].map(id=>S.byId?.get(id))
  .filter(s=>s&&(!type||s.type===type))
  .map(s=>({...s,name:S.settings?.aliases?.[s.id]||s.name}));
 const sections=(S,sys)=>(sys.components||[]).map(part=>({
  part,temperatures:assigned(S,part,'Temperature'),powers:assigned(S,part,'Power'),
  fans:assigned(S,part,'Fan'),controls:assigned(S,part,'Control'),
  flows:assigned(S,part,'Flow')
 }));
 const unique=arr=>[...new Map(arr.filter(Boolean).map(s=>[s.id,s])).values()];
 const tile=(title,readings,empty)=>{
  const shown=unique(readings).filter(s=>Number.isFinite(s.value));
  return '<div class="cooling-insight-tile"><span>'+esc(title)+'</span>'+
   (shown.length?shown.slice(0,4).map(s=>
    '<div class="cooling-insight-reading"><small>'+esc(s.name)+'</small>'+
    '<strong data-live-reading="'+esc(s.id)+'" data-live-format="unit" data-live-unit="'+
    (units[s.type]||'')+'">'+esc(fmt(s))+'</strong></div>').join(''):
    '<strong class="cooling-insight-empty">Not measured</strong><small>'+esc(empty)+'</small>')+
   '</div>';
 };
 const overview=(S,sys)=>{
  const parts=sections(S,sys);
  const byRole=kind=>parts.filter(p=>p.part.kind===kind);
  const heats=byRole('heat-source'),fans=byRole('fan-group'),pumps=byRole('pump');
  const roleSensors=(kind,field)=>byRole(kind).flatMap(p=>p[field]);
  const assignedIds=new Set((sys.components||[]).flatMap(p=>p.sensorIds||[]));
  const liquidSystems=(S.settings?.coolingSystems||[]).filter(s=>s.kind==='liquid');
  const nextDevices=(S.sensors||[]).filter(s=>/high flow next/i.test(s.device||'')&&
   !SDWorkspace.isHidden(S,s));
  // Prefer explicit loop associations. One liquid loop may display its unbound
  // NEXT chemistry readings without requiring duplicate SensorDash components.
  const ownsNext=nextDevices.some(s=>assignedIds.has(s.id))||
   (sys.kind==='liquid'&&liquidSystems.length===1);
  const next=ownsNext?nextDevices.map(s=>({...s,rawName:s.name,name:S.settings?.aliases?.[s.id]||s.name})):[];
  const nextBy=(type,name)=>next.filter(s=>s.type===type&&(!name||s.rawName.replace(/ /g,'_')===name));
  const coolant=unique([...roleSensors('coolant-probe','temperatures'),...nextBy('Temperature','Water_Temperature')]);
  const flow=unique([...roleSensors('flow-meter','flows'),...roleSensors('pump','flows'),...nextBy('Flow','Flow')]);
  const fanRPM=roleSensors('fan-group','fans');
  const pumpRPM=roleSensors('pump','fans');
  const heatTiles=heats.map(p=>tile(p.part.name,[...p.temperatures.slice(0,2),...p.powers.slice(0,1)],'Assign CPU/GPU readings'));
  const conductivity=nextBy('Conductivity','Conductivity');
  const air=unique([...roleSensors('case-air','temperatures'),...roleSensors('room-ambient','temperatures')])
   .filter(s=>Number.isFinite(s.value));
  const alarms=next.filter(s=>s.type==='Factor'&&Number.isFinite(s.value)&&s.value!==0);
  const cards=[
   tile('Coolant temperature',coolant,'Assign a coolant sensor'),
   tile('Flow rate',flow,'Assign a flow meter'),
   tile('Radiator fans · RPM',fanRPM,'Assign radiator tachometer'),
   tile('Pump RPM',pumpRPM,'Assign pump tachometer'),
   ...heatTiles,
   ...(conductivity.length?[tile('Coolant conductivity',conductivity,'Requires NEXT data')]:[]),
   ...(air.length?[tile('Air temperatures',air,'External probe not connected')]:[]),
   ...(alarms.length?[tile('Active NEXT alarm',alarms,'')]:[])
  ];
  return '<section class="cooling-insights" aria-label="System-aware cooling overview">'+
    '<div class="cooling-insights-head"><div><h3>Liquid loop · live conditions</h3>'+
    '<p>Coolant and circulation first. Thermal loads and fan/pump feedback are separate. '+ 
    'SensorDash controls hardware; Grafana holds long-term trends.</p></div>'+
    '<span class="cooling-insights-label">Read-only</span></div>'+
    '<div class="cooling-insight-grid">'+cards.join('')+'</div>'+
    (conductivity.length?'<p class="note">Conductivity indicates changes in coolant chemistry; '+ 
      'the NEXT water-quality percentage is derived from it and is not a separate chemical test.</p>':'')+
    '</section>';
 };
 return {roles,overview};
})();
