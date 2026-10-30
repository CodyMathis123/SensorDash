// Regression tests for missing history: no invented interpolation over
// collector sleep, disconnected sensors, or sparse long-range SQL buckets.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.join(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const source=read('src/SensorDash/wwwroot/chart-gaps.js');
const app=read('src/SensorDash/wwwroot/app.js');
const chart=vm.runInNewContext(source+'\nChartGaps;',{Math,Number});
const timeAxis=vm.runInNewContext(read('src/SensorDash/wwwroot/chart-time-axis.js')+'\nChartTimeAxis;',{Date,Math,Number,Intl});
const points=(times,value=32)=>times.map((t,i)=>({t,v:value+i}));
const min=60000,hour=60*min;
const t0=Date.UTC(2026,9,29,4,0,0);
const range=24; // SQL buckets approximately 2m53s at 24h
assert.equal(chart.bucketMs(1),10000);
assert.ok(chart.bucketMs(range)>=170000);
const raw=points([t0,t0+3*min,t0+6*min,t0+6*hour,t0+6*hour+3*min]);
const data=chart.prepare(raw,range);
assert.deepEqual(Array.from(data.segments.map(x=>x.length)),[3,2]);
assert.equal(data.gaps.length,1);
assert.equal(data.gaps[0].start,t0+6*min);
assert.equal(data.gaps[0].end,t0+6*hour);
assert.equal(chart.closest(data,t0+3*hour),null,'Do not report hours-old nearest temperature');
assert.equal(chart.closest(data,t0+3*min).v,33);
assert.equal(chart.closest(data,t0+6*hour+3*min).v,36);

const unit=chart.prepare(points([t0,t0+15*1000,t0+30*1000,t0+2*min+1000]),1);
assert.equal(unit.segments.length,2,'A >90s outage breaks a 1h chart');
const long=chart.prepare(points([t0,t0+20*min,t0+40*min,t0+9*hour]),168);
assert.equal(long.segments.length,2,'Long-range SQL buckets scale gap tolerance');
const only=chart.prepare([{t:t0,v:48}],1);
assert.equal(only.segments.length,1);
assert.equal(only.segments[0].length,1);
assert.equal(only.gaps.length,0);
assert.equal(chart.closest(only,t0+10*min),null);

const dirty=chart.prepare([
 {t:t0+min,v:42},{t:t0,v:40},{t:t0+min,v:43},{t:NaN,v:99},
 {t:t0+2*min,v:Infinity},{t:t0+4*min,v:47}
],1);
assert.deepEqual(Array.from(dirty.points.map(p=>p.v)),[40,43,47]);
assert.equal(dirty.segments.length,2);

const gpu=chart.prepare(points([t0,t0+4*min,t0+10*min,t0+6*hour+4*min,t0+6*hour+7*min]),range);
const shared=chart.sharedOutages([data,gpu]);
assert.equal(shared.length,1);
assert.equal(shared[0].start,t0+10*min);
assert.equal(shared[0].end,t0+6*hour);
const active=chart.prepare(points(Array.from({length:121},(_,i)=>t0+i*3*min)),range);
assert.equal(chart.sharedOutages([data,active]).length,0,
 'No shared shade when another sensor remained active');
assert.equal(chart.sharedOutages([data,only]).length,0,
 'One isolated sample cannot establish shared downtime');
assert.equal(chart.duration(192*min),'3h 12m');

// Exercise the real canvas painter using a minimal fake context. Assert
// temperature traces do not connect across the overnight gap, and only
// overlapping outages receive shading.
const start=app.indexOf('function drawCanvas(canvas){');
const end=app.indexOf('function drawCanvasOverlay(',start);
assert.ok(start>0&&end>start,'Could not locate production chart painter');
const drawSource=app.slice(start,end);
const drawn=[],bands=[];
const colors=['#1fbea7','#5289ea'];
let strokeStyle='';
const ctx={
 path:[],lineWidth:1,fillStyle:'',font:'',
 setTransform(){},clearRect(){},setLineDash(){},
 beginPath(){this.path=[]},
 moveTo(x,y){this.path.push(['M',x,y])},
 lineTo(x,y){this.path.push(['L',x,y])},
 arc(){},fill(){},
 stroke(){if(colors.includes(this.strokeStyle))drawn.push({color:this.strokeStyle,path:this.path.slice()})},
 fillRect(x,y,w,h){bands.push({x,y,w,h})},
 fillText(){},
 get strokeStyle(){return strokeStyle},set strokeStyle(v){strokeStyle=v}
};
const overlay={width:0,height:0,classList:{contains:s=>s==='chart-hover-overlay'},
 getContext:()=>ctx};
const status={classList:{contains:s=>s==='chart-gap-note'},hidden:true,textContent:''};
const canvas={dataset:{chart:'cpu|gpu',unit:'°C'},width:0,height:0,
 getBoundingClientRect:()=>({width:900,height:250}),
 getContext:()=>ctx,nextElementSibling:overlay,
 parentElement:{nextElementSibling:{nextElementSibling:status}}
};
const now=t0+7*hour;
const mock={
 S:{history:{cpu:data.points,gpu:gpu.points},liveCache:new Map(),hours:24},
 Date:{now:()=>now},window:{devicePixelRatio:1},
 ChartGaps:chart,colors,label:s=>s.name,sensor:id=>({name:id}),fmt:(v,d)=>v.toFixed(d),
 ChartTimeAxis:timeAxis,CoolingTimeline:{register:()=>{}},Math,Number
};
const painter=vm.runInNewContext(drawSource+'\ndrawCanvas;',mock);
painter(canvas);
const cpuPaths=drawn.filter(v=>v.color===colors[0]);
assert.equal(cpuPaths.length,2,'Canvas should stroke two separate CPU segments');
assert.equal(cpuPaths[0].path.length,3);
assert.equal(cpuPaths[1].path.length,2);
assert.equal(bands.length,1,'All-sensor outage should be shaded once');
assert.ok(bands[0].w>100);
assert.equal(status.hidden,false);
assert.match(status.textContent,/Shaded bands/);
assert.ok(app.includes('ChartGaps.closest(s,desired)'),'Hover must use gap-aware point lookup');
assert.ok(app.includes('No data'),'Hover should explicitly display missing data');
assert.ok(app.includes('data-cooling-timeline'),'Keep cross-chart cooling hover');
assert.ok(read('src/SensorDash/wwwroot/index.html').includes('/chart-gaps.js'));
assert.ok(read('src/SensorDash/Program.cs').includes('/chart-gaps.js'));
console.log('PASS: missing samples break lines, shade shared downtime, and never leak stale hover values');
