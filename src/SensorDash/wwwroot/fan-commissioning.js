'use strict';
// Non-privileged identification and high-duty calibration assistant.
// Never owns PWM: every timed write goes through the existing UAC-only endpoint.
const FanCommissioning=(()=>{
 let selected='',seenKey='',activeSince=0;
 let pendingCalibration=null,calibrationNotice='';
 const calibrationGuards=new Map();
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
   points.every(p=>p.dutyPercent>=10&&p.dutyPercent<=100&&Number.isFinite(p.rpm)&&p.rpm>0)&&
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
 const temperatureCells=p=>{
  const items=(p?.temperatures||[]).filter(t=>Number.isFinite(t.celsius));
  if(!items.length)return '<span class="note">Not recorded</span>';
  return items.map(t=>'<span class="fc-temperature-reading" title="'+esc(t.sensorId||'')+'">'+
    esc(t.sensorName||t.sensorId||'Temperature')+': '+
    Number(t.celsius).toFixed(1)+'°C</span>').join('');
 };
 const measurementRows=points=>points.map(p=>'<tr><td>'+
   Number(p.dutyPercent)+'% PWM</td><td>'+
   Number(p.rpm).toFixed(0)+' RPM</td><td>'+
   temperatureCells(p)+'</td><td>'+
   esc(new Date(p.observedAt).toLocaleTimeString())+'</td></tr>').join('');
 const persist=async(S,draft,h,message)=>{
  await SDWorkspace.saveSettings(S,draft);
  h.toast(message);h.render();
 };
 const render=S=>{
  const outputs=channels(S);
  if(!outputs.length)return '<section class="panel fc-panel"><h2>Identify · Name · Calibrate · Verify</h2>'+
   '<p class="note">No controllable fan outputs found.</p></section>';
  if(!outputs.some(o=>o.id===selected))selected=outputs[0].id;
  const output=outputs.find(o=>o.id===selected),map=current(S);
  const owner=activeProfile(S,selected);
  const rpm=map?.rpmSensorId||'';
  const tach=S.sensors.filter(s=>s.type==='Fan');
  const status=S.fanControl?.status||{};
  const calibration=S.fanControl?.persistent?.calibration;
  const calibrationHere=calibration?.channelId===selected?calibration:null;
  const temperatures=S.sensors.filter(x=>x.type==='Temperature');
  const linkedProfileDrafts=(S.settings?.fanProfiles||[])
    .filter(p=>(p.channelIds||[]).includes(selected));
  const linkedCurveIds=new Set(linkedProfileDrafts.flatMap(p=>p.curveIds||[]));
  const linkedTemps=(S.settings?.simulatedFanCurves||[])
    .filter(c=>linkedCurveIds.has(c.id)).map(c=>c.sourceSensorId)
    .filter(id=>temperatures.some(t=>t.id===id));
  const guardDefaults=calibrationGuards.get(selected)||{
    first:linkedTemps[0]||'',second:linkedTemps[1]||'',limit:'85'};
  const guardSelect=(id,label,selectedId,optional)=>'<label>'+label+
    '<select id="'+id+'">'+choice('',optional?'None':'Choose a temperature sensor',selectedId)+
    temperatures.map(t=>choice(t.id,S.settings?.aliases?.[t.id]||
      (t.device+' / '+t.name),selectedId)).join('')+'</select></label>';
  const activeOverride=(S.fanControl?.persistent?.overrides||[]).find(x=>x.channelId===selected);
  const profile=(S.fanControl?.persistent?.profiles||[])
    .find(p=>(p.channelIds||[]).includes(selected));
  const minOverride=Math.ceil(Math.max(10,profile?.minimumPercent||0,output.minimum||0));
  const maxOverride=Math.floor(Math.min(100,output.maximum??100));
  const feedback=S.fanControl?.persistent?.pwmFeedback?.[selected];
  const register=S.sensors.find(x=>x.id===selected && x.type==='Control');
  const calSamples=calibrationHere?.measurements||[];
  const autoRows=measurementRows(calSamples);
  const isFan=map?.kind==='fan';
  const observations=(map?.measurements||[]).slice().sort((a,b)=>a.dutyPercent-b.dutyPercent);
  const verified=!!map?.verifiedAt && qualifyResponse(map);
  const savedCount=observations.length;
  const savedRange=observations.length?
    Math.min(...observations.map(p=>p.dutyPercent))+'–'+
    Math.max(...observations.map(p=>p.dutyPercent))+'% PWM':'';
  const fullSaved=savedCount>=19&&observations.some(p=>p.dutyPercent<=10);
  const savedDescription=savedCount?
    (fullSaved?'Full calibration':'Saved RPM measurements')+
    ' · '+savedCount+' points · '+savedRange:'No saved RPM measurements';
  const identifyReady=isFan && !!rpm && output.writable;
  const responseReady=identifyReady && verified;
  const sampleRows=observations.length?measurementRows(observations):
   '<tr><td colspan="4">No RPM measurements saved for this fan yet</td></tr>';
  const readOnly=owner?'An active profile owns this header. Short standalone manual tests are disabled, but guarded response calibration remains available.':'';
  return '<section class="panel fc-panel"><div class="panel-head"><div><h2>Fan control & calibration</h2>'+
   '<p>Identify the physical fan and tachometer before configuring temperature curves</p></div>'+
   '<span class="pill '+(verified?'':'warn')+'">'+(verified?(fullSaved?'Full RPM response saved':'PWM/RPM pairing verified'):'Unverified pairing')+'</span></div>'+
   '<div class="help">Calibration belongs to the physical PWM header, not a CPU/GPU curve. The first sweep identifies its tachometer; the optional full sweep builds a PWM-to-RPM table. Both require local administrator authorization and supervision. Never test a pump.</div>'+
   '<div class="w-actions"><label>1 · Physical PWM output<select id="fcChannel">'+
   outputs.map(o=>choice(o.id,o.device+' / '+o.name+' ['+o.id+']',selected)).join('')+
   '</select></label><label>Type of connected equipment<select id="fcKind">'+
   [['unknown','Unknown / not identified'],['fan','Fan or fan bank'],['pump','Pump — never test here']]
   .map(([v,n])=>choice(v,n,map?.kind||'unknown')).join('')+'</select></label></div>'+
   '<div class="w-actions"><label>2 · Friendly name<input id="fcLabel" maxlength="80" value="'+
   esc(map?.label||'')+'" placeholder="e.g. Right radiator fans"></label>'+
   '<label>Optional tachometer (RPM) sensor<select id="fcRpm">'+
   choice('','No RPM feedback available',rpm)+tach.map(s=>choice(s.id,S.settings?.aliases?.[s.id]||(s.device+' / '+s.name),rpm)).join('')+
   '</select></label><button class="btn secondary" data-commissioning="save">Save identification</button>'+
   (map?'<button class="btn secondary" data-commissioning="remove">Forget mapping</button>':'')+'</div>'+
   '<p class="note">Discovered channel: '+esc(output.name)+' · '+esc(output.mode)+
   ' · supported '+output.minimum+'–'+output.maximum+'% PWM. '+esc(readOnly)+
   ' Selecting an RPM sensor does not prove it belongs to this output.</p>'+
   '<div class="fc-steps"><div class="fc-calibration-primary"><h3>Fan speed calibration</h3>'+
   '<p class="fc-calibration-intro">Full calibration measures up to 19 speeds, from 100% to 10% in 5% steps. Each point is recorded only after the fan stays at a stable RPM. Allow roughly 7–20 minutes; you can cancel at any time.</p>'+
   '<label class="w-fan-ack"><input type="checkbox" id="fcExclusive"><span>I can observe these fans and no other fan-control software is active. This output is not a pump.</span></label>'+
   '<p class="note fc-setup-details">New fan? Verify the PWM/RPM pairing first. An already verified fan can go straight to a full calibration.</p>'+
   '<div class="w-actions"><button class="btn" data-commissioning="start-identify" '+
     (!identifyReady||calibration?.state==='running'?'disabled':'')+
     '>Verify fan and RPM pairing</button></div>'+
   '<p class="fc-setup-details">Choose the temperatures to monitor during calibration. The test stops if a temperature rises too far, a sensor disappears, or fan RPM fails to stabilize. A 25-minute safety cutoff prevents indefinite testing.</p>'+
   '<div class="fc-calibration-guards">'+
     guardSelect('fcGuardPrimary','Primary thermal guard',guardDefaults.first,false)+
     guardSelect('fcGuardSecondary','Additional thermal guard',guardDefaults.second,true)+
     '<label>Stop test above (°C)<input type="number" id="fcGuardLimit" min="35" max="100" step="1" value="'+esc(guardDefaults.limit)+'"></label></div>'+
   '<div class="fc-calibration-actions"><button class="btn" data-commissioning="start-auto" '+
     (!responseReady||calibration?.state==='running'?'disabled':'')+
     '>Run full calibration</button>'+
   (calibrationHere?.state==='running'?
      '<button class="btn secondary" data-commissioning="cancel-auto">Cancel and restore control</button>':'')+
   (calibrationHere?.state==='complete'?
      '<button class="btn secondary" data-commissioning="save-auto">Save '+(calibrationHere.mode==='identify'?
        'verified pairing':'RPM calibration')+'</button>':'')+
   '</div>'+
   '<div class="fc-calibration-status" id="fcCalibrationBanner" role="status" aria-live="polite" data-status="'+
   (calibrationHere?.state||(pendingCalibration?'pending':calibrationNotice?'error':'idle'))+'">'+
   '<strong id="fcCalibrationHeading">'+(calibrationHere?.state==='running'?'Calibrating fan speed':
     calibrationHere?.state==='complete'?'Calibration complete':
     calibrationHere?.state==='aborted'?'Calibration stopped':
     pendingCalibration?'Waiting for Windows approval':
     calibrationNotice?'Calibration did not start':
     savedCount?'Saved RPM measurements: '+savedCount:'Ready to calibrate')+'</strong>'+
   '<span class="fc-calibration-feedback" id="fcAutoStatus">'+esc(
     calibrationHere?.message||calibrationNotice||
     (pendingCalibration?'Waiting for Windows approval and the service to start the test.':
     verified?'Pairing verified. The full calibration can measure slower speeds.':
     'Verify the fan/RPM pairing before performing a full test.'))+'</span>'+
   '<progress id="fcCalibrationProgress" value="'+(calibrationHere?.completedSteps||0)+
     '" max="'+(calibrationHere?.totalSteps||1)+'" '+
     (!calibrationHere?.state||calibrationHere.state==='aborted'?'hidden':'')+'></progress>'+
   '<small id="fcAutoProgress">'+(calibrationHere?
     calibrationHere.completedSteps+' of '+calibrationHere.totalSteps+' stable speeds measured':
     'No calibration running')+'</small></div>'+
   (calSamples.length?'<details class="fc-advanced-toggle" open><summary>Current run · '+
      calSamples.length+' measured speeds</summary><div class="table-container"><table><thead><tr>'+
      '<th>PWM</th><th>Settled RPM</th><th>Guard temperatures when measured</th><th>Measured at</th>'+
      '</tr></thead><tbody>'+autoRows+'</tbody></table></div></details>':'')+
   '<details class="fc-advanced-toggle" data-fc-saved open><summary>Saved calibration · '+
     esc(savedDescription)+'</summary><div>'+
     '<p class="note">'+(savedCount?
       'These readings are saved to this physical fan mapping in SensorDash settings. They persist after changing fans or restarting the service. Verification: '+
       esc(map?.verifiedAt?new Date(map.verifiedAt).toLocaleString():'not verified'):
       'No calibration saved for this fan output yet. A completed run needs an explicit Save RPM calibration action.')+
     '</p><div class="table-container"><table class="fc-saved-results"><thead><tr>'+
     '<th>PWM</th><th>Settled RPM</th><th>Guard temperatures when measured</th><th>Measured at</th>'+
     '</tr></thead><tbody>'+sampleRows+'</tbody></table></div></div></details>'+
   '<p class="fc-setup-details">Calibration measures this fan output only; your CPU and GPU curves remain unchanged. A single RPM sensor cannot verify every fan on a shared hub.</p></div>'+
   '<details class="fc-advanced-toggle"><summary>Manual speed controls & diagnostics</summary><div>'+
   '<p class="note">Run a 30-second test (70–100%) for unapproved channels, or temporarily override an approved fan curve. All changes require Windows elevation.</p>'+

   '<div class="w-actions">'+dutyValues.map(percent=>'<button class="btn secondary" data-commissioning="test" data-duty="'+percent+
     '" '+(!isFan||!output.writable||owner?'disabled':'')+'>Test '+percent+'%</button>').join('')+
   '<button class="btn secondary" data-commissioning="release">Release to firmware</button></div>'+
   (owner && isFan && output.writable && minOverride<=maxOverride?
      '<div class="w-component"><h3>Approved profile · temporary override</h3>'+
      '<p class="note">Your approved fan curve remains the fallback. Move the slider, then Apply; overrides expire in 5 minutes and thermal failsafes take priority.</p>'+
      '<div class="w-fan-quick-controls"><input type="range" id="fcOverrideDuty" min="'+minOverride+
      '" max="'+maxOverride+'" step="1" value="'+
      Math.min(maxOverride,Math.max(minOverride,activeOverride?.percent??profile?.minimumPercent??minOverride))+
      '"><output id="fcOverrideValue">'+
      Number(activeOverride?.percent??profile?.minimumPercent??minOverride).toFixed(0)+'%</output></div>'+
      '<div class="w-actions"><button class="btn secondary" data-commissioning="override">Apply for 5 minutes</button>'+
      (activeOverride?'<button class="btn secondary" data-commissioning="reset-override">Return to fan curve</button>':'')+
      '</div></div>':'')+
   '<div class="w-live-status">Status: <b id="fcStatus">'+esc(status.state||'off')+'</b>'+
   ' <span id="fcMessage">'+esc(status.message||'')+'</span>'+
   '<span id="fcLiveRpm">RPM: —</span></div>'+
   '<div class="w-actions"><button class="btn secondary" data-commissioning="capture" '+
    (!isFan||!rpm?'disabled':'')+'>Capture current RPM</button><span class="note" id="fcCaptureHelp">'+
   (rpm?'Requires a matching authorized manual test or override, fresh PWM readback and settled RPM':'Select and save an RPM sensor to record measurements')+
   '</span></div>'+
   '<div class="w-live-status">PWM register: <b id="fcLivePwm">'+
   (Number.isFinite(register?.value)?Number(register.value).toFixed(1)+'%':'—')+
   '</b> · Feedback check: '+esc(feedback?.state||'waiting')+'</div>'+
   '<p class="note">Previously saved PWM/RPM measurements are displayed above under Saved calibration. Manual tests can add an observation; they do not automatically replace the last calibration.</p></div>'+
   '</details>'+
   '<details class="fc-advanced-toggle"><summary>Confirm physical fan identification</summary><div>'+
   '<p class="note">Confirm the correct physical fan bank actually moved during testing. RPM response alone cannot establish the wiring, especially for a shared PWM hub.</p>'+
   '<label class="w-fan-ack"><input type="checkbox" id="fcVisual"><span>I personally confirmed that this PWM output drives the named fan or fan bank, not a pump.</span></label>'+
   '<div class="w-actions"><button class="btn secondary" data-commissioning="confirm-identity" '+
   (!isFan?'disabled':'')+'>Confirm physical fan identity</button>'+
   '<button class="btn" data-commissioning="verify" '+(!isFan?'disabled':'')+
   '>Verify mapping</button><span class="note">'+(verified?'Last verified '+esc(new Date(map.verifiedAt).toLocaleString()):
    'Unverified physical fan/RPM pairing')+'</span></div></div></details></div></section>';
 };
 const paint=S=>{
  if(S.view!=='curves'||!byId('fcStatus'))return;
  const status=S.fanControl?.status||{};
  const override=(S.fanControl?.persistent?.overrides||[]).find(x=>x.channelId===selected);
  const key=status.state==='active'&&status.mode==='manual'?
    status.channelId+'|'+status.target+'|'+status.expires:
    override?override.channelId+'|'+override.percent+'|'+override.expires:'';
  if(key!==seenKey){seenKey=key;activeSince=key?Date.now():0}
  byId('fcStatus').textContent=status.state||'off';
  byId('fcMessage').textContent=status.message||'';
  const id=current(S)?.rpmSensorId;
  const reading=S.sensors.find(s=>s.id===id);
  byId('fcLiveRpm').textContent='RPM: '+(reading&&Number.isFinite(reading.value)?
   Number(reading.value).toFixed(0):'—');
  const pwm=S.sensors.find(s=>s.id===selected && s.type==='Control');
  if(byId('fcLivePwm'))byId('fcLivePwm').textContent=Number.isFinite(pwm?.value)?
    Number(pwm.value).toFixed(1)+'%':'—';
  const cal=S.fanControl?.persistent?.calibration;
  const banner=byId('fcCalibrationBanner');
  const heading=byId('fcCalibrationHeading');
  const message=byId('fcAutoStatus');
  const progress=byId('fcCalibrationProgress');
  const details=byId('fcAutoProgress');
  if(cal?.channelId===selected){
   pendingCalibration=null;
   calibrationNotice='';
   if(banner)banner.dataset.status=cal.state;
   if(heading)heading.textContent=cal.state==='running'?
     'Calibrating fan speed':cal.state==='complete'?
     'Calibration complete':cal.state==='aborted'?
     'Calibration stopped':'Calibration status';
   if(message)message.textContent=cal.message;
   if(details)details.textContent=cal.completedSteps+' of '+
     cal.totalSteps+' stable speeds measured · '+Math.round(cal.currentDuty)+'% duty'+
     (Number.isFinite(cal.observedRpm)?' · '+Math.round(cal.observedRpm)+' RPM':'');
   if(progress){
    progress.hidden=cal.state==='aborted';
    progress.max=Math.max(1,cal.totalSteps);
    progress.value=cal.completedSteps;
   }
  }else if(pendingCalibration){
   if(Date.now()-pendingCalibration.started>30000){
    pendingCalibration=null;
    calibrationNotice='No calibration started within 30 seconds. Check whether Windows requested approval; the collector may have rejected the request.';
   }else{
    if(banner)banner.dataset.status='pending';
    if(heading)heading.textContent='Waiting for Windows approval';
    if(message)message.textContent='Waiting for Windows approval and the fan-control service to start.';
    if(details)details.textContent='No fan-speed measurements yet';
   }
  }
  if(calibrationNotice){
   if(banner)banner.dataset.status='error';
   if(heading)heading.textContent='Calibration did not start';
   if(message)message.textContent=calibrationNotice;
   if(details)details.textContent='No changes were made by this request';
  }
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
  if(action==='confirm-identity'){
   if(!map||map.kind!=='fan'||byId('fcKind')?.value!=='fan'||!map.rpmSensorId)
    throw Error('Save the fan and its matching RPM sensor before confirming identity.');
   if(!byId('fcVisual')?.checked)
    throw Error('Confirm this is physically a fan bank, not an essential pump.');
   const draft=structuredClone(S.settings);
   const entry=draft.fanChannelMappings.find(m=>m.channelId===selected);
   entry.visuallyIdentified=true;
   return persist(S,draft,h,'Fan identity confirmed. Calibration remains observational.');
  }
  if(action==='start-identify'||action==='start-auto'){
   const identifying=action==='start-identify';
   if(!map||map.kind!=='fan'||!map.rpmSensorId||byId('fcKind')?.value!=='fan'||
      byId('fcRpm')?.value!==map.rpmSensorId||!channel.writable)
    throw Error('Save an identified fan output and matching RPM sensor before calibrating.');
   if(!identifying&&(!map.visuallyIdentified||!map.verifiedAt||
      !qualifyResponse(map)))
    throw Error('First verify the physical PWM/RPM pairing at 70–100% duty.');
   if(!byId('fcExclusive')?.checked)
    throw Error('Confirm you can observe the fan and no other control software is active.');
   const guards=[byId('fcGuardPrimary')?.value,byId('fcGuardSecondary')?.value]
     .filter(Boolean).filter((x,i,a)=>a.indexOf(x)===i);
   const limit=Number(byId('fcGuardLimit')?.value||85);
   if(!identifying){
    if(!guards.length||guards.some(id=>!S.sensors.some(x=>x.id===id&&
      x.type==='Temperature')))
     throw Error('Select at least one real CPU/GPU temperature guard for low-duty calibration.');
    if(!Number.isFinite(limit)||limit<35||limit>100)
     throw Error('Choose a temperature limit between 35 and 100°C.');
    for(const id of guards){
     const sensor=S.sensors.find(x=>x.id===id);
     const age=Date.now()-Date.parse(sensor?.observedAt||'');
     if(!Number.isFinite(sensor?.value)||!Number.isFinite(age)||age>4000||
        age< -4000||sensor.value>=limit)
      throw Error('Guard temperatures must be fresh and below the configured limit.');
    }
   }
   const label=identifying?'verify the fan/RPM pairing':'run the full fan speed calibration';
   if(!confirm('Start '+label+' on '+(map.label||channel.name)+
      '? SensorDash will change fan speed and monitor temperatures. '+
      'You can cancel, and the prior fan control will be restored.'))return;
   calibrationNotice='';
   pendingCalibration={started:Date.now(),mode:identifying?'identify':'response'};
   if(byId('fcCalibrationBanner')){
    byId('fcCalibrationBanner').dataset.status='pending';
    byId('fcCalibrationHeading').textContent='Requesting Windows approval';
    byId('fcAutoStatus').textContent='Submitting the request to Windows. Calibration will begin only after the fan-control service accepts it.';
    byId('fcAutoProgress').textContent='No measurements recorded yet';
   }
   try{
    await command({action:'calibrate',channelId:selected,percent:100,
      durationSeconds:identifying?300:1500,calibrationMode:identifying?'identify':'response',
      temperatureSensorIds:guards,temperatureLimit:limit,
      confirmExclusiveOwnership:true});
   }catch(error){
    pendingCalibration=null;
    throw error;
   }
   if(byId('fcCalibrationHeading'))
     byId('fcCalibrationHeading').textContent='Waiting for Windows approval';
   if(byId('fcAutoStatus'))
     byId('fcAutoStatus').textContent=
       'Windows elevation requested. Awaiting the collector to confirm calibration started.';
   return;
  }
  if(action==='cancel-auto'){
   await command({action:'cancel-calibration',channelId:selected,percent:100,
     durationSeconds:30});
   h.toast('Approve elevation to cancel and restore the prior controller.');
   return;
  }
  if(action==='save-auto'){
   const result=S.fanControl?.persistent?.calibration;
   if(!result||result.channelId!==selected||result.state!=='complete'||
      !map||map.kind!=='fan'||result.rpmSensorId!==map.rpmSensorId)
    throw Error('Only a completed calibration of this fan and RPM sensor can be saved.');
   if(result.mode==='identify'&&!byId('fcVisual')?.checked)
    throw Error('Visually confirm that the selected output drives these fans before verifying the pairing.');
   if(result.mode==='response'&&(!map.verifiedAt||!map.visuallyIdentified))
    throw Error('A verified physical pairing is required before saving a full response.');
   const sample=result.measurements||[];
   if(!qualifyResponse({...map,visuallyIdentified:true,measurements:sample}))
    throw Error('Measured RPM response is inconsistent; previous calibration was preserved.');
   const draft=structuredClone(S.settings);
   const entry=draft.fanChannelMappings.find(m=>m.channelId===selected);
   if(result.mode==='identify')entry.visuallyIdentified=true;
   entry.measurements=sample;
   entry.verifiedAt=new Date().toISOString();
   return persist(S,draft,h,result.mode==='identify'?
     'PWM/RPM pairing verified. A full low-duty sweep can now be performed before assigning a temperature curve.':
     'Full fan PWM/RPM response saved to this fan output; its live fan profile can now show estimated RPM.');
  }
  if(action==='override'){
   if(!map||map.kind!=='fan'||!activeProfile(S,selected))
    throw Error('An approved fan profile is required for temporary override.');
   if(!byId('fcExclusive')?.checked)
    throw Error('Confirm exclusive ownership and that this output drives fans.');
   const percent=Number(byId('fcOverrideDuty')?.value);
   if(!Number.isFinite(percent)||percent<10||percent>100)
    throw Error('Choose a supported PWM percentage.');
   await command({action:'override',channelId:selected,percent,
      durationSeconds:300,confirmExclusiveOwnership:true});
   h.toast('Approve elevation to apply this temporary 5-minute fan override.');
   return;
  }
  if(action==='reset-override'){
   await command({action:'reset-override',channelId:selected,percent:100,
      durationSeconds:30});
   h.toast('Approve elevation to restore the automatic fan curve.');
   return;
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
   const override=(S.fanControl?.persistent?.overrides||[])
     .find(x=>x.channelId===selected && Date.parse(x.expires)>Date.now());
   const manual=control.state==='active'&&control.mode==='manual'&&
     control.channelId===selected&&[70,85,100].includes(Math.round(control.target))&&
     Math.abs(control.target-Math.round(control.target))<=0.1&&
     Date.parse(control.expires)>Date.now();
   if(!manual&&!override)
    throw Error('A currently authorized manual test or active profile override is required to capture RPM.');
   const percent=manual?Math.round(control.target):Math.round(override.percent);
   const key=manual?control.channelId+'|'+control.target+'|'+control.expires:
     override.channelId+'|'+override.percent+'|'+override.expires;
   if(key!==seenKey){seenKey=key;activeSince=Date.now()}
   if(Date.now()-activeSince<5000)
    throw Error('Let the fan settle for at least 5 seconds while this test is active.');
   const pwm=S.sensors.find(s=>s.id===selected && s.type==='Control');
   const pwmTime=Date.parse(pwm?.observedAt||'');
   if(!Number.isFinite(pwm?.value)||Math.abs(pwm.value-percent)>4||
      !Number.isFinite(pwmTime)||Math.abs(Date.now()-pwmTime)>6000)
    throw Error('Fresh PWM register feedback must match the active test duty.');
   const reading=S.sensors.find(s=>s.id===map.rpmSensorId);
   const observed=Date.parse(reading?.observedAt||S.frame?.timestamp||'');
   if(reading?.type!=='Fan'||!Number.isFinite(reading.value)||reading.value<=0||
      !Number.isFinite(observed)||Math.abs(Date.now()-observed)>6000)
    throw Error('A fresh, positive RPM reading is required. Confirm the tachometer sensor.');
   const draft=structuredClone(S.settings);
   const entry=draft.fanChannelMappings.find(m=>m.channelId===selected);
   entry.measurements=(entry.measurements||[]).filter(p=>p.dutyPercent!==percent);
   entry.measurements.push({dutyPercent:percent,rpm:reading.value,
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
   .catch(error=>{
    if(['start-auto','start-identify','cancel-auto','save-auto'].includes(action.dataset.commissioning)){
     pendingCalibration=null;
     calibrationNotice=error.message;
     const banner=byId('fcCalibrationBanner');
     if(banner)banner.dataset.status='error';
     if(byId('fcCalibrationHeading'))byId('fcCalibrationHeading').textContent='Calibration did not start';
     if(byId('fcAutoStatus'))byId('fcAutoStatus').textContent=error.message;
    }else h.toast(error.message);
   });
  return true;
 };
 const change=(e,S,h)=>{
  if(e.target.id==='fcOverrideDuty'){
   if(byId('fcOverrideValue'))byId('fcOverrideValue').textContent=e.target.value+'%';
   return true;
  }
  if(['fcGuardPrimary','fcGuardSecondary','fcGuardLimit'].includes(e.target.id)){
   calibrationGuards.set(selected,{
     first:byId('fcGuardPrimary')?.value||'',
     second:byId('fcGuardSecondary')?.value||'',
     limit:byId('fcGuardLimit')?.value||'85'
   });
   return true;
  }
  if(e.target.id!=='fcChannel')return false;
  selected=e.target.value;seenKey='';activeSince=0;h.render();
  return true;
 };
 return {render,paint,handle,change,qualifyResponse};
})();
