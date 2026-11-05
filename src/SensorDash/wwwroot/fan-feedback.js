'use strict';
// The mapping belongs to the physical fan output, not to a reusable
// CPU/GPU temperature curve. This entire display is observational.
const FanFeedback=(()=>{
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const mapping=(S,id)=>(S.settings?.fanChannelMappings||[]).find(x=>x.channelId===id);
 const label=(S,id)=>mapping(S,id)?.label||id;
 const reading=(S,id)=>{
  const map=mapping(S,id);
  const sensor=(S.sensors||[]).find(x=>x.id===map?.rpmSensorId);
  if(map?.kind!=='fan'||!map.rpmSensorId)return {text:'No RPM mapping',mapped:false};
  if(!sensor||sensor.type!=='Fan'||!Number.isFinite(sensor.value))
   return {text:'RPM unavailable',mapped:true};
  const timestamp=Date.parse(sensor.observedAt||S.frame?.timestamp||'');
  if(!Number.isFinite(timestamp)||Math.abs(Date.now()-timestamp)>10000)
   return {text:'RPM stale',mapped:true};
  return {text:Math.round(sensor.value).toLocaleString()+' RPM',mapped:true};
 };
 const target=(S,profileId,channelId)=>{
  const persistent=S.fanControl?.persistent||{};
  if(!(persistent.profiles||[]).some(p=>p.id===profileId))return null;
  const override=(persistent.overrides||[]).find(x=>x.channelId===channelId &&
    Number.isFinite(x.percent) && (!x.expires||Date.parse(x.expires)>Date.now()));
  if(override)return {percent:override.percent,source:'Temporary override'};
  const profile=(persistent.readings||[]).find(x=>x.id===profileId);
  if(!Number.isFinite(profile?.targetPercent))return null;
  return {percent:profile.targetPercent,source:'Profile MAX demand'};
 };
 // Interpolate only within the ACTUALLY measured duty range.
 // A mapping verified at 70-100% must not fabricate an RPM at 10%.
 const estimate=(S,channelId,percent)=>{
  if(!Number.isFinite(percent))return {text:'—',hint:'Profile has no current PWM target.'};
  const map=mapping(S,channelId);
  if(map?.kind!=='fan'||!map.verifiedAt)
   return {text:'Not calibrated',hint:'Verify the physical fan and RPM sensor first.'};
  const points=[...(map.measurements||[])]
    .filter(p=>Number.isFinite(p.dutyPercent)&&Number.isFinite(p.rpm)&&p.rpm>0)
    .sort((a,b)=>a.dutyPercent-b.dutyPercent);
  if(points.length<2)
   return {text:'Not calibrated',hint:'Run full fan-speed calibration to estimate RPM.'};
  const low=points[0],high=points.at(-1);
  if(percent<low.dutyPercent||percent>high.dutyPercent)
   return {text:'Not calibrated at '+Math.round(percent)+'%',
    hint:'Saved calibration covers '+low.dutyPercent+'–'+high.dutyPercent+
      '% PWM. Run full calibration to estimate this speed.'};
  const index=points.findIndex(p=>p.dutyPercent>=percent);
  const hi=points[index],lo=points[Math.max(0,index-1)];
  const rpm=hi.dutyPercent===lo.dutyPercent?hi.rpm:
    lo.rpm+(hi.rpm-lo.rpm)*(percent-lo.dutyPercent)/
      (hi.dutyPercent-lo.dutyPercent);
  return {text:'≈'+Math.round(rpm).toLocaleString()+' RPM',
    hint:'Interpolated using measurements for '+label(S,channelId)+
      ' at '+percent.toFixed(1)+'% PWM. This is not a live RPM reading.'};
 };
 const render=(S,ids,profileId)=>{
  if(!ids?.length)return '';
  return '<div class="w-fan-feedback"><strong>Fan speed feedback</strong>'+
   ids.map(id=>{
    const map=mapping(S,id),r=reading(S,id),duty=target(S,profileId,id);
    const prediction=estimate(S,id,duty?.percent);
    const help=map?.kind==='pump'?'Pump tachometer · do not control via a fan profile':
      map?.verifiedAt?'Verified physical fan/RPM pairing':'Pairing not verified';
    return '<div class="w-fan-feedback-row"><span class="w-fan-feedback-name">'+
      esc(label(S,id))+'<small>'+esc(help)+'</small></span>'+
      '<div class="w-fan-speed-metrics">'+
       '<span><small>Target PWM</small><strong data-fan-profile-duty="'+esc(id)+'" data-for-profile="'+esc(profileId)+'">'+
         (duty?duty.percent.toFixed(1)+'%':'—')+'</strong></span>'+
       '<span><small>Estimated RPM</small><strong data-fan-profile-estimate="'+esc(id)+'" data-for-profile="'+esc(profileId)+'" title="'+esc(prediction.hint)+'">'+
         esc(prediction.text)+'</strong></span>'+
       '<span><small>Measured RPM</small><strong data-mapped-rpm="'+esc(id)+'">'+
         esc(r.text)+'</strong></span>'+
       '</div></div>';
   }).join('')+
   '<p class="note">Estimates use saved PWM/RPM measurements for each fan output. A shared hub may report only one fan’s actual RPM.</p></div>';
 };
 const paint=S=>{
  document.querySelectorAll('[data-mapped-rpm]').forEach(node=>{
   const next=reading(S,node.dataset.mappedRpm).text;
   if(node.textContent!==next)node.textContent=next;
  });
  document.querySelectorAll('[data-fan-profile-duty]').forEach(node=>{
   const next=target(S,node.dataset.forProfile,node.dataset.fanProfileDuty);
   const text=next?next.percent.toFixed(1)+'%':'—';
   if(node.textContent!==text)node.textContent=text;
   node.title=next?.source||'No active approved fan target';
  });
  document.querySelectorAll('[data-fan-profile-estimate]').forEach(node=>{
   const id=node.dataset.fanProfileEstimate;
   const duty=target(S,node.dataset.forProfile,id);
   const estimated=estimate(S,id,duty?.percent);
   if(node.textContent!==estimated.text)node.textContent=estimated.text;
   node.title=estimated.hint;
  });
 };
 return {mapping,label,reading,target,estimate,render,paint};
})();
