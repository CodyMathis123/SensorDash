'use strict';
// Pure canvas-series preparation. Never interpolate samples across missing data.
// SQLite history is bucketed into ~500 bins per time range (at least 10 s), so
// gap tolerance scales with that bucket width rather than the visible pixel width.
const ChartGaps=(()=>{
 const bucketMs=hours=>Math.max(10000,(Number(hours)||1)*3600000/500);
 const toleranceMs=hours=>Math.max(90000,4*bucketMs(hours));
 // Half the maximum continuous interval is a conservative hover snap radius.
 const hoverRadiusMs=hours=>toleranceMs(hours)/2;
 const prepare=(raw,hours)=>{
  const points=(raw||[]).filter(p=>Number.isFinite(p?.t)&&Number.isFinite(p?.v))
   .sort((a,b)=>a.t-b.t);
  // One sample per timestamp. A fresh live reading wins a duplicate bucket.
  const unique=[];
  for(const p of points){
   if(unique.length && unique[unique.length-1].t===p.t)unique[unique.length-1]=p;
   else unique.push(p);
  }
  const segments=[],gaps=[];
  let current=[],previous=null;
  const tolerance=toleranceMs(hours);
  for(const p of unique){
   if(previous && p.t-previous.t>tolerance){
    segments.push(current);
    gaps.push({start:previous.t,end:p.t});
    current=[];
   }
   current.push(p);
   previous=p;
  }
  if(current.length)segments.push(current);
  return {points:unique,segments,gaps,hoverRadius:hoverRadiusMs(hours)};
 };
 const closest=(prepared,desired)=>{
  const points=prepared?.points||[];
  if(!points.length||!Number.isFinite(desired))return null;
  let low=0,high=points.length;
  while(low<high){
   const middle=(low+high)>>>1;
   if(points[middle].t<desired)low=middle+1;
   else high=middle;
  }
  const a=points[Math.max(0,low-1)],b=points[Math.min(points.length-1,low)];
  const nearest=Math.abs(a.t-desired)<=Math.abs(b.t-desired)?a:b;
  // Even the nearest point may be hours away, across a collector outage.
  if(Math.abs(nearest.t-desired)>prepared.hoverRadius)return null;
  return nearest;
 };
 const sharedOutages=preparedSeries=>{
  // Sensors with a single isolated sample offer no evidence of an outage's
  // boundaries, so don't use them to establish shared downtime.
  const eligible=(preparedSeries||[]).filter(s=>s?.points?.length>1);
  if(!eligible.length)return [];
  let common=eligible[0].gaps;
  for(const series of eligible.slice(1)){
   const next=[],other=series.gaps;
   let i=0,j=0;
   while(i<common.length&&j<other.length){
    const start=Math.max(common[i].start,other[j].start),
          end=Math.min(common[i].end,other[j].end);
    if(start<end)next.push({start,end});
    if(common[i].end<other[j].end)i++;else j++;
   }
   common=next;
   if(!common.length)break;
  }
  return common;
 };
 const duration=milliseconds=>{
  const minutes=Math.max(1,Math.round(milliseconds/60000));
  return minutes>=60?Math.floor(minutes/60)+'h '+String(minutes%60).padStart(2,'0')+'m':
   minutes+'m';
 };
 return {bucketMs,toleranceMs,hoverRadiusMs,prepare,closest,sharedOutages,duration};
})();
