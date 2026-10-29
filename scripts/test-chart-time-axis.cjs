// Calendar axis and hover regression tests: hardware-free, deterministic.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'..'),read=p=>fs.readFileSync(path.join(root,p),'utf8');
const src=read('src/SensorDash/wwwroot/chart-time-axis.js');
const app=read('src/SensorDash/wwwroot/app.js');
const axis=vm.runInNewContext(src+'\nChartTimeAxis;',{Date,Math,Number,Intl});
const date=Date.UTC(2026,9,29,21,30); // Oct 29 5:30 PM in Detroit
const opt=['en-US','America/Detroit'];
assert.match(axis.localDate(date,168,...opt),/Thu.*10\/29/);
assert.match(axis.localDate(date,720,...opt),/Oct.*29/);
assert.match(axis.tooltip(date,...opt),/Thu.*Oct.*29.*2026.*5:30\s*PM/);
assert.match(axis.shortTime(date,...opt),/5:30\s*PM/);
assert.ok(!axis.shortTime(date,...opt).includes('Oct'));
assert.ok(!axis.localDate(date,168,...opt).includes('PM'));

const hour=3600000,day=24*hour;
const windowEnd=Date.UTC(2026,9,29,21,0);
const view7=Array.from(axis.ticks(windowEnd-7*day,windowEnd,820,168,'en-US'));
assert.ok(view7.length>=5 && view7.length<=9,
 'Wide 7-day history should show calendar dates for almost every day');
assert.ok(view7.every(t=>/\d{1,2}\/\d{1,2}/.test(t.label)),
 'Every 7-day tick should contain a month/day');
assert.ok(view7.every(t=>/[A-Za-z]{3}/.test(t.label)),
 '7-day ticks should also include weekdays');
assert.ok(view7.every(t=>new Date(t.t).getHours()===0),
 'Calendar ticks should align to local midnight');
assert.ok(view7.every((t,i)=>i===0||t.t>view7[i-1].t),
 'Calendar ticks are chronological');

const small=Array.from(axis.ticks(windowEnd-7*day,windowEnd,300,168,'en-US'));
assert.ok(small.length>=1 && small.length<=4,
 'Narrow charts must not render overlapping date labels');
assert.ok(small.every(t=>t.label.includes('/')));
const view30=Array.from(axis.ticks(windowEnd-30*day,windowEnd,820,720,'en-US'));
assert.ok(view30.length>=4 && view30.length<=9);
assert.ok(view30.every(t=>/[A-Za-z]{3}\s+\d+/.test(t.label)),
 '30-day calendar ticks require month and day');
const short=Array.from(axis.ticks(windowEnd-hour,windowEnd,820,1,'en-US'));
assert.equal(short.length,5);
assert.ok(short.every(t=>/AM|PM/.test(t.label)), 'Short views retain time-of-day');
assert.ok(short.every(t=>!t.label.includes('/')));

// The 7-day axis crosses New Year and keeps the correct calendar date.
const jan=Date.UTC(2027,0,4,12);
const crossing=Array.from(axis.ticks(jan-7*day,jan,820,168,'en-US'));
assert.ok(crossing.some(t=>t.label.includes('12/')));
assert.ok(crossing.some(t=>t.label.includes('1/')));
assert.match(axis.tooltip(jan,'en-US','UTC'),/2027/);
assert.equal(axis.ticks(500,500,800,168).length,0);

// Confirm the production canvas uses the formatter for axis ticks, and its
// overlay uses a *full calendar date* even on short-range histories.
assert.ok(app.includes('ChartTimeAxis.ticks(xmin,xmax,cw,S.hours)'));
assert.ok(app.includes('ChartTimeAxis.tooltip(desired)'));
assert.ok(app.includes('headerWidth+20'),'Tooltip width should accommodate its full date');
assert.ok(!app.includes('ctx.fillText(shorttime(xmin+'),
 'Multi-day chart must not use time-only labels');
const html=read('src/SensorDash/wwwroot/index.html');
assert.ok(html.indexOf('/chart-time-axis.js')>=0);
assert.ok(html.indexOf('/chart-time-axis.js')<html.indexOf('/app.js'));
assert.ok(read('src/SensorDash/Program.cs').includes('"/chart-time-axis.js"'));
assert.ok(read('src/SensorDash/SensorDash.csproj').includes('wwwroot/chart-time-axis.js'));
console.log('PASS: 7/30-day dates, local-midnight ticks, compact views, year boundary and full hover date');
