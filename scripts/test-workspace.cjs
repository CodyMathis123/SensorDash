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
const fanDebug=source('src/SensorDash/wwwroot/fan-control-debug.js');
const fanFeedback=source('src/SensorDash/wwwroot/fan-feedback.js');
const coolingInsights=source('src/SensorDash/wwwroot/cooling-insights.js');
const coolingElectrical=source('src/SensorDash/wwwroot/cooling-electrical.js');
const locks=source('src/SensorDash/wwwroot/workspace-locks.js');
const app=source('src/SensorDash/wwwroot/app.js');
const css=source('src/SensorDash/wwwroot/style.css');
const model=source('src/SensorDash/WorkspaceModels.cs');
const backend=source('src/SensorDash/Program.cs');
let seq=0;
const context={
 presetCatalog:require('./validate-cooling-presets.cjs').readCatalog(),
 crypto:{randomUUID:()=>'test-'+(++seq)},
 document:{querySelectorAll:()=>[],querySelector:()=>({value:'heat-source'})},
 history:{replaceState:()=>{}},
 CSS:{escape:x=>x},
 confirm:()=>true,
 fetch:async(_url,opts)=>({ok:true,json:async()=>JSON.parse(opts.body)}),
 console
};
const workspace=vm.runInNewContext(locks+'\n'+fanUx+'\n'+fanDebug+'\n'+fanFeedback+'\n'+coolingInsights+'\n'+coolingElectrical+'\n'+script+'\nCoolingElectrical.setCatalog(presetCatalog);\nSDWorkspace;',context);
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
 pinnedSensors:[],highThresholds:{},
 trayWidgets:[{id:'tray',sensorId:'coolant',label:'Coolant'}],
 fanProfiles:[],
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

 assert.ok(coolant.some(r=>r.page==='tray'));
 assert.ok(coolant.some(r=>r.page==='curves'));
 assert.equal(references.get('rpm').filter(r=>r.page==='cooling').length,1);
 assert.equal(workspace.isHidden(S,readings[7]),false);
 const html=workspace.render(S,'cooling',h);
 assert.ok(!html.includes('<chart'),'Historical charts are Grafana owned');
 assert.ok(html.includes('data-cooling-component-id="probe-1"'));
 assert.ok(html.includes('CPU package power')&&html.includes('GPU board power'),'Separate heat-source power readings remain individually labeled; never summed');
 assert.ok(html.includes('108 L/h'),'Pump-associated physical flow appears in the live circulation tile');
 assert.ok(html.includes('Pump RPM')&&html.includes('Radiator fans'),'Separate pump and radiator tachometer readings remain visible');
 assert.ok(!html.includes('Radiator (legacy)'));
 S.focusCoolingTarget={systemId:'loop-1',componentId:'probe-1'};
 assert.match(workspace.render(S,'cooling',h),/w-referenced-component/);
 settings.hiddenHardwareTypes=['Controller'];
 assert.ok(workspace.isHidden(S,readings[7]));
 assert.ok(workspace.usageIndex(S).get('coolant').some(r=>r.page==='cooling'));
 settings.hiddenHardwareTypes=[];
 async function click(action,dataset={}){
  return workspace.handle({target:{closest:selector=>
   selector==='[data-work]'?{dataset:{work:action,...dataset},closest:()=>({querySelector:()=>({value:dataset.electricalPreset||''})})}:null},
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
 S.filter='Common types';
 const commonHtml=explorer();
 assert.match(commonHtml,/data-filter="Common types"[^>]*title="Temperature, Fan, Control, Power, Flow and coolant chemistry sensors"/,
  'The Common types preset must explain its included readings');
 assert.doesNotMatch(commonHtml,/data-filter="Key"/,
  'The ambiguous Key label must not remain');
 S.filter='In use';
 assert.ok(explorerHtml.includes('data-usage-system="loop-1"'));
 assert.ok(explorerHtml.includes('data-usage-component="probe-1"'));
 assert.ok(explorerHtml.includes('Cooling: Workstation loop / Water probes'));
 assert.ok(explorerHtml.includes('data-select="coolant"'));
 assert.ok(css.includes('grid-template-areas:"index preview sensor sensor label label remove"'),
  'Tray appearance editor must expose preview, style, accent and text color on dedicated grid tracks');
 assert.ok(script.includes('data-tray-style')&&script.includes('data-tray-text-mode')&&
  script.includes('data-tray-text-custom')&&script.includes('paintTrayPreview'),
  'Every tray widget must independently configure style and text color');
 assert.ok(script.includes('data-curve-temperature')&&script.includes('data-curve-percentage'));
 assert.ok(!script.includes('data-curve-points='));

 assert.ok(!backend.includes('SensorBindings')&&!app.includes('sensorBindings'));
 assert.ok(!app.includes('function drawAll(){')&&!app.includes('function ensureHistory(){'),
  'No local historical chart/cache is permitted');
 assert.ok(app.includes('formatMb(p?.workingSetBytes)')&&app.includes('id="refreshDiagnostics"'),
  'Local diagnostics must stay');
 assert.ok(app.includes('status.version')&&app.includes('fanOutputStatus'),
  'Version and live fan status must remain');
 assert.ok(!model.includes('"radiator" or "fan" or "sensor"'));
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
 assert.ok(curvesView.includes('Live fan profiles')&&curvesView.includes('Temperature curves')&&
   curvesView.includes('Curve library'));
 assert.equal((curvesView.match(/data-fan-curve-editor="[^"]+"/g)||[]).length,
   S.settings.simulatedFanCurves.length,
   'Every curve must have exactly one editor, even when rendered inside a profile');
 const related=S.settings.fanProfiles.find(p=>(p.curveIds||[]).length>0);
 assert.ok(curvesView.includes('data-profile-curve-row="'+related.id+'"'),
   'Live profile should show per-sensor PWM demand');
 assert.ok(curvesView.includes('Profile configuration'),
   'Infrequent profile settings should be separate from live demand');
 assert.ok(curvesView.includes('data-curve-current-values') &&
   curvesView.includes('data-curve-current-marker') &&
   curvesView.includes('data-curve-current-title') &&
   curvesView.includes('w-curve-current-label') &&
   css.includes('.w-curve-current-label') &&
   script.includes("marker.setAttribute('aria-label','Current: '+value)"),
   'The yellow current-temperature marker needs a visible label, hover title and accessible description');
 // Repaint the current marker without replacing SVG during live readings:
 // replacing the entire plot would steal point hover/focus during editing.
 const currentLabel={textContent:'—'};
 const currentTitle={textContent:'Current: —'};
 const lineAttributes={},dotAttributes={},markerAttributes={};
 const currentLine={setAttribute:(key,value)=>{lineAttributes[key]=value;}};
 const currentDot={setAttribute:(key,value)=>{dotAttributes[key]=value;}};
 const currentMarker={style:{display:'none'},setAttribute:(key,value)=>{markerAttributes[key]=value;},
   querySelector:selector=>selector==='[data-curve-current-line]'?currentLine:
     selector==='[data-curve-current-dot]'?currentDot:
     selector==='[data-curve-current-title]'?currentTitle:null};
 const currentGraph={dataset:{curveAxisMin:'0',curveAxisMax:'100'},
   querySelector:selector=>selector==='[data-curve-current-values]'?currentLabel:
     selector==='[data-curve-current-marker]'?currentMarker:null};
 const previewNode={dataset:{curvePreview:'curve'},
   querySelector:selector=>selector==='[data-curve-graph]'?currentGraph:null};
 const previousQueryAll=context.document.querySelectorAll;
 const previousSimulation=S.simulation;
 context.document.querySelectorAll=selector=>selector==='[data-curve-preview]'?
   [previewNode]:[];
 S.simulation={results:[{id:'curve',inputTemperature:25,targetPercent:10}]};
 workspace.paintSimulation(S);
 assert.equal(currentLabel.textContent,'25.0°C → 10.0% PWM');
 assert.equal(currentTitle.textContent,'Current: 25.0°C → 10.0% PWM');
 assert.equal(currentMarker.style.display,'');
 assert.equal(lineAttributes.x1,'142.0');
 assert.equal(dotAttributes.cy,'159.2');
 assert.ok(markerAttributes['aria-label'].includes('25.0°C'));
 S.simulation.results[0]={id:'curve',inputTemperature:40,targetPercent:25};
 workspace.paintSimulation(S);
 assert.equal(currentLabel.textContent,'40.0°C → 25.0% PWM');
 assert.equal(lineAttributes.x1,'200.8');
 assert.equal(dotAttributes.cy,'137.0');
 S.simulation.results=[];
 workspace.paintSimulation(S);
 assert.equal(currentLabel.textContent,'—');
 assert.equal(currentMarker.style.display,'none');
 context.document.querySelectorAll=previousQueryAll;
 S.simulation=previousSimulation;
 assert.ok(curvesView.includes('data-curve-point-index="0"') &&
   curvesView.includes('class="w-curve-point-marker"') &&
   curvesView.includes('data-curve-tooltip') &&
   curvesView.includes('role="tooltip"') &&
   script.includes('const handleCurvePointEnter=') &&
   script.includes('const handleCurvePointClick=') &&
   app.includes("document.addEventListener('pointerover'") &&
   app.includes("document.addEventListener('focusin'"),
   'Curve graph must expose interactive points linked to editor rows');
 // Simulate the delegated interaction without real fan hardware or a DOM lib.
 const classFlag=()=>{const flags=new Set();return{
   toggle:(k,v)=>v?flags.add(k):flags.delete(k),
   remove:k=>flags.delete(k),contains:k=>flags.has(k)}};
 const tooltipTemp={textContent:''},tooltipDuty={textContent:''};
 const tooltip={hidden:true,style:{},classList:classFlag(),
   querySelector:q=>q.includes('temp')?tooltipTemp:tooltipDuty};
 const dot={getBoundingClientRect:()=>({left:170,right:180,top:110,bottom:120})};
 const plot={getBoundingClientRect:()=>({left:0,top:0,width:700,height:300})};
 const input={focus(){this.wasFocused=true;}};
 const row={dataset:{curvePointIndex:'0'},classList:classFlag(),
   querySelector:()=>input,scrollIntoView(){this.wasScrolled=true;}};
 const point={dataset:{curvePointIndex:'0',curveDotTemperature:'40',
   curveDotPercent:'25'},classList:classFlag(),
   querySelector:()=>dot};
 const table={querySelectorAll:()=>[row]};
 const scope={
   querySelector:q=>q==='[data-curve-tooltip]'?tooltip:
     q==='[data-curve-table]'?table:null,
   querySelectorAll:q=>q.includes('w-curve-point-marker')?[point]:
     q.includes('tr[data-curve-point-row]')?[row]:[],
   contains:n=>n===row||n===input||n===point
 };
 row.closest=q=>q.includes('tr[data-curve-point-row]')?row:scope;
 point.closest=q=>q.includes('w-curve-point-marker')?point:
   q==='.w-curve-graph'?plot:scope;
 row.contains=n=>n===row||n===input;
 point.contains=n=>n===point||n===dot;
 assert.equal(workspace.handleCurvePointEnter({target:row,relatedTarget:null}),true);
 assert.equal(point.classList.contains('is-active'),true);
 assert.equal(row.classList.contains('is-active'),true);
 assert.equal(tooltip.hidden,false);
 assert.equal(tooltipTemp.textContent,'40 °C');
 assert.equal(tooltipDuty.textContent,'25% fan speed');
 let prevented=false;
 assert.equal(workspace.handleCurvePointClick({target:point,
   preventDefault(){prevented=true;}}),true);
 assert.equal(prevented,true);
 assert.equal(input.wasFocused,true);
 assert.equal(row.wasScrolled,true);
 assert.equal(workspace.handleCurvePointExit({target:row,relatedTarget:null}),true);
 assert.equal(tooltip.hidden,true);
 assert.ok(curvesView.includes('Hardware setup &amp; calibration') &&
   curvesView.includes('Temperature curves'),
   'Calibration remains accessible in a separate, collapsed workspace');
 assert.ok(curvesView.includes('data-fan-fold="calibration"') &&
   curvesView.indexOf('data-fan-fold="profiles"') <
     curvesView.indexOf('data-fan-fold="curves"') &&
   curvesView.indexOf('data-fan-fold="curves"') <
     curvesView.indexOf('data-fan-fold="calibration"') &&
   curvesView.includes('Hardware setup &amp; calibration') &&
   curvesView.includes('Temperature inputs · MAX') &&
   !curvesView.includes('data-fan-fold="manual"') &&
   !curvesView.includes('data-fan-fold="quick"'),
   'Live profiles must precede curve library and occasional hardware setup');
 const priorFanControl=S.fanControl;
 S.fanControl={
   status:{state:'off'},outputs:[{id:'pwm1',name:'Chassis PWM',minimum:10,maximum:100,writable:true}],
   persistent:{profiles:[{id:'shared',name:'Shared radiators',channelIds:['pwm1'],minimumPercent:20}],
     readings:[{id:'shared',targetPercent:45}],overrides:[]}
 };
 S.settings.fanChannelMappings=[{channelId:'pwm1',kind:'fan',label:'Radiator hub',rpmSensorId:'rpm1'}];
 let quick=workspace.render(S,'curves',h);
 assert.ok(quick.includes('Hardware setup &amp; calibration') &&
   !quick.includes('data-quick-slider=') &&
   !quick.includes('data-fan-fold="manual"'),
   'There must not be a duplicate manual override UI');
 S.settings.fanChannelMappings[0].kind='pump';
 assert.ok(!workspace.render(S,'curves',h).includes('data-quick-slider="pwm1"'),
   'Pump mappings must never appear in standalone quick-speed controls');
 S.settings.fanChannelMappings=[];
 S.fanControl=priorFanControl;
 // The radiator fan calibration belongs to one PWM header, so the CPU
 // and GPU MAX curves display estimates from the SAME measured response.
 const previousCurves=S.settings.simulatedFanCurves;
 const previousProfiles=S.settings.fanProfiles;
 const previousMapping=S.settings.fanChannelMappings;
 S.settings.simulatedFanCurves=[
  {id:'cpu',name:'CPU radiator curve',sourceSensorId:'cpuTemp',
    points:[{temperature:40,percent:25},{temperature:85,percent:100}]},
  {id:'gpu',name:'GPU hotspot radiator curve',sourceSensorId:'gpuTemp',
    points:[{temperature:40,percent:40},{temperature:85,percent:100}]}
 ];
 S.settings.fanProfiles=[{id:'radiator',name:'Radiator MAX',
   curveIds:['cpu','gpu'],channelIds:['pwm1']}];
 S.settings.fanChannelMappings=[{channelId:'pwm1',label:'Radiator Fans',
   kind:'fan',verifiedAt:new Date().toISOString(),rpmSensorId:'rpm',
   measurements:[{dutyPercent:10,rpm:490},
     {dutyPercent:40,rpm:1050},{dutyPercent:100,rpm:1980}]}];
 // The LIVE profile, not either reusable CPU/GPU curve, maps a
 // physical radiator PWM header to its independently calibrated RPM.
 const previousLive=S.fanControl;
 S.fanControl={outputs:[{id:'pwm1',name:'Radiator PWM',device:'IT8688E',
   writable:true}],persistent:{profiles:[{id:'radiator',name:'Radiator MAX',
   channelIds:['pwm1']}],readings:[{id:'radiator',name:'Radiator MAX',
   state:'running',targetPercent:40,message:'MAX of CPU/GPU curves'}],
   overrides:[]}};
 S.sensors.push(sensor('pwm1','Radiator PWM','Control',40),sensor('hiddenPwm','Unused PWM','Control',0));
 S.byId.set('pwm1',S.sensors.at(-2));
 S.byId.set('hiddenPwm',S.sensors.at(-1));
 S.settings.hiddenSensorIds=['hiddenPwm','pwm1'];
 S.fanControl.outputs.push({id:'hiddenPwm',name:'Unused PWM',device:'IT8688E',writable:true});
 const hiddenProfileHtml=workspace.render(S,'curves',h);
 assert.doesNotMatch(hiddenProfileHtml,/data-profile-channel="radiator" value="hiddenPwm"/,
  'Unassigned hidden physical PWM output must not appear in profile selector');
 assert.match(hiddenProfileHtml,/data-profile-channel="radiator" value="pwm1" checked/,
  'Existing hidden PWM output must remain checked in its configured profile');
 assert.match(hiddenProfileHtml,/Radiator PWM \[Hidden\]/,
  'Existing hidden channel must carry an explicit label');
 S.settings.hiddenSensorIds=[];S.fanControl.outputs.pop();
 S.byId.delete('hiddenPwm');S.byId.delete('pwm1');S.sensors.pop();S.sensors.pop();
 const estimated=workspace.render(S,'curves',h);
 assert.ok(estimated.includes('Target PWM')&&
   estimated.includes('Estimated RPM')&&
   estimated.includes('Measured RPM')&&
   estimated.includes('≈1,050 RPM')&&
   estimated.includes('40.0%') &&
   !estimated.includes('data-curve-estimated-rpm') &&
   !estimated.includes('w-curve-estimate-note'),
   'Only the MAX live profile maps the fan to calibrated RPM; curve editor is PWM-only');
 S.fanControl.persistent.readings[0].targetPercent=25;
 assert.ok(workspace.render(S,'curves',h).includes('≈770 RPM'),
   'Changing the active MAX profile demand reuses the same radiator calibration');
 S.fanControl.persistent.readings[0].targetPercent=5;
 assert.ok(workspace.render(S,'curves',h).includes('Not calibrated at 5%'),
   'Never invent fan RPM outside saved calibration range');
 S.fanControl=previousLive;
 S.settings.simulatedFanCurves=previousCurves;
 S.settings.fanProfiles=previousProfiles;
 S.settings.fanChannelMappings=previousMapping;
 assert.ok(curvesView.includes('data-fan-fold="profiles"')&&
   curvesView.includes('data-fan-fold="curves"')&&
   curvesView.includes('data-fan-fold="calibration"'),
   'Fan control and curves remain grouped into accessible folds');
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
 const lockedFans=workspace.render(S,'curves',h);
 assert.ok(lockedFans.includes('Unlock editing')&&!lockedFans.includes('data-curve-temperature'),
  'Locked Fan Control hides point inputs');
 assert.ok(lockedFans.includes('Release to firmware control'),
  'Locked Fan Control must still allow manual release');
 const before=S.settings.simulatedFanCurves.length;
 await click('add-curve');
 assert.equal(S.settings.simulatedFanCurves.length,before,'Locked editor cannot add curves');
 await click('toggle-fan-lock');
 assert.equal(S.settings.fanControlLocked,false);
 // Regression: clicking the ACTUAL preset action previously hit the
 // temporal-dead-zone of cfg, which was initialized below its handler.
 // Calling applyPreset() in isolation never exposed this runtime failure.
 S.view='cooling';
 let radiator=S.settings.coolingSystems.find(x=>x.id==='loop-1')
   .components.find(x=>x.id==='fans-1');
 assert.equal(radiator.electrical,undefined);
 const radResult=await click('electrical-preset',{
   electricalScope:'component:loop-1:fans-1',
   electricalPreset:'p12-pro-pst-ln'
 });
 assert.equal(radResult,true);
 radiator=S.settings.coolingSystems.find(x=>x.id==='loop-1')
   .components.find(x=>x.id==='fans-1');
 assert.equal(radiator.electrical.quantity,1,
   'ARCTIC button must persist the preset without cfg initialization errors');
 assert.equal(radiator.electrical.ampsPerUnit,0.11);
 assert.equal(radiator.electrical.volts,12);
 assert.equal(radiator.electrical.model,'ARCTIC P12 Pro PST LN');
 assert.deepEqual(Array.from(radiator.sensorIds),['rpm','duty'],
   'Applying specs must preserve existing assigned sensors');
 const pumpResult=await click('electrical-preset',{
   electricalScope:'component:loop-1:pump-1',electricalPreset:'mcp355'
 });
 assert.equal(pumpResult,true);
 const pump=S.settings.coolingSystems.find(x=>x.id==='loop-1')
   .components.find(x=>x.id==='pump-1');
 assert.equal(pump.electrical.wattsPerUnit,18,
   'MCP355 preset button must execute through the actual click handler');
 assert.match(workspace.render(S,'cooling',h),/19.32 W/);
 // Locked components must reject preset application.
 await click('toggle-cooling-lock',{id:'loop-1'});
 radiator=S.settings.coolingSystems.find(x=>x.id==='loop-1')
   .components.find(x=>x.id==='fans-1');
 radiator.electrical.quantity=8;
 await click('electrical-preset',{
   electricalScope:'component:loop-1:fans-1',electricalPreset:'p12-pro-pst-ln'
 });
 assert.equal(S.settings.coolingSystems.find(x=>x.id==='loop-1')
   .components.find(x=>x.id==='fans-1').electrical.quantity,8,
   'Locked cooling system must block preset changes');
 await click('toggle-cooling-lock',{id:'loop-1'});
 assert.ok(!curvesView.includes('This sandbox evaluates'));
 console.log('PASS: exact radiator Max/NVMe starters, durable control editor, data cache, tooltip and dynamic version footer');
 console.log('PASS: process diagnostics without history renderer and accurate live-control labels');
 console.log('PASS: cooling configuration, event references, sensor usage, fan curves and Grafana history separation');
})().catch(err=>{console.error(err);process.exitCode=1;});
