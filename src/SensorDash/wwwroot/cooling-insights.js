'use strict';
// Read-only cooling system analysis. Data is drawn from component roles and
// saved sensor IDs; it does not alter fan outputs, collection, or fan approval.
const CoolingInsights=(()=>{
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const roles=[
  {id:'coolant-probe',name:'Coolant probe',hint:'Coolant temperature only; useful for thermal soak and radiator comparisons'},
  {id:'case-air',name:'Case air probe',hint:'Air temperature inside the PC case, not room ambient'},
  {id:'room-ambient',name:'Room ambient probe',hint:'Room air temperature, not case air or coolant'}
 ];
 const unit={Temperature:'°C',Power:'W',Fan:'RPM',Control:'%',Flow:'L/h'};
 const fmt=(s)=>Number.isFinite(s?.value)?Number(s.value).toLocaleString(undefined,{
  maximumFractionDigits:s.type==='Fan'?0:1})+' '+(unit[s.type]||''):'—';
 const sensor=(S,id)=>S.byId?.get(id);
 const assigned=(S,part,type)=>[...new Set(part.sensorIds||[])].map(id=>sensor(S,id))
  .filter(s=>s && (!type||s.type===type))
  .map(s=>({...s,name:S.settings?.aliases?.[s.id]||s.name}));
 const sections=(S,sys)=> (sys.components||[]).map(part=>({
  part, temperatures:assigned(S,part,'Temperature'),powers:assigned(S,part,'Power'),
  fans:assigned(S,part,'Fan'),controls:assigned(S,part,'Control'),
  flows:assigned(S,part,'Flow')
 }));
 const names=(arr)=>arr.map(s=>esc(s.name)).join(', ');
 const tile=(title,readings,empty)=>{
  const shown=readings.filter(s=>Number.isFinite(s.value));
  return '<div class="cooling-insight-tile"><span>'+esc(title)+'</span>'+
   (shown.length?shown.slice(0,4).map(s=>
    '<div class="cooling-insight-reading"><small>'+esc(s.name)+'</small>'+
      '<strong data-live-reading="'+esc(s.id)+'" data-live-format="unit" data-live-unit="'+
      (unit[s.type]||'')+'">'+esc(fmt(s))+'</strong></div>').join(''):
    '<strong class="cooling-insight-empty">Not measured</strong><small>'+esc(empty)+'</small>')+
    '</div>';
 };
 const overview=(S,sys)=>{
  const parts=sections(S,sys),byRole=kind=>parts.filter(p=>p.part.kind===kind);
  const heats=byRole('heat-source'),fans=byRole('fan-group'),pumps=byRole('pump');
  const makeComponents=(groups)=>groups.map(p=>tile(p.part.name,
   [...p.temperatures.slice(0,3),...p.powers.slice(0,2),...p.fans.slice(0,1),...p.flows.slice(0,1)],
   'Assign a compatible sensor')).join('');
  return '<section class="cooling-insights" aria-label="System-aware cooling overview">'+
   '<div class="cooling-insights-head"><div><h3>System-aware overview</h3>'+
   '<p></p></div>'+
   '<span class="cooling-insights-label">Read-only</span></div>'+
   '<div class="cooling-insight-grid">'+
     makeComponents(heats)+makeComponents(fans)+makeComponents(pumps)+
     tile('Coolant',byRole('coolant-probe').flatMap(p=>p.temperatures),
      'Add a coolant probe when available')+
     tile('Case air',byRole('case-air').flatMap(p=>p.temperatures),
      'Optional sensor inside the case')+
     tile('Room ambient',byRole('room-ambient').flatMap(p=>p.temperatures),
      'Optional room sensor; case air is not ambient')+
   '</div>'+

   '</section>';
 };
 return {roles,overview};
})();
