// Deterministic, hardware-free tests for Cooling Systems phase 1.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.join(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const src=read('src/SensorDash/wwwroot/cooling-insights.js');
const timeline=read('src/SensorDash/wwwroot/cooling-timeline.js');
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

// Pair samples only when clocks are close. Ignore unmatched data and missing history.
const x=[],y=[];
for(let i=0;i<30;i++){
 x.push({t:now-600000+i*10000,v:25+i});
 y.push({t:now-599000+i*10000,v:30+i*.8});
}
assert.equal(modules.pair(x,y).length,30);
assert.equal(modules.pair(x,y.map(v=>({...v,t:v.t+1000000}))).length,0);
const r=modules.correlation(x,y);
assert.ok(r&&r.samples===30&&r.r>0.99);
assert.equal(modules.correlation(x.slice(0,5),y.slice(0,5)),null);
assert.equal(modules.correlation(x,x.map(p=>({...p,v:20}))),null);
S.history={cpuP:x,cpuT:y};
assert.match(modules.analysis(S,sys),/r = 1.00/);
assert.match(modules.analysis(S,sys),/GPU Heat Source[\s\S]*Waiting for at least 12 paired samples/);
S.history.gpuP=x.map(p=>({...p,v:8}));S.history.gpuT=y.map(p=>({...p,v:30}));
assert.match(modules.analysis(S,sys),/GPU Heat Source[\s\S]*one series is constant/);
const narrow=x.map((p,i)=>({...p,v:22+i*0.001}));
assert.ok(modules.correlation(x,narrow).lowVariation);
assert.ok(modules.correlation(x,narrow).r>0.99,'Small real variation must retain a valid correlation');
S.history.gpuT=narrow;S.history.gpuP=x;
assert.match(modules.analysis(S,sys),/GPU Heat Source[\s\S]*r = 1.00[\s\S]*Low variation/);
S.settings.aliases={gpuP:'GPU board power',gpuT:'GPU hot spot',pump:'Loop pump'};
assert.match(modules.analysis(S,sys),/GPU board power ↔ GPU hot spot/);
assert.match(modules.overview(S,sys),/Loop pump/);
assert.doesNotMatch(modules.overview(S,sys),/Pump tach/);
assert.equal(values.find(s=>s.id==='pump').name,'Pump tach','Presentation must not mutate discovered names');
assert.ok(!modules.analysis(S,sys).includes('GPU Watts ↔ GPU Core</span><b>r ='));

S.events=[
 {t:now-2000,sensor:'fan:channel:control1',message:'Manual PWM test authorized'},
 {t:now-3000,sensor:'fan:profile:radiators',message:'Fan profile approval accepted'},
 {t:now-4000,sensor:'cpuT',message:'CPU threshold crossed'},
 {t:now-5000,sensor:'fan:channel:unrelated',message:'Wrong output'},
 {t:now-6000,sensor:'extra',message:'Other system'},
 {t:now-7200000,sensor:'fan:all',message:'Old event'}
];
const relevant=modules.recentEvents(S,sys);
assert.deepEqual(Array.from(relevant.map(x=>x.message)),[
 'Manual PWM test authorized','Fan profile approval accepted','CPU threshold crossed'
]);
const eventMarkup=modules.activity(S,sys);
assert.match(eventMarkup,/data-cooling-focus="loop1"/);
assert.doesNotMatch(eventMarkup,/Wrong output|Other system|Old event/);
const other={id:'other',components:[{kind:'heat-source',sensorIds:['extra']}]};
assert.equal(modules.recentEvents(S,other).length,1);
assert.ok(!modules.activity(S,other).includes('Fan profile approval'));

// Two cooling systems cannot leak cursor updates into each other's charts.
const charts=[
 {dataset:{coolingTimeline:'loop1'}},{dataset:{coolingTimeline:'loop1'}},
 {dataset:{coolingTimeline:'other'}}
];
const seen=[[],[],[]];
const dom={querySelectorAll:()=>charts};
const timelineApi=vm.runInNewContext(timeline+'\nCoolingTimeline;',{document:dom});
charts.forEach((canvas,i)=>timelineApi.register(canvas,{
 clear:()=>seen[i].push('clear'),draw:t=>seen[i].push(t)
}));
assert.equal(timelineApi.hover(charts[0],now),true);
assert.ok(seen[0].includes(now)&&seen[1].includes(now)&&!seen[2].includes(now));
assert.equal(timelineApi.focus('other',now-3000),true);
assert.ok(seen[2].includes(now-3000));
timelineApi.reset();
const dashboardCharts=['dashboard:one','dashboard:one','dashboard:two'].map(id=>({dataset:{coolingTimeline:id}}));
charts.push(...dashboardCharts);
const dashboardDraws=[[],[],[]];
dashboardCharts.forEach((canvas,i)=>timelineApi.register(canvas,{clear:()=>dashboardDraws[i].push('clear'),draw:t=>dashboardDraws[i].push(t)}));
timelineApi.hover(dashboardCharts[0],now+123);
assert.ok(dashboardDraws[0].includes(now+123)&&dashboardDraws[1].includes(now+123));
assert.ok(!dashboardDraws[2].includes(now+123)&&!seen[0].includes(now+123),'Dashboard hover stays in its dashboard');
timelineApi.leave(dashboardCharts[0]);
assert.equal(dashboardDraws[1].at(-1),'clear');
assert.ok(!workspace.includes("false,'dashboard:'+chosen.id"),'Retired Windows dashboards must not define timeline groups');
assert.equal(timelineApi.hover({dataset:{}},now),false);

for(const role of ['coolant-probe','case-air','room-ambient'])
 assert.ok(model.includes('"'+role+'"'),role+' must round-trip through server settings');
assert.ok(workspace.includes('CoolingInsights.overview(S,sys)'));
assert.ok(!workspace.includes('CoolingInsights.activity(S,sys)'),'Historical cooling-event timelines are now on the Pi');
assert.ok(!workspace.includes('h.chart(limit,unit(type),false,sys.id)'),'Historical cooling charts now belong to Grafana');
assert.ok(app.includes('data-cooling-timeline')&&app.includes('CoolingTimeline.hover(canvas,desired)'));
assert.ok(app.includes('CoolingInsights.paint(S)')&&app.includes('CoolingInsights.paintEvents(S)'));
assert.ok(admin.includes('CoolingEventRecorder.Describe(command)')&&admin.includes('db.Event'));
assert.ok(recorder.includes('collector application pending'));
assert.ok(program.includes('CoolingEventRecorderSelfTest.Run()'));
assert.ok(program.includes('WebApplication.CreateSlimBuilder'),'Desktop minimal API host should use slim builder');
assert.ok(read('src/SensorDash/ServiceBridge.cs').includes('var frame = _display.Latest;'),
 'Tray should reuse the bridge snapshot rather than duplicating collector IPC reads');
for(const url of ['cooling-insights.js','cooling-timeline.js'])
 assert.ok(html.includes('/'+url)&&program.includes('/'+url));
console.log('PASS: role-aware cooling, optional probes, correlation math, event scoping, synchronized time cursor, and audit source');
