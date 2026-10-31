'use strict';
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const nodes=Object.fromEntries(['fastPollSeconds','storagePollSeconds','saveEverySeconds','retentionDays','historyMode','range','refresh'].map(id=>[id,{value:id==='historyMode'?'balanced':'30',addEventListener(){}}]));
const fields=[];const writes=[];
const context={structuredClone,console,setInterval(){},setTimeout(){},clearTimeout(){},
 document:{addEventListener(){},getElementById:id=>nodes[id]||null,querySelectorAll:selector=>fields.filter(f=>selector===f.selector)},
 window:{addEventListener(){}},confirm:()=>true,
 DriverStatus:{details:()=>''},HistoryInsights:{storagePanel:()=>''},
 SDWorkspace:{saveSettings:async(S,cfg)=>{S.settings=structuredClone(cfg);writes.push(structuredClone(cfg));}}
};
const source=fs.readFileSync('src/SensorDash/wwwroot/app.js','utf8').replace(/bootstrap\(\);\s*$/,'');
const app=vm.runInNewContext(source+'\nrender=()=>{};toast=()=>{};({S,settingsView,settingsCandidates,addAlias,changeThreshold,saveSettings});',context);
const S=app.S;S.view='settings';S.collectionGroups=[];
S.settings={aliases:{cpu:'Processor'},highThresholds:{cpu:0},disabledCollectionGroups:[],disabledCollectionDeviceIds:[],excludedHistoryHardwareTypes:[]};
S.sensors=[{id:'cpu',name:'CPU core',device:'CPU',type:'Temperature',value:40},{id:'fan',name:'Fan <2>',device:'Board',type:'Fan',value:500},{id:'volt',name:'Voltage',device:'Board',type:'Voltage',value:12},{id:'drive',name:'Drive temperature',device:'NVMe',type:'Temperature',value:45}];S.byId=new Map(S.sensors.map(s=>[s.id,s]));
const ids=field=>Array.from(app.settingsCandidates(field),s=>s.id);
assert.deepEqual(ids('aliases'),['drive','fan','volt']);
assert.deepEqual(ids('highThresholds'),['drive','fan'],'Zero threshold still counts as configured; voltage excluded');
let html=app.settingsView();
assert.match(html,/id="addAlias"/);assert.match(html,/Fan &lt;2&gt;/);
const options=id=>html.split('id="'+id+'"')[1].split('</select>')[0];
assert.doesNotMatch(options('newAliasId'),/value="cpu"/);assert.doesNotMatch(options('newThresholdId'),/value="cpu"/);
assert.match(html,/Balanced · key temperatures/);assert.match(html,/In use · cooling/);assert.match(html,/All sensors · largest history database/);
fields.push({selector:'[data-alias]',dataset:{alias:'cpu'},value:'Edited processor'});
fields.push({selector:'[data-threshold]',dataset:{threshold:'cpu'},value:'80'});
(async()=>{
 await app.addAlias('fan');assert.equal(writes.length,1);
 assert.equal(S.settings.aliases.fan,'Fan <2>');assert.equal(S.settings.aliases.cpu,'Edited processor');
 assert.equal(S.settings.highThresholds.cpu,80,'Adding an alias preserves pending threshold edits');
 await app.addAlias('fan');assert.equal(writes.length,1,'Duplicate adds do not write or overwrite');
 await app.addAlias('missing');assert.equal(writes.length,1);
 assert.ok(!ids('aliases').includes('fan'));
 await app.changeThreshold('fan');assert.equal(S.settings.highThresholds.fan,510);
 await app.changeThreshold('fan');assert.equal(writes.length,2,'Duplicate threshold cannot reset custom value');
 assert.ok(!ids('highThresholds').includes('fan'));
 await app.changeThreshold('fan',true);assert.ok(ids('highThresholds').includes('fan'));
 fields[0].value='';await app.saveSettings();assert.ok(ids('aliases').includes('cpu'),'Deleted aliases return to picker');
 S.settings.aliases=Object.fromEntries(S.sensors.map(s=>[s.id,s.name]));
 S.settings.highThresholds={cpu:0,fan:500,drive:50};html=app.settingsView();
 assert.match(html,/id="addAlias" disabled/);assert.match(html,/id="addThreshold" disabled/);
 console.log('PASS: Settings alias creation, duplicate guards, candidate filtering, draft preservation and recording-mode descriptions');
})().catch(e=>{console.error(e);process.exitCode=1;});
