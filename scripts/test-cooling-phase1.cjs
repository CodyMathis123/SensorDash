// Deterministic, hardware-free tests for Cooling Systems phase 1.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.join(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const src=read('src/SensorDash/wwwroot/cooling-insights.js');
const app=read('src/SensorDash/wwwroot/app.js');
const workspace=read('src/SensorDash/wwwroot/workspace.js');
const model=read('src/SensorDash/WorkspaceModels.cs');
const admin=read('src/SensorDash/AdminFanControl.cs');
const recorder=read('src/SensorDash/CoolingEventRecorder.cs');
const program=read('src/SensorDash/Program.cs');
const html=read('src/SensorDash/wwwroot/index.html');
const modules=vm.runInNewContext(src+'\nCoolingInsights;',{Date,console});
assert.deepEqual(Array.from(modules.roles.map(r=>r.id)),['coolant-probe','case-air','room-ambient']);
const now=Date.now();
const sensor=(id,type,value,name=id)=>({id,type,value,name,observedAt:new Date(now).toISOString()});
const values=[
 sensor('cpuT','Temperature',40,'CPU Core'),sensor('cpuP','Power',35,'CPU Watts'),
 sensor('gpuT','Temperature',30,'GPU Core'),sensor('gpuP','Power',12,'GPU Watts'),
 sensor('rad','Fan',489,'Radiator tach'),sensor('pump','Fan',4412,'Pump tach'),
 sensor('caseT','Temperature',32,'Case air sensor'),
 sensor('extra','Temperature',55,'Unrelated hardware')
];
const components=[
 {id:'cpu',kind:'heat-source',name:'CPU Heat Source',sensorIds:['cpuT','cpuP']},
 {id:'gpu',kind:'heat-source',name:'GPU Heat Source',sensorIds:['gpuT','gpuP']},
 {id:'fans',kind:'fan-group',name:'Radiator fans',sensorIds:['rad']},
 {id:'pump',kind:'pump',name:'Pump',sensorIds:['pump']},
 {id:'case',kind:'case-air',name:'Case sensor',sensorIds:['caseT']}
];
const sys={id:'loop1',name:'Liquid loop',kind:'liquid',components};
const S={view:'cooling',settings:{
 coolingSystems:[sys],fanChannelMappings:[{channelId:'control1',rpmSensorId:'rad',kind:'fan'}],
 fanProfiles:[{id:'radiators',name:'Radiators',channelIds:['control1']}]
},sensors:values,byId:new Map(values.map(s=>[s.id,s])),hours:1,history:{},events:[],
 fanControl:{persistent:{profiles:[{id:'radiators',channelIds:['control1']}]}}};
const overview=modules.overview(S,sys);
for(const phrase of ['CPU Heat Source','GPU Heat Source','Radiator fans','Pump','Case air sensor','Case air'])
 assert.ok(overview.includes(phrase),'Overview missing '+phrase);
assert.match(overview,/Coolant[\s\S]*Not measured/);
assert.match(overview,/Room ambient[\s\S]*Not measured/);
assert.ok(!overview.includes('Unrelated hardware'));
assert.ok(overview.includes('32 °C'),'Case air shows observed value, not an invented ambient');
assert.ok(overview.includes('Case air sensor'));
assert.ok(!overview.includes('cooling efficiency'));

for(const role of ['coolant-probe','case-air','room-ambient'])
 assert.ok(model.includes('"'+role+'"'),role+' must round-trip through server settings');
assert.ok(workspace.includes('CoolingInsights.overview(S,sys)'));
assert.ok(!workspace.includes('CoolingInsights.activity(S,sys)'),'Historical cooling-event timelines are now on the Pi');
assert.ok(!workspace.includes('h.chart(limit,unit(type),false,sys.id)'),'Historical cooling charts now belong to Grafana');
assert.ok(!app.includes('data-cooling-timeline')&&!app.includes('CoolingTimeline.hover(canvas,desired)'));
assert.ok(!app.includes('CoolingInsights.paint(S)')&&!app.includes('CoolingInsights.paintEvents(S)'));
assert.ok(admin.includes('CoolingEventRecorder.Describe(command)')&&admin.includes('db.Event'));
assert.ok(recorder.includes('collector application pending'));
assert.ok(program.includes('CoolingEventRecorderSelfTest.Run()'));
assert.ok(program.includes('WebApplication.CreateSlimBuilder'),'Desktop minimal API host should use slim builder');
assert.ok(read('src/SensorDash/ServiceBridge.cs').includes('var frame = _display.Latest;'),
 'Tray should reuse the bridge snapshot rather than duplicating collector IPC reads');
for(const url of ['cooling-insights.js'])
 assert.ok(html.includes('/'+url)&&program.includes('/'+url));
console.log('PASS: local role-aware cooling without historical duplication');
