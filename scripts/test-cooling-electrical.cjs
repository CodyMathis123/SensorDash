// Fan/pump inventory and power-budget regression without live hardware.
// In particular, 10% PWM must never be interpreted as 10% rated watts.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const base=path.resolve(__dirname,'..');
const read=p=>fs.readFileSync(path.join(base,p),'utf8');
const src=read('src/SensorDash/wwwroot/cooling-electrical.js');
const electrical=vm.runInNewContext(src+'\nCoolingElectrical;',{Date,Math,Number,Map,Intl});
const now=new Date().toISOString();
const readings=[
 {id:'radRPM',type:'Fan',name:'Radiators',value:490,observedAt:now},
 {id:'radDuty',type:'Control',name:'CPU_FAN PWM',value:10,observedAt:now},
 {id:'radPower',type:'Power',name:'Actual radiator bank watts',value:4,observedAt:now},
 {id:'pumpRPM',type:'Fan',name:'Pump tach',value:4412,observedAt:now}
];
const S={byId:new Map(readings.map(s=>[s.id,s])),sensors:readings,frame:{timestamp:now},
 settings:{coolingSystems:[]}};
const fans={id:'fan1',name:'Radiator fan bank',kind:'fan-group',sensorIds:['radRPM'],
 electrical:{model:'ARCTIC P12 Pro PST LN',quantity:19,volts:12,ampsPerUnit:0.11,
 powerSource:'Powered PWM hub',rpmSensorId:'radRPM',controlSensorId:'radDuty'}};
const pump={id:'pump1',name:'MCP355 Pump',kind:'pump',sensorIds:['pumpRPM'],
 electrical:{model:'Swiftech MCP355 / Laing DDC 3.2',quantity:1,
 volts:12,wattsPerUnit:18}};
const sys={id:'main',name:'My loop',components:[fans,pump]};
S.settings.coolingSystems=[sys];
assert.equal(Math.round(electrical.rate(fans.electrical)*100)/100,25.08);
assert.equal(electrical.rate(pump.electrical),18);
assert.equal(Math.round(electrical.summarize(S,sys).rated*100)/100,43.08);
assert.equal(electrical.ratedAmps(fans.electrical),2.09);
assert.equal(electrical.running(S,fans),null,
 'PWM percentage alone is not a wattage measurement');
assert.equal(electrical.running(S,pump),null,
 '18W pump nameplate is not measured live consumption');
const budget=electrical.summaryCard(S,sys);
assert.match(budget,/43.08 W/);
assert.match(budget,/Running draw unknown/);
assert.match(budget,/Rated power/i);
assert.doesNotMatch(budget,/4.31 W/,'10% is not 10% nameplate watts');
fans.electrical.sourceMaxAmps=1;
assert.equal(electrical.summarize(S,sys).entries[0].overLimit,true);
assert.match(electrical.summaryCard(S,sys),/exceeds entered source limit/);
delete fans.electrical.sourceMaxAmps;
fans.electrical.powerSensorId='radPower';
assert.equal(electrical.running(S,fans).watts,4);
assert.equal(electrical.running(S,fans).source,'measured');
fans.electrical.powerSensorId='';
fans.electrical.lowDutyPercent=10;
fans.electrical.highDutyPercent=100;
fans.electrical.measuredLowWatts=3;
fans.electrical.measuredHighWatts=23;
assert.equal(electrical.running(S,fans).watts,3);
readings[1].value=55;
assert.equal(electrical.running(S,fans).watts,13);
assert.equal(electrical.running(S,fans).source,'calibrated');
readings[1].value=10;
fans.electrical.controlSensorId='';
assert.equal(electrical.running(S,fans),null,'No interpolation without duty measurement');
fans.electrical.controlSensorId='radDuty';
readings[1].observedAt='2009-01-01T00:00:00Z';
assert.equal(electrical.running(S,fans),null,'Stale duty cannot appear current');
readings[1].observedAt=now;
assert.equal(electrical.summarize(S,sys).allRunning,false,
 'The fixed-speed pump is still unknown running watts');
const missing={id:'x',kind:'fan-group',name:'Unspecified fan bank',sensorIds:[]};
assert.equal(electrical.rate(null),null);
assert.equal(electrical.summarize(S,{components:[missing]}).rated,0);
assert.equal(electrical.summarize(S,{components:[missing]}).allRated,false);
const output=electrical.overview(S);
assert.match(output,/My loop/);
assert.match(output,/MCP355/);
assert.ok(!output.includes('NaN'));
assert.ok(electrical.editor(S,fans,'component:main:fan1',true).includes('data-electrical-field="quantity"'));
assert.ok(electrical.editor(S,fans,'component:main:fan1',true).includes('data-work="electrical-preset"'));
assert.ok(electrical.editor(S,fans,'component:main:fan1',true).includes('data-electrical-field="powerSensorId"'));
assert.ok(electrical.editor(S,pump,'component:main:pump1',true).includes('data-electrical-preset="mcp355"'));
const empty={id:'other',kind:'heat-source',sensorIds:[],name:'CPU'};
assert.equal(electrical.editor(S,empty,'component:main:other',true),'');
const auto={id:'other',kind:'fan-group',sensorIds:[],name:'Other'};
S.settings.coolingSystems.push({id:'more',components:[auto]});
assert.equal(electrical.applyPreset(S,'component:more:other','p12-pro-pst-ln'),true);
assert.equal(auto.electrical.quantity,19);
assert.equal(auto.electrical.ampsPerUnit,0.11);
assert.equal(electrical.applyPreset(S,'component:more:other','mcp355'),false);
assert.equal(electrical.applyPreset(S,'component:main:pump1','mcp355'),true);
assert.equal(pump.electrical.wattsPerUnit,18);
assert.ok(read('src/SensorDash/WorkspaceModels.cs').includes('CoolingElectricalConfig'));
assert.ok(read('src/SensorDash/wwwroot/workspace.js').includes('CoolingElectrical.summaryCard(S,sys)'));
assert.ok(read('src/SensorDash/wwwroot/workspace.js').includes('CoolingElectrical.overview(S)'));
assert.ok(read('src/SensorDash/wwwroot/workspace.js').includes("action==='electrical-preset'"));
assert.ok(read('src/SensorDash/Program.cs').includes('CoolingElectricalSelfTest.Run()'));
assert.ok(read('src/SensorDash/wwwroot/index.html').includes('/cooling-electrical.js'));
S.settings.aliases={pumpRPM:'Loop pump <tach>',radDuty:'Radiator duty',radPower:'Fan bank watts'};
const aliasedEditor=electrical.editor(S,pump,'component:main:pump1',true);
assert.match(aliasedEditor,/Loop pump &lt;tach&gt;/);
assert.doesNotMatch(aliasedEditor,/Pump tach/);
assert.match(aliasedEditor,/value="pumpRPM"/,'Aliases must not replace saved sensor IDs');
fans.electrical.powerSensorId='radPower';
assert.equal(electrical.running(S,fans).detail,'Fan bank watts');
console.log('PASS: rated 43.08W, unknown running draw, calibrated/model distinction, aliases, source-limit warning and UI persistence');
