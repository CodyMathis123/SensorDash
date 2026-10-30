'use strict';
// Read-only overview peaks and storage insights; load only on the relevant
// page, not in continuous sensor collection or the per-frame render loop.
const HistoryInsights=(()=>{
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const num=(n,d=1)=>Number.isFinite(n)?Number(n).toLocaleString(undefined,{maximumFractionDigits:d,minimumFractionDigits:d}):'—';
 const bytes=n=>Number.isFinite(n)?(n<1024?num(n,0)+' B':
   n<1048576?num(n/1024,1)+' KiB':num(n/1048576,1)+' MiB'):'—';
 const time=t=>Number.isFinite(t)?new Date(t).toLocaleString(undefined,
  {month:'short',day:'numeric',hour:'numeric',minute:'2-digit'}):'—';
 let range=720,statsCache=new Map(),storage=null,statsReq=null,storageReq=null,requestNumber=0;
 const retention=S=>Math.max(1,Math.min(1095,Number(S.settings?.retentionDays)||30));
 const span=S=>range==='retained'?retention(S)*24:Math.min(Number(range)||720,retention(S)*24);
 const selectIds=S=>{
  const sensors=(S.sensors||[]).filter(s=>
   (s.type==='Temperature'||s.type==='Power')&&s.deviceType!=='Virtual');
  const heat=(S.settings?.coolingSystems||[]).flatMap(x=>x.components||[])
   .filter(c=>c.kind==='heat-source');
  const assigned=new Set(heat.flatMap(c=>c.sensorIds||[]));
  const rank=(s,group)=>{
   const name=(s.name||'').toLowerCase(),device=(s.device||'').toLowerCase();
   if(group==='cpu')return /cpu|ryzen|processor|intel core/.test(device)?1:assigned.has(s.id)&&/cpu/.test(name)?2:0;
   if(group==='gpu')return /gpu|radeon|geforce|reaper/.test(device)?1:assigned.has(s.id)&&/gpu/.test(name)?2:0;
   if(group==='storage')return /storage|nvme|ssd|drive/.test((s.deviceType||'')+' '+device)?1:0;
   return 0;
  };
  const choose=(group,type,patterns)=>{
   const found=sensors.filter(s=>s.type===type&&rank(s,group)>0);
   for(const pattern of patterns){
    const matches=found.filter(s=>pattern.test(s.name||''));
    if(matches.length)return matches[0];
   }
   return found[0]||null;
  };
  const chosen=[
   choose('cpu','Temperature',[/Tctl\/Tdie|CPU Package|CPU Core|CPU Die|Package/i]),
   choose('gpu','Temperature',[/GPU Hot Spot|Hotspot/i]),
   choose('gpu','Temperature',[/GPU Core|GPU Temperature|Core/i]),
   choose('gpu','Temperature',[/GPU Memory|Memory Junction|VRAM/i]),
   choose('storage','Temperature',[/Composite|NVMe Temperature|Temperature/i]),
   choose('cpu','Power',[/CPU Package|CPU PPT|CPU Watts|Package Power|CPU Core/i]),
   choose('gpu','Power',[/GPU Board|Total Board|GPU Watts|GPU Power|Board Power|Total/i])
  ].filter(Boolean);
  return [...new Map(chosen.map(s=>[s.id,s])).values()].slice(0,8);
 };
 const markup=S=>{
  const selected=selectIds(S),options=[
   ['24','24 hours'],['168','7 days'],['720','30 days'],['2160','90 days'],
   ['retained','All retained ('+retention(S)+' days)']
  ];
  return '<div class="history-insights" data-history-insights>'+
   '<div class="history-insights-toolbar"><label>Summary period <select id="insightRange">'+
   options.map(([key,name])=>'<option value="'+key+'" '+(String(range)===key?'selected':'')+'>'+esc(name)+'</option>').join('')+
   '</select></label><span class="note">Recorded history, not lifetime hardware maxima</span></div>'+
   '<div id="insightSummary" aria-live="polite">'+
   (selected.length?'<p class="note">Calculating recorded peaks and power usage…</p>':
    '<p class="note">No compatible CPU, GPU or storage sensors detected.</p>')+'</div></div>';
 };
 const statsHtml=(S,series)=>{
  const selected=selectIds(S);
  if(!selected.length)return '<p class="note">No supported sensors available.</p>';
  const lookup=new Map((series||[]).map(x=>[x.id,x]));
  const temp=selected.filter(x=>x.type==='Temperature'),power=selected.filter(x=>x.type==='Power');
  const title=s=>S.settings?.aliases?.[s.id]||s.name;
  const tempRows=temp.map(s=>{
   const x=lookup.get(s.id);
   return '<tr><td>'+esc(title(s))+'<small>'+esc(s.device)+'</small></td>'+
    '<td>'+ (x?.count?num(x.max)+' °C':'—')+'</td>'+
    '<td>'+(x?.count?esc(time(x.peakAt)):'—')+'</td>'+
    '<td>'+(x?.count?num(x.average)+' °C':'—')+'</td></tr>';
  }).join('');
  const powRows=power.map(s=>{
   const x=lookup.get(s.id);
   return '<tr><td>'+esc(title(s))+'<small>'+esc(s.device)+'</small></td>'+
    '<td>'+(x?.count?num(x.max)+' W':'—')+'</td>'+
    '<td>'+(x?.count?num(x.average)+' W':'—')+'</td>'+
    '<td>'+(Number.isFinite(x?.energyWh)?num(x.energyWh,1)+' Wh':'—')+'</td>'+
    '<td>'+ (x?.count?num(x.coveredHours,2)+'h ('+num(x.coveragePercent,0)+'%)':'—')+
    '</td></tr>';
  }).join('');
  return (tempRows?'<h3>Recorded temperature peaks</h3><div class="table-container history-insights-table">'+
    '<table><thead><tr><th>Sensor</th><th>Max</th><th>Occurred</th><th>Sample average</th></tr></thead>'+
    '<tbody>'+tempRows+'</tbody></table></div>':'')+
   (powRows?'<h3>Measured component power</h3><div class="table-container history-insights-table">'+
    '<table><thead><tr><th>Sensor</th><th>Peak</th><th>Average</th><th>Est. energy</th><th>Observed span</th></tr></thead>'+
    '<tbody>'+powRows+'</tbody></table></div>':'')+
   '<p class="note">Maxima are from raw recorded measurements, not chart averages. Power is component-reported, not total PC wall draw. Watt-hours are estimated only between nearby recorded samples; unobserved periods are excluded. The percentage is observed time within the selected period, not a full energy bill. No CPU/GPU values are added together because reported power domains can overlap.</p>';
 };
 const storagePanel=()=>'<div class="history-storage-view">'+
  '<div class="w-actions"><button class="btn secondary" id="refreshStorageFootprint">Refresh storage usage</button></div>'+
  '<div id="historyStorageSummary" aria-live="polite"><p class="note">Reading current SQLite storage footprint…</p></div></div>';
 const storageHtml=(d,S={})=>{
  if(!d||!Number.isFinite(d.dbBytes))return '<p class="note">Storage information unavailable.</p>';
  const total=d.dbBytes+d.walBytes+d.shmBytes,
    count=Math.max(0,d.sampleCount||0);
  const share=n=>count?100*n/count:0;
  const estimate=n=>count?'~'+bytes(d.dbBytes*n/count):'—';
  const rows=(d.devices||[]).map(x=>'<tr><td>'+esc(x.device)+'</td><td>'+num(x.sensors,0)+
   '</td><td>'+num(x.samples,0)+'</td><td>'+num(share(x.samples),1)+'%</td>'+
   '<td>'+estimate(x.samples)+'</td></tr>').join('');
  const sensors=(d.sensors||[]).map(x=>'<tr><td>'+esc(S.settings?.aliases?.[x.id]||x.name)+
   '<small>'+esc(x.device)+'</small></td><td>'+num(x.count,0)+'</td>'+
   '<td>'+num(share(x.count),1)+'%</td><td>'+estimate(x.count)+'</td></tr>').join('');
  const observed=d.oldest&&d.newest?(d.newest-d.oldest)/86400000:null;
  const dailyRate=observed>1?d.dbBytes/observed:null;
  return '<div class="history-storage-metrics">'+
    '<div><small>Actual allocated files</small><strong>'+bytes(total)+'</strong></div>'+
    '<div><small>Main SQLite database</small><strong>'+bytes(d.dbBytes)+'</strong></div>'+
    '<div><small>Write-ahead log / shared state</small><strong>'+bytes(d.walBytes+d.shmBytes)+'</strong></div>'+
    '<div><small>Saved measurements</small><strong>'+num(count,0)+'</strong></div>'+
    '<div><small>Recorded sensors</small><strong>'+num(d.sensorCount||0,0)+'</strong></div>'+
    '<div><small>Reusable SQLite pages</small><strong>'+bytes(d.freePageBytes)+'</strong></div>'+
   '</div>'+
   '<p class="note">Retention: '+num(d.retentionDays,0)+' days · '+esc(d.mode)+' mode · saved every '+
     num(d.saveEverySeconds,0)+'s. Recorded span: '+esc(time(d.oldest))+' to '+esc(time(d.newest))+'.'+
     (dailyRate?' Current database size / observed span: ~'+bytes(dailyRate)+'/day (rough estimate; not a forecast).':'')+
     ' Shrinking retention deletes old rows but does not immediately shrink the database file; freed SQLite pages are reused.</p>'+
   '<h3>By hardware device</h3><div class="table-container history-storage-table"><table><thead>'+
    '<tr><th>Device</th><th>Sensors</th><th>Rows</th><th>Share</th><th>Est. DB portion</th></tr>'+
    '</thead><tbody>'+rows+'</tbody></table></div>'+
   '<details class="history-storage-sensors"><summary>Show individual sensor breakdown ('+
     num((d.sensors||[]).length,0)+')</summary>'+
    '<div class="table-container history-storage-table"><table><thead><tr>'+
    '<th>Sensor</th><th>Rows</th><th>Share</th><th>Est. DB portion</th></tr></thead><tbody>'+
    sensors+'</tbody></table></div></details>'+
   '<p class="note">The device/sensor size estimates allocate the main database file proportionally to sample-row counts. SQLite stores indexes, events and shared pages; these are <strong>estimates, not separately measured file sizes</strong>. The top-line database + WAL + SHM sizes are actual on-disk file sizes. Retention controls both samples and monitoring events.</p>';
 };
 const afterRender=S=>{
  if(S.view==='overview'){
   const el=document.getElementById('insightSummary');
   if(!el)return;
   const ids=selectIds(S).map(s=>s.id);
   if(!ids.length)return;
   const hours=span(S),key=hours+'|'+ids.join(',');
   const old=statsCache.get(key);
   if(old){
    el.innerHTML=statsHtml(S,old.series);
    if(Date.now()-old.time<120000)return;
   }
   if(statsReq?.key===key)return;
   const token=++requestNumber;
   const promise=fetch('/api/history/stats?ids='+encodeURIComponent(ids.join(','))+'&hours='+hours,
     {cache:'no-store'}).then(async response=>{
      if(!response.ok)throw Error('History summaries unavailable');
      const data=await response.json();
      statsCache.set(key,{time:Date.now(),series:data.series||[]});
      if(token===requestNumber && S.view==='overview' && document.getElementById('insightSummary'))
       document.getElementById('insightSummary').innerHTML=statsHtml(S,data.series||[]);
   }).catch(error=>{
    if(token===requestNumber&&document.getElementById('insightSummary')&&!old)
     document.getElementById('insightSummary').textContent=error.message;
   }).finally(()=>{if(statsReq?.promise===promise)statsReq=null});
   statsReq={key,promise};
  }
  if(S.view==='settings'){
   const el=document.getElementById('historyStorageSummary');
   if(!el)return;
   if(storage){
    el.innerHTML=storageHtml(storage.value,S);
    if(Date.now()-storage.time<120000)return;
   }
   if(storageReq)return;
   storageReq=fetch('/api/history/storage',{cache:'no-store'}).then(async r=>{
    if(!r.ok)throw Error('Storage statistics unavailable');
    const value=await r.json();storage={value,time:Date.now()};
    if(S.view==='settings'&&document.getElementById('historyStorageSummary'))
     document.getElementById('historyStorageSummary').innerHTML=storageHtml(value,S);
   }).catch(err=>{
    if(!storage&&document.getElementById('historyStorageSummary'))
     document.getElementById('historyStorageSummary').textContent=err.message;
   }).finally(()=>{storageReq=null});
  }
 };
 const handle=(e,S)=>{
  if(e.target.id==='insightRange'){
   range=e.target.value;
   requestNumber++;
   const el=document.getElementById('insightSummary');
   if(el)el.innerHTML='<p class="note">Calculating recorded history…</p>';
   afterRender(S);return true;
  }
  if(e.target.id==='refreshStorageFootprint'){
   storage=null;
   const el=document.getElementById('historyStorageSummary');
   if(el)el.innerHTML='<p class="note">Reading storage…</p>';
   afterRender(S);return true;
  }
  return false;
 };
 return {markup,storagePanel,afterRender,handle,selectIds,statsHtml,storageHtml};
})();
