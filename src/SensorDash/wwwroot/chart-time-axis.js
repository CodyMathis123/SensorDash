'use strict';
// Calendar-aware time axes for every history chart. Day ticks use *local*
// midnight and Date#setDate rather than 24-hour increments so they remain
// correct across daylight-saving changes and month/year boundaries.
const ChartTimeAxis=(()=>{
 const DAY=86400000;
 const dateOptions=(hours)=>{
  // Compact local calendar date for 7-day views; slightly wider dates for
  // 30-day views where weekday names provide little useful information.
  return hours<=168
    ? {weekday:'short',month:'numeric',day:'numeric'}
    : {month:'short',day:'numeric'};
 };
 const localDate=(t,hours,locale,timeZone)=>{
  const options={...dateOptions(hours)};
  if(timeZone)options.timeZone=timeZone;
  return new Intl.DateTimeFormat(locale,options).format(new Date(t));
 };
 const shortTime=(t,locale,timeZone)=>{
  const options={hour:'numeric',minute:'2-digit'};
  if(timeZone)options.timeZone=timeZone;
  return new Intl.DateTimeFormat(locale,options).format(new Date(t));
 };
 const tooltip=(t,locale,timeZone)=>{
  const options={weekday:'short',month:'short',day:'numeric',year:'numeric'};
  if(timeZone)options.timeZone=timeZone;
  return new Intl.DateTimeFormat(locale,options).format(new Date(t))+
   ' · '+shortTime(t,locale,timeZone);
 };
 const ticks=(start,end,width,hours,locale,timeZone)=>{
  if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start)return [];
  // Existing time-only grid remains appropriate for <=24-hour histories.
  if(hours<=24){
   const divisions=4;
   return Array.from({length:divisions+1},(_,i)=>{
    const t=start+(end-start)*i/divisions;
    return {t,label:shortTime(t,locale,timeZone)};
   });
  }
  // Require enough space per date; thin charts show fewer local days instead
  // of allowing labels to overlap or silently hiding all calendar dates.
  const rangeDays=(end-start)/DAY;
  const maxLabels=Math.max(2,Math.floor(Math.max(0,width)/94));
  const daySteps=[1,2,3,4,5,7,10,14,21,30];
  const step=daySteps.find(days=>rangeDays/days<=maxLabels-1)||30;
  const d=new Date(start);
  d.setHours(0,0,0,0);
  if(d.getTime()<start)d.setDate(d.getDate()+1);
  const output=[];
  for(let i=0;i<100 && d.getTime()<=end;i++){
   const t=d.getTime();
   output.push({t,label:localDate(t,hours,locale,timeZone)});
   d.setDate(d.getDate()+step);
  }
  // A very narrow plot must still display a *date*. For ordinary 7/30 day
  // widths, midnight ticks above provide readable calendar-aligned labels.
  if(!output.length){
   const mid=(start+end)/2;
   output.push({t:mid,label:localDate(mid,hours,locale,timeZone)});
  }
  return output;
 };
 return {ticks,tooltip,shortTime,localDate};
})();
