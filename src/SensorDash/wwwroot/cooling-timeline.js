'use strict';
// Synchronized hover inspection for existing canvas charts. Does not fetch
// data or create timers. Cooling systems and dashboards each own a group;
// dashboard IDs use a dashboard: prefix to isolate their cursor state.
const CoolingTimeline=(()=>{
 const contexts=new WeakMap();
 let group='',timestamp=null;
 const register=(canvas,context)=>contexts.set(canvas,context);
 const members=id=>[...document.querySelectorAll('canvas[data-cooling-timeline]')]
  .filter(c=>c.dataset.coolingTimeline===id&&contexts.has(c));
 const clear=id=>{
  for(const canvas of members(id))contexts.get(canvas).clear();
 };
 const hover=(canvas,time)=>{
  const id=canvas.dataset.coolingTimeline;
  if(!id||!Number.isFinite(time))return false;
  if(group&&group!==id)clear(group);
  group=id;timestamp=time;
  for(const node of members(id)){
   const entry=contexts.get(node);
   entry.clear();
   // The same instant is rendered in every chart's own units and scale.
   entry.draw(time);
  }
  return true;
 };
 const focus=(id,time)=>{
  if(!id||!Number.isFinite(time))return false;
  if(group&&group!==id)clear(group);
  group=id;timestamp=time;
  const charts=members(id);
  if(!charts.length)return false;
  for(const node of charts){const entry=contexts.get(node);entry.clear();entry.draw(time);}
  return true;
 };
 const leave=canvas=>{
  const id=canvas.dataset.coolingTimeline;
  if(!id)return false;
  if(group===id){clear(id);group='';timestamp=null;}
  return true;
 };
 const reset=()=>{if(group)clear(group);group='';timestamp=null};
 return {register,hover,focus,leave,reset};
})();
