// Hardware-free regression checks for configurable cooling and sensor usages.
// Run with: node scripts/test-workspace.cjs
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '..');
const source = file => fs.readFileSync(path.join(root,file),'utf8');
const script=source('src/SensorDash/wwwroot/workspace.js');
const app=source('src/SensorDash/wwwroot/app.js');
const css=source('src/SensorDash/wwwroot/style.css');
const model=source('src/SensorDash/WorkspaceModels.cs');
const backend=source('src/SensorDash/Program.cs');
let seq=0;
const context={
 crypto:{randomUUID:()=>'test-'+(++seq)},
 document:{querySelectorAll:()=>[],querySelector:()=>({value:'heat-source'})},
 history:{replaceState:()=>{}},
 CSS:{escape:x=>x},
 confirm:()=>true,
 fetch:async(_url,opts)=>({ok:true,json:async()=>JSON.parse(opts.body)}),
 console
};
const workspace=vm.runInNewContext(script+'\nSDWorkspace;',context);
function sensor(id,name,type,value,device='Generic hardware',deviceType='Controller'){
 return {id,name,type,value,device,deviceType,deviceId:'device-'+deviceType};
}
const readings=[
 sensor('cpuTemp','CPU package','Temperature',45,'Processor','Cpu'),
 sensor('cpuW','CPU package power','Power',90,'Processor','Cpu'),
 sensor('gpuTemp','GPU core','Temperature',55,'Graphics','GpuAmd'),
 sensor('gpuW','GPU board power','Power',220,'Graphics','GpuAmd'),
 sensor('rpm','Fans RPM','Fan',1220),
 sensor('pump','Pump RPM','Fan',3000),
 sensor('duty','Fans PWM','Control',64),
 sensor('coolant','Coolant inlet','Temperature',31),
 sensor('outlet','Coolant outlet','Temperature',34),
 sensor('flow','Loop flow','Flow',108)
];
const system={id:'loop-1',name:'Workstation loop',kind:'liquid',components:[
 {id:'heat-1',name:'Computer heat sources',kind:'heat-source',sensorIds:['cpuTemp','cpuW','gpuTemp','gpuW']},
 {id:'fans-1',name:'Radiator fan bank',kind:'fan-group',sensorIds:['rpm','duty']},
 {id:'pump-1',name:'Circulation pump',kind:'pump',sensorIds:['pump','flow']},
 {id:'probe-1',name:'Water probes',kind:'temperature-probe',sensorIds:['coolant','outlet']}
]};
const settings={
 aliases:{},hiddenSensorIds:[],hiddenDeviceIds:[],hiddenHardwareTypes:[],
 coolingSystems:[system],
 dashboards:[{id:'board',name:'Thermal',locked:false,widgets:[
  {id:'widget',title:'Coolant reading',kind:'metric',sensorIds:['coolant']}]}],
 pinnedSensors:[],highThresholds:{},
 trayWidgets:[{id:'tray',sensorId:'coolant',label:'Coolant'}],
 virtualSensors:[],
 simulatedFanCurves:[{id:'curve',name:'Fan cooling',sourceSensorId:'coolant',
  outputSensorId:'rpm',points:[{temperature:25,percent:25},{temperature:50,percent:100}]}]
};
const S={sensors:readings,byId:new Map(readings.map(r=>[r.id,r])),settings};
const h={
 metric:()=>'<metric/>',
 panel:(name,desc,body)=>'<section data-chart-title="'+name+'">'+body+'</section>',
 chart:(s,unit)=>'<chart unit="'+unit+'" sensors="'+s.map(x=>x.id).join(',')+'"/>',
 render:()=>{},toast:msg=>{if(msg.startsWith('Could not'))throw Error(msg);}
};
(async()=>{
 const references=workspace.usageIndex(S);
 const coolant=references.get('coolant');
 assert.ok(coolant.some(r=>r.page==='cooling'&&r.systemId==='loop-1'&&r.componentId==='probe-1'));
 assert.ok(coolant.some(r=>r.page==='dashboards'&&r.dashboardId==='board'));
 assert.ok(coolant.some(r=>r.page==='tray'));
 assert.ok(coolant.some(r=>r.page==='curves'));
 assert.equal(references.get('rpm').filter(r=>r.page==='cooling').length,1);
 assert.equal(workspace.isHidden(S,readings[7]),false);
 const html=workspace.render(S,'cooling',h);
 for(const unit of ['°C','W','RPM','%','L/h'])
  assert.ok(html.includes('unit="'+unit+'"'),'Missing history chart '+unit);
 assert.ok(html.includes('data-cooling-component-id="probe-1"'));
 assert.ok(html.includes('2 power readings'),'Separate power readings should not be summed');
 assert.ok(!html.includes('Radiator (legacy)'));
 S.focusCoolingTarget={systemId:'loop-1',componentId:'probe-1'};
 assert.match(workspace.render(S,'cooling',h),/w-referenced-component/);
 settings.hiddenHardwareTypes=['Controller'];
 assert.ok(workspace.isHidden(S,readings[7]));
 assert.ok(workspace.usageIndex(S).get('coolant').some(r=>r.page==='cooling'));
 settings.hiddenHardwareTypes=[];
 async function click(action,dataset={}){
  return workspace.handle({target:{closest:selector=>
   selector==='[data-work]'?{dataset:{work:action,...dataset}}:null},
   preventDefault(){},stopPropagation(){}},S,h);
 }
 await click('add-system',{kind:'liquid'});
 assert.deepEqual(Array.from(settings.coolingSystems.at(-1).components.map(c=>c.kind)),
  ['heat-source','fan-group','pump','temperature-probe']);
 assert.ok(!settings.coolingSystems.at(-1).components.some(c=>c.kind==='radiator'));
 await click('cooling-to-dashboard',{id:system.id});
 const board=settings.dashboards.at(-1);
 assert.match(board.name,/Workstation loop/);
 for(const title of ['Temperature history','Power history','RPM history','Flow history'])
  assert.ok(board.widgets.some(w=>w.title===title),'Missing generated dashboard widget '+title);
 assert.ok(workspace.usageIndex(S).get('coolant').some(r=>r.dashboardId===board.id));
 assert.equal(S.view,'dashboards');
 assert.ok(app.includes('data-usage-component')&&app.includes('scrollIntoView'));
 assert.ok(css.includes('.w-referenced-component'));
 assert.ok(!backend.includes('SensorBindings')&&!app.includes('sensorBindings'));
 assert.ok(!model.includes('"radiator" or "fan" or "sensor"'));
 assert.ok(app.includes('for(let i=0;i<ids.length;i+=12)'),
  'Batched history is required for multiple cooling charts');
 console.log('PASS: cooling analytics, chart groups, templates, sensor usage, deep links, visibility, dashboard creation and removal of legacy roles');
})().catch(err=>{console.error(err);process.exitCode=1;});
