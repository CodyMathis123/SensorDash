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
   '<p>Grouped from your assigned physical components; no inferred coolant or ambient values.</p></div>'+
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
   '<p class="note">Heat-source power readings are shown separately; package, core and device power readings can overlap. Fan-bank tachometers may represent only one fan on a splitter.</p>'+
   '</section>';
 };
 // Match timestamped samples within a bounded interval. Do not interpolate
 // across missing history or infer heat flow from power measurements.
 const pair=(x=[],y=[],maxGap=15000)=>{
  const left=x.filter(p=>Number.isFinite(p?.t)&&Number.isFinite(p?.v)).sort((a,b)=>a.t-b.t);
  const right=y.filter(p=>Number.isFinite(p?.t)&&Number.isFinite(p?.v)).sort((a,b)=>a.t-b.t);
  const out=[];let j=0, last=-1;
  for(const a of left){
   while(j+1<right.length&&right[j+1].t<=a.t)j++;
   const choices=[j,j+1].filter(k=>k<right.length&&k>last);
   if(!choices.length)continue;
   const k=choices.reduce((best,k)=>Math.abs(right[k].t-a.t)<Math.abs(right[best].t-a.t)?k:best);
   if(Math.abs(a.t-right[k].t)<=maxGap){out.push([a.v,right[k].v]);last=k;}
  }
  return out;
 };
 const correlation=(x,y)=>{
  const matched=pair(x,y);
  if(matched.length<12)return null;
  const n=matched.length,ax=matched.reduce((a,p)=>a+p[0],0)/n,
    ay=matched.reduce((a,p)=>a+p[1],0)/n;
  let sx=0,sy=0,cov=0;
  for(const [a,b] of matched){const dx=a-ax,dy=b-ay;sx+=dx*dx;sy+=dy*dy;cov+=dx*dy;}
  // Pearson r is undefined for a constant series. Small but real variation
  // still has a coefficient; flag its sensitivity rather than hiding it.
  if(!matched.some(p=>p[0]!==matched[0][0])||!matched.some(p=>p[1]!==matched[0][1])||sx<=0||sy<=0)return null;
  return {r:Math.max(-1,Math.min(1,cov/Math.sqrt(sx*sy))),samples:n,lowVariation:sx/n<1||sy/n<0.25};
 };
 const correlateHtml=(S,sys)=>{
  const heat=sections(S,sys).filter(p=>p.part.kind==='heat-source');
  const pairs=heat.map(p=>{
   const power=p.powers[0],temp=p.temperatures[0];
   const samples=power&&temp?pair(S.history?.[power.id],S.history?.[temp.id]):[];
   return {part:p.part.name,power,temp,
     result:power&&temp?correlation(S.history?.[power.id],S.history?.[temp.id]):null,
     reason:!power||!temp?'Assign both power and temperature sensors':samples.length<12?
       'Waiting for at least 12 paired samples (within 15 seconds)':'Readings are present, but one series is constant in this time range'};
  });
  if(!pairs.length)return '<p class="note">Add a heat source with temperature and power sensors to compare their histories.</p>';
  return '<div class="cooling-correlation-list">'+pairs.map(p=>
   '<div class="cooling-correlation-row"><strong>'+esc(p.part)+'</strong>'+
    '<span>'+esc(p.power?.name||'Power not assigned')+' ↔ '+esc(p.temp?.name||'Temperature not assigned')+'</span>'+
    (p.result?'<b>r = '+p.result.r.toFixed(2)+'</b><small>'+p.result.samples+' paired samples</small>'+
      (p.result.lowVariation?'<small class="correlation-note">Low variation—interpret cautiously</small>':''):
      '<b>Not calculated</b><small class="correlation-note">'+esc(p.reason)+'</small>')+'</div>'
  ).join('')+'</div>'+
  '<p class="note">Pearson r describes simultaneous co-movement, not cooling efficiency or causation. Thermal lag, workload and sensor selection can change it. Independent CPU/GPU readings are not summed.</p>';
 };
 const analysis=(S,sys)=>'<section class="cooling-analysis" data-cooling-analysis="'+esc(sys.id)+'">'+
  '<div class="cooling-insights-head"><div><h3>Power ↔ temperature relationship</h3>'+
  '<p>Exploratory comparison using the same cached history as the charts.</p></div></div>'+
  '<div data-cooling-correlations="'+esc(sys.id)+'">'+correlateHtml(S,sys)+'</div></section>';
 const eventIds=(S,sys)=>{
  const sensors=new Set((sys.components||[]).flatMap(c=>c.sensorIds||[]));
  const channels=new Set((S.settings?.fanChannelMappings||[])
   .filter(m=>m.rpmSensorId&&sensors.has(m.rpmSensorId)).map(m=>m.channelId));
  const profiles=new Set((S.settings?.fanProfiles||[])
   .filter(p=>(p.channelIds||[]).some(id=>channels.has(id))).map(p=>p.id));
  for(const p of S.fanControl?.persistent?.profiles||[])
   if((p.channelIds||[]).some(id=>channels.has(id)))profiles.add(p.id);
  return {sensors,channels,profiles};
 };
 const recentEvents=(S,sys)=>{
  const known=eventIds(S,sys),cutoff=Date.now()-(S.hours||1)*3600000;
  return (S.events||[]).filter(e=>{
   if(!Number.isFinite(e.t)||e.t<cutoff||e.t>Date.now()+60000)return false;
   if(known.sensors.has(e.sensor))return true;
   if(e.sensor==='fan:all')return known.channels.size>0;
   if(e.sensor?.startsWith('fan:channel:'))return known.channels.has(e.sensor.slice(12));
   if(e.sensor?.startsWith('fan:profile:'))return known.profiles.has(e.sensor.slice(12));
   return false;
  }).sort((a,b)=>b.t-a.t).slice(0,10);
 };
 const activityHtml=(S,sys)=>{
  const events=recentEvents(S,sys);
  if(!events.length)return '<p class="note">No relevant recent events recorded in this time range. Future approved fan actions and monitoring alerts will appear here; older fan actions cannot be reconstructed.</p>';
  return '<div class="cooling-activity-list">'+events.map(e=>
   '<button type="button" class="cooling-activity-row" data-cooling-focus="'+esc(sys.id)+'" data-cooling-at="'+e.t+
   '" title="Inspect this instant across the cooling charts"><time>'+esc(new Date(e.t).toLocaleTimeString())+'</time>'+
   '<span>'+esc(e.message)+'</span></button>').join('')+'</div>';
 };
 const activity=(S,sys)=>'<section class="cooling-activity"><div class="cooling-insights-head">'+
  '<div><h3>Recent cooling activity</h3><p>Recorded fan-control requests and sensor monitoring alerts in this time range.</p></div></div>'+
  '<div data-cooling-activity="'+esc(sys.id)+'">'+activityHtml(S,sys)+'</div></section>';
 const paint=(S)=>{
  if(S.view!=='cooling')return;
  document.querySelectorAll('[data-cooling-correlations]').forEach(node=>{
   const sys=S.settings?.coolingSystems?.find(x=>x.id===node.dataset.coolingCorrelations);
   if(!sys)return;
   const html=correlateHtml(S,sys);
   if(node.innerHTML!==html)node.innerHTML=html;
  });
 };
 const paintEvents=(S)=>{
  if(S.view!=='cooling')return;
  document.querySelectorAll('[data-cooling-activity]').forEach(node=>{
   const sys=S.settings?.coolingSystems?.find(x=>x.id===node.dataset.coolingActivity);
   if(!sys)return;
   const html=activityHtml(S,sys);
   if(node.innerHTML!==html)node.innerHTML=html;
  });
 };
 return {roles,overview,analysis,activity,paint,paintEvents,correlation,pair,recentEvents,eventIds};
})();
