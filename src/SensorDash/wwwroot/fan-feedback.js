'use strict';
// PWM-to-RPM mapping is the single source of truth for physical fan feedback.
// It is observational and never participates in authorization or duty planning.
const FanFeedback=(()=>{
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const mapping=(S,id)=>(S.settings?.fanChannelMappings||[]).find(x=>x.channelId===id);
 const reading=(S,id)=>{
  const map=mapping(S,id);
  const sensor=S.sensors.find(x=>x.id===map?.rpmSensorId);
  if(map?.kind!=='fan'||!map.rpmSensorId)return {text:'No RPM mapping',mapped:false};
  if(!sensor||sensor.type!=='Fan'||!Number.isFinite(sensor.value))
   return {text:'RPM unavailable',mapped:true};
  const timestamp=Date.parse(sensor.observedAt||S.frame?.timestamp||'');
  if(!Number.isFinite(timestamp)||Math.abs(Date.now()-timestamp)>10000)
   return {text:'RPM stale',mapped:true};
  return {text:Math.round(sensor.value).toLocaleString()+' RPM',mapped:true};
 };
 const label=(S,id)=>mapping(S,id)?.label||id;
 const render=(S,ids)=>{
  if(!ids?.length)return '';
  return '<div class="w-fan-feedback"><strong>Mapped fan feedback</strong>'+
   ids.map(id=>{
    const map=mapping(S,id),r=reading(S,id);
    const help=map?.kind==='pump'?'Pump tachometer · fan profile must not control this channel':
      map?.verifiedAt?'Verified PWM/RPM response':'Unverified or not mapped';
    return '<div class="w-fan-feedback-row"><span>'+esc(label(S,id))+
      '<small>'+esc(help)+'</small></span><strong data-mapped-rpm="'+esc(id)+'">'+
      esc(r.text)+'</strong></div>';
   }).join('')+
   '<p class="note">A shared fan hub may report only one fan’s RPM.</p></div>';
 };
 const paint=S=>{
  document.querySelectorAll('[data-mapped-rpm]').forEach(node=>{
   const next=reading(S,node.dataset.mappedRpm).text;
   if(node.textContent!==next)node.textContent=next;
  });
 };
 return {mapping,label,reading,render,paint};
})();
