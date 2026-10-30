'use strict';
// Regression coverage for historical maxima, observed energy and SQLite
// storage accounting without hardware, network access or a running service.
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
const root=path.resolve(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const js=read('src/SensorDash/wwwroot/history-insights.js');
const src=read('src/SensorDash/Program.cs');
const bridge=read('src/SensorDash/ServiceBridge.cs');
const history=read('src/SensorDash/HistoryStatistics.cs');
const ui=vm.runInNewContext(js+'\nHistoryInsights;', {Date,Math,Number,Intl,Map,fetch:async()=>{throw Error('Unexpected fetch')},document:{}});
const readings=[
{id:'cpuT',name:'CPU Core',device:'AMD Ryzen 9',deviceType:'Cpu',type:'Temperature',value:36},
{id:'gpuHot',name:'GPU Hot Spot',device:'Radeon GPU',deviceType:'GpuAmd',type:'Temperature',value:44},
{id:'gpuCore',name:'GPU Core',device:'Radeon GPU',deviceType:'GpuAmd',type:'Temperature',value:29},
{id:'gpuMemory',name:'GPU Memory',device:'Radeon GPU',deviceType:'GpuAmd',type:'Temperature',value:54},
{id:'nvme',name:'Composite',device:'NVMe SSD',deviceType:'Storage',type:'Temperature',value:42},
{id:'cpuP',name:'CPU Package Power',device:'AMD Ryzen 9',deviceType:'Cpu',type:'Power',value:38},
{id:'gpuP',name:'GPU Board Power',device:'Radeon GPU',deviceType:'GpuAmd',type:'Power',value:17},
{id:'badP',name:'GPU Memory Power',device:'Radeon GPU',deviceType:'GpuAmd',type:'Power',value:10}
];
const S={sensors:readings,settings:{aliases:{},retentionDays:365,coolingSystems:[]},view:'overview'};
const selected=Array.from(ui.selectIds(S).map(x=>x.id));
assert.deepEqual(selected,['cpuT','gpuHot','gpuCore','gpuMemory','nvme','cpuP','gpuP'],
 'Prefer package/board totals over sub-component power to avoid double counting');
const overview=ui.markup(S);
assert.match(overview,/Recorded history/);
assert.match(overview,/30 days/);
assert.match(overview,/All retained \(365 days\)/);
const now=Date.now(),stats=[
{id:'cpuT',type:'Temperature',count:10,max:88,peakAt:now,average:45},
{id:'gpuHot',type:'Temperature',count:10,max:71,peakAt:now,average:41},
{id:'gpuCore',type:'Temperature',count:10,max:57,peakAt:now,average:33},
{id:'gpuMemory',type:'Temperature',count:10,max:77,peakAt:now,average:64},
{id:'nvme',type:'Temperature',count:10,max:61,peakAt:now,average:50},
{id:'cpuP',type:'Power',count:10,max:150,average:48,energyWh:15.5,coveredHours:.5,coveragePercent:2},
{id:'gpuP',type:'Power',count:10,max:250,average:110,energyWh:61.2,coveredHours:.7,coveragePercent:3}
];
const out=ui.statsHtml(S,stats);
for(const token of ['88.0 °C','71.0 °C','61.0 °C','150.0 W','250.0 W','15.5 Wh','61.2 Wh','Observed span','not total PC wall draw'])
 assert.ok(out.includes(token),'Missing stats token: '+token);
assert.ok(!out.includes('GPU Memory Power'),'Cannot double-count GPU rail');
const storage=ui.storageHtml({dbBytes:10485760,walBytes:1048576,shmBytes:32768,
 freePageBytes:1048576,allocatedPageBytes:10485760,sampleCount:1000,sensorCount:2,
 retentionDays:365,saveEverySeconds:10,mode:'balanced',
 oldest:now-10*86400000,newest:now,
 devices:[{device:'AMD Ryzen 9',samples:600,sensors:1},{device:'Radeon GPU',samples:400,sensors:1}],
 sensors:[{id:'cpuT',name:'CPU Core',device:'AMD Ryzen 9',count:600},
 {id:'gpuCore',name:'GPU Core',device:'Radeon GPU',count:400}]});
for(const token of ['11.0 MiB','~1.0 MiB/day','60.0%','40.0%','CPU Core','Radeon GPU',
 'Storage used','Estimated growth','Saved readings'])
 assert.ok(storage.includes(token),'Missing storage token: '+token);
assert.ok(!storage.includes('SQLite pages'));
assert.ok(src.includes('requested.RetentionDays = Math.Clamp(requested.RetentionDays, 1, 1095)'));
assert.ok(src.includes('ReadStorage(SensorFrame? latest,DashboardSettings settings)'));
assert.ok(src.includes('ReadStatistics(string[] ids,double hours'));
assert.ok(bridge.includes('"historyStats" => new'));
assert.ok(bridge.includes('"historyStorage" => db.ReadStorage'));
assert.ok(history.includes('interval<=maximumGapMs'));
assert.ok(history.includes('Temperature readings never claim electricity usage'));
assert.ok(read('src/SensorDash/wwwroot/workspace.js').includes('HistoryInsights.markup(S)'));
assert.ok(read('src/SensorDash/wwwroot/app.js').includes('HistoryInsights.afterRender(S)'));
assert.ok(read('src/SensorDash/wwwroot/app.js').includes('HistoryInsights.storagePanel()'));
assert.ok(read('src/SensorDash/wwwroot/index.html').includes('/history-insights.js'));
console.log('PASS: month/retention peak summaries, power coverage, actual disk files, device/sensor shares and adjustable retention');

const accessories=ui.statsHtml({sensors:[],settings:{}},[],[{name:'Pump',system:'Loop',source:'Rated estimate',ratedWatts:18,stats:{count:12,max:18,average:18,energyWh:.1,coveredHours:1/180,coveragePercent:.5}}]);
for(const text of ['Component power','Pump','18.0 W','0.1 Wh','Rated estimate','info-tip','Actual use may be lower'])assert.ok(accessories.includes(text));
const unknown=ui.statsHtml({sensors:[],settings:{}},[],[{name:'Fan',system:'Loop',source:'Rating only',ratedWatts:25,stats:{count:0}}]);
assert.ok(unknown.includes('25.0 W rated'));assert.ok(!unknown.includes('0.0 Wh'));
