'use strict';
// Non-privileged identification and high-duty calibration assistant.
// Never owns PWM: every timed write goes through the existing UAC-only endpoint.
const FanCommissioning=(()=>{
 let selected='',seenKey='',activeSince=0;
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const byId=id=>document.getElementById(id);
 const mappings=S=>S.settings?.fanChannelMappings||[];
 const channels=S=>(S.fanControl?.outputs||[]);
 const current=S=>mappings(S).find(m=>m.channelId===selected);
 const activeProfile=(S,id)=>(S.fanControl?.persistent?.profiles||[])
   .some(p=>(p.channelIds||[]).includes(id));
 const dutyValues=[100,85,70];
 const qualifyResponse=m=>{
  if(!m||m.kind!=='fan'||!m.visuallyIdentified||!m.rpmSensorId)return false;
  const points=[...(m.measurements||[])].sort((a,b)=>a.dutyPercent-b.dutyPercent);
  return points.length>=2&&points.at(-1).dutyPercent-points[0].dutyPercent>=10&&
   points.every(p=>p.dutyPercent>=70&&p.dutyPercent<=100&&Number.isFinite(p.rpm)&&p.rpm>0)&&
   points.every((p,i)=>i===0||p.rpm>=points[i-1].rpm*0.92)&&
   points.at(-1).rpm>=points[0].rpm*1.08;
 };
 const choice=(id,label,chosen)=>'<option value="'+esc(id)+'" '+(id===chosen?'selected':'')+'>'+esc(label)+'</option>';
 const command=async(payload)=>{
  const r=await fetch('/api/fan-control/authorize',{
   method:'POST',headers:{'Content-Type':'application/json','X-SensorDash-Action':'fan-control'},
   body:JSON.stringify(payload)
  });
  if(!r.ok)throw Error((await r.text()).slice(0,150));
 };
 const persist=async(S,draft,h,message)=>{
  await SDWorkspace.saveSettings(S,draft);
  h.toast(message);h.render();
 };
 const render=S=>{
  const outputs=channels(S);
  if(!outputs.length)return '<section class="panel fc-panel"><h2>Identify · Name · Calibrate · Verify</h2>'+
   '<p class="note">No PWM outputs discovered. The collector must expose an actual writable control first.</p></section>';
  if(!outputs.some(o=>o.id===selected))selected=outputs[0].id;
  const output=outputs.find(o=>o.id===selected),map=current(S);
  const owner=activeProfile(S,selected);
  const rpm=map?.rpmSensorId||'';
  const tach=S.sensors.filter(s=>s.type==='Fan');
  const status=S.fanControl?.status||{};
  const isFan=map?.kind==='fan';
  const observations=(map?.measurements||[]).slice().sort((a,b)=>a.dutyPercent-b.dutyPercent);
  const verified=!!map?.verifiedAt && qualifyResponse(map);
  const sampleRows=observations.length?observations.map(p=>
   '<tr><td>'+p.dutyPercent+'% PWM</td><td>'+Number(p.rpm).toFixed(0)+' RPM</td><td>'+
   esc(new Date(p.observedAt).toLocaleString())+'</td></tr>').join(''):
   '<tr><td colspan="3">No verified RPM measurements yet</td></tr>';
  const readOnly=owner?'This channel belongs to an enabled profile. Stop the profile before a commissioning test.':'';
  return '<section class="panel fc-panel"><div class="panel-head"><div><h2>Fan output commissioning</h2>'+
   '<p>Identify → Name → Calibrate → Verify · No automatic low-duty sweeps</p></div>'+
   '<span class="pill '+(verified?'':'warn')+'">'+(verified?'Response verified':'Unverified')+'</span></div>'+
   '<div class="help">These are guided, time-limited observations — not automatic fan or pump detection. Never test an essential pump channel. Do not depend on SensorDash to recover from a crash or power loss.</div>'+
   '<div class="w-actions"><label>1 · Physical PWM output<select id="fcChannel">'+
   outputs.map(o=>choice(o.id,o.device+' / '+o.name+' ['+o.id+']',selected)).join('')+
   '</select></label><label>Type of connected equipment<select id="fcKind">'+
   [['unknown','Unknown / not identified'],['fan','Fan or fan bank'],['pump','Pump — never test here']]
   .map(([v,n])=>choice(v,n,map?.kind||'unknown')).join('')+'</select></label></div>'+
   '<div class="w-actions"><label>2 · Friendly name<input id="fcLabel" maxlength="80" value="'+
   esc(map?.label||'')+'" placeholder="e.g. Right radiator fans"></label>'+
   '<label>Optional tachometer (RPM) sensor<select id="fcRpm">'+
   choice('','No RPM feedback available',rpm)+tach.map(s=>choice(s.id,s.device+' / '+s.name,rpm)).join('')+
   '</select></label><button class="btn secondary" data-commissioning="save">Save identification</button>'+
   (map?'<button class="btn secondary" data-commissioning="remove">Forget mapping</button>':'')+'</div>'+
   '<p class="note">Discovered channel: '+esc(output.name)+' · '+esc(output.mode)+
   ' · supported '+output.minimum+'–'+output.maximum+'% PWM. '+esc(readOnly)+
   ' Selecting an RPM sensor does not prove it belongs to this output.</p>'+
   '<div class="fc-steps"><div><h3>3 · High-duty calibration</h3>'+
   '<p class="note">Request one 30-second elevated test at 100%, 85%, or 70% PWM. Confirm the correct physical fan bank responds. Wait until RPM settles, then record a sample while the timed test is active. The minimum stable/startup duty is not determined by this procedure.</p>'+
   '<label class="w-fan-ack"><input type="checkbox" id="fcExclusive"><span>I have closed other fan controllers, confirmed this is a <b>fan, not an essential pump</b>, and can observe it during the test.</span></label>'+
   '<div class="w-actions">'+dutyValues.map(percent=>'<button class="btn secondary" data-commissioning="test" data-duty="'+percent+
     '" '+(!isFan||!output.writable||owner?'disabled':'')+'>Test '+percent+'%</button>').join('')+
   '<button class="btn secondary" data-commissioning="release">Release to firmware</button></div>'+
   '<div class="w-live-status">Status: <b id="fcStatus">'+esc(status.state||'off')+'</b>'+
   ' <span id="fcMessage">'+esc(status.message||'')+'</span>'+
   '<span id="fcLiveRpm">RPM: —</span></div>'+
   '<div class="w-actions"><button class="btn secondary" data-commissioning="capture" '+
    (!isFan||!rpm?'disabled':'')+'>Capture current RPM</button><span class="note" id="fcCaptureHelp">'+
   (rpm?'Requires matching active manual test and fresh, settled RPM':'Select and save an RPM sensor to record measurements')+
   '</span></div>'+
   '<div class="table-container"><table class="w-output-table"><thead><tr><th>Command</th><th>Observed tachometer</th><th>Recorded</th></tr></thead><tbody>'+
    sampleRows+'</tbody></table></div></div>'+
   '<div><h3>4 · Verify response</h3>'+
   '<p class="note">Confirm you saw the intended physical fan bank change speed. At least two fresh recorded measurements separated by 10% PWM must show a meaningful RPM increase. Verification is observational only and does <b>not</b> authorize unattended fan control.</p>'+
   '<label class="w-fan-ack"><input type="checkbox" id="fcVisual"><span>I personally confirmed that this PWM output drives the named fan or fan bank, not a pump.</span></label>'+
   '<div class="w-actions"><button class="btn" data-commissioning="verify" '+(!isFan?'disabled':'')+
   '>Verify mapping</button><span class="note">'+(verified?'Last verified '+esc(new Date(map.verifiedAt).toLocaleString()):
    'Not verified · no operating minimum has been established')+'</span></div></div></div></section>';
 };
 const paint=S=>{
  if(S.view!=='curves'||!byId('fcStatus'))return;
  const status=S.fanControl?.status||{};
  const key=status.state==='active'&&status.mode==='manual'?
    status.channelId+'|'+status.target+'|'+status.expires:'';
  if(key!==seenKey){seenKey=key;activeSince=key?Date.now():0}
  byId('fcStatus').textContent=status.state||'off';
  byId('fcMessage').textContent=status.message||'';
  const id=current(S)?.rpmSensorId;
  const reading=S.sensors.find(s=>s.id===id);
  byId('fcLiveRpm').textContent='RPM: '+(reading&&Number.isFinite(reading.value)?
   Number(reading.value).toFixed(0):'—');
 };
 const doAction=async(action,el,S,h)=>{
  const channel=channels(S).find(o=>o.id===selected),map=current(S);
  if(!channel)throw Error('Select a discovered physical output first.');
  if(action==='save'){
   const kind=byId('fcKind')?.value||'unknown';
   const label=byId('fcLabel')?.value.trim()||'';
   const rpmSensorId=byId('fcRpm')?.value||'';
   if(!label)throw Error('Give this output a meaningful name.');
   if(rpmSensorId&&!S.sensors.some(s=>s.id===rpmSensorId&&s.type==='Fan'))
    throw Error('Choose a genuine Fan/RPM sensor.');
   const draft=structuredClone(S.settings);
   draft.fanChannelMappings=draft.fanChannelMappings||[];
   const others=draft.fanChannelMappings.filter(m=>m.channelId!==channel.id);
   const changed=map&&(map.kind!==kind||map.rpmSensorId!==rpmSensorId);
   const updated={channelId:channel.id,label,kind,rpmSensorId,
    visuallyIdentified:changed?false:(map?.visuallyIdentified||false),
    verifiedAt:changed?null:(map?.verifiedAt||null),
    measurements:changed?[]:(map?.measurements||[])};
   draft.fanChannelMappings=[...others,updated];
   return persist(S,draft,h,'Identification saved; no hardware command was sent.');
  }
  if(action==='remove'){
   if(!confirm('Forget the saved identification and all measurements for this channel?'))return;
   const draft=structuredClone(S.settings);
   draft.fanChannelMappings=(draft.fanChannelMappings||[]).filter(m=>m.channelId!==selected);
   return persist(S,draft,h,'Channel mapping removed.');
  }
  if(action==='release'){
   await command({action:'release',channelId:'',durationSeconds:30,percent:100});
   h.toast('Windows elevation requested to restore firmware control.');
   return;
  }
  if(action==='test'){
   const duty=Number(el.dataset.duty);
   if(!map||map.kind!=='fan'||!map.label||byId('fcKind')?.value!=='fan')
    throw Error('Select Fan and save the identification before requesting a test.');
   if(![70,85,100].includes(duty))throw Error('Only the approved 70–100% test steps are available.');
   if(!channel.writable||activeProfile(S,selected))
    throw Error('This channel is unavailable or assigned to an enabled profile.');
   if(!byId('fcExclusive')?.checked)throw Error('Confirm the fan/pump and controller safety checks.');
   await command({action:'arm',channelId:selected,curveId:'',percent:duty,
    durationSeconds:30,confirmExclusiveOwnership:true});
   seenKey='';activeSince=0;
   h.toast('Approve Windows elevation. Observe the physical fan bank.');
   return;
  }
  if(action==='capture'){
   if(!map||map.kind!=='fan'||!map.rpmSensorId||
      byId('fcKind')?.value!=='fan'||byId('fcRpm')?.value!==map.rpmSensorId)
    throw Error('Save the current fan type and RPM sensor before recording data.');
   const r=await fetch('/api/fan-control');
   if(!r.ok)throw Error('Could not confirm current hardware-control status.');
   const control=(await r.json()).status||{};
   if(control.state!=='active'||control.mode!=='manual'||control.channelId!==selected||
      ![70,85,100].includes(Math.round(control.target))||
      Math.abs(control.target-Math.round(control.target))>0.1||
      Date.parse(control.expires)<=Date.now())
    throw Error('An authorized 70%, 85%, or 100% manual test must be active on this exact channel.');
   const key=control.channelId+'|'+control.target+'|'+control.expires;
   if(key!==seenKey){seenKey=key;activeSince=Date.now()}
   if(Date.now()-activeSince<5000)
    throw Error('Let the fan settle for at least 5 seconds while this test is active.');
   const reading=S.sensors.find(s=>s.id===map.rpmSensorId);
   const observed=Date.parse(reading?.observedAt||S.frame?.timestamp||'');
   if(reading?.type!=='Fan'||!Number.isFinite(reading.value)||reading.value<=0||
      !Number.isFinite(observed)||Math.abs(Date.now()-observed)>6000)
    throw Error('A fresh, positive RPM reading is required. Confirm the tachometer sensor.');
   const draft=structuredClone(S.settings);
   const entry=draft.fanChannelMappings.find(m=>m.channelId===selected);
   entry.measurements=(entry.measurements||[]).filter(p=>p.dutyPercent!==Math.round(control.target));
   entry.measurements.push({dutyPercent:Math.round(control.target),rpm:reading.value,
    observedAt:new Date(observed).toISOString()});
   entry.verifiedAt=null;
   return persist(S,draft,h,'RPM observation recorded. Release the test or collect another duty.');
  }
  if(action==='verify'){
   if(!map||map.kind!=='fan'||byId('fcKind')?.value!=='fan'||
      byId('fcRpm')?.value!==map.rpmSensorId)
    throw Error('Save any identification edits before verifying the response.');
   if(!byId('fcVisual')?.checked)
    throw Error('Confirm the physical fan bank visibly responded to this PWM output.');
   const draft=structuredClone(S.settings);
   const entry=draft.fanChannelMappings.find(m=>m.channelId===selected);
   entry.visuallyIdentified=true;
   if(!qualifyResponse(entry))
    throw Error('Need two positive PWM/RPM samples at least 10% apart, showing an 8% RPM increase.');
   entry.verifiedAt=new Date().toISOString();
   return persist(S,draft,h,'Observed fan response verified; no low-duty safety claim.');
  }
 };
 const handle=(e,S,h)=>{
  const action=e.target.closest('[data-commissioning]');
  if(!action)return false;
  e.preventDefault();
  if(WorkspaceLocks.fan(S)&&action.dataset.commissioning!=='release'){
   h.toast('Unlock Fan Control before changing commissioning data or requesting a test.');
   return true;
  }
  void doAction(action.dataset.commissioning,action,S,h)
   .catch(error=>h.toast(error.message));
  return true;
 };
 const change=(e,S,h)=>{
  if(e.target.id!=='fcChannel')return false;
  selected=e.target.value;seenKey='';activeSince=0;h.render();
  return true;
 };
 return {render,paint,handle,change,qualifyResponse};
})();
