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
const recorder=read('src/SensorDash/CoolingEventRecorder.cs');
const program=read('src/SensorDash/Program.cs');
const html=read('src/SensorDash/wwwroot/index.html');
const modules=vm.runInNewContext(src+'\nCoolingInsights;',{Date,console,SDWorkspace:{isHidden:()=>false}});
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
assert.ok(!overview.includes('Room ambient'),'Uninstalled air sensors must not consume dashboard space');
assert.ok(!overview.includes('Unrelated hardware'));
assert.ok(overview.includes('32 °C'),'Case air shows observed value, not an invented ambient');
assert.ok(overview.includes('Case air sensor'));
assert.ok(!overview.includes('cooling efficiency'));
const nextSensors=[
 {...sensor('water','Temperature',24,'Water Temperature'),device:'high flow NEXT'},
 {...sensor('flow','Flow',225.8,'Flow'),device:'high flow NEXT'},
 {...sensor('cond','Conductivity',19.6,'Conductivity'),device:'high flow NEXT'},
 {...sensor('quality','Level',100,'Water Quality'),device:'high flow NEXT'},
 {...sensor('ext','Temperature',null,'External Temperature'),device:'high flow NEXT'},
 {...sensor('volt','Voltage',4.7,'VCC'),device:'high flow NEXT'}
];
sys.components.push({id:'water-probe',kind:'coolant-probe',name:'Coolant',sensorIds:['water']});
sys.components.push({id:'flow-meter',kind:'flow-meter',name:'Flow meter',sensorIds:['flow']});
S.sensors=[...values,...nextSensors];
S.byId=new Map(S.sensors.map(s=>[s.id,s]));
const withNext=modules.overview(S,sys);
for(const text of ['225.8 L/h','24 °C','19.6 µS/cm','Coolant conductivity','Flow rate'])
 assert.ok(withNext.includes(text),'NEXT loop summary missing '+text);
assert.equal((withNext.match(/data-live-reading="water"/g)||[]).length,1,
 'Assigned coolant probe and USB discovery must not duplicate the same sensor');
assert.equal((withNext.match(/data-live-reading="flow"/g)||[]).length,1,
 'Assigned flow meter and USB discovery must not duplicate the same sensor');
assert.ok(!withNext.includes('4.7 V'),'NEXT voltage stays on advanced diagnostics');
assert.ok(!withNext.includes('100 %'),'Derived water quality is not a second independent chemistry tile');
assert.ok(!withNext.includes('External Temperature'),'Unavailable external thermistor not treated as live ambient');


for(const role of ['coolant-probe','case-air','room-ambient'])
 assert.ok(model.includes('"'+role+'"'),role+' must round-trip through server settings');
assert.ok(workspace.includes('CoolingInsights.overview(S,sys)'));
assert.ok(!workspace.includes('CoolingInsights.activity(S,sys)'),'Historical cooling-event timelines are now on the Pi');
assert.ok(!workspace.includes('h.chart(limit,unit(type),false,sys.id)'),'Historical cooling charts now belong to Grafana');
assert.ok(!app.includes('data-cooling-timeline')&&!app.includes('CoolingTimeline.hover(canvas,desired)'));
assert.ok(!app.includes('CoolingInsights.paint(S)')&&!app.includes('CoolingInsights.paintEvents(S)'));
assert.ok(program.includes('CoolingEventRecorder.Describe(command)')&&program.includes('db.Event'),
 'The service-hosted API must audit accepted fan-control requests');
assert.ok(recorder.includes('collector application pending'));
assert.ok(program.includes('CoolingEventRecorderSelfTest.Run()'));
assert.ok(program.includes('WebApplication.CreateSlimBuilder')&&program.includes('AddWindowsService'),
 'The minimal API host must run inside the Windows service');
assert.ok(program.includes('/api/tray-state')&&read('tray/SensorDashTray.cpp').includes('WinHttpOpenRequest'),
 'Native tray must read the service status endpoint instead of duplicating collector IPC reads');
for(const url of ['cooling-insights.js'])
 assert.ok(html.includes('/'+url)&&program.includes('/'+url));
console.log('PASS: local role-aware cooling without historical duplication');
