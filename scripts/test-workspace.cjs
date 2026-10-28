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
const fanUx=source('src/SensorDash/wwwroot/fan-control-ux.js');
const fanFeedback=source('src/SensorDash/wwwroot/fan-feedback.js');
const locks=source('src/SensorDash/wwwroot/workspace-locks.js');
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
const workspace=vm.runInNewContext(locks+'\n'+fanUx+'\n'+fanFeedback+'\n'+script+'\nSDWorkspace;',context);
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
 virtualSensors:[],fanProfiles:[],
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
 assert.ok(html.includes('108 L/h'),'Single physical flow reading should appear in circulation summary');
 assert.ok(html.includes('RPM sensors'),'Pump RPM should not be misidentified as a fan count');
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
 assert.deepEqual(Array.from(S.settings.coolingSystems.at(-1).components.map(c=>c.kind)),
  ['heat-source','fan-group','pump','temperature-probe']);
 assert.ok(!S.settings.coolingSystems.at(-1).components.some(c=>c.kind==='radiator'));
 // Different cooling systems can reference the same hardware without
 // cross-system charts or changes to hardware ownership.
 const secondary=S.settings.coolingSystems.at(-1);
 secondary.name='Secondary cooling system';
 secondary.components[0].sensorIds=['gpuTemp','gpuW'];
 const allSystems=workspace.render(S,'cooling',h);
 assert.ok(allSystems.includes('Workstation loop'));
 assert.ok(allSystems.includes('Secondary cooling system'));
 assert.ok(allSystems.includes('108 L/h'));
 assert.equal(S.settings.coolingSystems.length,2);
 await click('cooling-to-dashboard',{id:system.id});
 const board=S.settings.dashboards.at(-1);
 assert.match(board.name,/Workstation loop/);
 for(const title of ['Temperature history','Power history','RPM history','Flow history'])
  assert.ok(board.widgets.some(w=>w.title===title),'Missing generated dashboard widget '+title);
 assert.ok(workspace.usageIndex(S).get('coolant').some(r=>r.dashboardId===board.id));
 assert.equal(S.view,'dashboards');
 assert.ok(app.includes('data-usage-component')&&app.includes('scrollIntoView'));
 assert.ok(css.includes('.w-referenced-component'));
 // Exercise the actual Explorer renderer, not only the index helper.
 const explorerSource=app.slice(app.indexOf('function explorer(){'),app.indexOf('function events(){'));
 S.filter='In use';S.search='';S.selected='coolant';S.deviceFilter='';
 S.explorerGrouping='hardware';S.showHidden=false;
 const escapeHtml=v=>String(v==null?'':v).replace(/&/g,'&amp;').replace(/</g,'&lt;')
   .replace(/"/g,'&quot;').replace(/>/g,'&gt;');
 const browser={
  S,SDWorkspace:workspace,esc:escapeHtml,
  panel:h.panel,chart:h.chart,latest:s=>String(s?.value),
  typeUnit:type=>type==='Temperature'?'°C':type
 };
 const explorer=vm.runInNewContext(explorerSource+'\nexplorer;',browser);
 const explorerHtml=explorer();
 assert.ok(explorerHtml.includes('data-usage-system="loop-1"'));
 assert.ok(explorerHtml.includes('data-usage-component="probe-1"'));
 assert.ok(explorerHtml.includes('Cooling: Workstation loop / Water probes'));
 assert.ok(explorerHtml.includes('data-select="coolant"'));
 // Layout locks are presentation-only: edit actions disappear, metrics remain.
 S.workspaceDashboardId=board.id;
 board.locked=true;
 const locked=workspace.render(S,'dashboards',h);
 assert.ok(locked.includes('Unlock layout'));
 assert.ok(!locked.includes('data-widget-title'));
 assert.ok(locked.includes('Coolant reading')||locked.includes('Current readings'));
 assert.ok(css.includes('grid-template-areas:"index sensor label color precision remove"'));
 assert.ok(script.includes('data-curve-temperature')&&script.includes('data-curve-percentage'));
 assert.ok(!script.includes('data-curve-points='));

 assert.ok(!backend.includes('SensorBindings')&&!app.includes('sensorBindings'));
 assert.ok(!model.includes('"radiator" or "fan" or "sensor"'));
 assert.ok(app.includes('for(let at=0;at<needed.length;at+=12)'),
  'History batching is required for multiple cooling charts');
 assert.ok(app.includes('S.historyCache')&&app.includes('HISTORY_CACHE_LIMIT=120'),
  'History should be cached and bounded across navigation');
 assert.ok(app.includes('names[i]')&&app.includes('series[i].color'),
  'Chart hover must include the sensor name alongside the color swatch');
 assert.ok(app.includes('status.version')&&app.includes('fanOutputStatus'),
  'Build version and fan output footer must be dynamic');
 const executableVersion=source('src/SensorDash/SensorDash.csproj').split('<Version>')[1]?.split('</Version>')[0];
 assert.ok(executableVersion,'Expected a versioned executable');
 assert.ok(source('.github/workflows/windows-build.yml').includes('name: SensorDash-Setup-'+executableVersion+'-x64'),
  'Uploaded MSI artifact must carry the same release version');
 assert.equal(source('installer/SensorDash.wxs').split('Manufacturer="CodyMathis" Version="')[1]?.split('"')[0],executableVersion,
  'MSI upgrade version must match the executable version');
 assert.equal(source('installer/SensorDash.wixproj').split('<OutputName>SensorDash-Setup-')[1]?.split('-x64</OutputName>')[0],executableVersion,
  'MSI artifact name must match the executable version');
 assert.ok(backend.includes('process = ProcessTelemetry.Snapshot()') &&
  source('src/SensorDash/ServiceBridge.cs').includes('process = ProcessTelemetry.Snapshot()'),
  'Both collector and desktop process metrics must be reported separately');
 assert.ok(app.includes('formatMb(p?.workingSetBytes)') &&
  app.includes("id=\"refreshDiagnostics\"") &&
  app.includes('S.chartStats.lastMs'),
  'Performance diagnostics must be available without touching the hardware');
 assert.ok(app.includes('const generation=++S.chartGeneration') &&
  app.includes('performance.now()-frameStart<7') &&
  app.includes('requestAnimationFrame(paintBatch)'),
  'Chart rendering must be time-sliced and cancel outdated navigation work');
 assert.ok(app.includes('S.historyStats.lastFetchMs=Date.now()-started'),
  'History fetch duration must be observable');
 assert.ok(!app.includes('Simulation only · No writes') &&
  !script.includes("'Curve sandbox'"),
  'Version 0.6 must not present real fan control as simulation-only');
 // Exercise actual chart scheduler behavior with fake frame timing, without hardware or a browser.
 const paintSource=app.slice(app.indexOf('function drawAll(){'),app.indexOf('function drawCanvas(canvas){'));
 assert.ok(paintSource.includes('requestAnimationFrame(paintBatch)'));
 const frames=[],painted=[],canvases=Array.from({length:7},(_,i)=>({id:i,isConnected:true}));
 let clock=0,hovered=false;
 const paintContext={
  S:{chartGeneration:0,chartDirty:false,lastChartPaint:0,
    chartStats:{paints:0,lastMs:0,slowestMs:0,lastCharts:0,lastBatches:0}},
  document:{hidden:false,querySelectorAll:()=>canvases},
  chartInUse:()=>hovered,
  requestAnimationFrame:cb=>frames.push(cb),
  performance:{now:()=>clock},
  drawCanvas:canvas=>{painted.push(canvas);clock+=3}
 };
 const schedulePaint=vm.runInNewContext(paintSource+String.fromCharCode(10)+'drawAll;',paintContext);
 const flushFrames=()=>{
  let guard=0;
  while(frames.length){
   assert.ok(++guard<20,'Paint scheduler did not terminate');
   frames.shift()();
  }
 };
 // The first scheduled generation must cancel when navigation requests a newer paint.
 schedulePaint();
 schedulePaint();
 flushFrames();
 assert.equal(painted.length,7,'Only the newest generation should paint');
 assert.ok(paintContext.S.chartStats.lastBatches>=2,'Large chart collections should yield frames');
 assert.equal(paintContext.S.chartStats.lastCharts,7);
 assert.equal(paintContext.S.chartStats.paints,1);
 assert.ok(paintContext.S.chartStats.lastMs>=21);
 painted.length=0;
 schedulePaint();
 paintContext.S.chartGeneration++; // A navigation destroyed the canvas elements.
 flushFrames();
 assert.equal(painted.length,0,'Detached navigation batches must be cancelled');
 hovered=true;
 schedulePaint();
 assert.equal(frames.length,0);
 assert.equal(paintContext.S.chartDirty,true);
 hovered=false;
 await click('starter-radiators');
 const shared=S.settings.fanProfiles.at(-1);
 assert.equal(shared.curveIds.length,2);
 assert.equal(shared.channelIds.length,0,'Never guess a physical motherboard fan channel');
 assert.deepEqual(Array.from(S.settings.simulatedFanCurves.at(-2).points.map(p=>[p.temperature,p.percent])),
  [[40,10],[55,10],[60,15],[65,20],[70,25],[75,35],[80,50],[85,75],[90,100]]);
 assert.deepEqual(Array.from(S.settings.simulatedFanCurves.at(-1).points.map(p=>[p.temperature,p.percent])),
  [[40,10],[55,10],[60,20],[65,25],[70,35],[75,50],[85,75],[95,100]]);
 await click('starter-nvme');
 const nvme=S.settings.simulatedFanCurves.at(-1);
 assert.deepEqual(Array.from(nvme.points.map(p=>[p.temperature,p.percent])),
  [[35,30],[40,30],[45,40],[50,55],[55,75],[60,100]]);
 assert.equal(S.settings.fanProfiles.at(-1).channelIds.length,0);
 const curvesView=workspace.render(S,'curves',h);
 assert.ok(curvesView.includes('Live fan profiles')&&curvesView.includes('Temperature curves'));
 assert.ok(curvesView.includes('data-fan-fold="profiles"')&&curvesView.includes('data-fan-fold="curves"')&&
  curvesView.includes('data-fan-fold="commissioning"')&&curvesView.includes('data-fan-fold="manual"'),
  'Fan control must be grouped into collapsible sections');
 // Editor locks stay at the UI level and do not stop the service or remove
 // readings. Verify cooling systems are individually lockable.
 await click('toggle-cooling-lock',{id:'loop-1'});
 assert.equal(S.settings.coolingSystems[0].locked,true);
 let cooling=workspace.render(S,'cooling',h);
 assert.ok(cooling.includes('Viewing mode')&&cooling.includes('Unlock editing'));
 assert.ok(!cooling.includes('data-component-name="component:loop-1:'));
 await click('add-component',{id:'loop-1'});
 assert.equal(S.settings.coolingSystems[0].components.length,4,
  'Cannot edit a locked cooling system');
 await click('toggle-cooling-lock',{id:'loop-1'});
 assert.equal(S.settings.coolingSystems[0].locked,false);
 S.view='curves';
 await click('toggle-fan-lock');
 assert.equal(S.settings.fanControlLocked,true);
 const locked=workspace.render(S,'curves',h);
 assert.ok(locked.includes('Unlock editing')&&!locked.includes('data-curve-temperature'),
  'Locked Fan Control hides point inputs');
 assert.ok(locked.includes('Release to firmware control'),
  'Locked Fan Control must still allow manual release');
 const before=S.settings.simulatedFanCurves.length;
 await click('add-curve');
 assert.equal(S.settings.simulatedFanCurves.length,before,'Locked editor cannot add curves');
 await click('toggle-fan-lock');
 assert.equal(S.settings.fanControlLocked,false);
 assert.ok(!curvesView.includes('This sandbox evaluates'));
 console.log('PASS: exact radiator Max/NVMe starters, durable control editor, data cache, tooltip and dynamic version footer');
 console.log('PASS: read-only process diagnostics, browser chart timing, time-sliced navigation and accurate live-control labels');
 console.log('PASS: cooling analytics, chart groups, templates, sensor usage, deep links, visibility, dashboard creation and removal of legacy roles');
})().catch(err=>{console.error(err);process.exitCode=1;});
