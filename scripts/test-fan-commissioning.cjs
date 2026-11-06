// No browser or hardware needed: test guided identification and local browser confirmations.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.join(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const source=read('src/SensorDash/wwwroot/fan-commissioning.js');
const model=read('src/SensorDash/FanCommissioningModels.cs');
const workspace=read('src/SensorDash/wwwroot/workspace.js');
const page=read('src/SensorDash/wwwroot/index.html');
const app=read('src/SensorDash/wwwroot/app.js');
const backend=read('src/SensorDash/Program.cs');
const requests=[];
const nodes=new Map([
 ['fcExclusive',{checked:false}],
 ['fcLabel',{value:'Right radiator bank'}],
 ['fcKind',{value:'fan'}],
 ['fcRpm',{value:'tach1'}],
 ['fcVisual',{checked:false}],
 ['fcGuardPrimary',{value:'temp1'}],
 ['fcGuardSecondary',{value:''}],
 ['fcGuardLimit',{value:'85'}]
]);
const scope={
 WorkspaceLocks:{fan:()=>false},
 document:{getElementById:id=>nodes.get(id)||null},
 fetch:async(url,opts)=>{requests.push({url,payload:JSON.parse(opts.body)});
  return {ok:true,json:async()=>url==='/api/settings'?JSON.parse(opts.body):{}};},
 confirm:()=>true,
 structuredClone,
 console,
 Date
};
const controller=vm.runInNewContext(source+'\nFanCommissioning;',scope);
scope.SDWorkspace={isHidden:(S,s)=>(S.settings?.hiddenSensorIds||[]).includes(s.id)||
  (S.settings?.hiddenDeviceIds||[]).includes(s.deviceId)||
  (S.settings?.hiddenHardwareTypes||[]).includes(s.deviceType),saveSettings:async(S,draft)=>{
 const r=await scope.fetch('/api/settings',{body:JSON.stringify(draft)});
 S.settings=await r.json();return S.settings;
}};
const S={
 view:'curves',
 frame:{timestamp:new Date().toISOString()},
 sensors:[{id:'tach1',type:'Fan',name:'Radiator tach',device:'Mainboard',value:1100,
  observedAt:new Date().toISOString()},
  {id:'temp1',type:'Temperature',name:'CPU package',device:'CPU',value:38,
   observedAt:new Date().toISOString()}],
 settings:{fanChannelMappings:[{channelId:'channel1',label:'Right radiator bank',
  kind:'pump',rpmSensorId:'tach1',measurements:[],visuallyIdentified:false,verifiedAt:null}]},
 fanControl:{outputs:[{id:'channel1',name:'PWM 1',device:'Mainboard',
  mode:'Undefined',minimum:0,maximum:100,writable:true}],
 status:{state:'off'},persistent:{profiles:[]}}
};
const h={render(){},toast(s){messages.push(s)}};
const messages=[];
async function click(action,duty){
 const target={dataset:{commissioning:action,duty:String(duty)},closest:key=>
   key==='[data-commissioning]'?target:null};
 assert.equal(controller.handle({target,preventDefault(){}},S,h),true);
 await new Promise(setImmediate);
}
(async()=>{
 S.sensors.push({id:'tachHidden',type:'Fan',name:'Hidden RPM',device:'Mainboard',deviceId:'hiddenDevice',value:400},
  {id:'tempHidden',type:'Temperature',name:'Hidden temperature',device:'CPU',deviceId:'hiddenDevice',value:44});
 S.settings.hiddenDeviceIds=['hiddenDevice'];
 S.sensors.push({id:'channelHidden',type:'Control',name:'Hidden PWM',device:'Mainboard',deviceId:'hiddenDevice',value:0});
 S.fanControl.outputs.push({id:'channelHidden',name:'Hidden PWM',device:'Mainboard',mode:'Undefined',minimum:0,maximum:100,writable:true});
 let html=controller.render(S);
 assert.doesNotMatch(html,/<option value="channelHidden"/,
  'Hidden physical PWM outputs must not appear in calibration choices');
 S.settings.fanChannelMappings.push({channelId:'channelHidden',label:'Previously configured',kind:'fan',rpmSensorId:'tach1'});
 html=controller.render(S);
 assert.match(html,/<option value="channelHidden"[^>]*>[^<]*\[Hidden\]/,
  'Previously configured hidden PWM output must remain identifiable');
 S.settings.fanChannelMappings.pop();
 assert.doesNotMatch(html,/value="tachHidden"|value="tempHidden"/,
  'Hidden device sensors must be excluded from new calibration selections');
 S.settings.fanChannelMappings[0].rpmSensorId='tachHidden';
 html=controller.render(S);
 assert.match(html,/<option value="tachHidden" selected>[^<]*\[Hidden\]/,
  'A configured hidden RPM sensor must remain visible in its existing assignment');
 S.settings.fanChannelMappings[0].rpmSensorId='tach1';
 S.settings.simulatedFanCurves=[{id:'hiddenCurve',sourceSensorId:'tempHidden'}];
 S.settings.fanProfiles=[{channelIds:['channel1'],curveIds:['hiddenCurve']}];
 html=controller.render(S);
 assert.match(html,/<option value="tempHidden" selected>[^<]*\[Hidden\]/,
  'An already assigned hidden guard remains identified and selectable');
 S.settings.simulatedFanCurves=[];S.settings.fanProfiles=[];
 const historicalSampleAt='2025-01-02T15:04:00Z';
 S.settings.fanChannelMappings[0].measurements=[{dutyPercent:100,rpm:1800,observedAt:historicalSampleAt}];
 html=controller.render(S);
 assert.match(html,/Jan 2, 2025|2 Jan 2025|2025[^<]*Jan 2/,
  'Saved calibration dates must display a year as well as a time');
 S.settings.fanChannelMappings[0].measurements=[];
 assert.match(html,/Identify the physical fan and tachometer/);
 assert.match(html,/Pump — never test here/);
 assert.match(html,/Test 100%/);
 assert.match(html,/disabled/,'Pump mapping must disable hardware testing');
 await click('test',100);
 assert.equal(requests.length,0,'Pump mapping must never send a PWM command');
 S.settings.fanChannelMappings[0].kind='fan';
 html=controller.render(S);
 assert.match(html,/30-second test/);
 await click('test',100);
 assert.equal(requests.length,0,'A new test requires explicit ownership acknowledgment');
 nodes.get('fcExclusive').checked=true;
 await click('test',100);
 assert.equal(requests.length,1);
 assert.equal(requests[0].url,'/api/fan-control/authorize');
 assert.deepEqual(Object.assign({},requests[0].payload),{
  action:'arm',channelId:'channel1',curveId:'',percent:100,
  durationSeconds:30,confirmExclusiveOwnership:true
 });
 assert.ok(!source.includes('percent:30') && source.includes('const dutyValues=[100,85,70]'));
 assert.equal(controller.qualifyResponse({
  kind:'fan',visuallyIdentified:true,rpmSensorId:'tach1',
  measurements:[{dutyPercent:70,rpm:600},{dutyPercent:100,rpm:1600}]}),true);
 assert.equal(controller.qualifyResponse({
  kind:'fan',visuallyIdentified:true,rpmSensorId:'tach1',
  measurements:[{dutyPercent:70,rpm:1000},{dutyPercent:100,rpm:1030}]}),false);
 assert.equal(controller.qualifyResponse({
  kind:'fan',visuallyIdentified:true,rpmSensorId:'tach1',
  measurements:[{dutyPercent:70,rpm:700},{dutyPercent:85,rpm:2000},{dutyPercent:100,rpm:1500}]}),false,
  'A contradictory middle RPM sample must block verification');
 assert.equal(controller.qualifyResponse({
  kind:'pump',visuallyIdentified:true,rpmSensorId:'tach1',
  measurements:[{dutyPercent:70,rpm:600},{dutyPercent:100,rpm:1600}]}),false);
 assert.ok(source.includes("control.state==='active'") &&
  source.includes("control.channelId===selected") &&
  source.includes("Date.now()-activeSince<5000"),
  'RPM sampling must require current matching active control plus stabilization');
 assert.ok(source.includes('Math.abs(Date.now()-observed)>6000'),
  'Reject stale RPM readings');
 assert.ok(model.includes('m.Kind=="fan"') &&
  model.includes('p.DutyPercent is >=10 and <=100') &&
  model.includes('m.VerifiedAt=null'),
  'Server-side normalization must refuse pump data, unsafe points and invalid verification');
 assert.ok(workspace.includes('FanCommissioning.render(S)') &&
  workspace.includes("'calibration','Hardware setup & calibration'") &&
  !workspace.includes("FanControlUx.section(S,'manual'") &&
  !workspace.includes("FanControlUx.section(S,'quick'") &&
  workspace.includes("mapping(o.id)?.label") &&
  read('src/SensorDash/FanControlRuntime.cs').includes('Channel identified as a pump; manual session stopped.') &&
  app.includes('FanCommissioning.handle(e,S') &&
  app.includes('FanCommissioning.paint(S)') &&
  page.includes('/fan-commissioning.js') &&
  backend.includes('FanCommissioningSelfTest.Run()'),
  'Commissioning must integrate into the existing dashboard and executable self-test');
 // Fan/RPM identification must be available before a curve exists.
 S.fanControl.persistent.profiles=[];
 S.settings.fanChannelMappings[0].visuallyIdentified=false;
 nodes.get('fcExclusive').checked=false;
 await click('start-identify');
 assert.equal(requests.length,1,'Identification still requires ownership acknowledgment');
 nodes.get('fcExclusive').checked=true;
 await click('start-identify');
 assert.equal(requests.length,2);
 assert.equal(requests[1].payload.action,'calibrate');
 assert.equal(requests[1].payload.calibrationMode,'identify');
 assert.equal(requests[1].payload.channelId,'channel1');
 assert.equal(requests[1].payload.confirmExclusiveOwnership,true);
 assert.equal(requests[1].payload.durationSeconds,300);
 // Once the high-duty response is physically verified, a guarded full
 // calibration may run even without a configured or approved profile.
 S.settings.fanChannelMappings[0].visuallyIdentified=true;
 S.settings.fanChannelMappings[0].verifiedAt=new Date().toISOString();
 S.settings.fanChannelMappings[0].measurements=[
  {dutyPercent:70,rpm:700},{dutyPercent:85,rpm:1050},{dutyPercent:100,rpm:1600}];
 await click('start-auto');
 assert.equal(requests.length,3);
 assert.equal(requests[2].payload.calibrationMode,'response');
 assert.equal(requests[2].payload.durationSeconds,1500,
  'Full calibration must allow the 19-point stable sweep to finish');
 assert.equal(requests[2].payload.temperatureLimit,85);
 assert.deepEqual(Array.from(requests[2].payload.temperatureSensorIds),['temp1']);
 assert.equal(requests[2].payload.confirmExclusiveOwnership,true);
 const enabled=controller.render(S);
 assert.match(enabled,/Verify fan and RPM pairing/);
 assert.match(enabled,/Run full calibration/);
 assert.match(enabled,/19 speeds/);
 assert.match(enabled,/5% steps/);
 assert.match(enabled,/Primary thermal guard/);
 assert.match(enabled,/PWM register/);
 assert.match(enabled,/Calibration belongs to the physical PWM header/i);
 assert.match(enabled,/Saved calibration/);
 assert.match(enabled,/70–100% PWM/);
 assert.match(enabled,/3 running-speed points/);
 const sampleAt=new Date().toISOString();
 S.fanControl.persistent.calibration={
  channelId:'channel1',rpmSensorId:'tach1',mode:'response',
  state:'complete',completedSteps:3,totalSteps:3,currentDuty:70,
  message:'Completed calibration',measurements:[
   {dutyPercent:70,rpm:700,observedAt:sampleAt,
    temperatures:[{sensorId:'temp1',sensorName:'CPU package',celsius:38.5,observedAt:sampleAt}]},
   {dutyPercent:85,rpm:1050,observedAt:sampleAt,
    temperatures:[{sensorId:'temp1',sensorName:'CPU package',celsius:39.0,observedAt:sampleAt}]},
   {dutyPercent:100,rpm:1600,observedAt:sampleAt,
    temperatures:[{sensorId:'temp1',sensorName:'CPU package',celsius:40.0,observedAt:sampleAt}]}
  ]
 };
 const during=controller.render(S);
 assert.match(during,/Current run · 3 settled running speeds/);
 assert.match(during,/CPU package: 38.5°C/);
 await click('save-auto');
 const saved=requests.at(-1);
 assert.equal(saved.url,'/api/settings');
 assert.equal(saved.payload.fanChannelMappings[0].measurements.length,3);
 assert.equal(saved.payload.fanChannelMappings[0].measurements[0].temperatures[0].celsius,38.5);
 S.fanControl.persistent.calibration=null;
 const afterRestart=controller.render(S);
 assert.match(afterRestart,/Saved calibration/);
 assert.match(afterRestart,/CPU package: 38.5°C/,
  'Saved RPM and guard-temperature observations must remain visible after session ends');
 assert.doesNotMatch(afterRestart,/Current run · 3 settled running speeds/);
 S.settings.aliases={temp1:'My CPU Guard'};
 let aliased=controller.render(S);
 assert.match(aliased,/My CPU Guard: 38.5°C/,
  'Stored calibration points should respect current sensor aliases even if renamed later');
 assert.doesNotMatch(aliased,/CPU package: 38.5°C/,
  'Do not show the unaliased hardware sensor name in saved measurement rows');
 assert.match(aliased,/My CPU Guard<\/option>/,
  'Guard temperature dropdown should show the configured sensor alias');

 // A running calibration on fan A blocks fan B with a clear explanation,
 // not just a mysterious disabled button. The progress remains visible.
 S.fanControl.outputs.push({id:'channel2',name:'NVMe PWM',device:'Mainboard',
  mode:'Undefined',minimum:0,maximum:100,writable:true});
 S.settings.fanChannelMappings.push({channelId:'channel2',label:'NVMe Fan',
  kind:'fan',rpmSensorId:'tach2',visuallyIdentified:true,
  verifiedAt:new Date().toISOString(),measurements:[
   {dutyPercent:70,rpm:900},{dutyPercent:100,rpm:1700}]});
 S.sensors.push({id:'tach2',type:'Fan',name:'NVMe tach',device:'Mainboard',
  value:1200,observedAt:new Date().toISOString()});
 S.fanControl.persistent.calibration={
  channelId:'channel1',rpmSensorId:'tach1',mode:'response',
  state:'running',completedSteps:12,totalSteps:19,currentDuty:40,
  message:'Settling at 40%',measurements:[]
 };
 const onOwner=controller.render(S);
 assert.match(onOwner,/Calibration in progress/);
 assert.match(onOwner,/12 of 19 stable speeds measured/);
 assert.match(onOwner,/Cancel and restore control/);
 assert.equal(controller.change({target:{id:'fcChannel',value:'channel2'}},S,h),true);
 const onOther=controller.render(S);
 assert.match(onOther,/Another calibration is running/);
 assert.match(onOther,/Right radiator bank is currently being calibrated/);
 assert.match(onOther,/Calibration unavailable for this fan/);
 assert.match(onOther,/data-commissioning="start-auto" disabled title=/);
 assert.match(onOther,/data-commissioning="start-identify" disabled title=/);
 assert.match(onOther,/data-commissioning="view-active-calibration"/);
 assert.match(onOther,/12 of 19 stable speeds measured/);
 assert.match(onOther,/40% PWM/);
 const requestCount=requests.length;
 await click('start-auto');
 assert.equal(requests.length,requestCount,
  'Other fan calibration must be blocked before requesting service control');
 await click('view-active-calibration');
 const returned=controller.render(S);
 assert.match(returned,/Cancel and restore control/,
  'View active calibration should select the running fan');
 assert.doesNotMatch(returned,/Calibration unavailable for this fan/);
 S.fanControl.persistent.calibration=null;
 assert.doesNotMatch(controller.render(S),/id="fcGlobalCalibration"/,
  'Global progress indicator should disappear after calibration ends');
 // The NVMe fan can stop at 10% after 18 stable positive readings.
 // Saving the usable points and an observed 0 RPM cutoff is explicitly
 // allowed, and the 0 must not enter positive RPM interpolation.
 const cutoffAt=new Date().toISOString();
 const responsePoints=Array.from({length:18},(_,i)=>{
  const dutyPercent=100-i*5;
  return {dutyPercent,rpm:350+dutyPercent*95,observedAt:cutoffAt,
   temperatures:[{sensorId:'temp1',sensorName:'CPU package',
    celsius:39,observedAt:cutoffAt}]};
 });
 S.fanControl.persistent.calibration={
  channelId:'channel1',rpmSensorId:'tach1',mode:'response',
  state:'cutoff',completedSteps:18,totalSteps:19,currentDuty:10,
  cutoff:{dutyPercent:10,observedAt:cutoffAt,temperatures:[
    {sensorId:'temp1',sensorName:'CPU package',celsius:39,observedAt:cutoffAt}]},
  message:'Observed fan stop at 10% PWM',measurements:responsePoints
 };
 const withStop=controller.render(S);
 assert.match(withStop,/Fan stop observed · results ready to save/);
 assert.match(withStop,/Save 18 RPM points \+ observed stop/);
 assert.match(withStop,/10% PWM · observed stop/);
 assert.match(withStop,/0 RPM \(confirmed\)/);
 assert.match(withStop,/lowest measured running duty is 15%/i);
 const beforeSave=requests.length;
 await click('save-auto');
 assert.equal(requests.length,beforeSave+1,
  'Saving an observed fan cutoff persists calibration without any fan write');
 const cutoffSaved=requests.at(-1);
 assert.equal(cutoffSaved.url,'/api/settings');
 assert.equal(cutoffSaved.payload.fanChannelMappings[0].measurements.length,18);
 assert.equal(cutoffSaved.payload.fanChannelMappings[0].cutoff.dutyPercent,10);
 assert.equal(cutoffSaved.payload.fanChannelMappings[0].measurements.every(p=>p.rpm>0),true,
  'Observed zero RPM must remain separate from running-speed calibration');
 S.fanControl.persistent.calibration=null;
 const persisted=controller.render(S);
 assert.match(persisted,/Fan stop observed at 10%/);
 assert.match(persisted,/18 running-speed points/);
 assert.match(persisted,/10% PWM · observed stop/);
 assert.match(persisted,/A restart threshold was not measured/);

 // Other safety aborts may retain earlier valid measurements. Allow an
 // explicitly confirmed partial save but do not invent a fan-stop duty.
 S.fanControl.persistent.calibration={
  channelId:'channel1',rpmSensorId:'tach1',mode:'response',
  state:'aborted',completedSteps:18,totalSteps:19,currentDuty:10,
  message:'Missing guard data; previous control restored',
  measurements:responsePoints
 };
 assert.match(controller.render(S),/Save 18 valid RPM points \(partial\)/);
 await click('save-auto');
 assert.equal(requests.at(-1).payload.fanChannelMappings[0].cutoff,null);
 assert.equal(requests.at(-1).payload.fanChannelMappings[0].measurements.length,18);
 console.log('PASS: confirmed low-duty fan stop is persistent diagnostic data, not discarded calibration');
 console.log('PASS: single active fan calibration status, alias-aware guards and saved RPM history');
})().catch(e=>{console.error(e);process.exitCode=1});
