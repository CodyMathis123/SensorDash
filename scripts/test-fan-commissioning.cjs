// No browser or hardware needed: test guided identification and UAC gating.
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
  return {ok:true,json:async()=>({})};},
 confirm:()=>true,
 structuredClone,
 console,
 Date
};
const controller=vm.runInNewContext(source+'\nFanCommissioning;',scope);
scope.SDWorkspace={saveSettings:async(S,draft)=>{
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
 let html=controller.render(S);
 assert.match(html,/Identify and test your fans/);
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
  workspace.includes("'calibration','Fan control & calibration'") &&
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
 assert.equal(requests[1].payload.durationSeconds,240);
 // Once the high-duty response is physically verified, a guarded full
 // calibration may run even without a configured or approved profile.
 S.settings.fanChannelMappings[0].visuallyIdentified=true;
 S.settings.fanChannelMappings[0].verifiedAt=new Date().toISOString();
 S.settings.fanChannelMappings[0].measurements=[
  {dutyPercent:70,rpm:700},{dutyPercent:85,rpm:1050},{dutyPercent:100,rpm:1600}];
 await click('start-auto');
 assert.equal(requests.length,3);
 assert.equal(requests[2].payload.calibrationMode,'response');
 assert.equal(requests[2].payload.temperatureLimit,85);
 assert.deepEqual(Array.from(requests[2].payload.temperatureSensorIds),['temp1']);
 assert.equal(requests[2].payload.confirmExclusiveOwnership,true);
 const enabled=controller.render(S);
 assert.match(enabled,/Auto-identify PWM\/RPM pairing/);
 assert.match(enabled,/Calibrate full PWM\/RPM response/);
 assert.match(enabled,/Primary thermal guard/);
 assert.match(enabled,/PWM register/);
 assert.match(enabled,/before or after a temperature curve/);
 console.log('PASS: calibration before curves, elevated pairing verification, guarded response sweep, pump exclusion and PWM diagnostics');
})().catch(e=>{console.error(e);process.exitCode=1});
